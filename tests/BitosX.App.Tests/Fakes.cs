using BitosX.Core.Domain;
using BitosX.Core.Services;
using BitosX.Core.Settings;
using BitosX.Crypto;
using BitosX.Platform.Clipboard;
using BitosX.Platform.Files;

namespace BitosX.App.Tests;

/// <summary>Fake vault service: reports Unlocked; CRUD is unused by the restore-boundary tests.</summary>
internal sealed class FakeVaultService : IVaultService
{
    public VaultState State { get; set; } = VaultState.Unlocked;
    public int LockCount { get; private set; }

    public event EventHandler<VaultState>? StateChanged;

    public IReadOnlyList<VaultItem> Items => [];

    public Task<VaultState> RefreshStateAsync(CancellationToken cancellationToken = default) => Task.FromResult(State);

    public void Lock()
    {
        LockCount++;
        State = VaultState.Locked;
        StateChanged?.Invoke(this, State);
    }

    public Task CreateVaultAsync(string masterPassword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task UnlockAsync(string masterPassword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task ChangeMasterPasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public int RotateCount { get; private set; }
    public Exception? RotateThrow { get; set; }
    public Task RotateVaultKeyAsync(string currentPassword, CancellationToken cancellationToken = default)
    {
        RotateCount++;
        if (RotateThrow is not null)
        {
            return Task.FromException(RotateThrow);
        }

        return Task.CompletedTask;
    }
    public Task<LoginItem> AddLoginAsync(LoginItem item, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<VaultItem> UpdateItemAsync(VaultItem item, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeleteItemAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<VaultItem> ToggleFavoriteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

/// <summary>Records calls to the destructive commit and validation, and can simulate failures.</summary>
internal sealed class FakeVaultBackupService : IVaultBackupService
{
    public int ExportCount { get; private set; }
    public int PrepareCount { get; private set; }
    public int CommitCount { get; private set; }
    public Exception? PrepareThrow { get; set; }
    public Exception? CommitThrow { get; set; }

    public Task ExportAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        ExportCount++;
        return Task.CompletedTask;
    }

    public Task<BackupPreview> PrepareRestoreAsync(string backupPath, string backupPassword, CancellationToken cancellationToken = default)
    {
        PrepareCount++;
        if (PrepareThrow is not null)
        {
            throw PrepareThrow;
        }

        return Task.FromResult(SamplePreview());
    }

    public Task CommitRestoreAsync(BackupPreview preview, CancellationToken cancellationToken = default)
    {
        CommitCount++;
        if (CommitThrow is not null)
        {
            throw CommitThrow;
        }

        return Task.CompletedTask;
    }

    private static BackupPreview SamplePreview()
    {
        var envelope = new EncryptedVault(
            1, "argon2id", Argon2Parameters.ForTesting,
            new byte[16], "AES-256-GCM", new byte[12], new byte[48], new byte[12], new byte[62]);
        return new BackupPreview(envelope, 1, 2, "backup.bitosx-backup", 1234);
    }
}

internal sealed class FakeFilePicker : IFilePicker
{
    public string? OpenPath { get; set; }
    public string? SavePath { get; set; }

    public Task<string?> PickBackupSaveAsync(string suggestedFileName, CancellationToken cancellationToken = default) => Task.FromResult(SavePath);
    public Task<string?> PickBackupOpenAsync(CancellationToken cancellationToken = default) => Task.FromResult(OpenPath);
}

internal sealed class FakeClipboardService : IClipboardService
{
    public TimeSpan ClearDelay => TimeSpan.FromSeconds(30);
    public Task CopySecretAsync(string value, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ClearIfUnchangedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class FakeAppSettingsStore : IAppSettingsStore
{
    public AppSettings Load() => AppSettings.Default;
    public void Save(AppSettings settings) { }
}
