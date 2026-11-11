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
- **Untrusted:** the vault file at rest, its backups/copies, the BitosX server and everything it
  returns (an envelope that comes down is let in exactly as a backup file is: parsed fail-closed,
  opened with its master password on the device, validated, and only then, after an acknowledgement,
  written — [SYNC.md](SYNC.md)).

## Threats

| # | Threat | BitosX response | Residual risk |
|---|---|---|---|
| 1 | **Vault file theft** | Encrypted with AES-256-GCM; key derived with Argon2id (memory-hard). | Offline brute force succeeds against weak master passwords. |
| 2 | **Stolen computer (vault locked / app closed)** | Only ciphertext on disk; requires the master password. | Depends on master-password strength. |
| 3 | **Stolen computer (vault unlocked)** | Not defended while unlocked. **Automatic locking** (inactivity, workstation lock, suspend, optional minimize) shortens the window; see [AUTO_LOCK.md](AUTO_LOCK.md). A pointer that the system reports where it already was does not count as activity. | High while the vault is open and in use. |
| 4 | **Malware / keylogger with user privileges** | Not defensible from within the app. | High; outside BitosX's control. |
| 5 | **Memory scraping** | Minimized secret lifetime; byte buffers wiped best-effort; vault key wiped on lock and on shutdown, also when an operation was in progress at that moment. | `string` secrets cannot be reliably wiped in managed memory. |
| 6 | **Clipboard history / sync** | Timed clear; cleared on exit and on workstation lock / suspend; never erases content the user copied afterwards. The copy carries the marker of the platform that asks the system and clipboard managers not to store it. Verified against the real clipboard of **Windows** (and its clipboard history), **Linux** and **macOS**; Klipper, the clipboard manager of KDE, does not record a copy that carries the marker. | The markers are requests: software that reads the clipboard directly still sees the value, and a clipboard manager may ignore them — **CopyQ 7.1.0 records a marked secret**. Clipboard managers for macOS and Windows, the clipboard history of macOS and the cloud clipboard of Windows are **NOT VERIFIED**. |
| 7 | **Malicious dependency (supply chain)** | Dependencies pinned to specific verified versions; minimal crypto surface. | A compromised upstream package remains a risk. |
| 8 | **Compromised server** | The server receives the account email and password (stored hashed) and the session tokens — never the master password, the vault key, or vault content; `IBitosXApiClient` has no method that could carry them. | Residual: the account password itself, if it is reused as the master password; the application cannot tell. See ACCOUNT.md. |
| 8b | **A server impersonated on the network** | TLS as the OS validates it, with online revocation checks; no redirects followed; the production origin is fixed in the build, another only by an explicit command-line option that refuses plain http to any host but this machine. | Residual: a CA the operating system trusts that should not be. |
| 8c | **A stolen session token** | Short-lived access token (15 min on the server); refresh is single-use and strictly rotated on the server; tokens in memory only, dropped on lock, sign-out, 401 and exit — and since 0.11.0 the server session is ended on lock (inactivity included) and on exit too, refreshing first if needed, so a 30-day refresh token does not stay valid on the server after the machine is left. | Residual: malware reading process memory while signed in (threat 1). |
| 9 | **Phishing** | No autofill/browser integration to abuse; also no protection offered. | User can still be deceived elsewhere. |
| 10 | **Weak master password** | Argon2id raises brute-force cost — new vaults are created with 256 MiB since 0.7.0, and an existing vault can be moved to stronger settings (see [KEY_DERIVATION.md](KEY_DERIVATION.md)); the UI rates the password when a vault is created and when the password is changed, discounting repetition, runs, keyboard patterns and common passwords; minimum length enforced. | A weak password remains the dominant risk. The rating has no dictionary. Copies of the vault made before the settings were strengthened keep the old ones. |
| 11 | **Corrupted local storage / failed write / power loss** | Atomic, durable write (unique temp file, flushed to disk, then renamed; on Unix the directory is flushed as well); a failed write is reconciled against the file instead of assumed; reads fail closed. **Encrypted export/backup + restore** are available (see [BACKUP_RESTORE.md](BACKUP_RESTORE.md)). | The user must actually make and safeguard backups; a lost/forgotten backup password is unrecoverable. |
| 14 | **Malicious backup file on restore** | Backup treated as attacker-controlled: size-capped, strictly parsed (incl. KDF bounds and duplicate-property rejection), authenticated and model-validated **before** any replacement; the live vault is preserved as a recovery copy and replaced atomically. | Restore is destructive by design; mitigated by validate-before-replace, recovery copy, and explicit confirmation. |
| 12 | **Tampered vault file** | AEAD + header bound as associated data; tampering fails authentication. | None for confidentiality/integrity; a tampered file simply won't open. |
| 13 | **Downgrade / malformed / oversized file** | Version and structure validated; unknown and duplicate properties rejected; invalid UTF-8 rejected; size limit enforced on the bytes read; no plaintext fallback. | None beyond denial of access to a broken file. |
| 15 | **Exposed / possibly-compromised vault key** | **Vault key rotation** generates a new vault key and re-encrypts all content under it, retiring the old key; requires the current password; format unchanged (v1). | Not retroactive — pre-rotation backups keep the old key material and remain decryptable with the master password. |
| 16 | **The vault file changes while it is open** (second instance, sync tool, restored copy) | **Every** write — save, master-password change, key rotation — first verifies an envelope-wide SHA-256 fingerprint of the file against the session (catches different-key **and** same-key content changes) and fails closed, writing nothing. A cancellation boundary and write-failure reconciliation guarantee the terminal state is never `disk = new / session = old`. Operations run one at a time, and a second instance on the same vault refuses to start. | A window of milliseconds remains between the check and the write for a writer that ignores the single-instance guard. |
| 17 | **Malformed content inside an authentic vault or backup** | Decrypted content is validated on load with the same rules as on edit; every failure is a validation error that never quotes the content. | Producing such content requires the vault key. |
| 18 | **A component failing at the moment the vault opens** | The vault key has exactly one owner; if a subscriber cannot be notified that the vault is open, the vault locks again. All subscribers are notified even if one throws. | None known. |
| 19 | **Predictable temporary files** | Unique names, created exclusively, owner-only on Unix; a file or link planted at a predictable name is neither followed nor overwritten. The permissions are verified on Linux and macOS. | None known. |
| 20 | **The same password typed differently** (another keyboard, operating system or input method) | The key is derived from a canonical encoding (spaces mapped, Unicode NFC). Vaults and backups from before 0.6.0 are opened by trying the encodings they could have been keyed with, and move to the canonical one on the next password change, rotation or strengthening. See [MASTER_PASSWORD.md](MASTER_PASSWORD.md). | A vault from before 0.6.0 keyed with no-break spaces does not open for ordinary spaces until it has been moved. A wrong non-ASCII password costs up to four key derivations instead of one. |
| 21 | **A vault whose settings ask for more memory than the device has** | The derivation fails before anything is decrypted, with an error that names the memory needed and is not the one for a wrong password. Settings are tried out on the device before a vault is moved to them, and a new vault is created with less memory on a device that cannot spare what new vaults normally get. | The vault cannot be opened on that device. Nothing is lost: it opens where the memory is available. |
| 22 | **Another program reads a password field** through the accessibility interface of the operating system (a screen reader, a remote-support or automation tool, a program that records what is on screen) | A password field tells that interface what it shows on screen — dots, until the user reveals the text — and sends no notification when the text changes. Verified on the real application, from another process. | What is shown on screen is also told to that interface: item names, user names, a revealed or generated password. The fields are not reported *as* password fields (the framework cannot), so a screen reader may speak what is typed. Software that records the keyboard is not affected; see threat 4. |
| 23 | **A fault nobody foresaw** (an error that reaches the user interface unhandled) | Fails closed: the vault is locked, a copied secret is cleared, and the user is told. What the fault says is neither shown nor logged. Faults that keep coming are let through and end the application. | A fault outside the user interface — in another thread, or one the runtime cannot recover from — still ends the application, and then a copied secret is not cleared. |
| 24 | **The application is started on another data directory** (`--data-directory`, for example from a changed shortcut) | The window title names the directory, so it is visible which vault a window belongs to. An option that cannot be used ends the application instead of opening the default vault. One instance per vault still holds. | Whoever can change how the application is started can equally replace the application; see threat 4. A vault created in a directory someone else can read is still encrypted, but the file is in their hands (threat 1). |
| 25 | **Settings that are not what they seem** | The Settings page shows and saves the settings that apply. Before 0.7.0, after a lock and unlock, it showed those the application had started with, and saving one of them put the others back — a stricter setting could be undone unnoticed. | None known. |
| 26 | **A hostile file to import** (anyone may have written it) | Refused as a whole beyond 16 MiB or 10,000 entries; strict UTF-8; any document type declaration refused on the text itself and by the XML reader, which has no resolver (no entities, nothing fetched); nesting counted while reading (XML 100, groups 64, JSON 64); a name twice in a JSON object refused; every reader makes one pass. Refusals and reasons are fixed texts, and nothing of a file is logged. An entry that breaks a limit is listed and left out, never cut. The import is one write, refused as a whole beyond 10,000 items or a 16 MiB vault file. See [IMPORT_EXPORT.md](IMPORT_EXPORT.md) §12. | What is imported is what the file says: a file can add many logins, or logins made to mislead. The preview shows names, counts and reasons, not every value. Tested with made-up files only (see VERIFICATION.md). |
| 27 | **The secrets of an import file, while it is open** | The bytes of the file are wiped once parsed. What was parsed is held as text from the preview until it is imported, called off, or the vault locks; it is never given to the page, which shows names, counts and reasons. A row whose values moved by a column is listed without its "name". | Text cannot be wiped in managed memory. The file itself stays where it is, in plain text: the user is reminded to delete it, and deleting does not erase. |
| 28 | **An unencrypted export made by whoever is at an unlocked vault** | The unlocked session is not enough: the master password is typed again and verified against the stored vault before anything is written; a wrong one writes nothing. A warning and a box come first; locking closes the export and empties what was typed. The log records, as a warning, that an export was written — not where. | Whoever knows the master password can export. From then on the file is outside BitosX: plain text on disk, in backups of that disk, in synced folders. |
| 29 | **An export half-written, or readable by others** | Written to a temporary file in the chosen folder (owner-only on Unix; on Windows with the permissions of the folder), flushed, then renamed into place; a failed write deletes the temporary file; the vault file is never the destination. | **If the application or the machine dies during an export, the temporary file (`<name>.<32 hex digits>.tmp`) stays in that folder with some or all of the passwords in plain text.** Nothing removes it, unless a later export goes to the same name in the same folder more than ten minutes afterwards: BitosX does not look for such files at start-up and does not remember where exports went. |
| 30 | **A spreadsheet program running a formula found in an export** | Not defended in the file: values are written exactly as they are, because the file is for another password manager and a changed password is a wrong password. The user is told, before anything is written, not to open it in a spreadsheet program. | Opening a CSV export in a spreadsheet program can run what a value that starts with `=`, `+`, `-` or `@` asks for. |

