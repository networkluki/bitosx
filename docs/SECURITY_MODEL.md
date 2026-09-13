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
- **Casual disk/backup exposure.** A backup or synced copy of the vault file is ciphertext.
- **Accidental plaintext persistence.** The plaintext vault is only ever held in memory while
  unlocked; only encrypted data is written to disk.
- **Shoulder-surfing of stored passwords in the UI.** Passwords are masked by default and shown
  only on explicit request.

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
- **Clipboard history / clipboard sync.** BitosX clears the clipboard after a delay and avoids
  overwriting content you copied afterward, but it cannot remove entries already captured by OS
  clipboard history, clipboard managers, or cloud clipboard sync.
- **Phishing.** BitosX has no server or browser integration in this milestone and does not
  autofill; it cannot detect or prevent a user entering credentials on a fraudulent site.
- **A malicious or compromised dependency.** BitosX relies on the .NET runtime, Avalonia, and a
  third-party Argon2 library. A supply-chain compromise of these is out of scope for the code
  here (dependencies are pinned to specific verified versions to reduce risk).
- **A compromised future server.** No server exists yet. The zero-knowledge intent is that a
  future server compromise must not expose secrets, but that protection depends on the (not yet
  built) server-side design.
- **Corrupted local storage / disk failure.** Atomic writes prevent BitosX from destroying an
  existing vault on a failed write, but BitosX does not provide backups. Keep your own backups of
  the (encrypted) vault file; a lost or corrupted file with no backup means lost data.
- **Forgotten master password.** There is no recovery mechanism, backdoor, or universal key. A
  forgotten master password means the vault cannot be decrypted.

## Data handling summary

- **Persisted:** only the encrypted, versioned vault (ciphertext + public parameters).
- **In memory while unlocked:** the decrypted vault, the vault key (byte buffer, wiped on lock),
  and transiently the master password bytes during create/unlock (wiped after use).
- **Never persisted or logged:** master password, item passwords, secure notes, encryption keys,
  decrypted contents.

See [THREAT_MODEL.md](THREAT_MODEL.md) for a threat-by-threat breakdown and
[CRYPTOGRAPHY.md](CRYPTOGRAPHY.md) for algorithm details.
