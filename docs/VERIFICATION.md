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
| **AUTOMATED ON THE REAL APPLICATION** | A program started the real application as a process of its own, in a real window, and operated it through the accessibility interface of the operating system (since 0.7.0). Nobody looked at the window. |
| **NOT VERIFIED** | Not confirmed by any of the above. |

> The manual GUI click-through (all 17 steps) was performed on **2026-09-13** on a
> Windows 11 / .NET 10.0.12 machine, alongside the build, automated tests, and KDF benchmark.
> It surfaced a real clipboard-clear defect that automated tests missed; the defect was fixed and
> the affected steps re-verified (see "Clipboard auto-clear defect" below).
>
> Nobody has gone through it by hand since 0.5.0. Since 0.7.0 programs do, for every change, on
> Windows, Linux and macOS; what that does and does not amount to is said in the section on 0.7.0.

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
| Atomic write preserves prior vault on failure, leaves nothing behind | AUTOMATED TESTED | `FileLocalVaultStorageTests` (byte-for-byte comparison; directory listing). |
| Oversized vault file rejected before large allocation | AUTOMATED TESTED | `FileLocalVaultStorageTests.Read_OversizedFile_FailsClosed`. |
| Vault item field-length / count / URL bounds | AUTOMATED TESTED | `VaultValidationTests` (on add/update), `VaultLoadValidationTests` (on load, since 0.5.1). |
| Clipboard copy only on explicit action | VERIFIED | `VaultPageViewModel` copy commands call `ClipboardService`. |
| Clipboard auto-clear after timeout | AUTOMATED TESTED + MANUALLY VERIFIED (2026-09-13) + VERIFIED AGAINST THE REAL CLIPBOARD (2026-09-29) | `ClipboardServiceTests.AutoClear_AfterDelay_ClearsClipboard`; `tools/BitosX.ClipboardProbe`. |
| Clipboard does not erase content the user copied afterward | AUTOMATED TESTED + MANUALLY VERIFIED (2026-09-13) | `ClipboardServiceTests.AutoClear_DoesNotEraseContentTheUserReplaced`; confirmed live. |
| Repeated copies / dispose / accessor failure handled safely | AUTOMATED TESTED | `ClipboardServiceTests`. |
| Lock removes decrypted state; no navigation back without re-auth | VERIFIED + AUTOMATED TESTED + MANUALLY VERIFIED (2026-09-13) | State-driven shell (`MainWindowViewModel`); `VaultServiceTests` locking tests; GUI steps 13–15. |
| Argon2id unlock latency | VERIFIED | `tools/BitosX.KdfBenchmark`, on four machines since 0.7.0; see the section on 0.7.0. |
| **End-to-end GUI behavior (create/unlock/edit/copy/lock/persist)** | **MANUALLY VERIFIED (2026-09-13)** + AUTOMATED ON THE REAL APPLICATION (since 0.7.0) | All 17 checklist steps passed by hand for 0.1.0; corroborated by on-disk checks (vault created, no plaintext leak, add/edit/delete persisted). Since 0.7.0 a program carries them out on the real application for every change. |
| Linux and macOS | AUTOMATED TESTED (since 0.7.0) | All tests run there for every change. The application itself has not been started there. |
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

## Vault key rotation (milestone after v0.4.0) — automated + manual verified

New random vault key with full content re-encryption; the master password and on-disk format (v1) are
unchanged. Guarded by an on-disk/in-memory consistency fingerprint, a cancellation boundary, and a
write-failure reconciliation. See [KEY_ROTATION.md](KEY_ROTATION.md).

| Item | Status |
|---|---|
| New vault key; content re-encrypted; old key can no longer decrypt; format/KDF/params preserved; wrong password + tamper fail closed | AUTOMATED TESTED (`VaultKeyRotationTests`) |
| Envelope fingerprint detects different-key **and** same-key content change | AUTOMATED TESTED (`EncryptedVaultFingerprintTests`) |
| Service: unlocked+password required; stale/external change fails closed; cancellation boundary; write-failure reconciliation (OLD/OLD, NEW/NEW, UNKNOWN→LOCKED); byte-for-byte-unchanged on every pre-commit failure | AUTOMATED TESTED (`VaultServiceRotationConsistencyTests`, `VaultServiceTests`) |
| ViewModel: success/failure messaging incl. stale-vault and indeterminate-state safe messages | AUTOMATED TESTED (`SettingsPageViewModelRotationTests`) |
| End-to-end GUI (real file storage, restart durability, historical backup) | **MANUAL VERIFIED** 2026-09-14 (R-series, see below) |
| Linux/macOS GUI | **NOT VERIFIED** |

### Manual rotation verification (2026-09-14, Windows 11, real Avalonia UI)

Method: each step was performed in the live app, then the encrypted vault file on disk was SHA-256
hashed and its public envelope fields compared, to prove exactly what changed. No decrypted secrets or
key material were exposed. Synthetic credentials/items only. Pre-rotation baseline live vault
`8e92f216…` (2 items); a byte-identical pre-rotation backup was preserved throughout.

