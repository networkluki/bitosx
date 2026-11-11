using System.Buffers;

namespace BitosX.Core.ImportExport;

/// <summary>What an address value of an import file is taken to be.</summary>
internal enum AddressKind
{
    /// <summary>An http or https address, kept as it is.</summary>
    Web = 0,

    /// <summary>Something that looks like a host and is given <c>https://</c> in front.</summary>
    HostWithoutScheme = 1,

    /// <summary>Anything else: not an address BitosX can store.</summary>
    Other = 2,
}

/// <summary>
/// The two rules by which an address value is told apart, as exact patterns. They are written out by
/// hand rather than as regular expressions so that their cost is plainly linear, and so that the web
/// vault can copy them; ImportAddressesTests holds them against the patterns of the specification.
/// </summary>
internal static class ImportAddresses
{
    private const string HttpsPrefix = "https://";

    private static readonly SearchValues<char> HexDigits = SearchValues.Create("0123456789abcdefABCDEF");
    private static readonly SearchValues<char> IPv6Characters = SearchValues.Create("0123456789abcdefABCDEF:.");

    /// <summary>
    /// Tells what <paramref name="value"/> is. It has been trimmed, is not empty, and holds no line
    /// end.
    /// <list type="number">
    /// <item>A value with a control character (U+0000 to U+001F, U+007F) is <see cref="AddressKind.Other"/>.</item>
    /// <item>A value that starts with a scheme and <c>://</c>
    /// (<c>^[A-Za-z][A-Za-z0-9+.-]*://</c>) is <see cref="AddressKind.Web"/> if the scheme is
    /// <c>http</c> or <c>https</c> in any case, and <see cref="AddressKind.Other"/> if not.</item>
    /// <item>A value that looks like a host (<see cref="LooksLikeHost"/>) is
    /// <see cref="AddressKind.HostWithoutScheme"/>.</item>
    /// <item>Everything else is <see cref="AddressKind.Other"/>.</item>
    /// </list>
    /// </summary>
    public static AddressKind Classify(string value)
    {
        if (ImportText.HasControlCharacter(value))
        {
            return AddressKind.Other;
        }

        int schemeLength = SchemeLength(value);
        if (schemeLength > 0)
        {
            ReadOnlySpan<char> scheme = value.AsSpan(0, schemeLength);
            return scheme.Equals("http", StringComparison.OrdinalIgnoreCase) || scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
                ? AddressKind.Web
                : AddressKind.Other;
        }

        return LooksLikeHost(value) ? AddressKind.HostWithoutScheme : AddressKind.Other;
    }

    /// <summary><paramref name="hostLike"/> as the address it is stored as.</summary>
    public static string WithHttps(string hostLike) => HttpsPrefix + hostLike;

    /// <summary>
    /// The length of the scheme <paramref name="value"/> starts with, if <c>://</c> follows it:
    /// <c>^[A-Za-z][A-Za-z0-9+.-]*://</c>. Zero if it does not start that way.
    /// </summary>
    internal static int SchemeLength(string value)
    {
        if (value.Length == 0 || !IsAsciiLetter(value[0]))
        {
            return 0;
        }

        int i = 1;
        while (i < value.Length && (IsAsciiLetterOrDigit(value[i]) || value[i] is '+' or '.' or '-'))
        {
            i++;
        }

        return value.AsSpan(i).StartsWith("://", StringComparison.Ordinal) ? i : 0;
    }

