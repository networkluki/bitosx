# Security Model

This document states, as plainly as possible, what BitosX protects against and what it does not.
It describes the current **local vault** milestone. No marketing claims are made: BitosX is not
"unhackable," "military-grade," or "100% secure."

## Design goals

- Keep vault contents encrypted at rest with a key derived from the user's master password.
- A stolen vault file must not reveal plaintext passwords without the master password.
- Move toward a zero-knowledge design: as server features arrive, the master password, plaintext
  secrets, the raw vault key, and decrypted contents must never leave the device.
- Fail closed and never fall back to plaintext.

## What BitosX protects against

- **Theft of the vault file.** The vault is encrypted with AES-256-GCM under a key derived from
  the master password via Argon2id. Without the master password, an attacker faces an offline,
  memory-hard brute-force problem whose difficulty scales with the master password's strength.
- **Tampering with the vault file.** Authenticated encryption detects any modification to the
  ciphertext, tag, nonce, salt, wrapped key, or the header fields bound as associated data.
  Tampered or corrupted files are rejected.
- **Downgrade / malformed input.** Unknown versions, unsupported algorithms, invalid structure,
  and oversized files are rejected; there is no plaintext fallback.
- **Resource exhaustion from a malicious vault file.** The vault file is treated as untrusted
  input: its size is capped (16 MiB) before reading, and the Argon2id cost parameters are bounded
  (memory ≤ 1 GiB, iterations ≤ 64, parallelism ≤ 64) before any derivation, so a tampered file
  cannot request an absurd allocation or unbounded CPU on unlock. See
  [CRYPTOGRAPHY.md](CRYPTOGRAPHY.md) — "KDF parameter bounds".
- **Casual disk/backup exposure.** A backup or synced copy of the vault file is ciphertext. The
  built-in **encrypted backup/export** is the same encrypted envelope copied byte-for-byte (no
  plaintext, no new format); restore authenticates and validates the backup before replacing the
  vault and preserves a recovery copy. See [BACKUP_RESTORE.md](BACKUP_RESTORE.md).
- **Retiring a possibly-exposed vault key.** **Vault key rotation** generates a new vault key and
  re-encrypts all content under it, so an old vault key that may have been exposed no longer protects
  the current file. Rotation requires the current master password, keeps the on-disk format (v1), and
  is guarded so it fails closed if the vault changed on disk and never leaves disk and session
  disagreeing. It is **not** retroactive to pre-rotation backups. See
  [KEY_ROTATION.md](KEY_ROTATION.md).
- **A session that no longer matches the file.** If the vault file was changed by something else
  while it was open — a second instance, a sync tool, a restored copy — every operation that would
  replace it (saving a change, changing the master password, rotating the key) fails closed and
  writes nothing, instead of overwriting the other change. Without this a stale session could
  silently undo a master-password change or produce a vault that no password opens. The
  application also refuses to start a second instance on the same vault.
- **Malformed decrypted content.** Decrypted content is validated when it is loaded, with the same
  rules as when items are edited, so the in-memory model is always well formed; failures never
  quote the content.
- **An interrupted write, including power loss.** New content is written to a temporary file,
  flushed to the physical disk, and only then renamed over the vault, so the file is always either
  the complete previous vault or the complete new one. If a write fails, the outcome is read back
  from disk rather than assumed.
- **Clipboard history and cloud clipboard (Windows).** A copied secret is marked so that Windows
  keeps it out of the clipboard history (Win+V) and does not upload it to the cloud clipboard.
  It is also cleared when the application closes and when the workstation is locked or the system
  suspends, not only after the timed delay.
- **Accidental plaintext persistence.** The plaintext vault is only ever held in memory while
  unlocked; only encrypted data is written to disk.
- **Shoulder-surfing of stored passwords in the UI.** Passwords are masked by default and shown
  only on explicit request.
