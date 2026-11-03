using BitosX.Core.Domain;
using BitosX.Core.Security;
using BitosX.Core.Services;
using BitosX.Crypto;
using Microsoft.Extensions.Logging.Abstractions;

namespace BitosX.Core.Tests;

/// <summary>
/// The key-derivation settings a new vault is created with (0.7.0): the settings for new vaults,
/// or — on a device that cannot spare the memory they ask for — the fallback, so that a vault can
/// always be opened on the device it was created on. Small settings stand in for the real ones.
/// </summary>
public sealed class VaultServiceCreationSettingsTests
{
    private const string MasterPassword = "correct horse battery";

    private static readonly Argon2Parameters ForNewVaults = new(4096, 2, 1, 32);
    private static readonly Argon2Parameters Fallback = new(1024, 2, 1, 32);

    /// <summary>Fails the way a device without enough memory does, for settings above a limit.</summary>
    private sealed class MemoryLimitedKeyDerivation(int limitKiB) : IKeyDerivation
    {
        private readonly Argon2idKeyDerivation _inner = new();

        public string Name => _inner.Name;

        public int Attempts { get; private set; }

        public byte[] DeriveKey(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, Argon2Parameters parameters)
        {
            Attempts++;
            return parameters.MemoryKiB > limitKiB
                ? throw new KeyDerivationResourceException(parameters.MemoryKiB, new InsufficientMemoryException())
                : _inner.DeriveKey(password, salt, parameters);
        }
    }

    private static VaultService NewService(
        InstrumentedVaultStorage storage, IKeyDerivation derivation, Argon2Parameters? fallback)
        => new(
            new VaultCryptor(derivation, new AesGcmCipher()),
            storage,
            ForNewVaults,
            fallback,
            NullLogger<VaultService>.Instance);

    /// <summary>Records which kind of thread the key derivation ran on.</summary>
    private sealed class ThreadRecordingKeyDerivation : IKeyDerivation
    {
        private readonly Argon2idKeyDerivation _inner = new();

        public string Name => _inner.Name;

        public List<bool> RanOnThreadPool { get; } = [];

        public byte[] DeriveKey(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, Argon2Parameters parameters)
        {
            lock (RanOnThreadPool)
            {
                RanOnThreadPool.Add(Thread.CurrentThread.IsThreadPoolThread);
            }

            return _inner.DeriveKey(password, salt, parameters);
        }
    }

    [Fact]
    public async Task CreatingAndUnlocking_DeriveTheKeyOffTheCallersThread()
    {
        // The caller is the UI thread in the application, which must keep drawing meanwhile. A thread
        // of its own stands in for it here: it is not a pool thread, so whatever runs on it shows.
        var storage = new InstrumentedVaultStorage();
        var derivation = new ThreadRecordingKeyDerivation();
        using VaultService service = NewService(storage, derivation, Fallback);

        Task? created = null;
        var caller = new Thread(() => created = service.CreateVaultAsync(MasterPassword));
        caller.Start();
        caller.Join();
        await created!;

        service.Lock();
        Task? unlocked = null;
        caller = new Thread(() => unlocked = service.UnlockAsync(MasterPassword));
        caller.Start();
        caller.Join();
        await unlocked!;

        Assert.NotEmpty(derivation.RanOnThreadPool);
        Assert.All(derivation.RanOnThreadPool, Assert.True);
    }

    [Fact]
    public async Task ADeviceWithTheMemory_GetsTheSettingsForNewVaults()
    {
        var storage = new InstrumentedVaultStorage();
        using VaultService service = NewService(storage, new Argon2idKeyDerivation(), Fallback);

        await service.CreateVaultAsync(MasterPassword);

        Assert.Equal(ForNewVaults, service.KdfParameters);
        Assert.Equal(ForNewVaults, storage.Stored!.KdfParameters);
    }

