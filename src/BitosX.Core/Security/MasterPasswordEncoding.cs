using System.Globalization;
using System.Text;
using BitosX.Crypto;

namespace BitosX.Core.Security;

/// <summary>
/// Turns a master password, as typed, into the bytes the key is derived from.
/// </summary>
/// <remarks>
/// <para>
/// The same password can reach the application as different sequences of code points: "é" is one
/// code point from one keyboard and "e" followed by a combining accent from another, and what looks
/// like a space may be a no-break space. Fed to the key derivation as typed, these are different
/// passwords, and the vault does not open. The <b>canonical encoding</b> removes that difference,
/// following the rules RFC 8265 gives for passwords (the <c>OpaqueString</c> profile):
/// </para>
/// <list type="number">
/// <item>every space character (Unicode category Zs) becomes U+0020;</item>
/// <item>the result is normalized to Unicode Normalization Form C;</item>
/// <item>and encoded as UTF-8.</item>
/// </list>
/// <para>
/// Nothing else is changed: no trimming, no case folding, and no compatibility mapping, all of which
/// would make different passwords equal and so reduce what a password is worth.
/// </para>
/// <para>
/// Vaults created before 0.6.0 were keyed with the password exactly as it was typed. They keep
/// opening: <see cref="EncodeCandidates"/> offers the canonical encoding first and then the
/// encodings an earlier version could have used. The vault file has no field saying which one
/// applies — adding one would change the format — so the candidates are simply tried in turn.
/// </para>
/// <para>
/// A password made of ASCII characters only has a single encoding, and none of this changes anything
/// for it.
/// </para>
/// </remarks>
public static class MasterPasswordEncoding
{
    /// <summary>
    /// The canonical form of <paramref name="password"/>. Two passwords that a person would call the
    /// same have the same canonical form.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The password is not valid Unicode (it contains half of a surrogate pair). Such text cannot be
    /// normalized, and cannot be encoded as UTF-8 without replacing the offending character.
    /// </exception>
    public static string Canonicalize(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        if (!TryCanonicalize(password, out string canonical))
        {
            throw new ArgumentException(
                "The master password contains characters that are not valid text.", nameof(password));
        }

        return canonical;
    }

    /// <summary>The canonical encoding of <paramref name="password"/>. The caller owns and must wipe it.</summary>
    /// <exception cref="ArgumentException">The password is not valid Unicode.</exception>
    public static byte[] Encode(string password) => Encoding.UTF8.GetBytes(Canonicalize(password));

    /// <summary>
    /// The encodings to try when opening an existing vault, most likely first: the canonical one,
    /// then those a version before 0.6.0 could have used — the password exactly as typed, and its
    /// composed and decomposed forms. Encodings that come out identical are offered once, so an
    /// ASCII password yields a single candidate. The caller owns and must wipe every buffer.
    /// </summary>
    public static PasswordCandidates EncodeCandidates(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        var encodings = new List<byte[]>(4);
        bool hasCanonical = TryCanonicalize(password, out string canonical);
        if (hasCanonical)
        {
            encodings.Add(Encoding.UTF8.GetBytes(canonical));
        }

        // Exactly what earlier versions did, including for text that is not valid Unicode.
        AddIfNew(encodings, Encoding.UTF8.GetBytes(password));

        if (hasCanonical)
        {
            AddIfNew(encodings, Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormC)));
            AddIfNew(encodings, Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormD)));
        }

        return new PasswordCandidates(encodings, hasCanonical);
    }

    private static bool TryCanonicalize(string password, out string canonical)
    {
        try
        {
            canonical = MapSpaces(password).Normalize(NormalizationForm.FormC);
            return true;
        }
        catch (ArgumentException)
        {
            // Normalize rejects text that is not valid Unicode.
            canonical = string.Empty;
            return false;
        }
    }

    private static string MapSpaces(string text)
    {
        // Every character in category Zs is a single UTF-16 unit, so this cannot split a pair.
        int first = -1;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != ' ' && char.GetUnicodeCategory(text[i]) == UnicodeCategory.SpaceSeparator)
            {
                first = i;
                break;
            }
        }

        if (first < 0)
        {
            return text;
        }

        return string.Create(text.Length, text, static (span, source) =>
        {
            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];
                span[i] = char.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator ? ' ' : c;
            }
        });
    }

    private static void AddIfNew(List<byte[]> encodings, byte[] candidate)
    {
        foreach (byte[] existing in encodings)
        {
            if (existing.AsSpan().SequenceEqual(candidate))
            {
                CryptoBuffers.Wipe(candidate);
                return;
            }
        }

        encodings.Add(candidate);
    }
}

/// <summary>
/// The encodings of one typed password to try against an existing vault, in order.
/// </summary>
/// <param name="Encodings">Never empty. Sensitive: the caller owns and must wipe every buffer.</param>
/// <param name="FirstIsCanonical">
/// Whether the first encoding is the canonical one. It is not when the password is not valid
/// Unicode, in which case only the encoding earlier versions used is offered.
/// </param>
public sealed record PasswordCandidates(IReadOnlyList<byte[]> Encodings, bool FirstIsCanonical)
{
    /// <summary>
    /// Runs <paramref name="attempt"/> with each encoding in turn until one is accepted, and returns
    /// its result together with whether that was the canonical encoding. An encoding is rejected by
    /// <paramref name="attempt"/> throwing <see cref="VaultAuthenticationException"/>; if the last one
    /// is rejected too, that exception is what the caller sees — exactly as for a wrong password.
    /// Any other exception ends the search at once.
    /// </summary>
    public (T Result, bool UsedCanonical) FirstAccepted<T>(Func<byte[], T> attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        for (int i = 0; ; i++)
        {
            try
            {
                return (attempt(Encodings[i]), i == 0 && FirstIsCanonical);
            }
            catch (VaultAuthenticationException) when (i < Encodings.Count - 1)
            {
                // Not this encoding; the vault may have been keyed with an earlier one.
            }
        }
    }

    /// <summary>Overwrites every encoding with zeros.</summary>
    public void Wipe()
    {
        foreach (byte[] encoding in Encodings)
        {
            CryptoBuffers.Wipe(encoding);
        }
    }
}
