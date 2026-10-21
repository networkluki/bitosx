# BitosX

BitosX is a security-focused password manager. This repository contains the cross-platform
Avalonia desktop application: an **encrypted local vault**, the **account sign-in** to the BitosX
server (0.8.0), and the **encrypted sync** of the vault through that server (0.9.0). The server
stores the vault only as the encrypted file it is on disk and cannot open it; nothing decrypted is
ever sent. There is intentionally **no subscription, browser extension, or mobile app** yet. See
[docs/ACCOUNT.md](docs/ACCOUNT.md) and [docs/SYNC.md](docs/SYNC.md).

## What works in this milestone

- Create a local vault protected by a master password (Argon2id with 256 MiB of memory,
  AES-256-GCM).
- Unlock / lock the vault; incorrect passwords are rejected.
- Automatic locking: inactivity timeout (configurable), workstation lock and system suspend
  (Windows), and optional lock-on-minimize. See [docs/AUTO_LOCK.md](docs/AUTO_LOCK.md).
- Change the master password (re-wraps the vault key; contents are not re-encrypted and the vault
  format is unchanged). See [docs/CRYPTOGRAPHY.md](docs/CRYPTOGRAPHY.md).
- Strengthen the key derivation of an existing vault (more memory for every guess at the master
  password; the settings can be measured on the device first). See
  [docs/KEY_DERIVATION.md](docs/KEY_DERIVATION.md).
- The vault opens however the same master password is typed (composed or decomposed accents,
  either kind of space). See [docs/MASTER_PASSWORD.md](docs/MASTER_PASSWORD.md).
- Rotate the vault key (generates a new vault key and re-encrypts all content under it; the master
  password and on-disk format are unchanged). See [docs/KEY_ROTATION.md](docs/KEY_ROTATION.md).
- Encrypted backup / export and restore (the encrypted envelope copied byte-for-byte; restore is
  authenticated and validated before it replaces the vault). See [docs/BACKUP_RESTORE.md](docs/BACKUP_RESTORE.md).
- Encrypted sync through the BitosX account: the encrypted vault file goes up and comes down as
  opaque bytes, revisions are compare-and-set, nothing is merged, and a conflict is shown with both
  ways out rather than resolved. A downloaded vault is opened with its master password and validated
  on the device before it replaces the local vault. See [docs/SYNC.md](docs/SYNC.md).
- Add, edit, delete, favorite, and search login items; deleting is asked for and then confirmed.
- Copy username / password to the clipboard with an automatic timed clear.
- Show/hide passwords (hidden by default).
- Cryptographically secure password generator.
- Security Center with local-only checks (reused / weak passwords, duplicate entries, HTTP URLs).
- Encrypted, versioned, atomic and durable on-disk vault format that fails closed on tampering.
- Refuses to overwrite a vault that changed on disk while it was open, and runs only one instance
  per vault.
- Copied secrets are marked so that the system does not keep them: on Windows this keeps them out
  of the clipboard history; on Linux and macOS the marker is on the clipboard, and the clipboard
  manager of KDE honors it. Not every clipboard manager does (see
  [docs/SECURITY_MODEL.md](docs/SECURITY_MODEL.md)). The clipboard is also cleared on exit and
  when the workstation is locked or suspended.
- Password fields do not hand what is typed into them to other programs through the accessibility
  interface of the operating system, and every input says what it is for to a screen reader.
- Fails closed on an unexpected error: the vault is locked and a copied secret is cleared.
- Can be run on a data directory of its own (`--data-directory`), next to the vault at the default
  location. See [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).
- Sign in to a BitosX account (created at `vault.bitosx.se`) with the account email and account
  password — a different secret from the master password — and see and end the account's sessions.
  Tokens are held in memory only and dropped when the vault locks. `--api-server none` makes a run
  local-only; `--api-server https://host` points it at a server of your own. See
  [docs/ACCOUNT.md](docs/ACCOUNT.md).

## Requirements

- .NET SDK **10.0** (developed against 10.0.401).
- Windows, Linux, or macOS (the crypto and storage are cross-platform; the app is Avalonia). The
  tests run on all three for every change. The application itself has only been run on Windows.

## Build, test, run

```bash
dotnet restore
dotnet build
dotnet test
dotnet run --project src/BitosX.App
```

## Solution layout

```
src/
  BitosX.App        Avalonia desktop UI (views, view models, DI composition root)
  BitosX.Core       Domain models, vault service, password generator, security analyzer, validation
  BitosX.Crypto     KDF (Argon2id), AEAD (AES-256-GCM), versioned encrypted format, RNG
  BitosX.Storage    Atomic encrypted local file persistence
  BitosX.Platform   Platform policy (clipboard, single instance, OS session signals), secure-storage hook
  BitosX.ApiClient  The BitosX server: HTTPS client, failure model, in-memory account session
tests/
  BitosX.ApiClient.Tests  The HTTPS client against a scripted server; the account session
  BitosX.App.Tests        View models; the real views and the whole application, run without a display
  BitosX.Core.Tests
  BitosX.Crypto.Tests
  BitosX.Platform.Tests   Clipboard policy and single-instance tests
  BitosX.Storage.Tests
tools/
  BitosX.KdfBenchmark     Diagnostic-only Argon2id unlock-latency benchmark, per profile (not shipped)
  BitosX.ClipboardProbe   Diagnostic-only check against the real Windows clipboard (not shipped, not in the solution)
  BitosX.ClipboardCheck   Diagnostic-only check against the real clipboard of Linux and macOS (not shipped)
  BitosX.GuiCheck         Diagnostic-only: the manual checklists, on the real application (not shipped, not in the solution)
docs/
  ARCHITECTURE.md  SECURITY_MODEL.md  THREAT_MODEL.md  CRYPTOGRAPHY.md  DEVELOPMENT.md
  VERIFICATION.md  VERSIONING.md  AUTO_LOCK.md  BACKUP_RESTORE.md  KEY_ROTATION.md
  KEY_DERIVATION.md  MASTER_PASSWORD.md  ACCOUNT.md  SECURITY_REVIEW_0.5.1.md  SECURITY_REVIEW_0.7.0.md
.github/workflows/ci.yml  Builds and tests on Linux, macOS and Windows for every change
```

`BitosX.Core` and `BitosX.Crypto` do not depend on Avalonia.

## Where the vault is stored

By default the encrypted vault lives at your per-user application data directory, e.g.
`%APPDATA%\BitosX\vault.bitosx` on Windows. The plaintext vault contents are never written to disk.
`--data-directory <full path>` keeps the vault and the settings somewhere else.

## Security

BitosX aims for a zero-knowledge design as it grows. See [SECURITY.md](SECURITY.md) and the
documents under [`docs/`](docs/) for the security model, threat model, and cryptographic design,
**including an explicit list of what BitosX does and does not protect against.**

The findings of the latest security review, and what was done about each, are in
[docs/SECURITY_REVIEW_0.7.0.md](docs/SECURITY_REVIEW_0.7.0.md). What has and has not been
verified, and how, is in [docs/VERIFICATION.md](docs/VERIFICATION.md).

If you forget your master password, your vault cannot be recovered. There is no backdoor,
recovery key, or universal decryption key.