| Scenario | Expected | Result |
|---|---|---|
| Wrong current password | Rejected; disk byte-for-byte unchanged | **PASS** — `8e92f216…` unchanged, no `.tmp` |
| Valid rotation | File hash + salt + key-wrap nonce + wrapped key + content nonce + ciphertext all change; version stays 1; Argon2 params unchanged | **PASS** — all changed; v1; decoded ciphertext length identical (same 2-item plaintext). (File shrank 40 B purely from JSON `+` escaping of `+` in base64 — 8 fewer `+` chars × 5 B — not a data change.) |
| Same-password lock → unlock | Unlocks under the new key with the same password; 2 items intact; no migration prompt | **PASS** — authenticated unlock is the plaintext-integrity proof |
| Post-rotation save | Writes under the **rotated** header/key (retired pre-rotation salt/wrapped-key do not return); no stale-session error | **PASS** — item count 3; rotated salt/wrapped-key retained |
| Full application restart | Rotated vault is durable across a process restart; reopens under the same password | **PASS** — no startup/close rewrite; 3 items intact |
| Historical pre-rotation backup validate | Old backup still validates with the same password; shows the older 2-item generation; live vault untouched; distinct generation | **PASS** — rotation is not retroactive |
| Locked-state rotation availability | Rotate not reachable/executable while locked | **PASS** — locked UI exposes only the Unlock screen; nothing wrote |
| Abandon/cancel rotation | Entering the password and abandoning (navigating away) commits nothing; field not retained | **PASS** — no write; field empty on return |

Every pre-commit failure left the live vault byte-for-byte unchanged, and rotation only ever committed
on the explicit "Rotate vault key" click.

## Security hardening (0.5.1) — verified 2026-09-29

The findings, their evidence and the fixes are described in
[SECURITY_REVIEW_0.5.1.md](SECURITY_REVIEW_0.5.1.md). This section records how each was verified.
Machine: Windows 11 Pro (10.0.26200), .NET SDK 10.0.401.

### Automated

468 tests pass in Debug and in Release (217 before the review), with 0 build warnings.

| Project | Before | After |
|---|---|---|
| `BitosX.Core.Tests` | 96 | 208 |
| `BitosX.Crypto.Tests` | 60 | 113 |
| `BitosX.Storage.Tests` | 29 | 52 |
| `BitosX.Platform.Tests` | 12 | 35 |
| `BitosX.App.Tests` | 20 | 60 |

**The new tests were run against the 0.5.0 code as well**, in a separate checkout, to confirm that
they detect the defects rather than merely pass. Of the 112 tests added to `BitosX.Core.Tests`,
**77 fail against 0.5.0** and pass against 0.5.1; the other 35 are invariants that already held and
are kept as regression guards. (The 0.5.0 service was given three one-line members so that the
tests compile; its behaviour was not touched. Tests in the other projects exercise components that
do not exist in 0.5.0.)

| Item | Status |
|---|---|
| A stale session cannot overwrite the file: save, password change, rotation; recovery by lock + reopen; missing file is written back; unreadable file is left alone | AUTOMATED TESTED (`VaultServiceSessionConsistencyTests`) |
| A write that throws is reconciled (old / new / indeterminate → locked), for all three write paths | AUTOMATED TESTED (`VaultServiceSessionConsistencyTests`, `VaultServiceRotationConsistencyTests`) |
| `Items` never shows unpersisted state; a rejected change writes nothing | AUTOMATED TESTED (`VaultServiceSessionConsistencyTests`) |
| A failing subscriber cannot leave the service open around a destroyed key; all subscribers are notified | AUTOMATED TESTED (`VaultServiceLockingGuaranteeTests`) |
| `Lock()` during rotation, password change, save and unlock leaves no key in memory and a vault that opens | AUTOMATED TESTED (`VaultServiceLockingGuaranteeTests`) |
| Overlapping operations run one at a time | AUTOMATED TESTED (`VaultServiceLockingGuaranteeTests`) |
| Decrypted content validated on load: 33 kinds of invalid document, each fails closed as a validation error that never quotes the content | AUTOMATED TESTED (`VaultLoadValidationTests`) |
| Clock moved back: item stays editable; affected vaults and backups still open | AUTOMATED TESTED (`VaultTests`, `VaultLoadValidationTests`, `VaultBackupServiceTests`) |
| Envelope parsing: `null` and missing fields, wrong lengths, unknown properties, invalid UTF-8, byte order mark | AUTOMATED TESTED (`EncryptedVaultSerializerTests`, `FileLocalVaultStorageTests`) |
| Temporary files: unique, exclusive, nothing left behind, stale ones removed, a planted file untouched | AUTOMATED TESTED (`FileLocalVaultStorageTests`, `VaultBackupServiceTests`, `FileAppSettingsStoreTests`) |
| Primitives match published reference outputs; associated data of format v1 pinned | AUTOMATED TESTED (`KnownAnswerTests`) |
| Strength rating: 16 patterned passwords never better than Weak; 500 generated passwords never worse than Strong | AUTOMATED TESTED (`PasswordStrengthTests`) |
| Clipboard: retry when busy; cleared on workstation lock and suspend; user content never erased | AUTOMATED TESTED (`ClipboardHardeningTests`, fake clipboard and fake OS signals) |
| Content handed to the clipboard carries the markers | AUTOMATED TESTED (`SensitiveClipboardDataTests`) |
| Single instance per vault | AUTOMATED TESTED (`SingleInstanceGuardTests`) |
| Commands report failures instead of letting them escape | AUTOMATED TESTED (`ViewModelFailureHandlingTests`) |
| Settings describes the open vault; new master password is rated | AUTOMATED TESTED (`ViewModelFailureHandlingTests`) |
| Unix file permissions (owner only) | **NOT VERIFIED** — the test is skipped on Windows and no Unix machine was available |

