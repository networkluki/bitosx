# Security Model

This document states, as plainly as possible, what BitosX protects against and what it does not.
It describes the current **local vault** milestone. No marketing claims are made: BitosX is not
"unhackable," "military-grade," or "100% secure."

## Design goals

- Keep vault contents encrypted at rest with a key derived from the user's master password.
- A stolen vault file must not reveal plaintext passwords without the master password.
- Zero-knowledge towards the server: the master password, plaintext secrets, the raw vault key,
  and decrypted contents never leave the device. Since 0.8.0 the application talks to the server
  for the account only; what crosses the wire is listed in [ACCOUNT.md](ACCOUNT.md).
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
  the complete previous vault or the complete new one. On Unix the directory is flushed as well,
  so that the rename itself survives a power loss. If a write fails, the outcome is read back
  from disk rather than assumed.
- **Clipboard history and clipboard managers that honor the marker.** A copied secret carries the
  marker of the system it is copied on, which asks the system and clipboard managers not to keep
  it. On **Windows** this keeps it out of the clipboard history (Win+V) and asks that it not be
  uploaded to the cloud clipboard; verified against the real clipboard and its history. On
  **Linux and macOS** the marker is on the real clipboard next to the text, and the clipboard
  manager of KDE (Klipper) does not record a text that carries it; verified on the machines of a
  build service. Not every clipboard manager honors it (see below). The secret is also cleared
  when the application closes and when the workstation is locked or the system suspends, not only
  after the timed delay.
- **Other programs reading a password field.** Operating systems let programs read and operate
  the windows of other programs — for screen readers, and for whatever else uses that interface.
  A password field tells that interface what it shows on screen: dots, until the user reveals the
  text. Before 0.7.0 it handed out the text. See
  [SECURITY_REVIEW_0.7.0.md](SECURITY_REVIEW_0.7.0.md).
- **A fault nobody foresaw.** If an error reaches the user interface unhandled, the application
  fails closed: the vault is locked and a copied secret is cleared, instead of the application
  ending with the secret left on the clipboard. What the fault says is neither shown nor logged.
- **The same master password typed differently.** The key is derived from a canonical encoding of
  the password, so a vault opens whether an accented letter arrives as one code point or as a
  letter and a combining mark, and whichever kind of space was typed. Vaults from before 0.6.0
  keep opening. See [MASTER_PASSWORD.md](MASTER_PASSWORD.md).
- **Key-derivation settings that have become too weak.** An existing vault can be moved to
  stronger settings, which makes every guess at the master password more expensive for someone
  holding a copy of the file made afterwards. Settings can be strengthened, never weakened. Since
  0.7.0 a new vault is created with four times the memory an older one was (256 MiB). See
  [KEY_DERIVATION.md](KEY_DERIVATION.md).
- **Accidental plaintext persistence.** The plaintext vault is only ever held in memory while
  unlocked; only encrypted data is written to disk.
- **Shoulder-surfing of stored passwords in the UI.** Passwords are masked by default and shown
  only on explicit request.
- **An unlocked vault left unattended.** BitosX locks automatically on inactivity, on workstation
  lock and system suspend (where the OS reliably reports them — currently Windows), and optionally
  on minimize. This shrinks the window of exposure but does not eliminate it: while unlocked and in
  use, decrypted contents are in memory. See [AUTO_LOCK.md](AUTO_LOCK.md) for the platform support
  and verification status (OS-event detection on Linux/macOS is **NOT VERIFIED / unsupported**).
- **Deleting an item by mistake.** Deleting cannot be undone, so it is asked for and then
  confirmed.

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
  password can be brute-forced offline regardless of the cipher. Argon2id raises the cost, and
  stronger settings raise it further, but nothing rescues a password that can be guessed in a few
  thousand attempts.
- **Copies of the vault made before its protection was improved.** Strengthening the key
  derivation, rotating the vault key and changing the master password all apply to the vault file
  from then on. A backup or any other copy made earlier keeps the protection it was made with.
- **Anything that copies the clipboard for itself.** The markers BitosX sets are requests
  (`org.nspasteboard.ConcealedType` on macOS, `x-kde-passwordManagerHint` on Linux, four formats
  on Windows). An application that reads the clipboard directly sees the secret while it is
  there, and BitosX cannot take back a copy that has been made. **A clipboard manager may ignore
  the request:** CopyQ 7.1.0, the version in Ubuntu 24.04, recorded a secret that carried the
  marker. No clipboard manager for macOS or Windows has been asked, nor the clipboard history of
  macOS, nor the cloud clipboard of Windows; these are **NOT VERIFIED**. If you use a clipboard
  manager, find out what it does with passwords. If a clipboard on macOS or Linux refuses the
  marker, the value is copied without it rather than not at all. The timed clear never erases
  content you copied afterwards.
- **Software that uses the accessibility interface.** What the window shows, it also tells that
  interface: the names, user names and addresses of your items, a password you have chosen to
  reveal, a password the generator shows. Only what is hidden on screen is hidden there. The
  interface does not report the password fields *as* password fields — the user-interface
  framework has no way of saying so — which a screen reader may use to decide whether to speak
  what is typed. Software with that access can also operate the application.
- **A master password made of ordinary words.** The strength rating discounts repetition, runs,
  keyboard patterns and the most common passwords, but it has no dictionary and no knowledge of
  breached passwords. A good rating means no obvious weakness was found, nothing more.
- **Phishing.** BitosX has no browser integration and does not autofill; it cannot detect or
  prevent a user entering credentials on a fraudulent site. The application itself talks only to
  the server fixed in the build (or the one named on its command line) over TLS.
- **The account password, on the server.** Signing in sends it to `api.bitosx.se`, where it is
  stored as an Argon2id hash. A server compromise could expose what is sent to it at the time:
  the email, the account password, the session tokens. It cannot expose the vault, whose key the
  server never has — provided the account password is not the master password. Nothing in the
  application can check that it is not.
- **Someone at an unlocked vault with the account signed in** can see and end the account's
  sessions and sign it out. Locking the vault signs the account out, so the window is the same as
  for the vault itself.
- **A malicious or compromised dependency.** BitosX relies on the .NET runtime, Avalonia, and a
  third-party Argon2 library. A supply-chain compromise of these is out of scope for the code
  here (dependencies are pinned to specific verified versions to reduce risk).
- **A compromised server, for the vault.** The server never receives the master password, the
  vault key, or vault content, so compromising it exposes none of them. What it can expose is
  listed above under the account password.
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
  and on shutdown), transiently the master password bytes during create/unlock (wiped after
  use), and, while signed in, the account's access and refresh tokens (dropped on sign-out, on
  lock, on a 401, and on shutdown; never written to disk).
- **Sent to the server, over TLS:** the account email and account password on sign-in; the tokens
  afterwards; the product name, version and operating system as `User-Agent`. See
  [ACCOUNT.md](ACCOUNT.md). Locking takes effect immediately even while an operation is in progress: the operation
  finishes on a private copy of the key, which is wiped, and never puts key material back into
  the locked service.
- **Never persisted or logged:** master password, item passwords, secure notes, encryption keys,
  decrypted contents, the account password, the account tokens.

See [THREAT_MODEL.md](THREAT_MODEL.md) for a threat-by-threat breakdown and
[CRYPTOGRAPHY.md](CRYPTOGRAPHY.md) for algorithm details.
