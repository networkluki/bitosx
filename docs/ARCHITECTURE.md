# Architecture

BitosX is organized into layered projects with clear responsibilities. The two security-critical
layers (`BitosX.Core` and `BitosX.Crypto`) have no dependency on the UI framework.

## Projects and dependencies

```
BitosX.App  ──> Core, Crypto, Storage, Platform, ApiClient   (Avalonia MVVM desktop UI)
BitosX.Core ──> Crypto                                        (domain + orchestration)
BitosX.Crypto                                                 (KDF, AEAD, format, RNG)
BitosX.Storage ──> Core (+ Crypto)                            (atomic file persistence)
BitosX.Platform ──> Core                                      (clipboard policy, secure-storage hook)
BitosX.ApiClient ──> Core                                     (local-only API abstraction)
```

- **BitosX.Crypto** — cryptographic primitives and the versioned encrypted vault format:
  `Argon2idKeyDerivation`, `AesGcmCipher`, `VaultCryptor`, `EncryptedVault(+Serializer)`,
  `SecureRandom`, `CryptoBuffers`. No dependency on Core or the UI.
- **BitosX.Core** — UI-independent domain and logic: `Vault`, `VaultItem`/`LoginItem`,
  `VaultService` (create/unlock/lock, CRUD, save), `PasswordGenerator`, `SecurityAnalyzer`,
  validation, serialization, logging abstractions, and the `ILocalVaultStorage` interface.
- **BitosX.Storage** — `FileLocalVaultStorage`, an atomic file implementation of
  `ILocalVaultStorage`, plus `FileVaultBackup` (`IVaultBackup`) for byte-for-byte encrypted
  export and recovery-copy-protected atomic restore.
- **BitosX.Platform** — platform *policy* that does not need the UI framework: the clipboard
  auto-clear policy (`ClipboardService`) over an injected `IClipboardAccessor`,
  `ClipboardSessionGuard` (clears a copied secret on workstation lock / suspend),
  `SingleInstanceGuard` (one running instance per vault), OS session and power signals
  (`ISystemSessionEvents`), plus an `ISecureStorage` extension point for future OS key protection.
- **BitosX.ApiClient** — `IBitosXApiClient` boundary and a `LocalOnlyApiClient` that performs
  no network I/O. Reserved for future, zero-knowledge server features.
- **BitosX.App** — Avalonia views and view models, the DI composition root
  (`ServiceConfiguration`), the Avalonia-specific `IClipboardAccessor` (which marks copied secrets
  as sensitive, see `SensitiveClipboardData`), and `ExitClipboardCleaner`.

## UI structure (MVVM)

- `MainWindowViewModel` switches the visible screen from the vault lifecycle state:
  `NoVault → CreateVaultView`, `Locked → UnlockView`, `Unlocked → UnlockedShellView`.
- `UnlockedShellViewModel` hosts navigation (Vault, Generator, Security Center, Settings) and
  the lock action.
- Views are thin. All logic lives in view models and services; there are **no cryptographic
  operations in code-behind**. Views resolve to view models via `ViewLocator`.
- Bindings are compiled (`x:DataType`).

## Vault lifecycle

1. **Create** — `VaultService.CreateVaultAsync` validates the password, creates an empty vault,
   asks `VaultCryptor` to derive a KEK, generate + wrap a random vault key, and encrypt the
   content, then persists via `ILocalVaultStorage`. The service stays unlocked, holding only the
   vault key (not the master password).
2. **Unlock** — reads the encrypted vault, derives the KEK, unwraps the vault key, decrypts the
   content, and deserializes **and validates** it. Wrong password / tampering fail with a single
   generic error.
3. **Edit/Save** — a change is applied to a *copy* of the vault, which is validated, re-encrypted
   under the retained vault key (no re-derivation), and written atomically. Only then does the copy
   become the vault the rest of the application sees. There is nothing to roll back if the write
   fails, and `Items` never shows state that is not on disk.
4. **Lock** — wipes the vault key buffer and drops decrypted state, returning to the locked screen.

### Consistency and concurrency in `VaultService`

- **One writer at a time.** Operations that read or replace the stored vault are serialized.
- **The consistency gate.** Before any write, the file must be byte-for-byte the envelope the
  session loaded or last wrote (`EncryptedVaultFingerprint`); otherwise the operation fails closed
  with `VaultChangedExternallyException`. A missing file is the one exception: the session then
  holds the last copy of the data, so it is written back.
- **One commit routine.** If a write throws, the file is read back: still the previous vault → the
  failure is reported; already the new vault → success; anything else → the session locks
  (`VaultInconsistentStateException`). The write itself is not cancellable.
- **`Lock()` never waits.** It clears the session immediately. An operation in flight works on a
  private copy of the key, remembers the session it started in, and adopts its result only if that
  session still exists.
- **One owner for the key.** It passes to the session exactly once and is wiped only there. If a
  `StateChanged` subscriber cannot be notified that the vault is open, the vault locks again; every
  subscriber is notified even if an earlier one throws.

`StateChanged` is raised while the session is held, so notifications arrive in the order the
transitions happened. Handlers must not block waiting on another thread.

## Persistence

`FileLocalVaultStorage` writes to a uniquely named temporary file that it creates exclusively,
flushes it to the physical disk, and then atomically replaces the target (`File.Replace`), so an
interrupted or failed write — including a crash or power loss — cannot corrupt or destroy the
existing vault. Reads enforce a maximum file size on the bytes actually read and delegate parsing
to the fail-closed `EncryptedVaultSerializer`. Backups and the settings file go through the same
primitives (`StorageFiles`).

## Extensibility

- The encrypted format is versioned; new versions can be added with an upgrade path while old
  vaults remain readable.
- `ISecureStorage` and `IBitosXApiClient` are seams for future OS secret storage and server
  sync. The `StateChanged` event on `IVaultService` is what automatic locking builds on.