### Against the real Windows clipboard

Unit tests use an in-memory clipboard, and the one clipboard defect this project has had was
invisible to them. `tools/BitosX.ClipboardProbe` therefore drives the **production** classes
(`AvaloniaClipboardAccessor`, `ClipboardService`, `ExitClipboardCleaner`) against the real clipboard
and the real clipboard history, and reads the result back the way any other application would
(Win32). It uses marker strings only, puts back the text that was on the clipboard, and removes its
markers from the history. Run it with:

```
dotnet run -c Release --project tools/BitosX.ClipboardProbe
```

Result on 2026-09-29, clipboard history **enabled**, cloud clipboard not enabled — all 16 checks
passed:

| Check | Result |
|---|---|
| A copied secret can be pasted by other applications | PASS |
| BitosX can read its own copy back (needed for the "unchanged" guard) | PASS |
| The clipboard carries each of the four markers | PASS (4) |
| Clearing removes everything BitosX placed on the clipboard | PASS |
| **Control:** an unmarked copy **is** recorded in the clipboard history | PASS |
| A copied secret is **not** recorded in the clipboard history | PASS |
| The secret is on the clipboard right after copying; the timed clear removes it | PASS (2) |
| The secret was pasted once before closing | PASS |
| **Control:** without the exit cleaner a pasted secret survives the application closing | PASS |
| With the exit cleaner the secret is gone once the application has closed, and the clipboard is empty | PASS (2) |
| Closing does not erase what the user copied after the secret | PASS |

The two controls are what make the result meaningful: they show that the history check and the
exit check can fail, and do fail for the 0.5.0 behaviour.

One observation worth keeping: Windows hands out clipboard content on request, and only content
that has been requested at least once stays behind when its owner exits. A secret that was copied
but never pasted, and that no clipboard listener read, disappears with the application even without
the cleaner. In normal use the secret *has* been pasted, which is the case the cleaner is for.

Not verified: that Windows also keeps the value out of the **cloud** clipboard (it was not enabled
on the machine; the marker is set and documented by Microsoft), and the behaviour of any third-party
clipboard manager.

### With two real processes

Release build of the application, started twice against the default vault location:

| Step | Result |
|---|---|
| Second instance started while the first is running | Exits by itself after 86 ms with exit code 2; one process remains |
| First instance closed the way a user would (window close) | Exits normally, exit code 0 |
| A new instance started afterwards | Starts and stays |
| Vault file (SHA-256 and modification time) and its directory | Unchanged; no temporary file left |

This also shows that the application starts and shuts down cleanly with the changed composition
(disposable vault service, clipboard guard, exit cleaner).

### NOT VERIFIED — needs a person at the GUI

| Item | Why |
|---|---|
| The password rating under "New password", the key-derivation note, and the "could not be saved" notice in Settings | New bindings; the view models are tested, the rendered page has not been looked at |
| Clearing the clipboard on workstation lock (Win+L) and on suspend **in the running application** | The policy is tested with fake OS signals; the real signals were verified for auto-lock (A6, A7) but not for this consumer |
| Closing the real application window shortly after copying | The same `ExitClipboardCleaner` class was verified through the probe, in a window of its own |
| The message shown when a save is refused because the vault changed on disk | Needs two instances, which the application now prevents; covered by `ViewModelFailureHandlingTests` |
| The full 17-step checklist below, and the A-, H- and R-series | **Not re-run for 0.5.1.** Re-run before tagging a release |
| Everything on Linux and macOS | No machine available |

Since 0.6.0 the first, third and fourth of these are covered by tests that run the real views
without a display — to the extent described in the next section. None has been looked at by a
person. Since 0.7.0 the second, the fifth and the sixth are covered as far as a program can cover
them; see the section on 0.7.0.

## Master password encoding, key derivation, clipboard markers (0.6.0) — verified 2026-09-29

Three things that [SECURITY_REVIEW_0.5.1.md](SECURITY_REVIEW_0.5.1.md) left open. The design of
each is described in [MASTER_PASSWORD.md](MASTER_PASSWORD.md), [KEY_DERIVATION.md](KEY_DERIVATION.md)
and `SensitiveClipboardData`. Machine: Windows 11 Pro (10.0.26200), .NET SDK 10.0.401, 16 logical
CPUs, 64 GiB. The on-disk format is unchanged (v1).

