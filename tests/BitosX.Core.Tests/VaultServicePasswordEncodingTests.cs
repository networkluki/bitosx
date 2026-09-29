using System.Text;
using BitosX.Core.Domain;
using BitosX.Core.Serialization;
using BitosX.Core.Services;
using BitosX.Crypto;
using Microsoft.Extensions.Logging.Abstractions;

namespace BitosX.Core.Tests;

/// <summary>
/// Tests that a vault opens for the same master password however it is typed (0.6.0), and that
/// vaults keyed by earlier versions keep opening. Non-ASCII text is written with escapes so the form
/// under test does not depend on how this file is saved.
/// </summary>
public sealed class VaultServicePasswordEncodingTests
{
    private const string Composed = "lösenord-räksmörgås";        // as typed on Windows
    private const string Decomposed = "lösenord-räksmörgås";  // the same text, decomposed
    private const string WithNoBreakSpaces = "mitt hemliga lösenord";
    private const string WithOrdinarySpaces = "mitt hemliga lösenord";
    private const string NewPassword = "ett helt annat lösenord";

    /// <summary>Counts key derivations, to show what a failed attempt costs.</summary>
    private sealed class CountingKeyDerivation : IKeyDerivation
    {
        private readonly Argon2idKeyDerivation _inner = new();

        public int Count { get; private set; }

        public string Name => _inner.Name;

        public byte[] DeriveKey(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, Argon2Parameters parameters)
        {
            Count++;
            return _inner.DeriveKey(password, salt, parameters);
        }
    }

    private static VaultService CreateService(InstrumentedVaultStorage storage, IKeyDerivation? kdf = null) =>
        new(
            new VaultCryptor(kdf ?? new Argon2idKeyDerivation(), new AesGcmCipher()),
            storage,
            Argon2Parameters.ForTesting,
            NullLogger<VaultService>.Instance);

    /// <summary>A vault as a version before 0.6.0 wrote it: keyed with the password exactly as typed.</summary>
    private static InstrumentedVaultStorage LegacyVault(string passwordAsTyped)
    {
        var vault = new Vault();
        vault.Add(new LoginItem { Name = "Example", Username = "user", Password = "P@ssw0rd-Strong-123" });

        var storage = new InstrumentedVaultStorage();
        storage.ReplaceExternally(VaultCryptor.CreateDefault().Create(
            Encoding.UTF8.GetBytes(passwordAsTyped),
            VaultSerializer.SerializeToUtf8Bytes(vault),
            Argon2Parameters.ForTesting).Encrypted);
        return storage;
    }

    private static async Task<bool> OpensAsync(InstrumentedVaultStorage storage, string password)
    {
        VaultService service = CreateService(storage);
        try
        {
            await service.UnlockAsync(password);
            return service.State == VaultState.Unlocked;
        }
        catch (VaultAuthenticationException)
        {
            return false;
        }
    }

    // ---- New vaults ----

    [Theory]
    [InlineData(Composed, Decomposed)]
    [InlineData(Decomposed, Composed)]
    [InlineData(WithNoBreakSpaces, WithOrdinarySpaces)]
    [InlineData(WithOrdinarySpaces, WithNoBreakSpaces)]
    public async Task NewVault_Opens_HoweverTheSamePasswordIsTyped(string typedAtCreation, string typedLater)
    {
        var storage = new InstrumentedVaultStorage();
        VaultService service = CreateService(storage);
        await service.CreateVaultAsync(typedAtCreation);

        // Before 0.6.0 this failed: the two are different code points and gave different keys.
        Assert.True(await OpensAsync(storage, typedLater));
        Assert.True(await OpensAsync(storage, typedAtCreation));
    }

    [Fact]
    public async Task NewVault_UsesTheCanonicalEncoding()
    {
        var storage = new InstrumentedVaultStorage();
        VaultService service = CreateService(storage);
        await service.CreateVaultAsync(Decomposed);

        Assert.False(service.UsesLegacyPasswordEncoding);

        // Opened by the primitives alone, with the UTF-8 of the composed form.
        UnlockResult unlocked = VaultCryptor.CreateDefault().Unlock(storage.Stored!, Encoding.UTF8.GetBytes(Composed));
        Assert.NotEmpty(unlocked.Plaintext);
    }

    [Fact]
    public async Task AsciiPassword_IsUnaffected()
    {
        InstrumentedVaultStorage storage = LegacyVault("correct horse battery");
        VaultService service = CreateService(storage);

        await service.UnlockAsync("correct horse battery");

        // An ASCII password has one encoding, so a vault from any version is already canonical.
        Assert.False(service.UsesLegacyPasswordEncoding);
    }

    [Theory]
    [InlineData("Different-password-1")]
    [InlineData("lösenord-räksmörgåS")]   // one letter in another case
    [InlineData("losenord-raksmorgas")]                       // without the accents
    public async Task WrongPassword_IsStillRejected(string wrong)
    {
        var storage = new InstrumentedVaultStorage();
        await CreateService(storage).CreateVaultAsync(Composed);
        VaultService service = CreateService(storage);

        await Assert.ThrowsAsync<VaultAuthenticationException>(() => service.UnlockAsync(wrong));

        Assert.NotEqual(VaultState.Unlocked, service.State);
        Assert.False(service.HoldsKeyMaterial);
    }

