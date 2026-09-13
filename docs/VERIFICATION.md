# Verification Status

This document records how thoroughly each part of the **local encrypted vault** milestone has
been verified, and provides a manual GUI checklist that has **not yet** been performed. It is kept
deliberately honest: anything not actually confirmed is marked `NOT VERIFIED`.

## Legend

| Status | Meaning |
|---|---|
| **VERIFIED** | Confirmed by code review / static reasoning of the current source. |
| **AUTOMATED TESTED** | Covered by an automated test in the suite that currently passes. |
| **MANUALLY VERIFIED** | A person ran the application and confirmed the behavior end to end. |
| **NOT VERIFIED** | Not confirmed by any of the above. |

> As of this pass, **no manual GUI click-through has been performed**, so nothing is
> `MANUALLY VERIFIED`. The build, the automated tests, and the KDF benchmark were executed on a
> Windows 11 / .NET 10.0.12 machine.

## Status matrix

| Area | Status | Evidence |
|---|---|---|
| Debug build (`dotnet build -c Debug`) | AUTOMATED TESTED | Builds with 0 warnings / 0 errors. |
| Release build (`dotnet build -c Release`) | AUTOMATED TESTED | Builds with 0 warnings / 0 errors. |
| Debug tests (`dotnet test -c Debug`) | AUTOMATED TESTED | All tests pass. |
| Release tests (`dotnet test -c Release`) | AUTOMATED TESTED | All tests pass. |
| AES-256-GCM round-trip / tamper / wrong-key fail-closed | AUTOMATED TESTED | `AesGcmCipherTests`, `VaultCryptorTests`. |
| Fresh unique nonce/salt/vault-key per operation | AUTOMATED TESTED | `VaultCryptorTests` (unique salt/nonce, independent keys). |
| Format-version / KDF / algorithm validated before decryption | VERIFIED + AUTOMATED TESTED | `VaultCryptor.ValidateFormat`, `EncryptedVaultSerializerTests`. |
| No plaintext fallback on corrupt / unknown input | AUTOMATED TESTED | `EncryptedVaultSerializerTests`, `FileLocalVaultStorageTests`. |
| KDF parameter upper bounds (resource-exhaustion guard) | AUTOMATED TESTED | `Argon2idKeyDerivationTests`, `EncryptedVaultSerializerTests`, `VaultServiceTests`. |
| Master password never persisted / logged / retained | VERIFIED | `VaultService` (wiped after use), `VaultLog` (no secret fields). |
| Vault key wiped and released on lock | VERIFIED + AUTOMATED TESTED | `VaultService.Lock`, `VaultServiceTests` locking tests. |
| Vault plaintext never written to disk | VERIFIED + AUTOMATED TESTED | `FileLocalVaultStorage` writes ciphertext only; `VaultPersistenceIntegrationTests` plaintext-leak check. |
| No accidental plaintext in the persisted file | AUTOMATED TESTED | `VaultPersistenceIntegrationTests` (per-field distinctive markers). |
| Atomic write preserves prior vault on failure | AUTOMATED TESTED | `FileLocalVaultStorageTests.FailedWrite_DoesNotDestroyExistingVault`. |
| Oversized vault file rejected before large allocation | AUTOMATED TESTED | `FileLocalVaultStorageTests.Read_OversizedFile_FailsClosed`. |
| Vault item field-length / count / URL bounds | AUTOMATED TESTED | `VaultValidationTests` (enforced on add/update). |
| Clipboard copy only on explicit action | VERIFIED | `VaultPageViewModel` copy commands call `ClipboardService`. |
| Clipboard auto-clear after timeout | AUTOMATED TESTED | `ClipboardServiceTests.AutoClear_AfterDelay_ClearsClipboard`. |
| Clipboard does not erase content the user copied afterward | AUTOMATED TESTED | `ClipboardServiceTests.AutoClear_DoesNotEraseContentTheUserReplaced`. |
| Repeated copies / dispose / accessor failure handled safely | AUTOMATED TESTED | `ClipboardServiceTests`. |
| Lock removes decrypted state; no navigation back without re-auth | VERIFIED + AUTOMATED TESTED | State-driven shell (`MainWindowViewModel`); `VaultServiceTests` locking tests. |
| Argon2id production-profile unlock latency | VERIFIED | `tools/BitosX.KdfBenchmark` (~46 ms median on the reference machine). |
| **End-to-end GUI behavior (create/unlock/edit/copy/lock/persist)** | **NOT VERIFIED** | Requires the manual checklist below. |
| Secure erasure of secrets held as managed `string` | NOT VERIFIED (not achievable) | Documented limitation in `SECURITY_MODEL.md`. |