### Automated

684 tests pass in Debug and in Release (468 in 0.5.1), with 0 build warnings.

| Project | 0.5.1 | 0.6.0 |
|---|---|---|
| `BitosX.Core.Tests` | 208 | 318 |
| `BitosX.Crypto.Tests` | 113 | 142 |
| `BitosX.App.Tests` | 60 | 133 |
| `BitosX.Storage.Tests` | 52 | 56 |
| `BitosX.Platform.Tests` | 35 | 35 |

| Item | Status |
|---|---|
| The same password has one encoding however it is typed; nothing else is changed (no trimming, case folding or compatibility mapping); different passwords stay different; invalid Unicode is rejected for a new password | AUTOMATED TESTED (`MasterPasswordEncodingTests`) |
| Encodings offered for an existing vault: canonical first, never twice, one for ASCII | AUTOMATED TESTED (`MasterPasswordEncodingTests`) |
| A new vault opens for either form; vaults keyed by earlier versions keep opening; opening never rewrites the vault | AUTOMATED TESTED (`VaultServicePasswordEncodingTests`) |
| What a failed attempt costs: one derivation for ASCII, one per encoding otherwise | AUTOMATED TESTED (counting key derivation) |
| A vault moves to the canonical encoding on password change, rotation and strengthening; a wrong password is rejected and nothing is written | AUTOMATED TESTED (`VaultServicePasswordEncodingTests`) |
| Backups from earlier versions restore however they were keyed | AUTOMATED TESTED (`VaultBackupServiceTests`) |
| Strengthening: same key and content, new wrapping; stronger only, in both dimensions; within the bounds; the settings are bound to the wrapped key; not retroactive | AUTOMATED TESTED (`KeyDerivationChangeTests`) |
| Strengthening through the service: wrong password, locked, stale session, cancellation, write-failure reconciliation, lock during the operation, a device without the memory | AUTOMATED TESTED (`VaultServiceKeyDerivationTests`) |
| Settings view model: what is offered, measuring, ten kinds of failure each reported without claiming success, the entered password not kept on success | AUTOMATED TESTED (`SettingsPageViewModelKeyDerivationTests`) |
| Clipboard content per platform; the markers never contain the secret | AUTOMATED TESTED (`SensitiveClipboardDataTests`) |
| A clipboard that refuses the marker: plain copy on macOS / Linux, failure on Windows | AUTOMATED TESTED (`AvaloniaClipboardAccessorTests`) |

### The views, run without a display

New in 0.6.0: `SettingsPageUiTests` and `VaultFlowUiTests` load the application's **real views**
— the same XAML, styles, resources and view locator — on the headless platform of the UI
framework, on top of the real vault service, cryptography and file storage in a temporary
directory. They type into the text boxes and activate buttons and check boxes from the keyboard,
and read what the text blocks show. 22 tests.

| Item | Status |
|---|---|
| Settings describes the open vault, warns when its settings are weaker than the default, and offers only stronger profiles | AUTOMATED TESTED (headless view) |
| Strengthening from the view: button disabled until a password is typed; wrong password reported and nothing written; success updates the page, empties the password box, and the file on disk has the new settings | AUTOMATED TESTED (headless view) |
| Measuring from the view shows time and memory and changes nothing; a device without the memory is reported | AUTOMATED TESTED (headless view) |
| A vault keyed the old way is pointed out until it has been moved | AUTOMATED TESTED (headless view) |
| The new password is rated as it is typed; changing the master password from the view works | AUTOMATED TESTED (headless view) |
| A setting that cannot be saved is reported on the page | AUTOMATED TESTED (headless view) |
| Creating a vault; unlocking with each form of the password; wrong password; a device without the memory | AUTOMATED TESTED (headless view) |
| Saving from the editor; a save refused because the vault changed on disk says what to do | AUTOMATED TESTED (headless view) |

**These tests can fail:** with the binding of one text block deliberately pointed at the wrong
property, two of them failed; with it restored, all pass.

**What this is and is not.** It verifies what the views show and do: bindings, visibility,
enabled state, commands, keyboard activation. It does not verify how they look — layout, wrapping,
colours, whether a text is cut off — nor anything that depends on a real window on a real screen.
Several items that were "NOT VERIFIED — needs a person at the GUI" in 0.5.1 are covered by it to
that extent; none of them has been looked at by a person.

### Against 0.5.1

The same test, using only what 0.5.1 has, run on a checkout of the tag `v0.5.1-security-hardening`
and on this version:

| Vault created with | Unlocked with | 0.5.1 | 0.6.0 |
|---|---|---|---|
| composed | decomposed | **fails** | opens |
| decomposed | composed | **fails** | opens |
| no-break spaces | ordinary spaces | **fails** | opens |
| ordinary spaces | no-break spaces | **fails** | opens |
| composed (control) | composed | opens | opens |

### A device without the memory

