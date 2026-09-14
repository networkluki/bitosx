using System.Text;
using BitosX.Core.Domain;
using BitosX.Core.Serialization;
using BitosX.Core.Services;
using BitosX.Crypto;
using Microsoft.Extensions.Logging.Abstractions;

namespace BitosX.Core.Tests;

/// <summary>
/// Release-blocking invariants for vault key rotation (0.5.0):
/// (1) rotation refuses to run if the on-disk vault no longer matches the unlocked session — detecting
///     both a different cryptographic identity and a same-key external content change; and
/// (2) a well-defined cancellation boundary that can never leave disk = rotated but memory = old key.
/// Every pre-commit failure asserts the on-disk envelope is byte-for-byte unchanged (SHA-256 equality).
/// </summary>
public sealed class VaultServiceRotationConsistencyTests
{
    private const string MasterPassword = "correct horse battery";

    private static VaultService CreateService(InstrumentedVaultStorage storage) =>
        new(VaultCryptor.CreateDefault(), storage, Argon2Parameters.ForTesting, NullLogger<VaultService>.Instance);

    private static LoginItem SampleLogin() =>
        new() { Name = "Example", Username = "user", Password = "P@ssw0rd-Strong-123" };

    private static byte[] Hash(EncryptedVault v) => EncryptedVaultFingerprint.Compute(v);

    private static async Task<(VaultService Service, InstrumentedVaultStorage Storage)> UnlockedWithOneItemAsync()
    {
        var storage = new InstrumentedVaultStorage();
        VaultService service = CreateService(storage);
        await service.CreateVaultAsync(MasterPassword);
        await service.AddLoginAsync(SampleLogin());
        return (service, storage);
    }

    // ---- 1. On-disk / in-memory consistency ----

    [Fact]
    public async Task Rotate_ExternalReplacement_DifferentKey_FailsClosed_DiskUntouched()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedWithOneItemAsync();

        // Another instance replaces the file with a DIFFERENT valid vault protected by the SAME password,
        // so a mere password check would not catch it.
        var cryptor = VaultCryptor.CreateDefault();
        byte[] pw = Encoding.UTF8.GetBytes(MasterPassword);
        byte[] plaintextB = VaultSerializer.SerializeToUtf8Bytes(new Vault());
        VaultCreationResult b = cryptor.Create(pw, plaintextB, Argon2Parameters.ForTesting);
        storage.ReplaceExternally(b.Encrypted);
        byte[] beforeHash = Hash(storage.Stored!);

        await Assert.ThrowsAsync<VaultChangedExternallyException>(() => service.RotateVaultKeyAsync(MasterPassword));

