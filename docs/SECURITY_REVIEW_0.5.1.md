# Security Review — 0.5.1

A review of the whole code base at `0.5.0` (crypto, core, storage, platform, app), followed by
fixes and regression tests. Released as **`0.5.1`**: security hardening only, no new feature, and
the **on-disk vault format is unchanged (v1)** — every existing vault and backup opens as before.

This document records what was found, how it was confirmed, and what was done. It makes no claim
that the review was exhaustive; see "Not changed" and "Still open" at the end.

## Method

1. Read every source file and the existing tests and documentation.
2. For each suspected weakness, write a test that **reproduces it against the 0.5.0 code** before
   changing anything. A suspicion that could not be reproduced was dropped.
3. Fix it, keeping the change as small as the defect allows.
4. Confirm the new tests **fail against 0.5.0 and pass against 0.5.1**: they were run against a
   separate checkout of the old code, so a test that could never fail would have been noticed.
5. For behaviour that unit tests cannot see (the real clipboard, a second process), verify against
   the real operating system. See [VERIFICATION.md](VERIFICATION.md).

Severity describes the consequence *if triggered*, together with how plausible the trigger is.

## Findings

| # | Finding | Severity | Status |
|---|---|---|---|
| 1 | A failing `StateChanged` subscriber zeroed the live vault key; the next save encrypted the vault under an all-zero key | **High** | Fixed |
| 2 | A second instance changing the master password after the first rotated the key produced a vault that **no password can open** | **High** | Fixed |
| 3 | A stale session's save silently **undid** a master-password change or key rotation made elsewhere | **High** | Fixed |
| 4 | Copied passwords were stored in the **Windows clipboard history** (and cloud clipboard, if enabled) | **Medium** | Fixed (Windows) |
| 5 | A secret copied shortly before closing the application **stayed on the clipboard** | **Medium** | Fixed |
| 6 | Locking during a key rotation put the **new vault key back into the locked service** | **Medium** | Fixed |
| 7 | Decrypted content was not validated on load; malformed content escaped as `NullReferenceException` / `NotSupportedException` | **Medium** | Fixed |
| 8 | After the system clock was moved back, an item could never be edited again and **its backup could not be restored** | **Medium** | Fixed |
| 9 | Vault writes were not flushed to disk before the rename: power loss could leave an **empty or truncated vault** | **Medium** | Fixed |
| 10 | The strength meter rated `aaaaaaaaaaaaaaaaaaaa` *Strong* and `P@ssw0rd!P@ssw0rd!` *Very strong* | **Medium** | Fixed |
| 11 | A write that threw *after* taking effect left memory and disk disagreeing (only rotation reconciled) | Low | Fixed |
| 12 | Temporary files had a predictable name and were opened without exclusivity | Low | Fixed |
| 13 | Several commands let exceptions escape, which terminates the application | Low | Fixed |
| 14 | `"kdfParameters": null` in a vault file escaped as `NullReferenceException` | Low | Fixed |
| 15 | One failed attempt cancelled the timed clipboard clear for good | Low | Fixed |
| 16 | The file size limit was checked before reading, not on what was read | Low | Fixed |
| 17 | Settings showed the *default* key-derivation parameters, not those of the open vault | Low | Fixed |
| 18 | On Unix the vault file was created readable by other local users | Low | Fixed, **NOT VERIFIED** (no Unix machine) |
| 19 | No known-answer tests: the primitives were only tested against themselves | Test gap | Fixed |

### 1. A failing subscriber destroyed the live key (High)

`UnlockAsync` and `CreateVaultAsync` assigned the vault key to the session, raised `StateChanged`,
and — if anything threw — wiped "the key that was not adopted". But it *had* been adopted: the same
array was already the session key. If a subscriber threw (for example the UI failing to build the
unlocked view), the session stayed `Unlocked` with a key of 32 zero bytes. The next save then
re-encrypted the **entire vault under the all-zero key** while the header still wrapped the real
one. Result: the vault could never be opened again, and its content on disk was protected by a key
anyone can guess.

Reproduced: unlock with a throwing subscriber → add an item (succeeds) → reopen with the correct
password → `VaultAuthenticationException`.

Fix: ownership of the key is explicit — it passes to the session exactly once, and only the session
wipes it. If a subscriber cannot be told that the vault is open, the vault **locks again** instead
of staying open behind its back. Every subscriber is notified even when an earlier one throws, so
auto-lock always learns about a transition.

### 2 and 3. A session that no longer matches the file (High)

Nothing stopped two instances from running on the same vault, and only key rotation checked that
the file on disk was still the one the session had loaded.

