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
  auto-clear policy (`ClipboardService`) over an injected `IClipboardAccessor`, plus an
  `ISecureStorage` extension point for future OS key protection.
- **BitosX.ApiClient** — `IBitosXApiClient` boundary and a `LocalOnlyApiClient` that performs
  no network I/O. Reserved for future, zero-knowledge server features.
- **BitosX.App** — Avalonia views and view models, the DI composition root
  (`ServiceConfiguration`), and the Avalonia-specific `IClipboardAccessor`.

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
   content, and deserializes it. Wrong password / tampering fail with a single generic error.
3. **Edit/Save** — mutations happen in memory; `VaultService` re-encrypts the content under the
   retained vault key (no re-derivation) and writes it atomically. In-memory changes roll back
   if the write fails, so memory and disk stay consistent.
4. **Lock** — wipes the vault key buffer and drops decrypted state, returning to the locked screen.

## Persistence

`FileLocalVaultStorage` writes to a temporary file and then atomically replaces the target
(`File.Replace`), so an interrupted or failed write cannot corrupt or destroy the existing vault.
Reads enforce a maximum file size and delegate parsing to the fail-closed
`EncryptedVaultSerializer`.

## Extensibility

- The encrypted format is versioned; new versions can be added with an upgrade path while old
  vaults remain readable.
- `ISecureStorage`, `IBitosXApiClient`, and the `StateChanged` event on `IVaultService` are
  seams for future OS secret storage, server sync, and automatic locking, respectively.