        Assert.Same(b.Encrypted, storage.Stored);            // disk is still exactly vault B
        Assert.Equal(beforeHash, Hash(storage.Stored!));     // byte-for-byte unchanged
        Assert.Single(service.Items);                        // in-memory session still holds A
    }

    [Fact]
    public async Task Rotate_ExternalModification_SameKey_ChangedContent_FailsClosed_DiskUntouched()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedWithOneItemAsync();
        EncryptedVault a = storage.Stored!;

        // Another valid writer keeps the SAME vault key/header but re-encrypts different content. A
        // key-only comparison would miss this; the envelope fingerprint must not.
        var cryptor = VaultCryptor.CreateDefault();
        UnlockResult ua = cryptor.Unlock(a, Encoding.UTF8.GetBytes(MasterPassword));
        byte[] differentPlaintext = VaultSerializer.SerializeToUtf8Bytes(new Vault()); // empty != A's 1 item
        EncryptedVault bPrime = cryptor.ReEncryptContent(ua.Header, ua.VaultKey, differentPlaintext);
        storage.ReplaceExternally(bPrime);

        Assert.Equal(a.EncryptedVaultKey, bPrime.EncryptedVaultKey); // same key confirmed
        byte[] beforeHash = Hash(storage.Stored!);

        await Assert.ThrowsAsync<VaultChangedExternallyException>(() => service.RotateVaultKeyAsync(MasterPassword));

        Assert.Same(bPrime, storage.Stored);                 // external state untouched
        Assert.Equal(beforeHash, Hash(storage.Stored!));     // byte-for-byte unchanged
        Assert.Single(service.Items);                        // in-memory session still holds A
    }

    [Fact]
    public async Task Rotate_DiskUnchanged_Succeeds_AndReopensWithSamePassword()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedWithOneItemAsync();

        await service.RotateVaultKeyAsync(MasterPassword);

        Assert.Equal(VaultState.Unlocked, service.State);
        VaultService reopen = CreateService(storage);
        await reopen.UnlockAsync(MasterPassword);
        Assert.Single(reopen.Items);
    }

    // ---- 2. Cancellation / commit boundary ----

    [Fact]
    public async Task Rotate_CancelledBeforeAnyWork_FailsClosed_DiskUnchanged()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedWithOneItemAsync();
        byte[] beforeHash = Hash(storage.Stored!);
        int writesBefore = storage.WriteCount;

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.RotateVaultKeyAsync(MasterPassword, cts.Token));

        Assert.Equal(writesBefore, storage.WriteCount);      // nothing written
        Assert.Equal(beforeHash, Hash(storage.Stored!));     // disk byte-for-byte unchanged

        // The session is intact: a normal rotation still works and the item survives.
        await service.RotateVaultKeyAsync(MasterPassword);
        Assert.Single(service.Items);
    }

    [Fact]
    public async Task Rotate_CancelledAfterCryptoBeforeCommit_FailsClosed_DiskUnchanged()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedWithOneItemAsync();
        byte[] beforeHash = Hash(storage.Stored!);
        int writesBefore = storage.WriteCount;

        using var cts = new CancellationTokenSource();
        storage.CancelOnRead = cts; // the token becomes cancelled during rotation's read, before commit
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.RotateVaultKeyAsync(MasterPassword, cts.Token));

        Assert.Equal(writesBefore, storage.WriteCount);      // commit never happened
        Assert.Equal(beforeHash, Hash(storage.Stored!));     // disk byte-for-byte unchanged
    }

    [Fact]
    public async Task Rotate_CommitWrite_IsIssuedWithNonCancellableToken()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedWithOneItemAsync();

        using var cts = new CancellationTokenSource(); // cancellable, but never cancelled
        Assert.True(cts.Token.CanBeCanceled);
        await service.RotateVaultKeyAsync(MasterPassword, cts.Token);

        // The atomic replacement must not carry the caller's cancellable token, so it cannot be
        // interrupted between disk replacement and in-memory adoption.
        Assert.False(storage.LastWriteToken.CanBeCanceled);
        Assert.Equal(VaultState.Unlocked, service.State);
    }

    [Fact]
    public async Task Rotate_CancellationAfterSuccessfulCommit_LeavesConsistentSession()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedWithOneItemAsync();

        using var cts = new CancellationTokenSource();
        await service.RotateVaultKeyAsync(MasterPassword, cts.Token); // completes and adopts the new key
        cts.Cancel(); // requested AFTER success — must not roll the session into an inconsistent state

        Assert.Equal(VaultState.Unlocked, service.State);
        await service.AddLoginAsync(new LoginItem { Name = "Second", Username = "u2", Password = "p2xxxxxx" });

        VaultService reopen = CreateService(storage);
        await reopen.UnlockAsync(MasterPassword); // disk = rotated + saved; memory adopted the new key
        Assert.Equal(2, reopen.Items.Count);
    }

    // ---- 3. Commit-write failure reconciliation (post-replacement ambiguity) ----

    [Fact]
    public async Task Rotate_WriteThrowsBeforeReplacement_KeepsOldKey_DiskUnchanged()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedWithOneItemAsync();
        byte[] beforeHash = Hash(storage.Stored!);

        storage.FailNextWrite = true; // throws before the file is replaced
        await Assert.ThrowsAsync<IOException>(() => service.RotateVaultKeyAsync(MasterPassword));

        // Reconciliation saw disk == old: surface the failure, keep the old key, disk byte-for-byte intact.
        Assert.Equal(beforeHash, Hash(storage.Stored!));
        Assert.Equal(VaultState.Unlocked, service.State);

        await service.AddLoginAsync(new LoginItem { Name = "Second", Username = "u2", Password = "p2xxxxxx" });
        VaultService reopen = CreateService(storage);
        await reopen.UnlockAsync(MasterPassword);
        Assert.Equal(2, reopen.Items.Count);
    }

    [Fact]
    public async Task Rotate_WriteReplacesThenThrows_AdoptsNewKey_SucceedsConsistently()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedWithOneItemAsync();
        EncryptedVault a = storage.Stored!;

        storage.ReplaceThenThrowNextWrite = true; // replacement takes effect, then WriteAsync throws
        await service.RotateVaultKeyAsync(MasterPassword); // reconciliation adopts -> no throw

        Assert.Equal(VaultState.Unlocked, service.State);
        Assert.NotSame(a, storage.Stored);   // disk now holds the rotated vault
        Assert.Single(service.Items);

        // Adoption is proven by a SECOND rotation succeeding: the in-memory fingerprint now matches disk.
        // (If memory had kept the old key/fingerprint, this would throw VaultChangedExternallyException.)
        await service.RotateVaultKeyAsync(MasterPassword);

        VaultService reopen = CreateService(storage);
        await reopen.UnlockAsync(MasterPassword);
        Assert.Single(reopen.Items);
    }

    [Fact]
    public async Task Rotate_WriteLeavesIndeterminateDisk_LocksSession_FailsClosed()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedWithOneItemAsync();

        // The failing write leaves disk holding a THIRD vault that is neither the old nor the rotated one.
        var cryptor = VaultCryptor.CreateDefault();
        VaultCreationResult other = cryptor.Create(
            Encoding.UTF8.GetBytes("some-other-password-xyz"),
            VaultSerializer.SerializeToUtf8Bytes(new Vault()),
            Argon2Parameters.ForTesting);
        storage.StoreThenThrowNextWrite = other.Encrypted;

        await Assert.ThrowsAsync<VaultInconsistentStateException>(() => service.RotateVaultKeyAsync(MasterPassword));

        // Fail closed: the session is locked (no authoritative in-memory state that could disagree with disk).
        Assert.Equal(VaultState.Locked, service.State);
        Assert.Empty(service.Items);
    }
}