- **Finding 2.** Instance B rotates the vault key. Instance A, still holding the old key, changes the
  master password: it re-wraps the key *found on disk* (the new one) but keeps the old key in
  memory. Its next save encrypts with the old key under a header that wraps the new one. Neither
  password opens the result. Reproduced exactly so.
- **Finding 3.** Instance B changes the master password. Instance A saves any change: it writes its
  old header back. The **old password works again**, the new one does not, and nothing told the
  user. The same happens to a key rotation. For someone who changed the password *because* the old
  one was exposed, this silently restores the exposure.

Fix, in three layers:

1. **Every** write — saving a change, changing the master password, rotating the key — now passes
   the consistency gate that rotation already had: the file must be byte-for-byte the envelope this
   session loaded or last wrote, or the operation fails closed with
   `VaultChangedExternallyException` and writes nothing. Recovery is to lock and reopen.
2. Operations run one at a time, so a double click cannot interleave two of them.
3. The application refuses to start a second instance on the same vault (`SingleInstanceGuard`)
   and brings the running one to the front. This is a convenience for the honest case; layer 1 is
   the guarantee.

One deliberate exception: if the vault file is **missing**, saving is allowed. The session then
holds the last copy of the data, and refusing to write it would turn an accidental deletion into
data loss. A file that is present but unreadable is never overwritten.

### 4 and 5. The clipboard kept secrets longer than promised (Medium)

Measured on Windows 11 with clipboard history enabled, before the fix:

- A copied value appeared in the clipboard history (Win+V) and **stayed there after BitosX cleared
  the clipboard**. With cloud clipboard enabled it would also have been uploaded.
- A value that had been pasted once stayed on the clipboard after the application was closed,
  because the timed clear lives in the process.

Fix:

- On Windows the copied value carries the documented marker formats
  (`ExcludeClipboardContentFromMonitorProcessing`, `CanIncludeInClipboardHistory = 0`,
  `CanUploadToCloudClipboard = 0`) and `Clipboard Viewer Ignore` for third-party clipboard
  managers. Pasting is unaffected.
- Closing the application clears the value first (bounded to two seconds, so a clipboard that does
  not respond cannot prevent closing).
- Locking the workstation or suspending the system clears it immediately: timers stop while a
  system sleeps, so the timed clear alone would leave the value there until after resume.

All of these only ever remove the value BitosX copied, never content the user copied afterwards.
Locking the *vault* (manually, on minimize, on inactivity) deliberately does **not** clear the
clipboard: "copy, switch window, paste" is the intended use, and the timed clear still applies.

These markers are requests. The operating system and well-behaved clipboard managers honor them; an
application that reads the clipboard directly still sees the text while it is there.

### 6. Locking during an operation (Medium)

`Lock()` is synchronous; rotation, password change and saving are not. If auto-lock fired while a
rotation was writing, the rotation finished afterwards and adopted the new vault key into a service
that was already locked — where it stayed until the next unlock, never wiped.

Fix: each operation works on a private copy of the key and remembers which session it started in.
When it finishes it adopts its result only if that session still exists; otherwise the result is
wiped. `Lock()` never waits for an operation, and an operation that is overtaken by a lock still
completes on disk with an intact key. `VaultService` is now disposable and wipes the key on
shutdown.

### 7. Decrypted content was trusted (Medium)

Item validation ran when items were added or edited, not when a vault was loaded. Content that is
correctly encrypted but malformed — which takes the key to produce, so this is defense in depth —
led to: a `null` item → `NullReferenceException`; an item without a type →
`NotSupportedException`; a `null` URL list → a crash later in the UI; a duplicated field → the last
value silently winning; an unknown field → silently dropped on the next save; a `javascript:` URL →
accepted. On the restore path the first two were unhandled and terminated the application.

Fix: loading validates every item with the same rules as editing, rejects duplicate and unknown
properties, and reports every failure as `VaultValidationException` with a message that never
quotes the content. This closes the limitation recorded in earlier versions of VERIFICATION.md.

### 8. A clock moved backwards (Medium)

An update stamped `UpdatedAt = now` after validating. With the system clock behind the item's
`CreatedAt` (a clock that was wrong when the item was created, then corrected), toggling a favorite
stored `UpdatedAt < CreatedAt`. That item then failed validation on every later edit, and a backup
containing it was rejected as invalid on restore.

Fix: an update never stamps a time earlier than `CreatedAt`, and the stamped item is what gets
validated. Items already affected are repaired on load rather than rejected — a timestamp must
never be the reason a vault or a backup cannot be opened.

### 9. Durability (Medium)

