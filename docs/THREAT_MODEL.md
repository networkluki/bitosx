# Threat Model

This is a threat-by-threat view of the current local vault milestone. It complements
[SECURITY_MODEL.md](SECURITY_MODEL.md). Ratings describe BitosX's response, not a guarantee.

## Assets

- Master password (root secret; never stored).
- Vault key (random 256-bit; in memory only while unlocked).
- Vault contents: login names, usernames, passwords, URLs, notes.

## Trust boundaries

- **Trusted:** the local OS user account and process while the vault is unlocked; the .NET
  runtime; pinned dependencies.
- **Untrusted:** the vault file at rest, its backups/copies, and any future network peer.

## Threats

| # | Threat | BitosX response | Residual risk |
|---|---|---|---|
| 1 | **Vault file theft** | Encrypted with AES-256-GCM; key derived with Argon2id (memory-hard). | Offline brute force succeeds against weak master passwords. |
| 2 | **Stolen computer (vault locked / app closed)** | Only ciphertext on disk; requires the master password. | Depends on master-password strength. |
| 3 | **Stolen computer (vault unlocked)** | Not defended while unlocked. **Automatic locking** (inactivity, workstation lock, suspend, optional minimize) shortens the window; see [AUTO_LOCK.md](AUTO_LOCK.md). | High while the vault is open and in use. |
| 4 | **Malware / keylogger with user privileges** | Not defensible from within the app. | High; outside BitosX's control. |
| 5 | **Memory scraping** | Minimized secret lifetime; byte buffers wiped best-effort; vault key wiped on lock and on shutdown, also when an operation was in progress at that moment. | `string` secrets cannot be reliably wiped in managed memory. |
| 6 | **Clipboard history / sync** | Timed clear; cleared on exit and on workstation lock / suspend; never erases content the user copied afterwards. On **Windows** the copy is marked so that the clipboard history and cloud clipboard do not store it (verified against the real clipboard). | The markers are requests: software that reads the clipboard directly still sees the value. **No markers on macOS / Linux.** |
| 7 | **Malicious dependency (supply chain)** | Dependencies pinned to specific verified versions; minimal crypto surface. | A compromised upstream package remains a risk. |
| 8 | **Compromised future server** | No server exists; local-only. Zero-knowledge intent for the future. | To be addressed by the (unbuilt) server design. |
| 9 | **Phishing** | No autofill/browser integration to abuse; also no protection offered. | User can still be deceived elsewhere. |
| 10 | **Weak master password** | Argon2id raises brute-force cost; the UI rates the password when a vault is created and when the password is changed, discounting repetition, runs, keyboard patterns and common passwords; minimum length enforced. | A weak password remains the dominant risk. The rating has no dictionary. |
| 11 | **Corrupted local storage / failed write / power loss** | Atomic, durable write (unique temp file, flushed to disk, then renamed); a failed write is reconciled against the file instead of assumed; reads fail closed. **Encrypted export/backup + restore** are available (see [BACKUP_RESTORE.md](BACKUP_RESTORE.md)). | The user must actually make and safeguard backups; a lost/forgotten backup password is unrecoverable. |
| 14 | **Malicious backup file on restore** | Backup treated as attacker-controlled: size-capped, strictly parsed (incl. KDF bounds and duplicate-property rejection), authenticated and model-validated **before** any replacement; the live vault is preserved as a recovery copy and replaced atomically. | Restore is destructive by design; mitigated by validate-before-replace, recovery copy, and explicit confirmation. |
| 12 | **Tampered vault file** | AEAD + header bound as associated data; tampering fails authentication. | None for confidentiality/integrity; a tampered file simply won't open. |
| 13 | **Downgrade / malformed / oversized file** | Version and structure validated; unknown and duplicate properties rejected; invalid UTF-8 rejected; size limit enforced on the bytes read; no plaintext fallback. | None beyond denial of access to a broken file. |
| 15 | **Exposed / possibly-compromised vault key** | **Vault key rotation** generates a new vault key and re-encrypts all content under it, retiring the old key; requires the current password; format unchanged (v1). | Not retroactive — pre-rotation backups keep the old key material and remain decryptable with the master password. |
| 16 | **The vault file changes while it is open** (second instance, sync tool, restored copy) | **Every** write — save, master-password change, key rotation — first verifies an envelope-wide SHA-256 fingerprint of the file against the session (catches different-key **and** same-key content changes) and fails closed, writing nothing. A cancellation boundary and write-failure reconciliation guarantee the terminal state is never `disk = new / session = old`. Operations run one at a time, and a second instance on the same vault refuses to start. | A window of milliseconds remains between the check and the write for a writer that ignores the single-instance guard. |
| 17 | **Malformed content inside an authentic vault or backup** | Decrypted content is validated on load with the same rules as on edit; every failure is a validation error that never quotes the content. | Producing such content requires the vault key. |
| 18 | **A component failing at the moment the vault opens** | The vault key has exactly one owner; if a subscriber cannot be notified that the vault is open, the vault locks again. All subscribers are notified even if one throws. | None known. |
| 19 | **Predictable temporary files** | Unique names, created exclusively, owner-only on Unix; a file or link planted at a predictable name is neither followed nor overwritten. | Unix permissions are **NOT VERIFIED**. |

## Explicitly out of scope for this milestone

Server authentication, synchronization, subscriptions/payments, browser extension, mobile app,
Cloudflare, and any production API. See the project brief.

## Planned mitigations (future milestones)

- **Automatic locking** on inactivity, workstation lock, suspend, and minimize.
  *(Implemented — see [AUTO_LOCK.md](AUTO_LOCK.md).)*
- **Optional OS-backed secret protection** via the `ISecureStorage` extension point (e.g. DPAPI,
  Keychain, Secret Service).
- **Master-password change** (re-wrap the vault key without re-encrypting all content). *(Implemented.)*
- **Encrypted backups / export**. *(Implemented — see [BACKUP_RESTORE.md](BACKUP_RESTORE.md).)*
- **Vault key rotation** (new vault key + full content re-encryption, retiring the old key).
  *(Implemented — see [KEY_ROTATION.md](KEY_ROTATION.md).)*
- **Unicode normalization of the master password**, with a migration that cannot lock out
  existing vaults.
- **Strengthening the key-derivation settings of an existing vault.**
- **Clipboard sensitivity markers on macOS and Linux.**

See [SECURITY_REVIEW_0.5.1.md](SECURITY_REVIEW_0.5.1.md) for the latest review.
