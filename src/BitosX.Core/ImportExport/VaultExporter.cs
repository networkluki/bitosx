using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using BitosX.Core.Domain;

namespace BitosX.Core.ImportExport;

/// <summary>The two formats a vault can be written out in, unencrypted.</summary>
public enum ExportFormat
{
    /// <summary>
    /// Comma-separated, in the layout Chrome writes and most password managers read:
    /// <c>name,url,username,password,note</c>. One address per item.
    /// </summary>
    Csv = 0,

    /// <summary>BitosX's own JSON: everything an item holds, every address included.</summary>
    BitosXJson = 1,
}

/// <summary>What a person is told, and has to acknowledge, before an unencrypted export: the same words in both clients.</summary>
public static class ExportTexts
{
    /// <summary>The warning shown before an export.</summary>
    public const string Warning =
        "This file will contain every password in plain text. Anyone who can read it can sign in as you. "
        + "Keep it only as long as you need it, and delete it afterwards. "
        + "Do not open it in a spreadsheet program: such programs can run formulas found in files like this. "
        + "To keep a copy of your vault, use the encrypted backup instead.";

    /// <summary>The box that has to be ticked before the file can be written.</summary>
    public const string Acknowledgement = "I understand that this file is not encrypted";
}

/// <summary>
/// Writes the items of a vault as an unencrypted file: items in, bytes out. What it produces holds
/// every password in plain text; who may ask for it, and where it is written, is decided by
/// <see cref="Services.IVaultExportService"/>.
/// </summary>
/// <remarks>
/// Values are written exactly as they are. In particular nothing is put in front of a value that a
/// spreadsheet program would take for a formula: the file is there to be read by another password
/// manager, and a password with a quote mark in front of it is another password. That such a file
/// must not be opened in a spreadsheet program is said where it is exported (docs/IMPORT_EXPORT.md).
/// </remarks>
public static class VaultExporter
{
    private const string CsvHeader = "name,url,username,password,note";
    private const string LineEnd = "\r\n";

