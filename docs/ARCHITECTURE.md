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
BitosX.ApiClient ──> Core                                     (the BitosX server: client + account session)
```

- **BitosX.Crypto** — cryptographic primitives and the versioned encrypted vault format:
  `Argon2idKeyDerivation`, `AesGcmCipher`, `VaultCryptor`, `EncryptedVault(+Serializer)`,
  `SecureRandom`, `CryptoBuffers`. No dependency on Core or the UI.
- **BitosX.Core** — UI-independent domain and logic: `Vault`, `VaultItem`/`LoginItem`,
  `VaultService` (create/unlock/lock, CRUD, save, re-wrap), `PasswordGenerator`, `SecurityAnalyzer`,
  `MasterPasswordEncoding`, `KeyDerivationProfile` and `KeyDerivationBenchmark`,
  validation, serialization, logging abstractions, and the `ILocalVaultStorage` interface.
  `ImportExport` reads what other password managers export and writes the two unencrypted
  exports — bytes in, a model out, no file and no vault — and `VaultImportService` and
  `VaultExportService` tie that to the vault; see [IMPORT_EXPORT.md](IMPORT_EXPORT.md).
  `Authenticator` (0.15.0) reads an authenticator key from text (`TotpKeyParser`) and makes its
  codes (`TotpGenerator`, RFC 6238); the key is `LoginItem.Totp` (`TotpKey`), stored strictly by
  `TotpKeyJsonConverter`, and a vault that holds one is written with content schema version 2
  (`Vault.RequiredSchemaVersion`); see [TOTP.md](TOTP.md).
- **BitosX.Storage** — `FileLocalVaultStorage`, an atomic file implementation of
  `ILocalVaultStorage`, plus `FileVaultBackup` (`IVaultBackup`) for byte-for-byte encrypted
  export and recovery-copy-protected atomic restore, and `FileImportExportFiles`
  (`IImportExportFiles`): an import file read within its size limit, an unencrypted export
  written atomically and for its owner only.
- **BitosX.Platform** — platform *policy* that does not need the UI framework: the clipboard
  auto-clear policy (`ClipboardService`) over an injected `IClipboardAccessor`,
  `ClipboardSessionGuard` (clears a copied secret on workstation lock / suspend),
  `SingleInstanceGuard` (one running instance per vault), OS session and power signals
  (`ISystemSessionEvents`), plus an `ISecureStorage` extension point for future OS key protection.
- **BitosX.ApiClient** — the BitosX server: `IBitosXApiClient` (login, refresh, logout, profile,
  sessions, and the sync status/download/upload/delete of the encrypted envelope),
  `HttpBitosXApiClient` over HTTPS with a fixed transport policy, `LocalOnlyApiClient` for a run
  without a server, `ApiServer` (the origin, validated), `ApiException`/`ApiFailure` (every
  failure as a fixed kind with a fixed message), `AccountSession` (the tokens in memory, one
  refresh at a time, signed out when the vault locks), and `Sync/VaultSyncService` (the sync
  decision over the vault file, the sync record and the account; no merge, fail closed). No method
  can carry decrypted vault content; see [ACCOUNT.md](ACCOUNT.md) and [SYNC.md](SYNC.md).
- **BitosX.App** — Avalonia views and view models, the DI composition root
  (`ServiceConfiguration`), the Avalonia-specific `IClipboardAccessor` (which marks copied secrets
  as sensitive, see `SensitiveClipboardData`), `ExitClipboardCleaner`, `UnexpectedErrorGuard`
  (fails closed when a fault reaches the user interface), and `SecretBox` (the password field, and
  since 0.15.0 the authenticator key's). `VaultPageViewModel` keeps the authenticator codes of the
  listed logins up to date with one timer of one second (`TimeProvider`), which runs only while the
  vault is unlocked; locking stops it and clears the codes.

## How the application is put together

- `AppLocation` says where the vault and the settings are: the per-user location, or the
  directory named by `--data-directory`. `ApiServerSelection` says which server to talk to: the
  production server, the one named by `--api-server`, or none. `Program` reads both from the
  command line before anything else; an option that cannot be used ends the application rather
  than opening the default vault or talking to the production server.
- `ServiceConfiguration.Build(location)` registers the services.
- `MainWindowSession.Create(services)` creates the main window and attaches what belongs to it:
  the view model, automatic locking, clearing the clipboard when the user leaves or the window is
  closed, and the guard against unexpected errors.

`App` does exactly this, and so do the tests that run the whole application
(`WholeApplication` in `BitosX.App.Tests`): there is one place where the application is put
together, and both use it.

## UI structure (MVVM)

- `MainWindowViewModel` switches the visible screen from the vault lifecycle state:
  `NoVault → CreateVaultView`, `Locked → UnlockView`, `Unlocked → UnlockedShellView`. It also
  holds the notice shown at the top of the window (`AppNotice`), whatever the screen.
- `UnlockedShellViewModel` hosts navigation (Vault, Generator, Security Center, Settings, Account)
  and the lock action. Its pages are built anew every time the vault is unlocked, so a page must take
  what it shows from what applies now, not from what the application started with.
- Views are thin. All logic lives in view models and services; there are **no cryptographic
  operations in code-behind**. Views resolve to view models via `ViewLocator`.
- Bindings are compiled (`x:DataType`).
- Every control has a `Name`, which is also what the accessibility interface of the operating
  system knows it by; every input has a name a screen reader can say
  (`AutomationProperties.Name`).
- A field for a password is a `SecretBox`, never a `TextBox` with a `PasswordChar`: a text box
  hands its text to the accessibility interface whatever it shows. A test fails if a view has one.

## Vault lifecycle

1. **Create** — `VaultService.CreateVaultAsync` validates the password, creates an empty vault,
   asks `VaultCryptor` to derive a KEK, generate + wrap a random vault key, and encrypt the
   content, then persists via `ILocalVaultStorage`. The service stays unlocked, holding only the
   vault key (not the master password). The key is derived with the settings for new vaults
   (`KeyDerivationProfile.ForNewVaults`) — or, on a device that cannot spare the memory they ask
   for, with the fallback settings, so that a vault can be opened where it was created.
2. **Unlock** — reads the encrypted vault, derives the KEK, unwraps the vault key, decrypts the
   content, and deserializes **and validates** it. Wrong password / tampering fail with a single
   generic error. An empty password is a wrong password.
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
- **Password encodings.** Wherever a master password is turned into bytes, it goes through
  `MasterPasswordEncoding`. A new key is always wrapped under the canonical encoding; an existing
  vault is opened by trying the encodings it could have been keyed with.

`StateChanged` is raised while the session is held, so notifications arrive in the order the
transitions happened. Handlers must not block waiting on another thread.

## Import and export

Importing is three steps, and only the last one writes: `VaultImportService.ReadAsync` reads the
file (at most 16 MiB) and parses it; `PreviewAsync` compares it with the unlocked vault and returns
an `ImportPlan` — what would be added, what is a duplicate, what is not imported and why;
`ApplyAsync` adds the items through `IVaultService.AddLoginsAsync`, which is **one** write of the
vault: all of them are stored, or none is. The comparison is made again at that moment, under the
gate every change goes through, so what is added is right for the vault as it is then.

Exporting writes the vault out unencrypted, which the unlocked session alone does not authorize:
`VaultExportService.ExportAsync` has the master password verified first
(`IVaultService.VerifyMasterPasswordAsync`, the check a master password change makes), writes the
items that check was for, and writes nothing if the password is wrong or the vault is locked
meanwhile.

The parsers treat a file as written by an attacker; what they refuse, and every rule the web vault
has to reproduce, is in [IMPORT_EXPORT.md](IMPORT_EXPORT.md).

## Persistence

`FileLocalVaultStorage` writes to a uniquely named temporary file that it creates exclusively,
flushes it to the physical disk, and then renames it over the target in one step of the file system
(`File.Move` with `overwrite`: `rename(2)` on Unix, `MoveFileEx` with `MOVEFILE_REPLACE_EXISTING` on
Windows), so an interrupted or failed write — including a crash or power loss — cannot corrupt or
destroy the existing vault. Until 0.13.1 Windows used `File.Replace` (`ReplaceFile`), which is
several steps in one call and documents failures after which the file being replaced is no longer
under its name; see [VERSIONING.md](VERSIONING.md). On Windows the rename is tried again for up to
1.5 s while another program has the file open. Reads enforce a maximum file size on the bytes actually read and delegate parsing
to the fail-closed `EncryptedVaultSerializer`. A vault that would be larger than that size is not
written (`VaultTooLargeException`): a file this application refuses to read must never take the
place of one it can read. Backups and the settings file go through the same
primitives (`StorageFiles`).

## Extensibility

- The encrypted format is versioned; new versions can be added with an upgrade path while old
  vaults remain readable.
- `ISecureStorage` is the seam for future OS secret storage (a persisted refresh token);
  `ISyncStateStore` is the sync record (`FileSyncStateStore` next to the vault). The
  `StateChanged` event on `IVaultService` is what automatic locking and the account sign-out on
  lock build on.