The failure path cannot be reached in a unit test without actually running out of memory, so it
was checked with a real process whose heap was limited to 400 MiB
(`DOTNET_GCHeapHardLimit=0x19000000`):

| Settings | Result |
|---|---|
| 64 MiB, 128 MiB | derived normally |
| 256 MiB, 512 MiB, 1 GiB | `KeyDerivationResourceException`, naming the memory needed; the process carried on and later derivations in it succeeded |

Reproduce with `tools/BitosX.KdfBenchmark`, which now takes a profile:

```
DOTNET_GCHeapHardLimit=0x19000000 dotnet run -c Release --project tools/BitosX.KdfBenchmark -- 3 maximum
```

It prints that the device does not have the memory and exits with code 3.

Measured without a limit: Standard about 0.05 s, Strong about 0.2 s, Maximum about 0.85 s per
derivation.

### Against the real Windows clipboard

`tools/BitosX.ClipboardProbe` was run again, since the code that places a secret on the clipboard
changed. All 18 checks passed (the 16 of 0.5.1 and two new ones):

| New check | Result |
|---|---|
| Mechanism only: with the macOS marker the secret is placed, carries `org.nspasteboard.ConcealedType`, and can be pasted | PASS |
| Mechanism only: with the Linux marker the secret is placed, carries `x-kde-passwordManagerHint`, and can be pasted | PASS |

"Mechanism only" is meant literally. These two show that the UI framework accepts the content
and that the text stays pasteable, on Windows. They say nothing about what macOS or a Linux
desktop does with the marker.

### The application itself

Release build, started against the default vault location: it starts and shows its window; a
second instance exits by itself with code 2; the first closes normally with code 0. The vault file
and the settings file were unchanged (SHA-256), and no temporary file was left.

### NOT VERIFIED

| Item | Why |
|---|---|
| **Everything on macOS and Linux**, including whether the clipboard markers have any effect there | No such machine. Virtualization is disabled on the development machine, so neither WSL 2 nor Docker could be used. |
| Unix file permissions (owner only), open since 0.5.1 | The same |
| How the views look | The headless tests check what is shown, not how |
| The 17-step manual checklist and the A-, H- and R-series | **Not re-run for 0.6.0**, nor for 0.5.1. Re-run before tagging a release |
| Clearing the clipboard on workstation lock and suspend in the running application | Tested with simulated OS signals only |
| The cloud clipboard, and third-party clipboard managers | Not enabled / not installed on the development machine |
| Normalization with a different Unicode version than the one of this runtime and operating system | One machine |

## Verifying what was not verified (0.7.0) — 2026-09-30

0.6.0 ended with a list of what had not been verified. This release is what came of going through
that list. The findings are in [SECURITY_REVIEW_0.7.0.md](SECURITY_REVIEW_0.7.0.md); this section
records what was verified, where, and how. The on-disk format is unchanged (v1).

| Machine | System | Logical CPUs | Memory |
|---|---|---|---|
| Development machine | Windows 11 Pro, .NET SDK 10.0.401 | 16 | 64 GiB |
| Build service | Ubuntu 24.04.5 LTS, .NET SDK 10.0.401 | 2 | 8 GiB |
| Build service | macOS 15.7.9, .NET SDK 10.0.401 | 5 | 14 GiB |
| Build service | Windows Server 2025, .NET SDK 10.0.401 | 2 | 16 GiB |

The build service is GitHub Actions (`.github/workflows/ci.yml`). Its machines are created for a
run and discarded after it; nobody sits at them.

### Automated, on three systems

819 tests (684 in 0.6.0), in Debug and in Release, with 0 build warnings (warnings are errors on
the build service).

| Project | 0.6.0 | 0.7.0 |
|---|---|---|
| `BitosX.Core.Tests` | 318 | 333 |
| `BitosX.Crypto.Tests` | 142 | 152 |
| `BitosX.App.Tests` | 133 | 232 |
| `BitosX.Storage.Tests` | 56 | 67 |
| `BitosX.Platform.Tests` | 35 | 35 |

| System | Passed | Skipped |
|---|---|---|
| Windows 11 (development machine) | 811 | 8 — what only Unix has |
| Windows Server 2025 | 811 | 8 — what only Unix has |
| Ubuntu 24.04 | 818 | 1 — what only Windows has |
| macOS 15 | 818 | 1 — what only Windows has |

A test of something one kind of system does not have is reported as skipped there. Until 0.7.0
such a test returned at once and was reported as passed.