## Known limitations found or reaffirmed in this pass

- **Managed-memory wiping is best-effort.** Byte buffers (KEK, vault key, password bytes,
  plaintext bytes) are zeroed, but decrypted values held as `string` (item passwords in the UI,
  and the last-copied value the clipboard policy compares against) cannot be reliably erased. See
  `SECURITY_MODEL.md`.
- **Decrypted vault content is not re-validated against `VaultValidation` bounds on load.**
  `VaultValidation` is enforced when items are added/updated, not when a decrypted vault is
  deserialized. This is defense-in-depth only: the content is AEAD-authenticated, so producing
  malicious content requires the vault key (i.e. the master password), and the pre-decryption file
  size is already capped at 16 MiB. Adding validation-on-load is a reasonable future hardening.
- **The production KDF profile is fast on high-end hardware** (~46 ms median on a 16-core desktop).
  It is comfortably interactive but on the low side for offline brute-force cost. Do **not** retune
  it from a single machine; a future milestone should add device-aware calibration and versioned
  migration.

## Manual GUI checklist (NOT VERIFIED)

Run the app with `dotnet run --project src/BitosX.App`. Perform each step and confirm the expected
result. This checklist has **not** been executed by the automated pass.

> The default vault lives at `%APPDATA%\BitosX\vault.bitosx` (Windows). To test "first launch"
> cleanly, ensure no vault file exists there first (back it up if it does).

| # | Step | Expected result |
|---|---|---|
| 1 | **First launch** (no vault file present) | The app opens on the **Create Vault** screen, not the unlock or main screen. |
| 2 | **Create vault** with a master password (≥ 8 chars) | The vault is created; the app transitions to the unlocked main shell showing an empty vault. A `vault.bitosx` file now exists. |
| 3 | **Restart the app** | The app opens on the **Unlock** screen (a vault now exists), not Create Vault. |
| 4 | **Enter the wrong master password** | Unlock is rejected with a single generic error ("master password is incorrect or the vault file has been modified"); the app stays locked. No hint distinguishes wrong-password from tampering. |
| 5 | **Enter the correct master password** | The vault unlocks and shows the main shell. |
| 6 | **Add a login** (name, username, password, URL, notes) | The item appears in the list after saving; the file on disk changes. |
| 7 | **Edit the login** (change a field) | The change is shown in the list and persists after save. |
| 8 | **Generate a password** in the editor / generator | A random password is produced; regenerating yields a different value. |
| 9 | **Copy the username** | A status message confirms the copy and states the clear countdown; the OS clipboard holds the username. |
| 10 | **Copy the password** | A status message confirms the copy and countdown; the OS clipboard holds the password. |
| 11 | **Wait for the clipboard timeout** (default 30 s) without copying anything else | The clipboard is cleared. Then repeat the copy, immediately copy something else yourself, and wait: **your** later content must **not** be erased. |
| 12 | **Favorite, then unfavorite** an item | The favorite indicator toggles; favorites sort to the top; the state persists across a lock/unlock. |
| 13 | **Lock the vault** (Lock command) | The app returns to the Unlock screen. |
| 14 | **Verify secrets disappear** | After locking, no item list, selected item, or password field is visible; there is no navigation path back into the vault without re-entering the master password. |
| 15 | **Unlock again** with the correct password | The vault and all items reappear unchanged. |
| 16 | **Delete an item** | The item disappears from the list and does not return after save. |
| 17 | **Restart and verify persistence** | After a full restart and unlock, previously added/edited items are present and the deleted item is gone. |

Record the outcome of each step (PASS / FAIL / notes) when the checklist is run, and update the
status matrix entry for end-to-end GUI behavior to `MANUALLY VERIFIED` only once all steps pass.