    [Fact]
    public async Task ADeviceWithoutTheMemory_GetsTheFallback_AndCanOpenWhatItCreated()
    {
        var storage = new InstrumentedVaultStorage();
        var device = new MemoryLimitedKeyDerivation(limitKiB: 2048);
        using VaultService service = NewService(storage, device, Fallback);

        await service.CreateVaultAsync(MasterPassword);
        await service.AddLoginAsync(new LoginItem { Name = "First" });

        Assert.Equal(VaultState.Unlocked, service.State);
        Assert.Equal(Fallback, service.KdfParameters);
        Assert.Equal(Fallback, storage.Stored!.KdfParameters);

        service.Lock();
        await service.UnlockAsync(MasterPassword);
        Assert.Single(service.Items);
    }

    [Fact]
    public async Task TheVaultThatGotTheFallback_IsOfferedTheStrongerSettings()
    {
        var storage = new InstrumentedVaultStorage();
        using VaultService service = NewService(storage, new MemoryLimitedKeyDerivation(limitKiB: 2048), Fallback);
        await service.CreateVaultAsync(MasterPassword);

        // What Settings compares, to say that the vault can be strengthened.
        Assert.False(service.KdfParameters!.IsAtLeastAsStrongAs(ForNewVaults));
        Assert.True(ForNewVaults.IsStrongerThan(service.KdfParameters!));
    }

    [Fact]
    public async Task WithoutAFallback_CreatingFails_AndNothingIsWritten()
    {
        var storage = new InstrumentedVaultStorage();
        using VaultService service = NewService(storage, new MemoryLimitedKeyDerivation(limitKiB: 2048), fallback: null);

        await Assert.ThrowsAsync<KeyDerivationResourceException>(() => service.CreateVaultAsync(MasterPassword));

        Assert.Equal(0, storage.WriteCount);
        Assert.Null(storage.Stored);
        Assert.NotEqual(VaultState.Unlocked, service.State);
    }

    [Fact]
    public async Task ADeviceThatCannotAffordTheFallbackEither_IsToldSo_AndNothingIsWritten()
    {
        var storage = new InstrumentedVaultStorage();
        using VaultService service = NewService(storage, new MemoryLimitedKeyDerivation(limitKiB: 512), Fallback);

        KeyDerivationResourceException refused =
            await Assert.ThrowsAsync<KeyDerivationResourceException>(() => service.CreateVaultAsync(MasterPassword));

        Assert.Contains("1 MiB", refused.Message, StringComparison.Ordinal); // the memory that was still too much
        Assert.Equal(0, storage.WriteCount);
        Assert.Null(storage.Stored);
    }

    [Theory]
    [InlineData(4096, 2)] // the same
    [InlineData(8192, 2)] // stronger
    [InlineData(8192, 1)] // more memory, fewer passes: not comparable
    public async Task AFallbackThatIsNotWeaker_IsNoFallback(int memoryKiB, int iterations)
    {
        var storage = new InstrumentedVaultStorage();
        var device = new MemoryLimitedKeyDerivation(limitKiB: 2048);
        using VaultService service = NewService(storage, device, new Argon2Parameters(memoryKiB, iterations, 1, 32));

        await Assert.ThrowsAsync<KeyDerivationResourceException>(() => service.CreateVaultAsync(MasterPassword));

        Assert.Equal(1, device.Attempts); // it was not even tried
        Assert.Null(storage.Stored);
    }

    [Fact]
    public async Task AnotherKindOfFailure_IsNotAnsweredWithTheFallback()
    {
        var storage = new InstrumentedVaultStorage { FailNextWrite = true };
        using VaultService service = NewService(storage, new Argon2idKeyDerivation(), Fallback);

        await Assert.ThrowsAnyAsync<IOException>(() => service.CreateVaultAsync(MasterPassword));

        Assert.Null(storage.Stored);
    }

    [Fact]
    public void WhatNewVaultsGet_IsStrongerThanWhatTheyFallBackTo_AndWithinTheFormat()
    {
        Argon2Parameters forNewVaults = KeyDerivationProfile.ForNewVaults.Parameters;
        Argon2Parameters fallback = KeyDerivationProfile.Standard.Parameters;

        forNewVaults.Validate();
        Assert.True(forNewVaults.IsStrongerThan(fallback));
        Assert.Equal(256 * 1024, forNewVaults.MemoryKiB);
        Assert.Equal(64 * 1024, fallback.MemoryKiB);
        Assert.Contains(KeyDerivationProfile.ForNewVaults, KeyDerivationProfile.All);
    }
}