## Explicitly out of scope for this milestone

Subscriptions/payments, browser extension, mobile app, Cloudflare, and the production
deployment of the API. Account sign-in (0.8.0) and encrypted sync (0.9.0) are in; what they send
and what the server can do with it is in [ACCOUNT.md](ACCOUNT.md) and [SYNC.md](SYNC.md). Not
verified against the real server, which is not publicly reachable yet.

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
  existing vaults. *(Implemented — see [MASTER_PASSWORD.md](MASTER_PASSWORD.md).)*
- **Strengthening the key-derivation settings of an existing vault.**
  *(Implemented — see [KEY_DERIVATION.md](KEY_DERIVATION.md).)*
- **Clipboard sensitivity markers on macOS and Linux.** *(Implemented; verified on the real
  clipboard of both since 0.7.0.)*
- **Verification on Linux and macOS.** *(Since 0.7.0 the tests run there for every change, on the
  machines of a build service. The application itself has not been started there.)*
- **Raising the default key-derivation settings for new vaults**, from measurements on more than
  one device. *(Implemented in 0.7.0 — see [KEY_DERIVATION.md](KEY_DERIVATION.md).)*
- **Locking on workstation lock and suspend on Linux and macOS.**
- **A dictionary for the strength rating.**

See [SECURITY_REVIEW_0.7.0.md](SECURITY_REVIEW_0.7.0.md) for the latest review, and
[SECURITY_REVIEW_0.5.1.md](SECURITY_REVIEW_0.5.1.md) for the one before.
