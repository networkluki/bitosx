using System.Text.Json;

namespace BitosX.Core.ImportExport;

/// <summary>
/// Reads the two JSON formats: Bitwarden's unencrypted export and BitosX's own. The document is
/// parsed strictly (no comments, no trailing commas, at most 64 levels, no name twice in one object,
/// no unpaired surrogate anywhere); what is read from it is read leniently, one member at a time: a
/// member that is absent, null, or of another JSON type than expected counts as absent.
/// </summary>
internal static class JsonImportReader
{
    /// <summary>The value of <c>format</c> in a BitosX export.</summary>
    public const string BitosXFormatName = "bitosx-export";

    /// <summary>The version of the BitosX export format this build reads and writes.</summary>
    public const int BitosXFormatVersion = 1;

    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        MaxDepth = ImportLimits.MaxJsonDepth,
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        MaxDepth = ImportLimits.MaxJsonDepth,
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,

        // As everywhere this application reads JSON it did not write: a name that occurs twice in one
        // object has no single value, so the document is refused rather than one of the two chosen.
        AllowDuplicateProperties = false,
    };

    public static ParsedImport Read(ReadOnlyMemory<byte> utf8)
    {
        RefuseUnpairedSurrogates(utf8.Span);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8, DocumentOptions);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // The parser's message can quote the file, so it is not passed on.
            throw new ImportRefusedException(ImportRefusal.InvalidJson);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ImportRefusedException(ImportRefusal.NotRecognised);
            }

            if (Text(root, "format") == BitosXFormatName)
            {
                return ReadBitosX(root);
            }

            // Both of Bitwarden's encrypted exports say so here; the one protected by a password has
            // no items at all.
            if (IsTrue(root, "encrypted"))
            {
                throw new ImportRefusedException(ImportRefusal.EncryptedExport);
            }

            if (TryGetArray(root, "items", out JsonElement items))
            {
                return ReadBitwarden(root, items);
            }

            throw new ImportRefusedException(ImportRefusal.NotRecognised);
        }
    }

    // ---- Bitwarden ----

    private static ParsedImport ReadBitwarden(JsonElement root, JsonElement items)
    {
        RefuseTooMany(items);

        // The folders of the export, by their id. Of two with one id, the first.
        var folderNames = new Dictionary<string, string>(StringComparer.Ordinal);
        if (TryGetArray(root, "folders", out JsonElement folders))
        {
            foreach (JsonElement folder in folders.EnumerateArray())
            {
                if (folder.ValueKind == JsonValueKind.Object && Text(folder, "id") is { Length: > 0 } id)
                {
                    folderNames.TryAdd(id, Text(folder, "name"));
                }
            }
        }

        var entries = new List<ImportEntry>();
        var unsupported = new Dictionary<ImportUnsupportedKind, int>();
        int inTrash = 0;
        int position = 0;

        foreach (JsonElement item in items.EnumerateArray())
        {
            position++;
            if (item.ValueKind != JsonValueKind.Object)
            {
                Count(unsupported, ImportUnsupportedKind.Other);
                continue;
            }

            // In the trash, whatever it is. (Bitwarden leaves deleted items out of an export; a file
            // that has one was put together some other way.)
            if (item.TryGetProperty("deletedDate", out JsonElement deleted) && deleted.ValueKind != JsonValueKind.Null)
            {
                inTrash++;
                continue;
            }

            ImportUnsupportedKind? kind = BitwardenKind(item);
            if (kind is not null)
            {
                Count(unsupported, kind.Value);
                continue;
            }

            var addresses = new List<string>();
            string username = string.Empty;
            string password = string.Empty;
            string totp = string.Empty;
            if (item.TryGetProperty("login", out JsonElement login) && login.ValueKind == JsonValueKind.Object)
            {
                username = Text(login, "username");
                password = Text(login, "password");
                totp = Text(login, "totp");
                if (TryGetArray(login, "uris", out JsonElement uris))
                {
                    foreach (JsonElement uri in uris.EnumerateArray())
                    {
                        if (uri.ValueKind == JsonValueKind.Object && Text(uri, "uri") is { Length: > 0 } address)
                        {
                            addresses.Add(address);
                        }
                    }
                }
            }

            var customFields = new List<ImportCustomField>();
            if (TryGetArray(item, "fields", out JsonElement fields))
            {
                foreach (JsonElement field in fields.EnumerateArray())
                {
                    if (field.ValueKind == JsonValueKind.Object)
                    {
                        customFields.Add(new ImportCustomField(ImportText.Trim(Text(field, "name")), Text(field, "value")));
                    }
                }
            }

            entries.Add(new ImportEntry
            {
                Position = position,
                Name = Text(item, "name"),
                Username = username,
                Password = password,
                Notes = Text(item, "notes"),
                Favorite = IsTrue(item, "favorite"),
                Addresses = addresses,
                Totp = totp,
                Folder = folderNames.GetValueOrDefault(Text(item, "folderId"), string.Empty),
                CustomFields = customFields,
            });
        }

        return new ParsedImport(
            ImportFormat.BitwardenJson, ImportTexts.LabelBitwardenJson, entries, position, unsupported, inTrash, ignoredColumns: []);
    }

    /// <summary>What an item is, if it is not a login: Bitwarden numbers its kinds, and a login is 1.</summary>
    private static ImportUnsupportedKind? BitwardenKind(JsonElement item)
    {
        if (!item.TryGetProperty("type", out JsonElement type)
            || type.ValueKind != JsonValueKind.Number
            || !type.TryGetDouble(out double number))
        {
            return ImportUnsupportedKind.Other;
        }

        return number switch
        {
            1 => null,
            2 => ImportUnsupportedKind.SecureNote,
            3 => ImportUnsupportedKind.Card,
            4 => ImportUnsupportedKind.Identity,
            5 => ImportUnsupportedKind.SshKey,
            6 => ImportUnsupportedKind.BankAccount,
            7 => ImportUnsupportedKind.DriversLicense,
            8 => ImportUnsupportedKind.Passport,
            _ => ImportUnsupportedKind.Other,
        };
    }

    // ---- BitosX ----

    private static ParsedImport ReadBitosX(JsonElement root)
    {
        if (!root.TryGetProperty("version", out JsonElement version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetDouble(out double number)
            || number != BitosXFormatVersion)
        {
            throw new ImportRefusedException(ImportRefusal.UnsupportedVersion);
        }

        if (!TryGetArray(root, "items", out JsonElement items))
        {
            throw new ImportRefusedException(ImportRefusal.NotRecognised);
        }

        RefuseTooMany(items);

        var entries = new List<ImportEntry>();
        var unsupported = new Dictionary<ImportUnsupportedKind, int>();
        int position = 0;

        foreach (JsonElement item in items.EnumerateArray())
        {
            position++;
            if (item.ValueKind != JsonValueKind.Object || Text(item, "type") != "login")
            {
                Count(unsupported, ImportUnsupportedKind.Other);
                continue;
            }

            var addresses = new List<string>();
            if (TryGetArray(item, "urls", out JsonElement urls))
            {
                foreach (JsonElement url in urls.EnumerateArray())
                {
                    if (url.ValueKind == JsonValueKind.String)
                    {
                        addresses.Add(url.GetString()!);
                    }
                }
            }

            entries.Add(new ImportEntry
            {
                Position = position,
                Name = Text(item, "name"),
                Username = Text(item, "username"),
                Password = Text(item, "password"),
                Notes = Text(item, "notes"),
                Favorite = IsTrue(item, "favorite"),
                Addresses = addresses,
            });
        }

        return new ParsedImport(
            ImportFormat.BitosXJson, ImportTexts.LabelBitosXJson, entries, position, unsupported, inTrash: 0, ignoredColumns: []);
    }

    // ---- Reading one member ----

    /// <summary>The string <paramref name="name"/> holds, or empty if it is absent, null or not a string.</summary>
    private static string Text(JsonElement parent, string name)
        => parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : string.Empty;

    private static bool IsTrue(JsonElement parent, string name)
        => parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private static bool TryGetArray(JsonElement parent, string name, out JsonElement array)
        => parent.TryGetProperty(name, out array) && array.ValueKind == JsonValueKind.Array;

    private static void Count(Dictionary<ImportUnsupportedKind, int> counts, ImportUnsupportedKind kind)
        => counts[kind] = counts.GetValueOrDefault(kind) + 1;

    private static void RefuseTooMany(JsonElement items)
    {
        if (items.GetArrayLength() > ImportLimits.MaxEntries)
        {
            throw new ImportRefusedException(ImportRefusal.TooManyEntries);
        }
    }

    // ---- Unpaired surrogates ----

    /// <summary>
    /// Refuses a document in which a name or a string holds half of a surrogate pair. The bytes are
    /// valid UTF-8 by now, so this can only be written as an escape (<c>\uD83D</c> without its other
    /// half). Such text cannot be stored: the vault is written as UTF-8. It is looked for in the
    /// whole document, so that what is refused does not depend on which members are read.
    /// This pass also finds damaged JSON and nesting beyond the limit.
    /// </summary>
    private static void RefuseUnpairedSurrogates(ReadOnlySpan<byte> utf8)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8, ReaderOptions);
            while (reader.Read())
            {
                if (reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName
                    && reader.ValueIsEscaped
                    && HasUnpairedSurrogateEscape(reader.ValueSpan))
                {
                    throw new ImportRefusedException(ImportRefusal.InvalidJson);
                }
            }
        }
        catch (JsonException)
        {
            throw new ImportRefusedException(ImportRefusal.InvalidJson);
        }
    }

    /// <summary>
    /// Whether the text between the quotes of a JSON string, escapes and all, has a <c>\uXXXX</c>
    /// for a high surrogate that no <c>\uXXXX</c> for a low surrogate follows at once, or one for a
    /// low surrogate that does not follow a high one. The reader has already checked that every
    /// escape is well formed.
    /// </summary>
    internal static bool HasUnpairedSurrogateEscape(ReadOnlySpan<byte> escaped)
    {
        bool expectLow = false;
        int i = 0;
        while (i < escaped.Length)
        {
            if (escaped[i] != (byte)'\\' || i + 1 >= escaped.Length)
            {
                if (expectLow)
                {
                    return true;
                }

                i++;
                continue;
            }

            if (escaped[i + 1] != (byte)'u' || i + 6 > escaped.Length)
            {
                // A two-character escape (\n, \", \\ ...).
                if (expectLow)
                {
                    return true;
                }

                i += 2;
                continue;
            }

            int unit = (Hex(escaped[i + 2]) << 12) | (Hex(escaped[i + 3]) << 8) | (Hex(escaped[i + 4]) << 4) | Hex(escaped[i + 5]);
            bool isHigh = unit is >= 0xD800 and <= 0xDBFF;
            bool isLow = unit is >= 0xDC00 and <= 0xDFFF;
            if (expectLow != isLow)
            {
                return true; // a low half without a high one before it, or a high half without its low one
            }

            expectLow = isHigh;
            i += 6;
        }

        return expectLow;
    }

    private static int Hex(byte digit) => digit switch
    {
        >= (byte)'0' and <= (byte)'9' => digit - '0',
        >= (byte)'a' and <= (byte)'f' => digit - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => digit - 'A' + 10,
        _ => 0,
    };
}
