# Verification Status

This document records how thoroughly each part of the **local encrypted vault** milestone has
been verified, including the manual GUI checklist. It is kept deliberately honest: anything not
actually confirmed is marked `NOT VERIFIED`.

## Legend

| Status | Meaning |
|---|---|
| **VERIFIED** | Confirmed by code review / static reasoning of the current source. |
| **AUTOMATED TESTED** | Covered by an automated test in the suite that currently passes. |
| **MANUALLY VERIFIED** | A person ran the application and confirmed the behavior end to end. |
| **NOT VERIFIED** | Not confirmed by any of the above. |

> The manual GUI click-through (all 17 steps) was performed on **2026-09-13** on a
> Windows 11 / .NET 10.0.12 machine, alongside the build, automated tests, and KDF benchmark.
> It surfaced a real clipboard-clear defect that automated tests missed; the defect was fixed and
> the affected steps re-verified (see "Clipboard auto-clear defect" below).

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
| Clipboard auto-clear after timeout | AUTOMATED TESTED + MANUALLY VERIFIED (2026-09-13) | `ClipboardServiceTests.AutoClear_AfterDelay_ClearsClipboard`; real-clipboard clear confirmed after the UI-thread fix. |
| Clipboard does not erase content the user copied afterward | AUTOMATED TESTED + MANUALLY VERIFIED (2026-09-13) | `ClipboardServiceTests.AutoClear_DoesNotEraseContentTheUserReplaced`; confirmed live. |
| Repeated copies / dispose / accessor failure handled safely | AUTOMATED TESTED | `ClipboardServiceTests`. |
| Lock removes decrypted state; no navigation back without re-auth | VERIFIED + AUTOMATED TESTED + MANUALLY VERIFIED (2026-09-13) | State-driven shell (`MainWindowViewModel`); `VaultServiceTests` locking tests; GUI steps 13–15. |
| Argon2id production-profile unlock latency | VERIFIED | `tools/BitosX.KdfBenchmark` (~46 ms median on the reference machine). |
| **End-to-end GUI behavior (create/unlock/edit/copy/lock/persist)** | **MANUALLY VERIFIED (2026-09-13)** | All 17 checklist steps passed; corroborated by on-disk checks (vault created, no plaintext leak, add/edit/delete persisted). |
| Secure erasure of secrets held as managed `string` | NOT VERIFIED (not achievable) | Documented limitation in `SECURITY_MODEL.md`. |

## Automatic locking (milestone after v0.1.0) — verified 2026-09-13

Full detail and the A1–A8 checklist live in [AUTO_LOCK.md](AUTO_LOCK.md). Summary:

| Item | Status |
|---|---|
| Auto-lock policy + settings persistence | AUTOMATED TESTED (`AutoLockControllerTests`, `AppSettingsTests`, `FileAppSettingsStoreTests`) |
| Inactivity lock, activity-resets, background-does-not-reset (Windows) | MANUALLY VERIFIED (2026-09-13) — A2–A4 |
| Settings persistence across restart (Windows) | MANUALLY VERIFIED (2026-09-13) — A1 |
| Lock on minimize (Windows) | MANUALLY VERIFIED (2026-09-13) — A5 |
| Workstation lock (Win+L) / suspend-resume (Windows) | MANUALLY VERIFIED (2026-09-13) — A6, A7 (no exceptions in app output) |
| Post-lock security state (Windows) | MANUALLY VERIFIED (2026-09-13) — A8 |
| Linux/macOS session-lock & suspend detection | **NOT VERIFIED / UNSUPPORTED** (intentionally not implemented) |

Production default inactivity timeout is **15 minutes** (configurable; 1 minute valid for testing).

## Master password change (milestone after v0.2.0) — automated tested

Re-wraps the existing vault key under a new password; vault key not rotated, content not
re-encrypted, format stays v1. See [CRYPTOGRAPHY.md](CRYPTOGRAPHY.md) — "Master password change".

| Item | Status |
|---|---|
| Re-wrap keeps same vault key + content; new password unlocks; old fails; fresh salt/nonce; wrong current password throws | AUTOMATED TESTED (`VaultCryptorTests`) |
| Service flow: reopen with new password, requires unlocked + current password, rejects new==old, write-failure rollback | AUTOMATED TESTED (`VaultServiceTests`) |
| Wrong current password leaves the vault file **byte-for-byte unchanged**; content ciphertext not re-encrypted end-to-end | AUTOMATED TESTED (`VaultPersistenceIntegrationTests`) |
| Settings "Change master password" GUI (form, success/failure messages, re-unlock) | **NOT VERIFIED** (needs manual run) |

## Encrypted backup / restore (milestone after v0.3.0) — automated tested

Byte-for-byte export of the encrypted envelope; restore authenticates + validates before an atomic,
recovery-copy-protected replace. Vault format v1 unchanged; no new crypto. See
[BACKUP_RESTORE.md](BACKUP_RESTORE.md).

