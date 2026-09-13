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
| 3 | **Stolen computer (vault unlocked)** | Not defended — contents are available while unlocked. | High; mitigated later by auto-lock (see below). |
| 4 | **Malware / keylogger with user privileges** | Not defensible from within the app. | High; outside BitosX's control. |
| 5 | **Memory scraping** | Minimized secret lifetime; byte buffers wiped best-effort; vault key wiped on lock. | `string` secrets cannot be reliably wiped in managed memory. |
| 6 | **Clipboard history / sync** | Timed clipboard clear; avoids overwriting content the user replaced. | OS/clipboard-manager history may retain copies. |
| 7 | **Malicious dependency (supply chain)** | Dependencies pinned to specific verified versions; minimal crypto surface. | A compromised upstream package remains a risk. |
| 8 | **Compromised future server** | No server exists; local-only. Zero-knowledge intent for the future. | To be addressed by the (unbuilt) server design. |
| 9 | **Phishing** | No autofill/browser integration to abuse; also no protection offered. | User can still be deceived elsewhere. |
| 10 | **Weak master password** | Argon2id raises brute-force cost; UI shows a strength estimate; minimum length enforced. | A weak password remains the dominant risk. |
| 11 | **Corrupted local storage / failed write** | Atomic write (temp + `File.Replace`); failed writes do not destroy the existing vault; reads fail closed. | No built-in backups; corruption without a backup means data loss. |
| 12 | **Tampered vault file** | AEAD + header bound as associated data; tampering fails authentication. | None for confidentiality/integrity; a tampered file simply won't open. |
| 13 | **Downgrade / malformed / oversized file** | Version and structure validated; size-limited; no plaintext fallback. | None beyond denial of access to a broken file. |

## Explicitly out of scope for this milestone

Server authentication, synchronization, subscriptions/payments, browser extension, mobile app,
Cloudflare, and any production API. See the project brief.

## Planned mitigations (future milestones)

- **Automatic locking** on inactivity, workstation lock, suspend, and minimize (the
  `IVaultService.StateChanged` seam and the locked/unlocked state machine are already in place).
- **Optional OS-backed secret protection** via the `ISecureStorage` extension point (e.g. DPAPI,
  Keychain, Secret Service).
- **Master-password change** (re-wrap the vault key without re-encrypting all content).
- **Encrypted backups / export**.