The vault was written to a temporary file and renamed over the target, which is atomic against
exceptions but not against power loss: the rename can reach the disk before the data does. The
temporary file is now flushed to the physical disk before the rename.

### 10. Strength meter (Medium)

The estimate was length × log2(character pool), so repetition counted as strength. Because the
master password is the root of the key hierarchy and this meter is the only guidance the user gets,
an over-generous rating is a security problem.

| Password | 0.5.0 | 0.5.1 |
|---|---|---|
| `aaaaaaaaaaaaaaaaaaaa` | Strong | Very weak |
| `P@ssw0rd!P@ssw0rd!` | Very strong | Very weak |
| `Qwerty123456!` | Strong | Very weak |
| `Password123!` | Fair | Very weak |
| `9x!Kd82@Lm#qZ4wR7vB1` | Very strong | Very strong |

The estimate now discounts repeated characters, runs (`abcd`, `4321`, keyboard rows), a block
repeated several times, and the most common passwords including the usual substitutions. It still
has **no dictionary**: it cannot tell that a password consists of ordinary words, names, or dates,
and knows nothing about breached passwords. A good rating means "no obvious weakness found".
Changing the master password now shows the same rating as creating a vault does.

### 11–18. Lower severity

- **11.** All three write paths share one commit routine. If a write throws, the file is read back:
  still the old vault → the failure is reported; already the new vault → success; anything else, or
  unreadable → the session locks and `VaultInconsistentStateException` is raised.
- **12.** Temporary files get a unique name and are created exclusively, so a file or link placed
  at a predictable name is neither followed nor overwritten, and two writers cannot share one.
  Leftovers from an interrupted write are removed once they are too old to be in use.
- **13.** Delete, favorite and the copy commands report a failure instead of letting it escape.
- **14, 16.** Parsing reads the raw bytes strictly (invalid UTF-8 is rejected, never replaced), and
  the size limit applies to the bytes actually read.
- **15.** The timed clear retries a few times when the clipboard is held by another application.
- **17.** Settings describes the vault that is open and points out when it uses weaker
  key-derivation settings than a new vault would get.
- **18.** On Unix the vault, its backups and the settings file are created with owner-only
  permissions, and the BitosX directory is owner-only. **NOT VERIFIED**: no Unix machine was
  available; the test that checks it is skipped on Windows.

### 19. Known-answer tests

Round-trip tests cannot tell a correct primitive from one that is consistently wrong. Added:
Argon2id against seven vectors from the reference implementation and the RFC 9106 vector; AES-256-GCM
against test cases 13, 14 and 16 of the GCM specification; and a test that pins the associated data
of format v1, which is derived rather than stored. **All passed without any change to the crypto
code** — the cryptographic core of 0.5.0 was sound.

## Reviewed and found sound

- Key hierarchy, nonce and salt generation, and the binding of the header as associated data.
- Wrong password and tampering are indistinguishable; no plaintext fallback anywhere.
- KDF parameter bounds are enforced before any derivation, in all three places.
- The password generator uses the CSPRNG without modulo bias.
- Logging: no secret reaches a log message.
- Restore: the backup is authenticated and validated before anything is replaced.
- Dependencies: `dotnet list package --vulnerable --include-transitive` reports none (2026-09-29).

## Not changed (deliberately)

- **The vault format and all cryptographic parameters.**
- **KDF parameters are preserved** by password change and rotation, as documented. There is still no
  path to strengthen an existing vault's parameters; Settings now at least says so.
- **The settings file is not authenticated.** Software that can write to the user's profile can turn
  auto-lock off. That is the application's trust boundary (see THREAT_MODEL.md); values are clamped
  and an unreadable file falls back to the secure defaults.
- **No unlock throttling.** The threat is offline guessing against a stolen file, which a delay in
  the UI does not affect.

## Still open

- **Master passwords are not Unicode-normalized.** A password with accented characters can be
  encoded differently by different keyboards or operating systems and then fail to unlock. Fixing it
  must not lock out existing vaults, so it needs a migration and is not done here.
- **Clipboard markers exist for Windows only.** macOS and Linux have conventions of their own
  (`org.nspasteboard.ConcealedType`, `x-kde-passwordManagerHint`); they are not set because they
  could not be verified.
- **Durability on Unix** would additionally need the directory entry flushed after the rename.
- **Decrypted values in managed memory** cannot be wiped reliably (unchanged, documented).
- **The new user-interface texts** (password rating and key-derivation note in Settings, the
  "could not be saved" notice) and **clearing on workstation lock in the running application** are
  covered by automated tests but have **not been checked by a person in the GUI**.