| Item | Status |
|---|---|
| Everything the 684 tests of 0.6.0 cover, on Linux and macOS | AUTOMATED TESTED — all of them passed there the first time they were run |
| The vault, the settings, a backup and the recovery copy are readable by their owner only, and the directory can be entered by its owner only (Unix) | AUTOMATED TESTED on Linux and macOS (`UnixFilesTests`), with a control that an ordinary new file is *not* owner-only |
| The directory is flushed after a file in it was replaced (Unix) | AUTOMATED TESTED on Linux and macOS: the flush succeeds, also for a name that is not ASCII. **Not** tested: that it makes the rename survive a power loss |
| The master password is encoded to the same bytes by the Unicode library of each system | AUTOMATED TESTED on all three (`MasterPasswordEncodingTests`, known answers) |
| The single-instance guard, atomic replacement and temporary files behave the same | AUTOMATED TESTED on all three |
| An empty password is a wrong password when opening, refused when protecting, at every layer | AUTOMATED TESTED (`EmptyPasswordTests`, `VaultServiceEmptyPasswordTests`, `VaultBackupServiceTests`, `ChecklistTests`) |
| What a new vault is created with, and what it falls back to on a device without the memory | AUTOMATED TESTED (`VaultServiceCreationSettingsTests`) |
| A password field tells the accessibility interface what it shows, never the hidden text; every field that shows dots is such a field | AUTOMATED TESTED (`SecretBoxTests`), with a control that an ordinary text box *does* hand out its text |
| Every input on every screen has a name for a screen reader | AUTOMATED TESTED (`AccessibilityTests`) |
| `--data-directory`: what it accepts, and that what it cannot use is refused rather than ignored | AUTOMATED TESTED (`AppLocationTests`) |
| A fault that reaches the user interface locks the vault, clears the clipboard, tells the user, and shows and logs nothing of what the fault says | AUTOMATED TESTED (`UnexpectedErrorGuardTests`, `ChecklistTests`) |
| The generator shows a password that belongs to its settings | AUTOMATED TESTED (`GeneratorPageViewModelTests`) |

### The whole application, without a display

`ChecklistTests` (27 tests) puts the application together the way the application does — the
same services, main window, cryptography with the same settings, and file storage, in a data
directory of its own — and carries out the manual checklists of this document on it. Replaced are
the things that reach outside the process: the session signals of the operating system, the clock
that automatic locking reads, the file dialogs, and how long a copied value stays on the
clipboard. It runs on all three systems.

| Checklist | Status |
|---|---|
| The 17 steps | AUTOMATED TESTED, in the order of the checklist, in one run |
| A1–A8 (automatic locking) | AUTOMATED TESTED. A5 and A6 with the session signal raised by the test |
| H1–H6 (the confirmation before a restore) | AUTOMATED TESTED, with the keys pressed in the window and the file compared byte for byte after each |
| The rotation scenarios | AUTOMATED TESTED, with the envelope of the vault file read after each step |
| Changing the master password from Settings (was "needs manual run") | AUTOMATED TESTED |
| After lock and unlock, Settings shows and saves what applies | AUTOMATED TESTED |
| A pointer that rests does not keep the vault open; one that moves does | AUTOMATED TESTED |
| Closing the window clears a secret that was just copied; locking the vault by hand does not | AUTOMATED TESTED |

### The real application, in a real window

`tools/BitosX.GuiCheck` starts the application as a process of its own on a data directory of its
own and operates it through UI Automation, the accessibility interface of Windows: it enters
text, activates buttons and check boxes, and reads what the window shows. Keys, mouse movement
and the session messages of Windows are posted to the window of the application — never to
whatever window is in front. The clipboard is read the way another application reads it, the
vault file is compared byte for byte, and its envelope is read after each step. The real file
dialogs of Windows are answered the same way.

On Windows Server 2025, on the build service, for every change. For the change that is released:
**all 118 checks passed, none left out.**

| Part | Checks | Status |
|---|---|---|
| Starting: an option that cannot be used ends the application with code 64 and writes nothing; the window title names the data directory; a second instance on the same directory ends by itself with code 2, one on another directory runs next to it | 11 | AUTOMATED ON THE REAL APPLICATION |
| The 17 steps, with the real clipboard and its thirty seconds, and with what is on disk read after each | 35 | AUTOMATED ON THE REAL APPLICATION |
| What the password fields tell other programs: dots, read from another process | 5 | AUTOMATED ON THE REAL APPLICATION |
| A1–A8. A1 and A2 in real time, with the timeout at one minute: the vault stays open for a minute and a half while the mouse is moved in the window, and locks between 55 and 85 seconds after it was last touched | 13 | AUTOMATED ON THE REAL APPLICATION. A5 and A6 with the message Windows sends, not with a locked workstation |
| Changing the master password | 8 | AUTOMATED ON THE REAL APPLICATION |
| The rotation scenarios, with a backup exported and chosen through the file dialogs of Windows | 12 | AUTOMATED ON THE REAL APPLICATION |
| Strengthening the key derivation, from 256 MiB to 1 GiB | 9 | AUTOMATED ON THE REAL APPLICATION |
| H1–H6, with the keys Enter, Space and Escape sent to the window | 16 | AUTOMATED ON THE REAL APPLICATION |
| A device without the memory: the application with its memory limited to 200 MiB creates the vault with 64 MiB, says so, and opens it again | 7 | AUTOMATED ON THE REAL APPLICATION |
| Afterwards: nothing of the run is left | 2 | — |

