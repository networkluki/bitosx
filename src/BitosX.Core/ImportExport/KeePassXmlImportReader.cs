using System.Text;
using System.Xml;
using BitosX.Core.Authenticator;
using BitosX.Core.Domain;

namespace BitosX.Core.ImportExport;

/// <summary>
/// Reads KeePass 2 XML, as KeePass 2 ("KeePass XML (2.x)") and KeePassXC ("XML") export it:
/// <c>KeePassFile/Root/Group…/Entry</c>, each entry with its <c>String</c> elements.
/// </summary>
/// <remarks>
/// The file is attacker-controlled, and XML is the format with the most ways to abuse a reader:
/// <list type="bullet">
/// <item>No document type declaration is accepted, so there are no entities to expand and nothing to
/// fetch. A file that contains <c>&lt;!DOCTYPE</c> anywhere is refused before it is parsed, and the
/// reader is configured to refuse one as well.</item>
/// <item>The document is read forward, one node at a time, and never built in memory. Nesting is
/// counted as it is read and refused beyond a limit; the reader itself has none, and keeps a record
/// for every open element.</item>
/// <item>The number of entries is counted as they are read, and a file with too many is refused at
/// the first one too many.</item>
/// </list>
/// </remarks>
internal static class KeePassXmlImportReader
{
    private const string TitleKey = "Title";
    private const string UserNameKey = "UserName";
    private const string PasswordKey = "Password";
    private const string UrlKey = "URL";
    private const string NotesKey = "Notes";

    // The strings an authenticator key is kept in (docs/IMPORT_EXPORT.md §5.3): KeePassXC's "otp"
    // (an otpauth:// link), and KeePass 2's TimeOtp-* (KeePass/Util/EntryUtil.cs). KeePass reads
    // the secret from the first of TimeOtp-Secret (UTF-8), -Hex, -Base32 and -Base64 that is not
    // empty; only Base32 is read here.
    private const string OtpKey = "otp";
    private const string TimeOtpSecretUtf8 = "TimeOtp-Secret";
    private const string TimeOtpSecretHex = "TimeOtp-Secret-Hex";
    private const string TimeOtpSecretBase32 = "TimeOtp-Secret-Base32";
    private const string TimeOtpAlgorithm = "TimeOtp-Algorithm";
    private const string TimeOtpLength = "TimeOtp-Length";
    private const string TimeOtpPeriod = "TimeOtp-Period";

    private static readonly string[] AuthenticatorKeys =
        [OtpKey, TimeOtpSecretUtf8, TimeOtpSecretHex, TimeOtpSecretBase32, TimeOtpAlgorithm, TimeOtpLength, TimeOtpPeriod];

    // The UUID of "no group": sixteen zero bytes, as both applications write it.
    private const string NoUuid = "AAAAAAAAAAAAAAAAAAAAAA==";

    public static ParsedImport Read(string text)
    {
        // Looked for in the text itself, so that the answer does not depend on a parser: a document
        // type declaration is refused wherever these characters are, even inside a comment.
        if (text.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase))
        {
            throw new ImportRefusedException(ImportRefusal.Doctype);
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = ImportLimits.MaxFileBytes,
            MaxCharactersFromEntities = 1024,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = false, // a value may be nothing but spaces
            CheckCharacters = true,
            ConformanceLevel = ConformanceLevel.Document,
            CloseInput = true,
        };

        var document = new Document();
        try
        {
            // Spaces, tabs and line ends before the first "<" are not part of the document.
            using var reader = XmlReader.Create(new StringReader(text.TrimStart(' ', '\t', '\r', '\n')), settings);
            document.Read(reader);
        }
        catch (XmlException)
        {
            // The parser's message can quote the file, so it is not passed on.
            throw new ImportRefusedException(ImportRefusal.InvalidXml);
        }