    // Text as it is, where JSON allows it: a password with "+" or "<" in it reads as that password.
    // (The "unsafe" in the name is about text that is put into an HTML page, which this is not.)
    private static readonly JsonWriterOptions JsonOptions = new()
    {
        Indented = true,
        IndentCharacter = ' ',
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The items in <paramref name="format"/>, as the bytes of the file.</summary>
    /// <param name="items">The items, in the order they are written.</param>
    /// <param name="format">The format.</param>
    /// <param name="exportedAt">When; written into a JSON export, to the second.</param>
    public static byte[] Export(IReadOnlyList<VaultItem> items, ExportFormat format, DateTimeOffset exportedAt)
        => format switch
        {
            ExportFormat.Csv => ToCsv(items),
            ExportFormat.BitosXJson => ToBitosXJson(items, exportedAt),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

    /// <summary>
    /// The items as comma-separated values: UTF-8 without a byte order mark; the header line
    /// <c>name,url,username,password,note</c>; then one line for each item, every value in double
    /// quotes with a double quote inside it written twice; CR LF after every line, the last one
    /// included. <c>url</c> is the item's first address, and empty if it has none: its other
    /// addresses are not in the file (<see cref="CountItemsWithSeveralUrls"/>).
    /// </summary>
    public static byte[] ToCsv(IReadOnlyList<VaultItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var text = new StringBuilder(CsvHeader);
        text.Append(LineEnd);
        foreach (VaultItem item in items)
        {
            if (item is not LoginItem login)
            {
                continue; // the only kind there is; another kind would have no place in this layout
            }

            AppendCsvValue(text, login.Name).Append(',');
            AppendCsvValue(text, login.Urls.Count > 0 ? login.Urls[0] : string.Empty).Append(',');
            AppendCsvValue(text, login.Username ?? string.Empty).Append(',');
            AppendCsvValue(text, login.Password ?? string.Empty).Append(',');
            AppendCsvValue(text, login.Notes ?? string.Empty).Append(LineEnd);
        }

        return Encoding.UTF8.GetBytes(text.ToString());
    }

    /// <summary>
    /// The items as BitosX's own export: UTF-8 JSON without a byte order mark, indented by two
    /// spaces, with LF line ends and one at the end.
    /// <code>
    /// { "format": "bitosx-export", "version": 1, "exportedAt": "2026-11-10T12:00:00Z",
    ///   "items": [ { "type": "login", "name": "…", "username": "…", "password": "…", "urls": ["…"],
    ///                "totp": { "secret": "…", "algorithm": "SHA1", "digits": 6, "period": 30 },
    ///                "notes": "…", "favorite": false, "createdAt": "…", "updatedAt": "…" } ] }
    /// </code>
    /// A value an item does not have is an empty string; <c>totp</c> is <c>null</c> for a login
    /// without an authenticator key. Times are UTC and end in <c>Z</c>; those of
    /// an item keep their fraction of a second, without trailing zeros.
    /// </summary>
    public static byte[] ToBitosXJson(IReadOnlyList<VaultItem> items, DateTimeOffset exportedAt)
    {
        ArgumentNullException.ThrowIfNull(items);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("format", JsonImportReader.BitosXFormatName);
            writer.WriteNumber("version", JsonImportReader.BitosXFormatVersion);
            writer.WriteString("exportedAt", exportedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            writer.WriteStartArray("items");
            foreach (VaultItem item in items)
            {
                if (item is not LoginItem login)
                {
                    continue;
                }

                writer.WriteStartObject();
                writer.WriteString("type", "login");
                writer.WriteString("name", login.Name);
                writer.WriteString("username", login.Username ?? string.Empty);
                writer.WriteString("password", login.Password ?? string.Empty);
                writer.WriteStartArray("urls");
                foreach (string url in login.Urls)
                {
                    writer.WriteStringValue(url);
                }

                writer.WriteEndArray();
                if (login.Totp is { } totp)
                {
                    writer.WriteStartObject("totp");
                    writer.WriteString("secret", totp.Secret);
                    writer.WriteString("algorithm", Authenticator.TotpTexts.AlgorithmName(totp.Algorithm));
                    writer.WriteNumber("digits", totp.Digits);
                    writer.WriteNumber("period", totp.Period);
                    writer.WriteEndObject();
                }
                else
                {
                    writer.WriteNull("totp");
                }

                writer.WriteString("notes", login.Notes ?? string.Empty);
                writer.WriteBoolean("favorite", login.Favorite);
                writer.WriteString("createdAt", Timestamp(login.CreatedAt));
                writer.WriteString("updatedAt", Timestamp(login.UpdatedAt));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    /// <summary>
    /// How many items have more than one address. A CSV export holds only the first of each: this is
    /// the number a person is told before choosing it.
    /// </summary>
    public static int CountItemsWithSeveralUrls(IReadOnlyList<VaultItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        int count = 0;
        foreach (VaultItem item in items)
        {
            if (item is LoginItem { Urls.Count: > 1 })
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// How many items have an authenticator key. A CSV export does not hold them: this is the number
    /// a person is told before choosing it.
    /// </summary>
    public static int CountItemsWithAuthenticatorKeys(IReadOnlyList<VaultItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items.Count(item => item is LoginItem { Totp: not null });
    }

    /// <summary>
    /// The name an export is suggested to be saved under: <c>BitosX-export-YYYY-MM-DD.csv</c> or
    /// <c>.json</c>, with the date of <paramref name="now"/> as it is given (the caller passes the
    /// local time).
    /// </summary>
    public static string SuggestedFileName(ExportFormat format, DateTimeOffset now)
    {
        string extension = format switch
        {
            ExportFormat.Csv => "csv",
            ExportFormat.BitosXJson => "json",
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

        return string.Create(CultureInfo.InvariantCulture, $"BitosX-export-{now:yyyy-MM-dd}.{extension}");
    }

    private static StringBuilder AppendCsvValue(StringBuilder text, string value)
    {
        text.Append('"');
        if (value.Contains('"'))
        {
            text.Append(value.Replace("\"", "\"\"", StringComparison.Ordinal));
        }
        else
        {
            text.Append(value);
        }

        return text.Append('"');
    }

    private static string Timestamp(DateTimeOffset time)
        => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);
}