| Item | Status |
|---|---|
| Byte-for-byte export; active vault untouched; no plaintext markers; export requires unlocked | AUTOMATED TESTED (`VaultBackupServiceTests`) |
| Restore replace + recovery copy; unlock with backup password | AUTOMATED TESTED |
| Wrong password / corrupted / unsupported version / malicious KDF / oversized / malformed → fail closed, live vault byte-for-byte unchanged | AUTOMATED TESTED |
| Duplicate JSON property rejected (strict parse) | AUTOMATED TESTED (`EncryptedVaultSerializerTests`) |
| Pre/post master-password-change backup passwords; foreign-vault replace-only (no merge) | AUTOMATED TESTED |
| Destructive-confirmation hardening: acknowledgement + **commit-side guard** state machine | AUTOMATED TESTED (`RestoreConfirmationStateTests`) |
| Real ViewModel commit boundary: destructive commit reached only with a pending preview AND acknowledgement; blocked on no-pending / not-acknowledged / stale (file or password change) / failed commit / failed validation / cancel | AUTOMATED TESTED (`SettingsPageViewModelRestoreTests`, `BitosX.App.Tests`) |
| Focus/keyboard (Cancel focused, Enter/Space don't restore, Esc cancels) + end-to-end GUI (file picker, confirmation, restart, recovery copy) | **MANUAL VERIFIED** 2026-09-13 (H1–H6, see below) |
| Linux/macOS file picker & behavior | **NOT VERIFIED** |

### Manual restore-hardening verification — H1–H6 (2026-09-13, Windows 11, real Avalonia UI)

Method: each destructive-input attempt was made on the live app, then the encrypted vault file on disk
was SHA-256 hashed to prove whether a restore commit occurred. Baseline live vault `c9d2bffb…`; a valid
backup preview was reached without ever writing the vault (validation is read-only).

| # | Scenario | Expected | Result |
|---|---|---|---|
| H1 | Enter pressed once at the preview (no default button) | No commit | **PASS** — live vault unchanged, no `.tmp` |
| H2 | Space pressed once at the preview (ack unchecked, Replace disabled) | No commit | **PASS** — live vault unchanged; Space activated the focused Cancel, closing the panel without writing |
| H3 | Preview shows acknowledgement unchecked + "Replace my vault" disabled | Correct initial state | **PASS** (focus indicator not clearly observed) |
| H4 | Checking acknowledgement enables Replace; commit only on explicit click | Commit only when clicked | **PASS** — enabling the button wrote nothing; commit occurred only on the explicit "Replace my vault" click |
| H5 (Esc) | Esc at the preview cancels; the cancelled preview cannot commit | Cancel, no commit | **PASS** — panel closed, live vault unchanged; `_pendingRestore`/acknowledgement cleared |
| H5 (Cancel) | Cancel button clears state; acknowledgement not retained on re-entry | Reset, no commit | **PASS** — after Cancel and re-validate, checkbox unchecked and Replace disabled |
| H6 | Changing the backup password or file after validate clears the pending restore | Pending cleared | **PASS** |

No inadvertent restore commit occurred in any scenario. The destructive path requires a pending
authenticated+validated preview **and** the acknowledgement, plus an explicit click; the commit-side
guard (`if (_pendingRestore is null || !RestoreAcknowledged) return;`) fails closed independently.

## Version display fix (2026-09-13)

The Settings → About line previously read "BitosX version 0.3.0.0" — the CLR/Windows 4-part
`AssemblyName.Version`, whose trailing `.0` revision is a platform artefact, not a real release
component. It now shows the 3-part SemVer product version (it tracks `<Version>` — e.g. "BitosX
version 0.4.0" for this release; manually verified reading exactly "BitosX version 0.3.0" at the time
of the fix, before the 0.4.0 bump).

- New helper `AppVersionInfo` (`src/BitosX.App/AppVersionInfo.cs`) derives the string from the
  assembly's **informational (product) version** (driven by `<Version>` in the build) and strips the
  SemVer `+build` metadata (the git commit the SDK appends). It falls back to the first three parts of
  the numeric assembly version only when no informational version is present. The value is not
  hardcoded.
- `SettingsPageViewModel.AppVersion` now uses `AppVersionInfo.Display`.
- Regression tests: `AppVersionInfoTests` (`BitosX.App.Tests`) assert build-metadata is stripped, the
  informational version is preferred, the CLR revision is dropped on fallback, and the live `Display`
  value is always a 3-part `MAJOR.MINOR.PATCH` with no `+` metadata.

## Clipboard auto-clear defect (found in manual GUI verification, 2026-09-13 — FIXED)

Manual step 11 revealed that after copying a value, the clipboard was **not** cleared when the
timeout elapsed — a copied username/password stayed on the clipboard indefinitely, defeating the
timed-clear protection. The automated tests missed it because they use an in-memory fake clipboard.

Root cause: **Avalonia clipboard access must run on the UI thread.** The initial copy runs from a UI
command (so it worked), but the delayed clear ran on a thread-pool thread; the clipboard read there
returned nothing, the "is it unchanged?" guard failed, and the clear was skipped entirely.

Fix (App/Platform layers only — no crypto/vault changes):
- `AvaloniaClipboardAccessor` now marshals every clipboard operation onto the UI thread
  (`Dispatcher.UIThread`), so reads/clears from the timer work.
- Clearing now uses the platform `IClipboard.ClearAsync()` instead of setting an empty string,
  which does not reliably clear the clipboard on Windows. (`IClipboardAccessor.ClearAsync` added.)
- Regression test added: `ClipboardServiceTests.Clear_UsesPlatformClearOperation_NotEmptyStringSet`.
  Note: the UI-thread aspect cannot be unit-tested with the fake accessor; it is covered by the
  manual re-verification (step 11 re-tested and passed).

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

## Manual GUI checklist — COMPLETED 2026-09-13 (all 17 steps PASS)

Run the app with `dotnet run --project src/BitosX.App`. Perform each step and confirm the expected
result. **All 17 steps were performed and passed on 2026-09-13** on Windows 11 / .NET 10.0.12
(step 11 passed after the clipboard fix above). Re-run this checklist for future releases.

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

All steps passed; on-disk checks corroborated the visual results (vault created at 888 bytes with no
plaintext leak; the added login persisted with none of its field values in cleartext; the deleted
item's data was gone from the file after save/restart). The status-matrix entry for end-to-end GUI
behavior is now `MANUALLY VERIFIED (2026-09-13)`.
