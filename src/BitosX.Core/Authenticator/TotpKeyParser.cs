using System.Text;
using BitosX.Core.Domain;
using BitosX.Core.ImportExport;
using BitosX.Crypto;

namespace BitosX.Core.Authenticator;

/// <summary>
/// Reads an authenticator key from text: what is typed into the editor, and what an import file
/// holds. The rules are docs/TOTP.md §3, the same in both clients; the shared fixtures
/// (<c>tests/fixtures/totp/keys.json</c>) hold this class to them.
/// </summary>
public static class TotpKeyParser
{
    /// <summary>The most UTF-16 code units a text may have (§3.1).</summary>
    public const int MaxTextLength = 2048;

    /// <summary>The fewest bytes a secret may decode to.</summary>
    public const int MinSecretBytes = 10;

    /// <summary>The most bytes a secret may decode to.</summary>
    public const int MaxSecretBytes = 128;

    /// <summary>The longest time step, in seconds.</summary>
    public const int MaxPeriod = 300;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Reads <paramref name="text"/>. The result is a key, a refusal, or — for a text that is
    /// empty once trimmed, or <see langword="null"/> — neither. Nothing about the text is logged
    /// or put in an exception.
    /// </summary>
    public static TotpParseResult Parse(string? text)
    {
        if (text is null)
        {
            return TotpParseResult.Empty;
        }

        if (text.Length > MaxTextLength)
        {
            return TotpParseResult.Refused(TotpRefusal.TooLong);
        }

        string trimmed = ImportText.Trim(text);
        if (trimmed.Length == 0)
        {
            return TotpParseResult.Empty;
        }

        if (StartsWithAsciiIgnoreCase(trimmed, "otpauth://"))
        {
            return ParseLink(trimmed);
        }

        if (StartsWithAsciiIgnoreCase(trimmed, "steam://"))
        {
            return TotpParseResult.Refused(TotpRefusal.SteamNotSupported);
        }

        return ParseSecret(trimmed, out string? secret) is { } refusal
            ? TotpParseResult.Refused(refusal)
            : TotpParseResult.Parsed(new TotpKey { Secret = secret!, Algorithm = TotpAlgorithm.Sha1, Digits = 6, Period = 30 });
    }

    /// <summary>§3.2: an <c>otpauth://</c> link, already trimmed.</summary>
    private static TotpParseResult ParseLink(string link)
    {
        string rest = link["otpauth://".Length..];

        // The type runs to the next '/'.
        int slash = rest.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            return TotpParseResult.Refused(TotpRefusal.InvalidLink);
        }

        string type = rest[..slash];
        if (EqualsAsciiIgnoreCase(type, "hotp"))
        {
            return TotpParseResult.Refused(TotpRefusal.HotpNotSupported);
        }

        if (!EqualsAsciiIgnoreCase(type, "totp"))
        {
            return TotpParseResult.Refused(TotpRefusal.InvalidLink);
        }