On the development machine (Windows 11) the tool was run in full once, on an earlier state of
this release: 108 of 109 checks passed. The one that did not was the tool's mistake, not the
application's — somebody was sitting at that machine and moved the mouse over the window, which
the application rightly counted as activity (see "A mistake of this review" in
[SECURITY_REVIEW_0.7.0.md](SECURITY_REVIEW_0.7.0.md)). A shorter run before it is what found that Settings
put settings back after a lock and unlock. It was not repeated there for the final state.

What this is and is not. It is the real application, its real window, the real clipboard and the
real file dialogs. It is not a person: nobody looked at the window. The workstation was not
locked and the system not put to sleep; the application was sent the messages Windows sends for
them. And it is Windows only: the application has not been started on Linux or macOS.

### How the views look

`ViewAppearanceTests` lays out and draws every screen with the renderer and the font of the
application, at the size the window opens with (1000 × 680) and at the smallest it can be given
(760 × 540): creating a vault, unlocking, the vault empty and with items, the editor, the question
before deleting, the generator, the Security Center, Settings with every message it can show at
once, and the notice after an unexpected error. 30 images per system.

| Item | Status |
|---|---|
| No text is cut off; nothing is outside what is shown unless it can be scrolled to; a shortened text can be read in full on hover | AUTOMATED TESTED, on all three systems |
| The images look right | Looked at — by the assistant that made the changes, not by a person. The images of the development machine, and a sample of those of Linux and macOS |
| A real window on a real screen: its title bar, another display scaling | **NOT VERIFIED** |

Looking at the images found what the tests of 0.6.0 could not: a vault list unreadable in a
narrow window, an editor taller than the smallest window, and a generator showing a password that
did not belong to its settings.

### Against 0.6.0

Each finding as a test that states how the application ought to behave, written with only what
0.6.0 has, and run on a checkout of the tag `v0.6.0-password-key-derivation`:

| What the test states | 0.6.0 | 0.7.0 |
|---|---|---|
| An empty password is a wrong password: unlock, change password, rotate, strengthen (11 tests at two layers) | **fails** — the exception of the library | passes |
| Rotating with nothing typed is reported, not thrown | **fails** — the exception leaves the command | passes |
| The Settings page built after unlocking shows what applies, and keeps it | **fails** | passes |
| Changing the length in the generator makes a password of that length | **fails** — 20 characters for a length of 128 | passes |
| A typed master password is not handed out through the accessibility interface | **fails** — handed out in full | passes |
| The vault page is laid out with nothing cut off, at two widths | **fails** — a name given 35 of the 480 pixels it needs | passes |
| The editor fits the smallest window when it has an error to show | **fails** — Save and Cancel below the edge | passes |
| One command does not delete an item | **fails** | passes |
| A rating is written in words | **fails** — "VeryWeak" | passes |
| The page behind the editor is out of reach | **fails** | passes |

21 of the 25 fail on 0.6.0. The four that pass there state that an empty password cannot protect
a vault, which was true before, by the library's doing.

### Taking the fixes out

For tests that use what 0.6.0 does not have, eight fixes were taken out of 0.7.0 again, one line
each, and the suite was run:

| Taken out | Tests that then fail |
|---|---|
| A pointer reported where it was is not activity | 1 |
| Clearing the clipboard when the window closes | 1 |
| Clearing the clipboard when the user leaves | 2 of the whole application, 3 of the policy |
| The fallback when a device cannot spare the memory for a new vault | 3 |
| The name of one input field | 1 |
| Locking the vault on an unexpected error | 3 |
| Attaching the guard against unexpected errors to the user interface | 1 |
| Refusing a misspelled `--data-directory` | 7 |

The third was not caught in full at first: one of the two cases of the test of the whole
application passed. Its check that a copied secret is cleared at once waited up to thirty seconds,
which is when the timer clears it anyway; and the tests of the views ran at the same time on one
clipboard, so that one test could clear what another was waiting to see cleared. Both were
corrected, and the table is what came after.

### The clipboard of Linux and macOS

`tools/BitosX.ClipboardCheck` places a secret on the real clipboard with the clipboard code of
the application, and reads the clipboard back with the tools of the system: `xclip` on Linux (an
X11 display, provided by Xvfb), the pasteboard of AppKit on macOS. All 10 checks passed on both.

| Check | Ubuntu 24.04 | macOS 15 |
|---|---|---|
| The copied text can be pasted | PASS | PASS |
| It carries the marker | PASS — `x-kde-passwordManagerHint` | PASS — `org.nspasteboard.ConcealedType` |
| The marker has the value it should have | PASS — `secret` | PASS — one byte |
| The marker does not repeat the secret | PASS | PASS |
| The clipboard accepted the marker (no fall back to an unmarked copy) | PASS | PASS |
| The application reads back what it copied | PASS | PASS |
| After its time the text is gone from the clipboard | PASS | PASS |
| What was copied by someone else afterwards is not cleared | PASS | PASS |
| An ordinary copy carries no marker (control) | PASS | PASS |
| Cleared at once when asked to | PASS | PASS |

### Clipboard managers