    [Fact]
    public async Task WrongAsciiPassword_CostsOneKeyDerivation()
    {
        var storage = new InstrumentedVaultStorage();
        await CreateService(storage).CreateVaultAsync("correct horse battery");
        var kdf = new CountingKeyDerivation();
        VaultService service = CreateService(storage, kdf);

        await Assert.ThrowsAsync<VaultAuthenticationException>(() => service.UnlockAsync("wrong horse battery"));

        Assert.Equal(1, kdf.Count);
    }

    [Fact]
    public async Task WrongNonAsciiPassword_CostsOneKeyDerivationPerEncoding_AndNoMore()
    {
        var storage = new InstrumentedVaultStorage();
        await CreateService(storage).CreateVaultAsync(Composed);
        var kdf = new CountingKeyDerivation();
        VaultService service = CreateService(storage, kdf);

        await Assert.ThrowsAsync<VaultAuthenticationException>(() => service.UnlockAsync("fel-lösenord-här"));

        Assert.Equal(2, kdf.Count); // composed and decomposed
    }

    [Fact]
    public async Task RightPassword_OnACanonicalVault_CostsOneKeyDerivation()
    {
        var storage = new InstrumentedVaultStorage();
        await CreateService(storage).CreateVaultAsync(Composed);
        var kdf = new CountingKeyDerivation();
        VaultService service = CreateService(storage, kdf);

        await service.UnlockAsync(Decomposed);

        Assert.Equal(1, kdf.Count);
    }

    [Theory]
    [InlineData(0xD800)]
    [InlineData(0xDC00)]
    public async Task NewPassword_ThatIsNotValidText_IsRejected(int loneSurrogate)
    {
        // Built here: text that is not valid Unicode does not survive being test-case data.
        string password = "password-" + (char)loneSurrogate + "-long-enough";
        var storage = new InstrumentedVaultStorage();
        VaultService service = CreateService(storage);

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateVaultAsync(password));