        // After the type: the label (not read), then the query from the first '?'; a fragment
        // from the first '#' is cut off first.
        string afterType = rest[(slash + 1)..];
        int hash = afterType.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            afterType = afterType[..hash];
        }

        int question = afterType.IndexOf('?', StringComparison.Ordinal);
        if (question < 0)
        {
            return TotpParseResult.Refused(TotpRefusal.InvalidLink);
        }

        string query = afterType[(question + 1)..];
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string part in query.Split('&'))
        {
            if (part.Length == 0)
            {
                continue;
            }

            int equals = part.IndexOf('=', StringComparison.Ordinal);
            if (equals < 0)
            {
                return TotpParseResult.Refused(TotpRefusal.InvalidLink);
            }

            string name = ImportText.ToLowerAscii(part[..equals]);
            string? value = PercentDecode(part[(equals + 1)..]);
            if (value is null || !parameters.TryAdd(name, value))
            {
                return TotpParseResult.Refused(TotpRefusal.InvalidLink);
            }
        }

        if (!parameters.TryGetValue("secret", out string? secretText) || secretText.Length == 0)
        {
            return TotpParseResult.Refused(TotpRefusal.MissingSecret);
        }

        if (ParseSecret(secretText, out string? secret) is { } secretRefusal)
        {
            return TotpParseResult.Refused(secretRefusal);
        }

        TotpAlgorithm algorithm = TotpAlgorithm.Sha1;
        if (parameters.TryGetValue("algorithm", out string? algorithmText))
        {
            if (EqualsAsciiIgnoreCase(algorithmText, "SHA1"))
            {
                algorithm = TotpAlgorithm.Sha1;
            }
            else if (EqualsAsciiIgnoreCase(algorithmText, "SHA256"))
            {
                algorithm = TotpAlgorithm.Sha256;
            }
            else if (EqualsAsciiIgnoreCase(algorithmText, "SHA512"))
            {
                algorithm = TotpAlgorithm.Sha512;
            }
            else
            {
                return TotpParseResult.Refused(TotpRefusal.UnsupportedAlgorithm);
            }
        }

        int digits = 6;
        if (parameters.TryGetValue("digits", out string? digitsText))
        {
            if (digitsText is not ("6" or "7" or "8"))
            {
                return TotpParseResult.Refused(TotpRefusal.UnsupportedDigits);
            }

            digits = digitsText[0] - '0';
        }

        int period = 30;
        if (parameters.TryGetValue("period", out string? periodText))
        {
            if (ParsePeriod(periodText) is not { } parsedPeriod)
            {
                return TotpParseResult.Refused(TotpRefusal.UnsupportedPeriod);
            }

            period = parsedPeriod;
        }

        return TotpParseResult.Parsed(new TotpKey { Secret = secret!, Algorithm = algorithm, Digits = digits, Period = period });
    }

    /// <summary>
    /// §3.3: a base32 secret. Returns the refusal, or <see langword="null"/> with the canonical
    /// secret in <paramref name="canonical"/>.
    /// </summary>
    private static TotpRefusal? ParseSecret(string text, out string? canonical)
    {
        canonical = null;

        // Spaces and dashes anywhere, then '=' at the end.
        var cleaned = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c is not (' ' or '-'))
            {
                cleaned.Append(c);
            }
        }

        int length = cleaned.Length;
        while (length > 0 && cleaned[length - 1] == '=')
        {
            length--;
        }

        cleaned.Length = length;
        string letters = cleaned.ToString();
        foreach (char c in letters)
        {
            if (Base32.ValueOf(c) < 0)
            {
                return TotpRefusal.InvalidSecret;
            }
        }

        int byteCount = Base32.DecodedLength(letters.Length);
        if (byteCount < MinSecretBytes)
        {
            return TotpRefusal.SecretTooShort;
        }

        if (byteCount > MaxSecretBytes)
        {
            return TotpRefusal.SecretTooLong;
        }

        byte[] bytes = Base32.Decode(letters);
        try
        {
            canonical = Base32.Encode(bytes);
        }
        finally
        {
            CryptoBuffers.Wipe(bytes);
        }

        return null;
    }

    /// <summary>A period: ASCII digits, no leading zero, 1 to 300; otherwise <see langword="null"/>.</summary>
    private static int? ParsePeriod(string text)
    {
        if (text.Length is 0 or > 3 || text[0] == '0')
        {
            return null;
        }

        int value = 0;
        foreach (char c in text)
        {
            if (c is < '0' or > '9')
            {
                return null;
            }

            value = (value * 10) + (c - '0');
        }

        return value <= MaxPeriod ? value : null;
    }

    /// <summary>
    /// Percent-decodes a value as UTF-8: every <c>%XX</c> (two hexadecimal digits, either case) is a
    /// byte; a run of them has to be valid UTF-8. Anything else after <c>%</c>, or bytes that are not
    /// UTF-8, gives <see langword="null"/>. A <c>+</c> stays a <c>+</c>.
    /// </summary>
    private static string? PercentDecode(string value)
    {
        if (!value.Contains('%', StringComparison.Ordinal))
        {
            return value;
        }

        var result = new StringBuilder(value.Length);
        var run = new List<byte>();
        int i = 0;
        while (i < value.Length)
        {
            if (value[i] != '%')
            {
                if (!FlushRun(run, result))
                {
                    return null;
                }

                result.Append(value[i]);
                i++;
                continue;
            }

            if (i + 2 >= value.Length)
            {
                return null;
            }

            int high = HexValue(value[i + 1]);
            int low = HexValue(value[i + 2]);
            if (high < 0 || low < 0)
            {
                return null;
            }

            run.Add((byte)((high << 4) | low));
            i += 3;
        }

        return FlushRun(run, result) ? result.ToString() : null;
    }

    private static bool FlushRun(List<byte> run, StringBuilder result)
    {
        if (run.Count == 0)
        {
            return true;
        }

        byte[] bytes = [.. run];
        run.Clear();
        try
        {
            result.Append(StrictUtf8.GetString(bytes));
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
        finally
        {
            CryptoBuffers.Wipe(bytes);
        }
    }

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'A' and <= 'F' => c - 'A' + 10,
        >= 'a' and <= 'f' => c - 'a' + 10,
        _ => -1,
    };

    private static bool StartsWithAsciiIgnoreCase(string text, string prefix)
        => text.Length >= prefix.Length && EqualsAsciiIgnoreCase(text[..prefix.Length], prefix);

    private static bool EqualsAsciiIgnoreCase(string a, string b)
        => string.Equals(ImportText.ToLowerAscii(a), ImportText.ToLowerAscii(b), StringComparison.Ordinal);
}

/// <summary>
/// What <see cref="TotpKeyParser.Parse"/> made of a text: a key, a refusal, or nothing (an empty
/// text). A class, not a record, so that nothing prints the key by accident.
/// </summary>
public sealed class TotpParseResult
{
    internal static readonly TotpParseResult Empty = new(null, null);

    private TotpParseResult(TotpKey? key, TotpRefusal? refusal)
    {
        Key = key;
        Refusal = refusal;
    }

    /// <summary>The key, if the text is one.</summary>
    public TotpKey? Key { get; }

    /// <summary>Why the text is not a key, if it is not.</summary>
    public TotpRefusal? Refusal { get; }

    /// <summary>Whether the text was empty once trimmed: no key, and no error.</summary>
    public bool IsEmpty => Key is null && Refusal is null;

    internal static TotpParseResult Parsed(TotpKey key) => new(key, null);

    internal static TotpParseResult Refused(TotpRefusal refusal) => new(null, refusal);
}