    /// <summary>
    /// Whether <paramref name="value"/> is a host name or an IPv4 address, optionally followed by a
    /// port, optionally followed by a path, query or fragment:
    /// <code>
    /// ^(?:DOMAIN|IPV4)(?::[0-9]{1,5})?(?:[/?#][\s\S]*)?$
    ///   DOMAIN = (?:LABEL\.)+TLD
    ///   LABEL  = [A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?
    ///   TLD    = [A-Za-z](?:[A-Za-z0-9-]*[A-Za-z0-9])?
    ///   IPV4   = OCTET(?:\.OCTET){3}
    ///   OCTET  = 25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9]
    /// </code>
    /// A name needs a dot, and its last part has to start with a letter: <c>example.com</c> and
    /// <c>192.168.1.1:8080/admin</c> are hosts; <c>localhost</c>, <c>v1.2</c>, <c>user@example.com</c>
    /// and <c>My bank</c> are not.
    /// </summary>
    internal static bool LooksLikeHost(string value)
    {
        int hostEnd = value.AsSpan().IndexOfAny(":/?#");
        if (hostEnd < 0)
        {
            hostEnd = value.Length;
        }

        ReadOnlySpan<char> host = value.AsSpan(0, hostEnd);
        if (!IsDomain(host) && !IsIPv4(host))
        {
            return false;
        }

        int i = hostEnd;
        if (i < value.Length && value[i] == ':')
        {
            int digits = 0;
            i++;
            while (i < value.Length && char.IsAsciiDigit(value[i]))
            {
                i++;
                digits++;
            }

            if (digits is < 1 or > 5)
            {
                return false;
            }
        }

        return i == value.Length || value[i] is '/' or '?' or '#';
    }