The marker is a request, and what a clipboard manager does with it is a fact about that clipboard
manager. `tools/ci/clipboard-managers.sh` asks two of them, on Ubuntu 24.04. For each: a text
copied *without* the marker must turn up in its history — otherwise it is not recording, and
nothing can be said — and a text copied *with* the marker, by the clipboard code of the
application, must not.

| Clipboard manager | Unmarked text | Marked text | |
|---|---|---|---|
| Klipper 5.27.12 (KDE) | recorded | **not recorded** | Honors the marker |
| CopyQ 7.1.0 | recorded | **recorded** | Does not honor the marker |

The build fails if Klipper starts recording a marked text: that would mean the marker is no
longer set the way it has to be. What CopyQ does is reported, and cannot be changed from here.

### What the key derivation costs

`tools/BitosX.KdfBenchmark`, median of five derivations, in Release. The machines of the build
service are virtual, shared, and not the same from one run to the next; the ranges are what four
runs gave.

| Machine | 64 MiB | 256 MiB | 1 GiB |
|---|---|---|---|
| Development machine (16 CPUs) | about 0.05 s | about 0.2 s | about 0.85 s |
| macOS 15 (5 CPUs) | 0.14 – 0.18 s | 0.3 – 0.5 s | 1.3 – 1.6 s |
| Ubuntu 24.04 (2 CPUs) | 0.12 – 0.41 s | 0.5 – 1.6 s | 2.0 – 6.5 s |
| Windows Server 2025 (2 CPUs) | 0.26 – 0.61 s | 1.0 – 2.5 s | 4.3 – 6.9 s |

On these figures new vaults are created with 256 MiB since this release; see
[KEY_DERIVATION.md](KEY_DERIVATION.md).

### NOT VERIFIED

| Item | Why |
|---|---|
| A person looking at a real window | The screens are drawn and checked without a display; the real window is operated by a program |
| **The application itself on Linux and macOS** | Its tests run there, including the whole application without a display, and its clipboard code in a window of a tool. The application has not been started there |
| Locking the workstation and putting the system to sleep, for real | The application is sent the messages Windows sends for them. The real signals were last verified by hand for 0.2.0 |
| The clipboard on Wayland | The Linux machine has an X11 display |
| Clipboard managers for macOS and Windows; the clipboard history of macOS; the cloud clipboard of Windows; handing the clipboard to other devices | None was available |
| That the password fields are announced as password fields by a screen reader | The user-interface framework cannot report it. What is typed may be spoken |
| That flushing the directory makes a rename survive a power loss | Only that the flush succeeds |
| The file dialogs of Linux and macOS | The application has not been started there |
| That Windows keeps a copied secret out of its clipboard history, for this release | `tools/BitosX.ClipboardProbe` ran on Windows Server 2025, where the history is turned off: its 16 other checks passed and the two of the history were left out. They were last made on a desktop with the history turned on for 0.6.0; the code they exercise is unchanged |

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
- ~~Decrypted vault content is not re-validated on load.~~ **Closed in 0.5.1**: loading validates
  every item with the same rules as editing (`VaultLoadValidationTests`).
- ~~The production KDF profile is fast on high-end hardware~~ (~46 ms median on a 16-core
  desktop for 64 MiB) **— addressed in two steps.** Since 0.6.0 an existing vault can be moved to
  stronger settings by its user, with a measurement from the device. Since 0.7.0, with the
  derivation measured on four machines instead of one, a new vault is created with 256 MiB
  ([KEY_DERIVATION.md](KEY_DERIVATION.md)). A vault created before keeps its settings until its
  user strengthens it.

## Manual GUI checklist — COMPLETED 2026-09-13 (all 17 steps PASS)

Run the app with `dotnet run --project src/BitosX.App`. Perform each step and confirm the expected
result. **All 17 steps were performed and passed on 2026-09-13** on Windows 11 / .NET 10.0.12
(step 11 passed after the clipboard fix above).

**Since 0.7.0 these steps are carried out for every change by a program** — on the whole
application without a display (`ChecklistTests`) and on the real application in a real window
(`tools/BitosX.GuiCheck`) — and no longer depend on someone finding the time. A person going
through them is still worth what it always was: it is how the clipboard defect above was found,
and a program does not notice what it was not told to look for.

> Do not use the vault at the default location for this. Start the application on a directory of
> its own: `dotnet run --project src/BitosX.App -- --data-directory <full path>`. A directory
> that does not exist yet gives a clean "first launch".

Step 16 has changed in 0.7.0: deleting is asked for and then confirmed.

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
| 16 | **Delete an item**, and confirm | Nothing is deleted until the question is answered; "Keep" calls it off. Once confirmed, the item disappears from the list and does not return after save. |
| 17 | **Restart and verify persistence** | After a full restart and unlock, previously added/edited items are present and the deleted item is gone. |

All steps passed; on-disk checks corroborated the visual results (vault created at 888 bytes with no
plaintext leak; the added login persisted with none of its field values in cleartext; the deleted
item's data was gone from the file after save/restart). The status-matrix entry for end-to-end GUI
behavior is now `MANUALLY VERIFIED (2026-09-13)`.
