using System.Security.Cryptography;

namespace BitosX.Crypto;

/// <summary>
/// Helpers for handling short-lived sensitive buffers.
/// </summary>
/// <remarks>
/// IMPORTANT LIMITATION: On the managed .NET runtime, zeroing a buffer here reduces,
/// but does not eliminate, secret exposure. The garbage collector may relocate and
/// copy heap objects (including <see cref="byte"/> arrays and, especially,
/// <see cref="string"/> instances), leaving copies that cannot be located or wiped.
/// This helper is a best-effort defence-in-depth measure for byte buffers only; it is
/// not a guarantee that secrets are removed from process memory. See docs/SECURITY_MODEL.md.
/// </remarks>
public static class CryptoBuffers
{
    /// <summary>Overwrites the contents of <paramref name="buffer"/> with zeros.</summary>
    public static void Wipe(Span<byte> buffer) => CryptographicOperations.ZeroMemory(buffer);

    /// <summary>Overwrites the contents of <paramref name="buffer"/> with zeros if it is non-null.</summary>
    public static void Wipe(byte[]? buffer)
    {
        if (buffer is not null)
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }
}
