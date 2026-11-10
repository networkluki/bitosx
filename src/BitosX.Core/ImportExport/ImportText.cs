using System.Text;

namespace BitosX.Core.ImportExport;

/// <summary>
/// The text operations of the import specification, written out rather than left to the runtime:
/// <c>string.Trim</c> and <c>ToLowerInvariant</c> do not do exactly what their JavaScript
/// counterparts do, and the web vault has to arrive at the same items.
/// </summary>
internal static class ImportText
{
    /// <summary>
    /// Whether <paramref name="c"/> is one of the characters "trimmed" removes from both ends of a
    /// value: U+0009 to U+000D, U+0020, U+0085, U+00A0, U+1680, U+2000 to U+200A, U+2028, U+2029,
    /// U+202F, U+205F, U+3000 and U+FEFF. That is what .NET and JavaScript trim, taken together.
    /// </summary>
    public static bool IsTrimmed(char c) => c switch
    {
        >= '\u0009' and <= '\u000D' => true,
        '\u0020' or '\u0085' or '\u00A0' or '\u1680' => true,
        >= '\u2000' and <= '\u200A' => true,
        '\u2028' or '\u2029' or '\u202F' or '\u205F' or '\u3000' or '\uFEFF' => true,
        _ => false,
    };

    /// <summary><paramref name="value"/> without the characters of <see cref="IsTrimmed"/> at either end.</summary>
    public static string Trim(string value)
    {
        int start = 0;
        int end = value.Length;
        while (start < end && IsTrimmed(value[start]))
        {
            start++;
        }

        while (end > start && IsTrimmed(value[end - 1]))
        {
            end--;
        }

        return start == 0 && end == value.Length ? value : value[start..end];
    }

    /// <summary>Whether nothing is left of <paramref name="value"/> once it is trimmed.</summary>
    public static bool IsBlank(string value)
    {
        foreach (char c in value)
        {
            if (!IsTrimmed(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary><paramref name="value"/> with A to Z as a to z, and every other character as it is.</summary>
    public static string ToLowerAscii(string value)
    {
        int first = -1;
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] is >= 'A' and <= 'Z')
            {
                first = i;
                break;
            }
        }

        if (first < 0)
        {
            return value;
        }

        return string.Create(value.Length, value, static (span, source) =>
        {
            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];
                span[i] = c is >= 'A' and <= 'Z' ? (char)(c + ('a' - 'A')) : c;
            }
        });
    }

    /// <summary>
    /// The lines of <paramref name="value"/>: it is split at every CR LF, at every LF, and at every
    /// CR that no LF follows. A value without a line end is one line.
    /// </summary>
    public static List<string> SplitLines(string value)
    {
        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c != '\r' && c != '\n')
            {
                continue;
            }

            lines.Add(value[start..i]);
            if (c == '\r' && i + 1 < value.Length && value[i + 1] == '\n')
            {
                i++;
            }

            start = i + 1;
        }

        lines.Add(value[start..]);
        return lines;
    }

    /// <summary>Whether <paramref name="value"/> holds a control character: U+0000 to U+001F, or U+007F.</summary>
    public static bool HasControlCharacter(string value)
    {
        foreach (char c in value)
        {
            if (c < '\u0020' || c == '\u007F')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Decodes the bytes of an import file: UTF-8, with or without a byte order mark, and nothing
    /// else. A UTF-16 or UTF-32 byte order mark is refused as such; a byte sequence that is not
    /// UTF-8 is refused rather than replaced.
    /// </summary>
    /// <param name="file">The file.</param>
    /// <param name="utf8">The same bytes without a UTF-8 byte order mark.</param>
    public static string Decode(ReadOnlyMemory<byte> file, out ReadOnlyMemory<byte> utf8)
    {
        ReadOnlySpan<byte> bytes = file.Span;

        // UTF-32 before UTF-16: the little-endian marks start with the same two bytes.
        if (bytes.StartsWith<byte>([0x00, 0x00, 0xFE, 0xFF])
            || bytes.StartsWith<byte>([0xFF, 0xFE, 0x00, 0x00])
            || bytes.StartsWith<byte>([0xFE, 0xFF])
            || bytes.StartsWith<byte>([0xFF, 0xFE]))
        {
            throw new ImportRefusedException(ImportRefusal.UnsupportedEncoding);
        }

        utf8 = bytes.StartsWith<byte>([0xEF, 0xBB, 0xBF]) ? file[3..] : file;

        try
        {
            return StrictUtf8.GetString(utf8.Span);
        }
        catch (DecoderFallbackException)
        {
            throw new ImportRefusedException(ImportRefusal.InvalidUtf8);
        }
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
}
