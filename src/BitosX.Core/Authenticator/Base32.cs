using BitosX.Crypto;

namespace BitosX.Core.Authenticator;

/// <summary>
/// RFC 4648 base32 as authenticator keys use it (docs/TOTP.md §3.3): the alphabet <c>A-Z2-7</c>,
/// five bits per character, most significant first, no padding. Reading also takes lower case;
/// bits left over at the end are dropped without a check.
/// </summary>
internal static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>The value of a base32 character in either case, or -1 if it is not one.</summary>
    public static int ValueOf(char c) => c switch
    {
        >= 'A' and <= 'Z' => c - 'A',
        >= 'a' and <= 'z' => c - 'a',
        >= '2' and <= '7' => c - '2' + 26,
        _ => -1,
    };

    /// <summary>The number of bytes <paramref name="characters"/> base32 characters decode to: floor(5n/8).</summary>
    public static int DecodedLength(int characters) => (int)(characters * 5L / 8);

    /// <summary>
    /// Decodes <paramref name="text"/>, every character of which has to be a base32 character
    /// (<see cref="ValueOf"/> ≥ 0), into <paramref name="destination"/>, which has to be
    /// <see cref="DecodedLength"/> bytes long.
    /// </summary>
    public static void Decode(ReadOnlySpan<char> text, Span<byte> destination)
    {
        if (destination.Length != DecodedLength(text.Length))
        {
            throw new ArgumentException("The destination has the wrong length.", nameof(destination));
        }

        int buffer = 0;
        int bits = 0;
        int written = 0;
        foreach (char c in text)
        {
            int value = ValueOf(c);
            if (value < 0)
            {
                throw new ArgumentException("The text is not base32.", nameof(text));
            }

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                destination[written++] = (byte)(buffer >> bits);
                buffer &= (1 << bits) - 1;
            }
        }

        // Whatever is left in the buffer are the bits that are dropped without a check.
    }

    /// <summary>Decodes base32 text into a new array, which the caller wipes once it is done with it.</summary>
    public static byte[] Decode(ReadOnlySpan<char> text)
    {
        byte[] bytes = new byte[DecodedLength(text.Length)];
        try
        {
            Decode(text, bytes);
            return bytes;
        }
        catch
        {
            CryptoBuffers.Wipe(bytes);
            throw;
        }
    }

    /// <summary>The canonical encoding of <paramref name="bytes"/>: upper case, no padding.</summary>
    public static string Encode(ReadOnlySpan<byte> bytes)
    {
        int length = (int)((bytes.Length * 8L + 4) / 5);
        char[] chars = new char[length];
        int buffer = 0;
        int bits = 0;
        int written = 0;
        foreach (byte b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                chars[written++] = Alphabet[(buffer >> bits) & 31];
            }

            buffer &= (1 << bits) - 1;
        }

        if (bits > 0)
        {
            chars[written++] = Alphabet[(buffer << (5 - bits)) & 31];
        }

        return new string(chars);
    }

    /// <summary>
    /// Whether <paramref name="text"/> is canonical base32 of 10 to 128 bytes: only <c>A-Z2-7</c>,
    /// and exactly the encoding of the bytes it decodes to (its length is one an encoding has, and
    /// the bits left over in its last character are zero).
    /// </summary>
    public static bool IsCanonicalSecret(string text, int minimumBytes, int maximumBytes)
    {
        foreach (char c in text)
        {
            if (c is not ((>= 'A' and <= 'Z') or (>= '2' and <= '7')))
            {
                return false;
            }
        }

        int byteCount = DecodedLength(text.Length);
        if (byteCount < minimumBytes || byteCount > maximumBytes)
        {
            return false;
        }

        // The canonical encoding of n bytes is ceil(8n/5) characters long ...
        if ((byteCount * 8L + 4) / 5 != text.Length)
        {
            return false;
        }

        // ... and its last character carries (8n mod 5) bits of data, the rest zero.
        int dataBits = (int)(byteCount * 8L % 5);
        if (dataBits == 0)
        {
            return true;
        }

        int last = ValueOf(text[^1]);
        int unusedMask = (1 << (5 - dataBits)) - 1;
        return (last & unusedMask) == 0;
    }
}
