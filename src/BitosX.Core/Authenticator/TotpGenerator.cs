using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using BitosX.Core.Domain;
using BitosX.Crypto;

namespace BitosX.Core.Authenticator;

/// <summary>
/// Makes the codes of an authenticator key (RFC 6238 with RFC 4226's dynamic truncation; docs/TOTP.md
/// §4). The decoded secret and the HMAC are wiped once a code is made.
/// </summary>
public static class TotpGenerator
{
    /// <summary>
    /// The code at <paramref name="unixSeconds"/> (seconds since 1970-01-01T00:00:00Z, not
    /// negative), and how many seconds it has left.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The time is before 1970.</exception>
    /// <exception cref="ArgumentException">The key is not valid (docs/TOTP.md §2).</exception>
    [SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "RFC 6238 and nearly every site that offers authenticator codes use HMAC-SHA-1; HMAC-SHA-1 is not weakened by SHA-1's collisions.")]
    public static TotpCode Generate(TotpKey key, long unixSeconds)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfNegative(unixSeconds);
        if (!Validation.VaultValidation.IsValidTotpKey(key))
        {
            // Says nothing of the key itself.
            throw new ArgumentException("The authenticator key is not valid.", nameof(key));
        }

        long step = unixSeconds / key.Period;
        int secondsLeft = (int)(key.Period - (unixSeconds % key.Period));

        Span<byte> message = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(message, step);

        Span<byte> mac = stackalloc byte[HMACSHA512.HashSizeInBytes];
        byte[] secret = Base32.Decode(key.Secret);
        try
        {
            int length = key.Algorithm switch
            {
                TotpAlgorithm.Sha1 => HMACSHA1.HashData(secret, message, mac),
                TotpAlgorithm.Sha256 => HMACSHA256.HashData(secret, message, mac),
                TotpAlgorithm.Sha512 => HMACSHA512.HashData(secret, message, mac),
                _ => throw new ArgumentException("The authenticator key is not valid.", nameof(key)),
            };

            // RFC 4226 §5.3: the low four bits of the last byte are the offset of four bytes,
            // read big-endian without their top bit.
            int offset = mac[length - 1] & 0x0F;
            int binary = ((mac[offset] & 0x7F) << 24)
                | (mac[offset + 1] << 16)
                | (mac[offset + 2] << 8)
                | mac[offset + 3];

            int modulus = key.Digits switch { 6 => 1_000_000, 7 => 10_000_000, _ => 100_000_000 };
            string code = (binary % modulus).ToString(CultureInfo.InvariantCulture).PadLeft(key.Digits, '0');
            return new TotpCode(code, secondsLeft, key.Period);
        }
        finally
        {
            CryptoBuffers.Wipe(secret);
            CryptoBuffers.Wipe(mac);
            CryptoBuffers.Wipe(message);
        }
    }

    /// <summary>The code at <paramref name="time"/>, with the seconds counted down from it.</summary>
    public static TotpCode Generate(TotpKey key, DateTimeOffset time)
        => Generate(key, time.ToUnixTimeSeconds());

    /// <summary>
    /// A code as it is shown: in two groups with a space between them, the first floor(n/2) digits
    /// long — <c>123 456</c>, <c>123 4567</c>, <c>1234 5678</c>.
    /// </summary>
    public static string Group(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        int split = code.Length / 2;
        return code.Length < 2 ? code : string.Concat(code.AsSpan(0, split), " ", code.AsSpan(split));
    }
}

/// <summary>
/// A code of an authenticator key at one moment. A class, not a record, so that nothing prints it
/// by accident.
/// </summary>
public sealed class TotpCode
{
    internal TotpCode(string code, int secondsLeft, int period)
    {
        Code = code;
        SecondsLeft = secondsLeft;
        Period = period;
    }

    /// <summary>The digits, without spaces: what is copied.</summary>
    public string Code { get; }

    /// <summary>The digits as they are shown (<see cref="TotpGenerator.Group"/>).</summary>
    public string Display => TotpGenerator.Group(Code);

    /// <summary>Seconds until the next code: 1 to <see cref="Period"/>.</summary>
    public int SecondsLeft { get; }

    /// <summary>The key's time step, in seconds.</summary>
    public int Period { get; }
}
