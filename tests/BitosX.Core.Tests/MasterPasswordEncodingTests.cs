using System.Text;
using BitosX.Core.Security;
using BitosX.Crypto;

namespace BitosX.Core.Tests;

/// <summary>
/// Tests for how a typed master password becomes key-derivation input (0.6.0). Non-ASCII text is
/// written with escapes throughout, so the form under test does not depend on how this file is
/// saved.
/// </summary>
public sealed class MasterPasswordEncodingTests
{
    private const string Composed = "café-räksmörgås";                 // NFC
    private const string Decomposed = "café-räksmörgås";         // NFD, same text

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes);

    [Fact]
    public void TheSamePassword_TypedComposedOrDecomposed_HasOneEncoding()
    {
        Assert.NotEqual(Composed, Decomposed); // different code points...
        Assert.Equal(Hex(MasterPasswordEncoding.Encode(Composed)), Hex(MasterPasswordEncoding.Encode(Decomposed)));
    }

    [Fact]
    public void Encode_IsUtf8OfTheComposedForm()
        => Assert.Equal(Hex(Encoding.UTF8.GetBytes(Composed)), Hex(MasterPasswordEncoding.Encode(Decomposed)));

    [Fact]
    public void AsciiPassword_IsEncodedExactlyAsBefore()
    {
        const string password = "correct horse battery staple! #42 ~`'\"\\";

        Assert.Equal(password, MasterPasswordEncoding.Canonicalize(password));
        Assert.Equal(Hex(Encoding.UTF8.GetBytes(password)), Hex(MasterPasswordEncoding.Encode(password)));
    }

    [Theory]
    [InlineData(' ')] // no-break space
    [InlineData(' ')] // ogham space mark
    [InlineData(' ')] // en quad
    [InlineData(' ')] // em space
    [InlineData(' ')] // thin space
    [InlineData(' ')] // narrow no-break space
    [InlineData(' ')] // medium mathematical space
    [InlineData('　')] // ideographic space
    public void EverySpaceCharacter_BecomesAnOrdinarySpace(char space)
        => Assert.Equal("my pass word", MasterPasswordEncoding.Canonicalize($"my{space}pass{space}word"));

    [Theory]
    [InlineData("  leading and trailing  ")]            // not trimmed
    [InlineData("two  spaces")]                         // not collapsed
    [InlineData("Case Matters")]                        // no case folding
    [InlineData("tab\tand\nnewline")]                   // not space characters; left alone
    [InlineData("ﬁsh")]                            // ligature fi: no compatibility mapping
    [InlineData("ＡＢＣ")]                  // full-width ABC
    [InlineData("x²")]                             // superscript two
    [InlineData("①")]                              // circled digit one
    [InlineData("zero​width")]                     // zero width space is category Cf, not Zs
    [InlineData("\U0001F511\U0001F512")]                // key and lock (surrogate pairs)
    public void NothingElse_IsChanged(string password)
        => Assert.Equal(password, MasterPasswordEncoding.Canonicalize(password));

    [Fact]
    public void DifferentPasswords_StayDifferent()
    {
        string[] passwords =
        [
            "password", "Password", "password ", " password", "pass word", "pass  word",
            "café", "cafe", "ﬁ", "fi", "Ａ", "A", "x²", "x2",
        ];

        Assert.Equal(passwords.Length, passwords.Select(p => Hex(MasterPasswordEncoding.Encode(p))).Distinct().Count());
    }

    [Theory]
    [InlineData("café")]
    [InlineData("café")]
    [InlineData("한")]          // Hangul jamo -> one syllable
    [InlineData("ạ̇")]               // two combining marks, canonical order
    [InlineData("ạ̇")]               // the same two, other order
    [InlineData("my pass")]
    public void Canonicalize_IsIdempotent(string password)
    {
        string once = MasterPasswordEncoding.Canonicalize(password);
        Assert.Equal(once, MasterPasswordEncoding.Canonicalize(once));
    }

    [Fact]
    public void CombiningMarks_InEitherOrder_AreTheSamePassword()
        => Assert.Equal(
            Hex(MasterPasswordEncoding.Encode("ạ̇")),
            Hex(MasterPasswordEncoding.Encode("ạ̇")));

    // ---- Text that is not valid Unicode ----

    [Theory]
    [InlineData(0xD800, 0)]        // lone high surrogate
    [InlineData(0xDC00, 0)]        // lone low surrogate
    [InlineData(0xDC00, 0xD800)]   // a pair the wrong way round
    public void InvalidUnicode_IsRejected_ForANewPassword(int first, int second)
    {
        // Built here: text that is not valid Unicode does not survive being test-case data.
        string password = "pass" + (char)first + (second == 0 ? string.Empty : ((char)second).ToString()) + "word";

        Assert.Throws<ArgumentException>(() => MasterPasswordEncoding.Canonicalize(password));
        Assert.Throws<ArgumentException>(() => MasterPasswordEncoding.Encode(password));
    }

    [Fact]
    public void InvalidUnicode_StillYieldsTheEncodingEarlierVersionsUsed()
    {
        const string password = "pass\uD800word";

        PasswordCandidates candidates = MasterPasswordEncoding.EncodeCandidates(password);

        Assert.False(candidates.FirstIsCanonical);
        byte[] only = Assert.Single(candidates.Encodings);
        Assert.Equal(Hex(Encoding.UTF8.GetBytes(password)), Hex(only));
    }

    // ---- Candidates for opening an existing vault ----

    [Fact]
    public void Candidates_ForAnAsciiPassword_AreJustTheOne()
    {
        PasswordCandidates candidates = MasterPasswordEncoding.EncodeCandidates("correct horse battery");

        Assert.True(candidates.FirstIsCanonical);
        Assert.Equal(Hex("correct horse battery"u8.ToArray()), Hex(Assert.Single(candidates.Encodings)));
    }

    [Theory]
    [InlineData(Composed)]
    [InlineData(Decomposed)]
    public void Candidates_CoverBothForms_CanonicalFirst(string typed)
    {
        PasswordCandidates candidates = MasterPasswordEncoding.EncodeCandidates(typed);

        Assert.True(candidates.FirstIsCanonical);
        Assert.Equal(
            [Hex(Encoding.UTF8.GetBytes(Composed)), Hex(Encoding.UTF8.GetBytes(Decomposed))],
            candidates.Encodings.Select(Hex));
    }

    [Fact]
    public void Candidates_IncludeThePasswordExactlyAsTyped()
    {
        const string typed = "my pass word"; // how a vault from before 0.6.0 may be keyed

        PasswordCandidates candidates = MasterPasswordEncoding.EncodeCandidates(typed);

        Assert.Equal(
            [Hex("my pass word"u8.ToArray()), Hex(Encoding.UTF8.GetBytes(typed))],
            candidates.Encodings.Select(Hex));
    }

    [Fact]
    public void Candidates_AreNeverOfferedTwice()
    {
        string[] samples = ["plain", Composed, Decomposed, "my café", "my café", "ﬁ "];

        foreach (string sample in samples)
        {
            PasswordCandidates candidates = MasterPasswordEncoding.EncodeCandidates(sample);
            Assert.Equal(candidates.Encodings.Count, candidates.Encodings.Select(Hex).Distinct().Count());
            Assert.InRange(candidates.Encodings.Count, 1, 4);
        }
    }

    [Fact]
    public void Wipe_ZeroesEveryEncoding()
    {
        PasswordCandidates candidates = MasterPasswordEncoding.EncodeCandidates("my café");
        Assert.True(candidates.Encodings.Count > 1);

        candidates.Wipe();

        Assert.All(candidates.Encodings, encoding => Assert.All(encoding, b => Assert.Equal(0, b)));
    }

    // ---- Trying the candidates ----

    [Fact]
    public void FirstAccepted_StopsAtTheFirstEncodingThatWorks()
    {
        PasswordCandidates candidates = MasterPasswordEncoding.EncodeCandidates(Composed);
        int attempts = 0;

        (string result, bool usedCanonical) = candidates.FirstAccepted(_ =>
        {
            attempts++;
            return "opened";
        });

        Assert.Equal("opened", result);
        Assert.True(usedCanonical);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void FirstAccepted_MovesOn_WhenAnEncodingIsRejected()
    {
        PasswordCandidates candidates = MasterPasswordEncoding.EncodeCandidates(Composed);
        string legacy = Hex(Encoding.UTF8.GetBytes(Decomposed));

        (string result, bool usedCanonical) = candidates.FirstAccepted(encoding =>
            Hex(encoding) == legacy ? "opened" : throw new VaultAuthenticationException());

        Assert.Equal("opened", result);
        Assert.False(usedCanonical);
    }

    [Fact]
    public void FirstAccepted_WhenNoEncodingWorks_FailsLikeAWrongPassword()
    {
        PasswordCandidates candidates = MasterPasswordEncoding.EncodeCandidates(Composed);
        int attempts = 0;

        Assert.Throws<VaultAuthenticationException>(() => candidates.FirstAccepted<string>(_ =>
        {
            attempts++;
            throw new VaultAuthenticationException();
        }));

        Assert.Equal(candidates.Encodings.Count, attempts);
    }

    [Fact]
    public void FirstAccepted_StopsAtOnce_OnAnyOtherFailure()
    {
        PasswordCandidates candidates = MasterPasswordEncoding.EncodeCandidates(Composed);
        int attempts = 0;

        Assert.Throws<UnsupportedVaultFormatException>(() => candidates.FirstAccepted<string>(_ =>
        {
            attempts++;
            throw new UnsupportedVaultFormatException("not a vault");
        }));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public void FirstAccepted_WithoutACanonicalEncoding_NeverClaimsOne()
    {
        PasswordCandidates candidates = MasterPasswordEncoding.EncodeCandidates("pass\uD800word");

        (_, bool usedCanonical) = candidates.FirstAccepted(_ => "opened");

        Assert.False(usedCanonical);
    }
}
