# BitosX

BitosX is a security-focused password manager. This repository currently contains the
**local encrypted vault foundation** — a cross-platform Avalonia desktop application that
creates and manages an encrypted local vault. There is intentionally **no server, sync,
account, subscription, browser extension, or mobile app** in this milestone; everything runs
and stays on the local device.

## What works in this milestone

- Create a local vault protected by a master password (Argon2id + AES-256-GCM).
- Unlock / lock the vault; incorrect passwords are rejected.
- Automatic locking: inactivity timeout (configurable), workstation lock and system suspend
  (Windows), and optional lock-on-minimize. See [docs/AUTO_LOCK.md](docs/AUTO_LOCK.md).
- Change the master password (re-wraps the vault key; contents are not re-encrypted and the vault
  format is unchanged). See [docs/CRYPTOGRAPHY.md](docs/CRYPTOGRAPHY.md).
- Add, edit, delete, favorite, and search login items.
- Copy username / password to the clipboard with an automatic timed clear.
- Show/hide passwords (hidden by default).
- Cryptographically secure password generator.
- Security Center with local-only checks (reused / weak passwords, duplicate entries, HTTP URLs).
- Encrypted, versioned, atomic on-disk vault format that fails closed on tampering.

## Requirements

- .NET SDK **10.0** (developed against 10.0.401).
- Windows, Linux, or macOS (the crypto and storage are cross-platform; the app is Avalonia).

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
  BitosX.Platform   Platform policy (clipboard auto-clear) and secure-storage extension point
  BitosX.ApiClient  Local-only API abstraction (no production server in this milestone)
tests/
  BitosX.Core.Tests
  BitosX.Crypto.Tests
  BitosX.Platform.Tests   Clipboard auto-clear policy tests
  BitosX.Storage.Tests
tools/
  BitosX.KdfBenchmark     Diagnostic-only Argon2id unlock-latency benchmark (not shipped)
docs/
  ARCHITECTURE.md  SECURITY_MODEL.md  THREAT_MODEL.md  CRYPTOGRAPHY.md  DEVELOPMENT.md
  VERIFICATION.md  VERSIONING.md  AUTO_LOCK.md
```

`BitosX.Core` and `BitosX.Crypto` do not depend on Avalonia.

## Where the vault is stored

By default the encrypted vault lives at your per-user application data directory, e.g.
`%APPDATA%\BitosX\vault.bitosx` on Windows. The plaintext vault contents are never written to disk.

## Security

BitosX aims for a zero-knowledge design as it grows. See [SECURITY.md](SECURITY.md) and the
documents under [`docs/`](docs/) for the security model, threat model, and cryptographic design,
**including an explicit list of what BitosX does and does not protect against.**

If you forget your master password, your vault cannot be recovered. There is no backdoor,
recovery key, or universal decryption key.
