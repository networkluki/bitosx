using BitosX.Core.Domain;
using BitosX.Core.Security;
using BitosX.Core.Services;
using BitosX.Crypto;
using Microsoft.Extensions.Logging.Abstractions;

namespace BitosX.Core.Tests;

/// <summary>
/// Tests for moving an existing vault to stronger key-derivation settings (0.6.0). Small settings
/// are used throughout to keep the tests fast; what is tested is the move, not the numbers.
/// </summary>
public sealed class VaultServiceKeyDerivationTests
{
    private const string MasterPassword = "correct horse battery";

    private static readonly Argon2Parameters Initial = Argon2Parameters.ForTesting;          // 1 MiB, 1 pass
    private static readonly Argon2Parameters Stronger = new(4096, 2, 1, 32);                 // 4 MiB, 2 passes

    /// <summary>Fails the way a device without enough memory does, for settings above a limit.</summary>
    private sealed class MemoryLimitedKeyDerivation(int limitKiB) : IKeyDerivation
    {
        private readonly Argon2idKeyDerivation _inner = new();

        public string Name => _inner.Name;

        public byte[] DeriveKey(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, Argon2Parameters parameters)
            => parameters.MemoryKiB > limitKiB
                ? throw new KeyDerivationResourceException(parameters.MemoryKiB, new InsufficientMemoryException())
                : _inner.DeriveKey(password, salt, parameters);
    }

    private static VaultService CreateService(InstrumentedVaultStorage storage, IKeyDerivation? kdf = null) =>
        new(
            new VaultCryptor(kdf ?? new Argon2idKeyDerivation(), new AesGcmCipher()),
            storage,
            Initial,
            NullLogger<VaultService>.Instance);

    private static LoginItem Login(string name) =>
        new() { Name = name, Username = "user", Password = "P@ssw0rd-Strong-123" };

    private static byte[] Hash(EncryptedVault v) => EncryptedVaultFingerprint.Compute(v);

    private static async Task<(VaultService Service, InstrumentedVaultStorage Storage)> UnlockedWithOneItemAsync(
        IKeyDerivation? kdf = null)
    {
        var storage = new InstrumentedVaultStorage();
        VaultService service = CreateService(storage, kdf);
        await service.CreateVaultAsync(MasterPassword);
        await service.AddLoginAsync(Login("First"));
        return (service, storage);
    }

    [Fact]
    public async Task Strengthen_MovesTheVaultToTheNewSettings_AndItStillOpens()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedWithOneItemAsync();
        Assert.Equal(Initial, service.KdfParameters);

        await service.StrengthenKeyDerivationAsync(MasterPassword, Stronger);

        Assert.Equal(Stronger, service.KdfParameters);
        Assert.Equal(Stronger, storage.Stored!.KdfParameters);
        Assert.Equal(VaultState.Unlocked, service.State);

