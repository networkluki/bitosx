using BitosX.Core.Domain;
using BitosX.Core.Services;
using Microsoft.Extensions.Logging;

namespace BitosX.Core.Diagnostics;

/// <summary>
/// Source-generated, high-performance structured logging for vault lifecycle events.
/// Every message here is intentionally free of secret values: no master password, item
/// passwords, keys, or decrypted contents are ever logged.
/// </summary>
public static partial class VaultLog
{
    [LoggerMessage(EventId = 1000, EventName = "application_started", Level = LogLevel.Information, Message = "Application started.")]
    public static partial void ApplicationStarted(ILogger logger);

    [LoggerMessage(EventId = 1001, EventName = "vault_created", Level = LogLevel.Information, Message = "Vault created.")]
    public static partial void VaultCreated(ILogger logger);

    [LoggerMessage(EventId = 1002, EventName = "vault_unlocked", Level = LogLevel.Information, Message = "Vault unlocked with {ItemCount} item(s).")]
    public static partial void VaultUnlocked(ILogger logger, int itemCount);

    [LoggerMessage(EventId = 1003, EventName = "vault_unlock_failed", Level = LogLevel.Warning, Message = "Vault unlock failed.")]
    public static partial void VaultUnlockFailed(ILogger logger);

    [LoggerMessage(EventId = 1004, EventName = "vault_locked", Level = LogLevel.Information, Message = "Vault locked.")]
    public static partial void VaultLocked(ILogger logger);

    [LoggerMessage(EventId = 1010, EventName = "vault_auto_locked", Level = LogLevel.Information, Message = "Vault auto-locked ({Reason}).")]
    public static partial void VaultAutoLocked(ILogger logger, AutoLockReason reason);

    [LoggerMessage(EventId = 1011, EventName = "master_password_changed", Level = LogLevel.Information, Message = "Master password changed.")]
    public static partial void MasterPasswordChanged(ILogger logger);

    [LoggerMessage(EventId = 1012, EventName = "master_password_change_failed", Level = LogLevel.Warning, Message = "Master password change failed.")]
    public static partial void MasterPasswordChangeFailed(ILogger logger);

    [LoggerMessage(EventId = 1013, EventName = "backup_exported", Level = LogLevel.Information, Message = "Encrypted backup exported.")]
    public static partial void BackupExported(ILogger logger);

    [LoggerMessage(EventId = 1014, EventName = "backup_restore_validation_failed", Level = LogLevel.Warning, Message = "Backup restore validation failed.")]
    public static partial void BackupRestoreValidationFailed(ILogger logger);

    [LoggerMessage(EventId = 1015, EventName = "backup_restored", Level = LogLevel.Information, Message = "Vault restored from encrypted backup.")]
    public static partial void BackupRestored(ILogger logger);

    [LoggerMessage(EventId = 1016, EventName = "vault_key_rotated", Level = LogLevel.Information, Message = "Vault key rotated and content re-encrypted.")]
    public static partial void VaultKeyRotated(ILogger logger);

    [LoggerMessage(EventId = 1017, EventName = "vault_key_rotation_failed", Level = LogLevel.Warning, Message = "Vault key rotation failed.")]
    public static partial void VaultKeyRotationFailed(ILogger logger);

    [LoggerMessage(EventId = 1005, EventName = "vault_saved", Level = LogLevel.Information, Message = "Vault saved.")]
    public static partial void VaultSaved(ILogger logger);

    [LoggerMessage(EventId = 1006, EventName = "vault_save_failed", Level = LogLevel.Error, Message = "Vault save failed.")]
    public static partial void VaultSaveFailed(ILogger logger);

    [LoggerMessage(EventId = 1007, EventName = "vault_item_added", Level = LogLevel.Information, Message = "Vault item added ({ItemType}).")]
    public static partial void VaultItemAdded(ILogger logger, VaultItemType itemType);

    [LoggerMessage(EventId = 1008, EventName = "vault_item_updated", Level = LogLevel.Information, Message = "Vault item updated ({ItemType}).")]
    public static partial void VaultItemUpdated(ILogger logger, VaultItemType itemType);

    [LoggerMessage(EventId = 1009, EventName = "vault_item_deleted", Level = LogLevel.Information, Message = "Vault item deleted.")]
    public static partial void VaultItemDeleted(ILogger logger);
}