    /// <summary>
    /// Whether the part of a web address that names the host is one both clients read the same way.
    /// The vault's own rule for an address is the one each client already had, and the two differ
    /// about unusual hosts (the desktop refuses <c>https://a..b</c>, which the web vault accepts,
    /// and accepts <c>https://example.123</c>, which the web vault refuses). An import asks this
    /// first, so that an entry is imported by both clients or by neither:
    /// <list type="number">
    /// <item>The authority is what follows <c>://</c>, up to the first <c>/</c>, <c>?</c> or
    /// <c>#</c>. It holds no backslash (a browser ends the host at one, so what follows an
    /// <c>@</c> after it is not the host a browser would go to). It holds at most one <c>@</c>;
    /// what follows the <c>@</c> is host and port.</item>
    /// <item>A host in square brackets holds only hexadecimal digits, colons and dots, and at
    /// least one of them. After the closing bracket comes nothing, or a colon and digits.</item>
    /// <item>Any other host ends at the first colon, after which come only digits. It is not
    /// empty; it holds only ASCII letters and digits, <c>-</c>, <c>.</c>, <c>_</c> and characters
    /// from U+0080 on; it does not start with a dot and has no two dots in a row.</item>
    /// <item>If its last part (not counting one dot at the very end) is all digits, or is
    /// <c>0x</c> and hexadecimal digits, the host is four numbers from 0 to 255 with dots between
    /// them, written without leading zeros.</item>
    /// </list>
    /// </summary>
    internal static bool HasPlausibleHost(string webAddress)
    {
        int start = webAddress.IndexOf("://", StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }

        ReadOnlySpan<char> rest = webAddress.AsSpan(start + 3);
        int end = rest.IndexOfAny("/?#");
        ReadOnlySpan<char> authority = end < 0 ? rest : rest[..end];

        // Also before the "@": "https://u\@example.com/" goes to the host "u" in a browser, and is
        // no address at all to .NET. The host itself may hold none either way (below).
        if (authority.Contains('\\'))
        {
            return false;
        }

        int at = authority.IndexOf('@');
        if (at >= 0)
        {
            authority = authority[(at + 1)..];
            if (authority.Contains('@'))
            {
                return false;
            }
        }

        if (authority.StartsWith("["))
        {
            int close = authority.IndexOf(']');
            if (close < 2 || authority[1..close].IndexOfAnyExcept(IPv6Characters) >= 0)
            {
                return false;
            }

            ReadOnlySpan<char> afterBracket = authority[(close + 1)..];
            return afterBracket.IsEmpty || (afterBracket[0] == ':' && afterBracket[1..].IndexOfAnyExceptInRange('0', '9') < 0);
        }

        ReadOnlySpan<char> host = authority;
        int colon = authority.IndexOf(':');
        if (colon >= 0)
        {
            if (authority[(colon + 1)..].IndexOfAnyExceptInRange('0', '9') >= 0)
            {
                return false;
            }

            host = authority[..colon];
        }

        if (host.IsEmpty || host[0] == '.' || host.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (char c in host)
        {
            if (c < '\u0080' && !IsAsciiLetterOrDigit(c) && c is not ('-' or '.' or '_'))
            {
                return false;
            }
        }

        ReadOnlySpan<char> withoutLastDot = host[^1] == '.' ? host[..^1] : host;
        ReadOnlySpan<char> lastPart = withoutLastDot[(withoutLastDot.LastIndexOf('.') + 1)..];
        return !LooksLikeANumber(lastPart) || IsIPv4(withoutLastDot);
    }

    /// <summary>All digits, or <c>0x</c> and hexadecimal digits: what a browser takes for part of an IPv4 address.</summary>
    private static bool LooksLikeANumber(ReadOnlySpan<char> part)
    {
        if (part.IsEmpty)
        {
            return false;
        }

        if (part.IndexOfAnyExceptInRange('0', '9') < 0)
        {
            return true;
        }

        return part.Length >= 2
            && part[0] == '0'
            && part[1] is 'x' or 'X'
            && part[2..].IndexOfAnyExcept(HexDigits) < 0;
    }

    /// <summary>
    /// The host of a web address, for the name of an entry that has none: what is between
    /// <c>://</c> and the next <c>/</c>, <c>?</c> or <c>#</c>, without anything up to an <c>@</c>
    /// and without a port, with A to Z as a to z. Taken from the text; the address is not parsed, so
    /// that both clients arrive at the same name.
    /// </summary>
    public static string HostOf(string webAddress)
    {
        int start = webAddress.IndexOf("://", StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        ReadOnlySpan<char> rest = webAddress.AsSpan(start + 3);
        int end = rest.IndexOfAny("/?#");
        ReadOnlySpan<char> authority = end < 0 ? rest : rest[..end];

        int at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            authority = authority[(at + 1)..];
        }

        ReadOnlySpan<char> host = authority;
        if (authority.StartsWith("["))
        {
            // An IPv6 address: everything up to its closing bracket.
            int close = authority.IndexOf(']');
            if (close >= 0)
            {
                host = authority[..(close + 1)];
            }
        }
        else
        {
            int colon = authority.LastIndexOf(':');
            if (colon >= 0 && authority[(colon + 1)..].IndexOfAnyExceptInRange('0', '9') < 0)
            {
                host = authority[..colon];
            }
        }

        return ImportText.ToLowerAscii(host.ToString());
    }

    private static bool IsDomain(ReadOnlySpan<char> host)
    {
        int labels = 0;
        while (true)
        {
            int dot = host.IndexOf('.');
            ReadOnlySpan<char> label = dot < 0 ? host : host[..dot];
            if (!IsLabel(label))
            {
                return false;
            }

            labels++;
            if (dot < 0)
            {
                // The last part: it has to start with a letter, and there has to be one before it.
                return labels >= 2 && IsAsciiLetter(label[0]);
            }

            host = host[(dot + 1)..];
        }
    }

    private static bool IsLabel(ReadOnlySpan<char> label)
    {
        if (label.IsEmpty || !IsAsciiLetterOrDigit(label[0]) || !IsAsciiLetterOrDigit(label[^1]))
        {
            return false;
        }

        foreach (char c in label)
        {
            if (!IsAsciiLetterOrDigit(c) && c != '-')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsIPv4(ReadOnlySpan<char> host)
    {
        for (int part = 1; part <= 4; part++)
        {
            int dot = host.IndexOf('.');
            if ((dot < 0) != (part == 4))
            {
                return false;
            }

            if (!IsOctet(dot < 0 ? host : host[..dot]))
            {
                return false;
            }

            host = dot < 0 ? default : host[(dot + 1)..];
        }

        return true;
    }

    /// <summary>0 to 255, in decimal, without a leading zero.</summary>
    private static bool IsOctet(ReadOnlySpan<char> text)
    {
        if (text.Length is < 1 or > 3 || text.IndexOfAnyExceptInRange('0', '9') >= 0)
        {
            return false;
        }

        if (text.Length > 1 && text[0] == '0')
        {
            return false;
        }

        return int.Parse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture) <= 255;
    }

    private static bool IsAsciiLetter(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z');

    private static bool IsAsciiLetterOrDigit(char c) => IsAsciiLetter(c) || c is >= '0' and <= '9';
}