        return document.ToParsedImport();
    }

    private sealed class Group(Group? parent)
    {
        public Group? Parent { get; } = parent;

        public string? Uuid { get; set; }

        public string? Name { get; set; }

        /// <summary>Whether this is the recycle bin or a group inside it. Worked out once per group.</summary>
        public bool? InRecycleBin { get; set; }
    }

    private sealed class Entry(Group group, int position)
    {
        public Group Group { get; } = group;

        public int Position { get; } = position;

        public string? Title { get; set; }

        public string? UserName { get; set; }

        public string? Password { get; set; }

        public string? Url { get; set; }

        public string? Notes { get; set; }

        public bool HasFieldTwice { get; set; }

        public List<ImportCustomField> CustomFields { get; } = [];

        /// <summary>
        /// For each string an authenticator key can be kept in, the custom field that is its first
        /// occurrence in the entry. Later occurrences are ordinary custom fields.
        /// </summary>
        public Dictionary<string, int> AuthenticatorFields { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Document
    {
        private readonly List<Entry> _entries = [];
        private string? _recycleBinUuid;
        private bool _hasMeta;
        private bool _hasRoot;

        public void Read(XmlReader reader)
        {
            reader.MoveToContent();
            if (reader.NodeType != XmlNodeType.Element || reader.Name != "KeePassFile")
            {
                throw new ImportRefusedException(ImportRefusal.NotRecognised);
            }

            Check(reader);
            ForEachChild(reader, name =>
            {
                if (name == "Meta" && !_hasMeta)
                {
                    _hasMeta = true;
                    ReadMeta(reader);
                }
                else if (name == "Root" && !_hasRoot)
                {
                    _hasRoot = true;
                    ReadRoot(reader);
                }
                else
                {
                    Skip(reader);
                }
            });

            // To the end: whatever follows the document has to be well formed too.
            while (reader.Read())
            {
            }
        }

        public ParsedImport ToParsedImport()
        {
            var entries = new List<ImportEntry>(_entries.Count);
            int inTrash = 0;

            foreach (Entry entry in _entries)
            {
                if (IsInRecycleBin(entry.Group))
                {
                    inTrash++;
                    continue;
                }

                (TotpKey? key, TotpRefusal? refusal, List<ImportCustomField> customFields) = AuthenticatorKeyOf(entry);
                entries.Add(new ImportEntry
                {
                    Position = entry.Position,
                    Name = entry.Title ?? string.Empty,
                    Username = entry.UserName ?? string.Empty,
                    Password = entry.Password ?? string.Empty,
                    Notes = entry.Notes ?? string.Empty,
                    Addresses = entry.Url is { Length: > 0 } url ? [url] : [],
                    Folder = PathOf(entry.Group),
                    CustomFields = customFields,
                    TotpKey = key,
                    TotpRefusal = refusal,
                    Problem = entry.HasFieldTwice ? ImportReason.FieldTwice : null,
                });
            }

            return new ParsedImport(
                ImportFormat.KeePassXml,
                ImportTexts.LabelKeePassXml,
                entries,
                _entries.Count,
                new Dictionary<ImportUnsupportedKind, int>(),
                inTrash,
                ignoredColumns: []);
        }

        private void ReadMeta(XmlReader reader)
            => ForEachChild(reader, name =>
            {
                if (name == "RecycleBinUUID" && _recycleBinUuid is null)
                {
                    _recycleBinUuid = ImportText.Trim(ReadText(reader));
                }
                else
                {
                    Skip(reader);
                }
            });

        private void ReadRoot(XmlReader reader)
            => ForEachChild(reader, name =>
            {
                if (name == "Group")
                {
                    ReadGroup(reader, parent: null, level: 1);
                }
                else
                {
                    Skip(reader);
                }
            });

        private void ReadGroup(XmlReader reader, Group? parent, int level)
        {
            if (level > ImportLimits.MaxGroupDepth)
            {
                throw new ImportRefusedException(ImportRefusal.NestedTooDeeply);
            }

            var group = new Group(parent);
            ForEachChild(reader, name =>
            {
                switch (name)
                {
                    case "UUID" when group.Uuid is null:
                        group.Uuid = ImportText.Trim(ReadText(reader));
                        break;
                    case "Name" when group.Name is null:
                        group.Name = ImportText.Trim(ReadText(reader));
                        break;
                    case "Entry":
                        ReadEntry(reader, group);
                        break;
                    case "Group":
                        ReadGroup(reader, group, level + 1);
                        break;
                    default:
                        Skip(reader);
                        break;
                }
            });
        }

        private void ReadEntry(XmlReader reader, Group group)
        {
            if (_entries.Count >= ImportLimits.MaxEntries)
            {
                throw new ImportRefusedException(ImportRefusal.TooManyEntries);
            }

            var entry = new Entry(group, _entries.Count + 1);
            _entries.Add(entry);

            // Only the strings are read. The earlier versions of the entry are in History, which is
            // skipped like everything else, so they are neither imported nor counted.
            ForEachChild(reader, name =>
            {
                if (name == "String")
                {
                    ReadString(reader, entry);
                }
                else
                {
                    Skip(reader);
                }
            });
        }

        private static void ReadString(XmlReader reader, Entry entry)
        {
            string? key = null;
            string? value = null;
            ForEachChild(reader, name =>
            {
                if (name == "Key" && key is null)
                {
                    key = ReadText(reader);
                }
                else if (name == "Value" && value is null)
                {
                    value = ReadText(reader);
                }
                else
                {
                    Skip(reader);
                }
            });

            if (key is null)
            {
                return;
            }

            value ??= string.Empty;
            switch (key)
            {
                case TitleKey:
                    entry.HasFieldTwice |= entry.Title is not null;
                    entry.Title ??= value;
                    break;
                case UserNameKey:
                    entry.HasFieldTwice |= entry.UserName is not null;
                    entry.UserName ??= value;
                    break;
                case PasswordKey:
                    entry.HasFieldTwice |= entry.Password is not null;
                    entry.Password ??= value;
                    break;
                case UrlKey:
                    entry.HasFieldTwice |= entry.Url is not null;
                    entry.Url ??= value;
                    break;
                case NotesKey:
                    entry.HasFieldTwice |= entry.Notes is not null;
                    entry.Notes ??= value;
                    break;
                default:
                    // Every other string is a custom field. Those an authenticator key is kept in
                    // are marked, by their name as written; whether they stay custom fields is
                    // decided once the entry is read (AuthenticatorKeyOf).
                    if (Array.IndexOf(AuthenticatorKeys, key) >= 0)
                    {
                        entry.AuthenticatorFields.TryAdd(key, entry.CustomFields.Count);
                    }

                    entry.CustomFields.Add(new ImportCustomField(ImportText.Trim(key), value));
                    break;
            }
        }

        /// <summary>
        /// The authenticator key of an entry (docs/IMPORT_EXPORT.md §5.3), and the custom fields that
        /// are left once the strings it was read from are taken away. The first string of a name
        /// counts; a value that is blank counts as none.
        /// <list type="number">
        /// <item><c>otp</c>: read by docs/TOTP.md §3.</item>
        /// <item>Otherwise <c>TimeOtp-Secret-Base32</c>, if <c>TimeOtp-Secret</c> and
        /// <c>TimeOtp-Secret-Hex</c> are blank (KeePass would use those first): read with
        /// <c>TimeOtp-Algorithm</c>, <c>-Length</c> and <c>-Period</c>.</item>
        /// </list>
        /// A key that is read takes its strings out of the custom fields. One that is refused leaves
        /// them where they are, and is counted as a key that could not be read.
        /// </summary>
        private static (TotpKey? Key, TotpRefusal? Refusal, List<ImportCustomField> CustomFields) AuthenticatorKeyOf(Entry entry)
        {
            string? Value(string name)
                => entry.AuthenticatorFields.TryGetValue(name, out int index) ? entry.CustomFields[index].Value : null;

            bool IsSet(string name) => Value(name) is { } value && !ImportText.IsBlank(value);

            TotpParseResult result;
            string[] used;
            if (IsSet(OtpKey))
            {
                result = TotpKeyParser.Parse(Value(OtpKey));
                used = [OtpKey];
            }
            else if (IsSet(TimeOtpSecretBase32) && !IsSet(TimeOtpSecretUtf8) && !IsSet(TimeOtpSecretHex))
            {
                result = TotpKeyParser.ParseKeePass2(
                    Value(TimeOtpSecretBase32)!, Value(TimeOtpAlgorithm), Value(TimeOtpLength), Value(TimeOtpPeriod));
                used = [TimeOtpSecretBase32, TimeOtpAlgorithm, TimeOtpLength, TimeOtpPeriod];
            }
            else
            {
                return (null, null, entry.CustomFields);
            }

            if (result.Key is null)
            {
                return (null, result.Refusal, entry.CustomFields);
            }

            var taken = new HashSet<int>();
            foreach (string name in used)
            {
                if (entry.AuthenticatorFields.TryGetValue(name, out int index))
                {
                    taken.Add(index);
                }
            }

            var left = new List<ImportCustomField>(entry.CustomFields.Count);
            for (int i = 0; i < entry.CustomFields.Count; i++)
            {
                if (!taken.Contains(i))
                {
                    left.Add(entry.CustomFields[i]);
                }
            }

            return (result.Key, null, left);
        }

        private bool IsInRecycleBin(Group group)
        {
            if (_recycleBinUuid is null || _recycleBinUuid.Length == 0 || _recycleBinUuid == NoUuid)
            {
                return false;
            }

            if (group.InRecycleBin is bool known)
            {
                return known;
            }

            // The recycle bin is the group the file names as such; what is in a group inside it is in it.
            bool inBin = group.Uuid == _recycleBinUuid || (group.Parent is not null && IsInRecycleBin(group.Parent));
            group.InRecycleBin = inBin;
            return inBin;
        }

        /// <summary>
        /// The names of the groups an entry is in, from the one below the root group down, with "/"
        /// between them. The root group is the database itself and is left out; so is a group
        /// without a name.
        /// </summary>
        private static string PathOf(Group group)
        {
            var names = new List<string>();
            for (Group? g = group; g?.Parent is not null; g = g.Parent)
            {
                if (g.Name is { Length: > 0 } name)
                {
                    names.Add(name);
                }
            }

            names.Reverse();
            return string.Join('/', names);
        }

        // ---- Walking the document ----

        /// <summary>
        /// What is checked on every element, read or skipped: how deep it is, and whether it says
        /// that its value is encrypted. A plain export has no such value anywhere.
        /// </summary>
        private static void Check(XmlReader reader)
        {
            if (reader.Depth >= ImportLimits.MaxXmlDepth)
            {
                throw new ImportRefusedException(ImportRefusal.NestedTooDeeply);
            }

            if (reader.HasAttributes
                && reader.GetAttribute("Protected") is { } isProtected
                && (isProtected == "1" || ImportText.ToLowerAscii(isProtected) == "true"))
            {
                throw new ImportRefusedException(ImportRefusal.ProtectedValues);
            }
        }

        /// <summary>
        /// Calls <paramref name="onChild"/> for every element directly inside the element the reader
        /// is on, with its name. The callback has to consume that element: when it returns, the
        /// reader is on its end tag (or still on it, if it is empty).
        /// </summary>
        private static void ForEachChild(XmlReader reader, Action<string> onChild)
        {
            if (reader.IsEmptyElement)
            {
                return;
            }

            int depth = reader.Depth;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    Check(reader);
                    onChild(reader.Name);
                }
                else if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                {
                    return;
                }
            }
        }

        /// <summary>Passes over the element the reader is on and everything inside it, checking every element.</summary>
        private static void Skip(XmlReader reader)
        {
            if (reader.IsEmptyElement)
            {
                return;
            }

            int depth = reader.Depth;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    Check(reader);
                }
                else if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                {
                    return;
                }
            }
        }

        /// <summary>
        /// The text of the element the reader is on: its own text and CDATA sections, one after the
        /// other, exactly as they are. An element inside it is passed over.
        /// </summary>
        private static string ReadText(XmlReader reader)
        {
            if (reader.IsEmptyElement)
            {
                return string.Empty;
            }

            int depth = reader.Depth;
            string? single = null;
            StringBuilder? several = null;
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                    case XmlNodeType.Whitespace:
                    case XmlNodeType.SignificantWhitespace:
                        if (single is null && several is null)
                        {
                            single = reader.Value;
                        }
                        else
                        {
                            several ??= new StringBuilder(single);
                            several.Append(reader.Value);
                        }

                        break;
                    case XmlNodeType.Element:
                        Check(reader);
                        Skip(reader);
                        break;
                    case XmlNodeType.EndElement when reader.Depth == depth:
                        return several?.ToString() ?? single ?? string.Empty;
                }
            }

            return several?.ToString() ?? single ?? string.Empty;
        }
    }
}