        VaultService reopen = CreateService(storage);
        await reopen.UnlockAsync(MasterPassword);
        Assert.Equal("First", Assert.Single(reopen.Items).Name);
        Assert.Equal(Stronger, reopen.KdfParameters);
    }

    [Fact]
    public async Task Strengthen_ReWrapsTheKey_AndLeavesTheContentAlone()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedWithOneItemAsync();
        EncryptedVault before = storage.Stored!;

        await service.StrengthenKeyDerivationAsync(MasterPassword, Stronger);
        EncryptedVault after = storage.Stored!;

        Assert.NotEqual(before.Salt, after.Salt);
        Assert.NotEqual(before.KeyWrapNonce, after.KeyWrapNonce);
        Assert.NotEqual(before.EncryptedVaultKey, after.EncryptedVaultKey);
        Assert.Equal(before.ContentNonce, after.ContentNonce);   // not re-encrypted
        Assert.Equal(before.Ciphertext, after.Ciphertext);
        Assert.Equal(before.Version, after.Version);             // format unchanged
    }

    [Fact]
    public async Task Strengthen_LaterSavesKeepTheNewSettings()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedWithOneItemAsync();
        await service.StrengthenKeyDerivationAsync(MasterPassword, Stronger);

        await service.AddLoginAsync(Login("Second"));
        await service.RotateVaultKeyAsync(MasterPassword);
        await service.ChangeMasterPasswordAsync(MasterPassword, "a different master password");

        Assert.Equal(Stronger, storage.Stored!.KdfParameters);
        VaultService reopen = CreateService(storage);
        await reopen.UnlockAsync("a different master password");
        Assert.Equal(2, reopen.Items.Count);
    }

    [Fact]
    public async Task Strengthen_CanBeRepeated_EachTimeToSomethingStronger()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedWithOneItemAsync();

        await service.StrengthenKeyDerivationAsync(MasterPassword, new Argon2Parameters(2048, 1, 1, 32));
        await service.StrengthenKeyDerivationAsync(MasterPassword, new Argon2Parameters(2048, 2, 1, 32));
        await service.StrengthenKeyDerivationAsync(MasterPassword, new Argon2Parameters(4096, 2, 4, 32));

        Assert.Equal(new Argon2Parameters(4096, 2, 4, 32), storage.Stored!.KdfParameters);
        VaultService reopen = CreateService(storage);
        await reopen.UnlockAsync(MasterPassword);
        Assert.Single(reopen.Items);
    }

    [Theory]
    [InlineData(4096, 2)]   // the same
    [InlineData(2048, 2)]   // less memory
    [InlineData(4096, 1)]   // fewer passes
    [InlineData(2048, 8)]   // less memory, more passes
    [InlineData(8192, 1)]   // more memory, fewer passes
    public async Task Strengthen_ToSettingsThatAreNotStronger_IsRefused(int memoryKiB, int iterations)
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedWithOneItemAsync();
        await service.StrengthenKeyDerivationAsync(MasterPassword, Stronger); // 4 MiB, 2 passes
        byte[] before = Hash(storage.Stored!);
        int writes = storage.WriteCount;

        await Assert.ThrowsAsync<WeakerKeyDerivationException>(
            () => service.StrengthenKeyDerivationAsync(MasterPassword, new Argon2Parameters(memoryKiB, iterations, 1, 32)));

        Assert.Equal(writes, storage.WriteCount);
        Assert.Equal(before, Hash(storage.Stored!));
        Assert.Equal(Stronger, service.KdfParameters);
    }

    [Theory]
    [InlineData(2_000_000_000, 3, 4)]  // beyond the memory ceiling
    [InlineData(65536, 1000, 4)]       // beyond the pass ceiling
    [InlineData(65536, 3, 1000)]       // beyond the lane ceiling
    [InlineData(4, 3, 1)]              // below the floor
    public async Task Strengthen_ToSettingsOutsideTheFormatsBounds_IsRefused(int memoryKiB, int iterations, int lanes)
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedWithOneItemAsync();
        byte[] before = Hash(storage.Stored!);

        await Assert.ThrowsAsync<UnsupportedVaultFormatException>(
            () => service.StrengthenKeyDerivationAsync(MasterPassword, new Argon2Parameters(memoryKiB, iterations, lanes, 32)));

        Assert.Equal(before, Hash(storage.Stored!));
    }

    [Fact]
    public async Task Strengthen_WrongPassword_IsRejected_AndNothingIsWritten()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedWithOneItemAsync();
        EncryptedVault before = storage.Stored!;

        await Assert.ThrowsAsync<VaultAuthenticationException>(
            () => service.StrengthenKeyDerivationAsync("not the password", Stronger));

        Assert.Same(before, storage.Stored);
        Assert.Equal(Initial, service.KdfParameters);
        Assert.Equal(VaultState.Unlocked, service.State);
    }

    [Fact]
    public async Task Strengthen_WhileLocked_Throws()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedWithOneItemAsync();
        service.Lock();
        EncryptedVault before = storage.Stored!;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StrengthenKeyDerivationAsync(MasterPassword, Stronger));

        Assert.Same(before, storage.Stored);
    }

    [Fact]
    public async Task Strengthen_OnAStaleSession_FailsClosed()
    {
        (VaultService stale, InstrumentedVaultStorage storage) = await UnlockedWithOneItemAsync();
        VaultService other = CreateService(storage);
        await other.UnlockAsync(MasterPassword);
        await other.AddLoginAsync(Login("Added elsewhere"));
        byte[] before = Hash(storage.Stored!);

        await Assert.ThrowsAsync<VaultChangedExternallyException>(
            () => stale.StrengthenKeyDerivationAsync(MasterPassword, Stronger));

        Assert.Equal(before, Hash(storage.Stored!));
        Assert.Equal(Initial, stale.KdfParameters);
    }

    [Fact]
    public async Task Strengthen_WhenTheDeviceLacksTheMemory_Fails_AndTheVaultIsUntouched()
    {
        var kdf = new MemoryLimitedKeyDerivation(limitKiB: 2048);
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedWithOneItemAsync(kdf);
        EncryptedVault before = storage.Stored!;

        // The new settings are tried out before anything is written, so a vault can never be moved
        // to settings the device it is on cannot open.
        KeyDerivationResourceException failure = await Assert.ThrowsAsync<KeyDerivationResourceException>(
            () => service.StrengthenKeyDerivationAsync(MasterPassword, Stronger));

        Assert.Equal(Stronger.MemoryKiB, failure.MemoryKiB);
        Assert.Same(before, storage.Stored);
        Assert.Equal(Initial, service.KdfParameters);
        Assert.Equal(VaultState.Unlocked, service.State);
        await service.AddLoginAsync(Login("Second")); // and the session is fully usable
    }

    [Fact]
    public async Task Unlock_WhenTheDeviceLacksTheMemory_SaysSo_RatherThanWrongPassword()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedWithOneItemAsync();
        await service.StrengthenKeyDerivationAsync(MasterPassword, Stronger);

        // The same vault, restored on a smaller device.
        VaultService smaller = CreateService(storage, new MemoryLimitedKeyDerivation(limitKiB: 2048));
        await Assert.ThrowsAsync<KeyDerivationResourceException>(() => smaller.UnlockAsync(MasterPassword));

        Assert.NotEqual(VaultState.Unlocked, smaller.State);
        Assert.False(smaller.HoldsKeyMaterial);
    }

    // ---- The same guarantees as the other operations that replace the vault ----

    [Fact]
    public async Task Strengthen_CommitWrite_IsIssuedWithNonCancellableToken()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedWithOneItemAsync();

        using var cts = new CancellationTokenSource();
        await service.StrengthenKeyDerivationAsync(MasterPassword, Stronger, cts.Token);

        Assert.False(storage.LastWriteToken.CanBeCanceled);
    }

    [Fact]
    public async Task Strengthen_CancelledBeforeCommit_WritesNothing()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedWithOneItemAsync();
        byte[] before = Hash(storage.Stored!);
        int writes = storage.WriteCount;

        using var cts = new CancellationTokenSource();
        storage.CancelOnRead = cts;
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.StrengthenKeyDerivationAsync(MasterPassword, Stronger, cts.Token));

        Assert.Equal(writes, storage.WriteCount);
        Assert.Equal(before, Hash(storage.Stored!));
        Assert.Equal(Initial, service.KdfParameters);
    }

    [Fact]
    public async Task Strengthen_WriteThrowsBeforeReplacement_KeepsTheOldSettings()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedWithOneItemAsync();
        byte[] before = Hash(storage.Stored!);

        storage.FailNextWrite = true;
        await Assert.ThrowsAsync<IOException>(() => service.StrengthenKeyDerivationAsync(MasterPassword, Stronger));

        Assert.Equal(before, Hash(storage.Stored!));
        Assert.Equal(Initial, service.KdfParameters);
        await service.AddLoginAsync(Login("Second"));
        VaultService reopen = CreateService(storage);
        await reopen.UnlockAsync(MasterPassword);
        Assert.Equal(2, reopen.Items.Count);
    }

    [Fact]
    public async Task Strengthen_WriteReplacesThenThrows_AdoptsTheNewSettings()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedWithOneItemAsync();

        storage.ReplaceThenThrowNextWrite = true;
        await service.StrengthenKeyDerivationAsync(MasterPassword, Stronger); // on disk, so a success

        Assert.Equal(Stronger, service.KdfParameters);
        await service.AddLoginAsync(Login("Second")); // must keep the new header
        Assert.Equal(Stronger, storage.Stored!.KdfParameters);
        VaultService reopen = CreateService(storage);
        await reopen.UnlockAsync(MasterPassword);
        Assert.Equal(2, reopen.Items.Count);
    }

    [Fact]
    public async Task Strengthen_WriteLeavesIndeterminateDisk_LocksSession_FailsClosed()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedWithOneItemAsync();
        var otherStorage = new InstrumentedVaultStorage();
        await CreateService(otherStorage).CreateVaultAsync("some-other-password-xyz");
        storage.StoreThenThrowNextWrite = otherStorage.Stored;

        await Assert.ThrowsAsync<VaultInconsistentStateException>(
            () => service.StrengthenKeyDerivationAsync(MasterPassword, Stronger));

        Assert.Equal(VaultState.Locked, service.State);
        Assert.False(service.HoldsKeyMaterial);
    }

    [Fact]
    public async Task Lock_DuringStrengthening_LeavesTheServiceLocked_AndTheVaultOpens()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedWithOneItemAsync();

        storage.OnWrite = service.Lock;
        await service.StrengthenKeyDerivationAsync(MasterPassword, Stronger);
        storage.OnWrite = null;

        Assert.Equal(VaultState.Locked, service.State);
        Assert.False(service.HoldsKeyMaterial);
        Assert.Null(service.KdfParameters);

        VaultService reopen = CreateService(storage);
        await reopen.UnlockAsync(MasterPassword);
        Assert.Equal(Stronger, reopen.KdfParameters);
        Assert.Single(reopen.Items);
    }

    // ---- Profiles and measuring ----

    [Fact]
    public void Profiles_AreOrderedWeakestFirst_AndWithinTheFormatsBounds()
    {
        IReadOnlyList<KeyDerivationProfile> all = KeyDerivationProfile.All;

        Assert.Equal(["Standard", "Strong", "Maximum"], all.Select(p => p.Name));
        Assert.Equal([64, 256, 1024], all.Select(p => p.MemoryMiB));
        Assert.All(all, profile => profile.Parameters.Validate());
        for (int i = 1; i < all.Count; i++)
        {
            Assert.True(all[i].Parameters.IsStrongerThan(all[i - 1].Parameters));
        }

        Assert.Same(Argon2Parameters.Default, KeyDerivationProfile.Standard.Parameters);
    }

    [Fact]
    public void Profiles_OfferedForAVault_AreOnlyTheStrongerOnes()
    {
        Assert.Equal(
            ["Strong", "Maximum"],
            KeyDerivationProfile.StrongerThan(Argon2Parameters.Default).Select(p => p.Name));
        Assert.Equal(
            ["Maximum"],
            KeyDerivationProfile.StrongerThan(Argon2Parameters.Strong).Select(p => p.Name));
        Assert.Empty(KeyDerivationProfile.StrongerThan(Argon2Parameters.Maximum));
        Assert.Equal(3, KeyDerivationProfile.StrongerThan(Argon2Parameters.ForTesting).Count);

        // More passes than any profile: moving to a profile would lower them, so none is offered.
        Assert.Empty(KeyDerivationProfile.StrongerThan(new Argon2Parameters(65536, 8, 4, 32)));
    }

    [Fact]
    public async Task Benchmark_MeasuresARealDerivation()
    {
        var benchmark = new KeyDerivationBenchmark(new Argon2idKeyDerivation());

        TimeSpan elapsed = await benchmark.MeasureAsync(Initial);

        Assert.True(elapsed > TimeSpan.Zero);
    }

    [Fact]
    public async Task Benchmark_ReportsADeviceWithoutEnoughMemory()
    {
        var benchmark = new KeyDerivationBenchmark(new MemoryLimitedKeyDerivation(limitKiB: 2048));

        await Assert.ThrowsAsync<KeyDerivationResourceException>(() => benchmark.MeasureAsync(Stronger));
        Assert.True(await benchmark.MeasureAsync(Initial) > TimeSpan.Zero);
    }

    [Fact]
    public async Task Benchmark_RefusesSettingsOutsideTheFormatsBounds()
    {
        var benchmark = new KeyDerivationBenchmark(new Argon2idKeyDerivation());

        await Assert.ThrowsAsync<UnsupportedVaultFormatException>(
            () => benchmark.MeasureAsync(new Argon2Parameters(2_000_000_000, 3, 4, 32)));
    }

    [Fact]
    public async Task Benchmark_HonorsCancellation()
    {
        var benchmark = new KeyDerivationBenchmark(new Argon2idKeyDerivation());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => benchmark.MeasureAsync(Initial, cts.Token));
    }
}