        Assert.Null(storage.Stored);
    }

    [Fact]
    public async Task MinimumLength_IsCountedInTheCanonicalForm()
    {
        var storage = new InstrumentedVaultStorage();
        VaultService service = CreateService(storage);

        // Four letters, each typed as a letter and a combining accent: eight code units, four characters.
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateVaultAsync("éééé"));

        Assert.Null(storage.Stored);
    }

    // ---- Vaults from before 0.6.0 ----

    [Theory]
    [InlineData(Composed, Composed)]
    [InlineData(Decomposed, Decomposed)]
    [InlineData(Decomposed, Composed)]      // keyed decomposed, now typed composed
    [InlineData(Composed, Decomposed)]      // keyed composed, now typed decomposed
    [InlineData(WithNoBreakSpaces, WithNoBreakSpaces)]
    public async Task LegacyVault_KeepsOpening(string keyedWith, string typedNow)
    {
        InstrumentedVaultStorage storage = LegacyVault(keyedWith);
        EncryptedVault before = storage.Stored!;
        VaultService service = CreateService(storage);

        await service.UnlockAsync(typedNow);

        Assert.Equal(VaultState.Unlocked, service.State);
        Assert.Single(service.Items);
        Assert.Same(before, storage.Stored); // opening never rewrites the vault
    }

    [Theory]
    [InlineData(Composed, false)]            // the composed form is the canonical one
    [InlineData(Decomposed, true)]
    [InlineData(WithNoBreakSpaces, true)]
    public async Task LegacyVault_ReportsWhetherItsEncodingIsCanonical(string keyedWith, bool legacy)
    {
        VaultService service = CreateService(LegacyVault(keyedWith));

        await service.UnlockAsync(keyedWith);

        Assert.Equal(legacy, service.UsesLegacyPasswordEncoding);
    }

    [Fact]
    public async Task LegacyVault_KeyedWithNoBreakSpaces_DoesNotOpenForOrdinarySpaces()
    {
        // Which space was typed cannot be guessed from an ordinary one. This is the limit of what
        // trying encodings can recover, and the reason such a vault is worth moving to the canonical
        // encoding.
        Assert.False(await OpensAsync(LegacyVault(WithNoBreakSpaces), WithOrdinarySpaces));
    }

    [Fact]
    public async Task LegacyEncoding_IsForgottenWhenTheVaultLocks()
    {
        VaultService service = CreateService(LegacyVault(Decomposed));
        await service.UnlockAsync(Decomposed);
        Assert.True(service.UsesLegacyPasswordEncoding);

        service.Lock();

        Assert.False(service.UsesLegacyPasswordEncoding);
    }

    [Fact]
    public async Task LegacyVault_CanBeSavedTo_WithoutChangingItsEncoding()
    {
        InstrumentedVaultStorage storage = LegacyVault(WithNoBreakSpaces);
        VaultService service = CreateService(storage);
        await service.UnlockAsync(WithNoBreakSpaces);

        await service.AddLoginAsync(new LoginItem { Name = "Second", Password = "P@ssw0rd-Strong-456" });

        Assert.True(service.UsesLegacyPasswordEncoding);
        Assert.True(await OpensAsync(storage, WithNoBreakSpaces));
    }

    // ---- Moving a legacy vault to the canonical encoding ----

    public enum Operation
    {
        ChangeMasterPassword,
        RotateVaultKey,
        StrengthenKeyDerivation,
    }

    [Theory]
    [InlineData(Operation.ChangeMasterPassword)]
    [InlineData(Operation.RotateVaultKey)]
    [InlineData(Operation.StrengthenKeyDerivation)]
    public async Task LegacyVault_MovesToTheCanonicalEncoding(Operation operation)
    {
        InstrumentedVaultStorage storage = LegacyVault(WithNoBreakSpaces);
        VaultService service = CreateService(storage);
        await service.UnlockAsync(WithNoBreakSpaces);
        string password = WithNoBreakSpaces;

        switch (operation)
        {
            case Operation.ChangeMasterPassword:
                password = "nytt lösenord här";
                await service.ChangeMasterPasswordAsync(WithNoBreakSpaces, password);
                break;
            case Operation.RotateVaultKey:
                await service.RotateVaultKeyAsync(WithNoBreakSpaces);
                break;
            case Operation.StrengthenKeyDerivation:
                await service.StrengthenKeyDerivationAsync(WithNoBreakSpaces, new Argon2Parameters(2048, 1, 1, 32));
                break;
        }

        Assert.False(service.UsesLegacyPasswordEncoding);

        // From now on the vault opens however the password is typed.
        string canonical = Security.MasterPasswordEncoding.Canonicalize(password);
        Assert.True(await OpensAsync(storage, password));
        Assert.True(await OpensAsync(storage, canonical));
        Assert.True(await OpensAsync(storage, canonical.Normalize(NormalizationForm.FormD)));

        // The session is consistent with what was written.
        await service.AddLoginAsync(new LoginItem { Name = "Second", Password = "P@ssw0rd-Strong-456" });
        VaultService reopen = CreateService(storage);
        await reopen.UnlockAsync(canonical);
        Assert.Equal(2, reopen.Items.Count);
        Assert.False(reopen.UsesLegacyPasswordEncoding);
    }

    [Theory]
    [InlineData(Operation.ChangeMasterPassword)]
    [InlineData(Operation.RotateVaultKey)]
    [InlineData(Operation.StrengthenKeyDerivation)]
    public async Task LegacyVault_WrongCurrentPassword_IsRejected_AndNothingIsWritten(Operation operation)
    {
        InstrumentedVaultStorage storage = LegacyVault(Decomposed);
        VaultService service = CreateService(storage);
        await service.UnlockAsync(Decomposed);
        EncryptedVault before = storage.Stored!;
        const string wrong = "inte-rätt-lösenord";

        Task attempt = operation switch
        {
            Operation.ChangeMasterPassword => service.ChangeMasterPasswordAsync(wrong, NewPassword),
            Operation.RotateVaultKey => service.RotateVaultKeyAsync(wrong),
            _ => service.StrengthenKeyDerivationAsync(wrong, new Argon2Parameters(2048, 1, 1, 32)),
        };

        await Assert.ThrowsAsync<VaultAuthenticationException>(() => attempt);

        Assert.Same(before, storage.Stored);
        Assert.True(service.UsesLegacyPasswordEncoding);
        Assert.Equal(VaultState.Unlocked, service.State);
    }

    [Theory]
    [InlineData(Composed, Decomposed)]
    [InlineData(Decomposed, Composed)]
    [InlineData(WithNoBreakSpaces, WithOrdinarySpaces)]
    public async Task ChangeMasterPassword_ToTheSamePasswordTypedDifferently_IsRefused(string current, string same)
    {
        var storage = new InstrumentedVaultStorage();
        VaultService service = CreateService(storage);
        await service.CreateVaultAsync(current);
        EncryptedVault before = storage.Stored!;

        await Assert.ThrowsAsync<ArgumentException>(() => service.ChangeMasterPasswordAsync(current, same));

        Assert.Same(before, storage.Stored);
    }

    [Fact]
    public async Task ChangeMasterPassword_NewPasswordOpens_HoweverItIsTyped()
    {
        var storage = new InstrumentedVaultStorage();
        VaultService service = CreateService(storage);
        await service.CreateVaultAsync("correct horse battery");

        await service.ChangeMasterPasswordAsync("correct horse battery", Decomposed);

        Assert.True(await OpensAsync(storage, Composed));
        Assert.True(await OpensAsync(storage, Decomposed));
        Assert.False(await OpensAsync(storage, "correct horse battery"));
    }
}