- **An unlocked vault left unattended.** BitosX locks automatically on inactivity, on workstation
  lock and system suspend (where the OS reliably reports them — currently Windows), and optionally
  on minimize. This shrinks the window of exposure but does not eliminate it: while unlocked and in
  use, decrypted contents are in memory. See [AUTO_LOCK.md](AUTO_LOCK.md) for the platform support
  and verification status (OS-event detection on Linux/macOS is **NOT VERIFIED / unsupported**).

## What BitosX does NOT protect against

- **A compromised device / malware / keyloggers.** Software running with your privileges (or
  higher) while the vault is unlocked can read decrypted contents from memory, capture the master
  password as you type it, or read the clipboard. BitosX cannot defend against this.
- **A stolen computer that is unlocked with the vault open.** If the vault is unlocked, its
  contents are available.
- **Memory scraping.** Managed .NET cannot guarantee secrets are removed from memory. The garbage
  collector may copy/relocate `byte[]` and (especially) `string` objects, leaving copies that
  cannot be located or wiped. BitosX minimizes secret lifetime and wipes byte buffers
  (KEK, vault key, password bytes, plaintext bytes) on a best-effort basis, but decrypted values
  held as `string` (item passwords in the UI) cannot be reliably erased.
- **A weak master password.** The master password is the root of the key hierarchy. A guessable
  password can be brute-forced offline regardless of the cipher. Argon2id raises the cost but
  cannot rescue a weak password.
- **Anything that copies the clipboard for itself.** The markers BitosX sets on Windows are
  requests: Windows and well-behaved clipboard managers honor them, but an application that reads
  the clipboard directly sees the secret while it is there, and BitosX cannot take back a copy
  that has been made. On **macOS and Linux no such markers are set**, so clipboard managers and
  history features there may keep copied secrets. The timed clear never erases content you copied
  afterwards.
- **A master password made of ordinary words.** The strength rating discounts repetition, runs,
  keyboard patterns and the most common passwords, but it has no dictionary and no knowledge of
  breached passwords. A good rating means no obvious weakness was found, nothing more.
- **Phishing.** BitosX has no server or browser integration in this milestone and does not
  autofill; it cannot detect or prevent a user entering credentials on a fraudulent site.
- **A malicious or compromised dependency.** BitosX relies on the .NET runtime, Avalonia, and a
  third-party Argon2 library. A supply-chain compromise of these is out of scope for the code
  here (dependencies are pinned to specific verified versions to reduce risk).
- **A compromised future server.** No server exists yet. The zero-knowledge intent is that a
  future server compromise must not expose secrets, but that protection depends on the (not yet
  built) server-side design.
- **Corrupted local storage / disk failure.** Atomic, durable writes prevent BitosX from
  destroying an existing vault on a failed write, but they cannot protect against a failing disk.
  Use the encrypted export to keep backups somewhere else; a lost or corrupted file with no backup
  means lost data.
- **Software that can write to your user profile.** The settings file holds preferences only and
  is not authenticated, so such software could, for example, turn automatic locking off. It could
  equally replace the application itself; this is the same boundary as the first point above.
- **Forgotten master password.** There is no recovery mechanism, backdoor, or universal key. A
  forgotten master password means the vault cannot be decrypted.

## Data handling summary

- **Persisted:** only the encrypted, versioned vault (ciphertext + public parameters).
- **In memory while unlocked:** the decrypted vault, the vault key (byte buffer, wiped on lock
  and on shutdown), and transiently the master password bytes during create/unlock (wiped after
  use). Locking takes effect immediately even while an operation is in progress: the operation
  finishes on a private copy of the key, which is wiped, and never puts key material back into
  the locked service.
- **Never persisted or logged:** master password, item passwords, secure notes, encryption keys,
  decrypted contents.

See [THREAT_MODEL.md](THREAT_MODEL.md) for a threat-by-threat breakdown and
[CRYPTOGRAPHY.md](CRYPTOGRAPHY.md) for algorithm details.
