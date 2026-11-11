# Security Policy

## Overview

BitosX is designed to keep your secrets encrypted locally and to move toward a zero-knowledge
architecture. This document summarizes the security posture of the current local-vault
milestone and how to report issues. Detailed models live in [`docs/`](docs/):

- [docs/SECURITY_MODEL.md](docs/SECURITY_MODEL.md) — what BitosX protects against and what it does not.
- [docs/THREAT_MODEL.md](docs/THREAT_MODEL.md) — specific threats and BitosX's response to each.
- [docs/CRYPTOGRAPHY.md](docs/CRYPTOGRAPHY.md) — algorithms, parameters, and the encrypted format.

## Core principles

- **The vault stays local.** The master password, plaintext secrets, the raw vault key, and
  decrypted vault contents are never sent to a server. Since 0.8.0 the application talks to the
  BitosX server for the account, and since 0.9.0 it can store the vault there as the encrypted file
  it is on disk, which the server cannot open; what is sent is listed in
  [docs/ACCOUNT.md](docs/ACCOUNT.md) and [docs/SYNC.md](docs/SYNC.md). `--api-server none` makes a
  run local-only.
- **Fail closed.** Unknown formats, unsupported versions, corrupted data, and authentication
  failures are rejected. There is never a plaintext fallback.
- **No backdoors.** There is no universal decryption key, no hidden recovery access, and no
  telemetry of secret values.

## What is never logged

The master password, item passwords, secure notes, encryption keys, authentication secrets,
the account password, the account tokens, session ids, and decrypted vault contents are never written to logs. Logging is limited to non-sensitive
lifecycle events (see `BitosX.Core.Diagnostics.VaultLog` and `BitosX.ApiClient.ApiLog`).

## The account session

The account tokens are held in memory only. Since 0.11.0 the session ends on the server — best
effort, refreshing first when the access token has expired — whenever the vault locks (by hand,
by inactivity, on workstation lock or sleep) and when BitosX closes; a sign-in or refresh that
completes after the lock does not bring it back. "Sign out everywhere" reports success only when
the server confirmed it. Changing the account password ends every other session. None of this
touches the vault master password, which never leaves the device. See [docs/ACCOUNT.md](docs/ACCOUNT.md).

## Cryptography summary

- **Key derivation:** Argon2id (memory-hard) from the master password.
- **Encryption:** AES-256-GCM authenticated encryption.
- A random 256-bit vault key encrypts the vault and is itself wrapped by a key derived from
  the master password. See [docs/CRYPTOGRAPHY.md](docs/CRYPTOGRAPHY.md).

## Known limitations (honest disclosure)

- Managed .NET memory cannot be guaranteed to be wiped; secrets in `string` objects may persist
  in process memory. BitosX minimizes secret lifetime and wipes byte buffers on a best-effort basis.
- Revealed and generated passwords are shown as text that cannot be selected, so that they leave
  the application only through Copy, which marks them as sensitive and clears them again. Anyone
  who can see the screen can still read a password that is shown.
- A copied secret is marked so that the operating system and clipboard managers leave it alone.
  The marker is a request. It was verified against the real clipboard of Windows (and its
  clipboard history), Linux and macOS, and the clipboard manager of KDE honors it — but CopyQ
  7.1.0 records a secret that carries it, and no clipboard manager for macOS or Windows has been
  asked. Nowhere can BitosX take back a copy that another application has already made of the
  clipboard.
- What is shown on screen is also told to the accessibility interface of the operating system,
  and with it to whatever software uses that interface. Password fields tell it the dots they
  show; they cannot tell it that they are password fields, so a screen reader may speak what is
  typed.
- A weak master password undermines all protection, because it is the root of the key hierarchy.
  The strength rating shown in the application is a heuristic without a dictionary. Stronger
  key-derivation settings make every guess more expensive; they do not rescue a password that can
  be guessed in a few thousand attempts.
- Improving a vault's protection — a new master password, a rotated key, stronger key derivation —
  applies from then on. Copies and backups made earlier keep the protection they were made with.
- An unencrypted export (Settings, "Import and export") is what it says: every password in plain
  text, in a file of your choosing. BitosX asks for the master password before it writes one and
  has no say over the file afterwards. If BitosX or the computer stops while one is being written,
  a temporary file with some or all of the passwords can be left next to it
  (`<name>.<32 hex digits>.tmp`), and nothing removes it for you. A file you import stays where it
  is, in plain text, until you delete it. See [docs/IMPORT_EXPORT.md](docs/IMPORT_EXPORT.md).
- Importing was written from the source code of the programs whose exports it reads, and tested
  with made-up files. No export produced by those programs has been imported yet.
- The application itself has only been run on Windows. On Linux and macOS its tests run for every
  change — including the whole application without a display — but the application has **not**
  been started there, and locking on workstation lock and suspend is not implemented there.
- Nobody has looked at a real window since 0.5.0. The screens are drawn and checked without a
  display, and the manual checklists are carried out on the real application by a tool.

See [docs/SECURITY_MODEL.md](docs/SECURITY_MODEL.md) for the full list,
[docs/VERIFICATION.md](docs/VERIFICATION.md) for what has and has not been verified, and
[docs/SECURITY_REVIEW_0.7.0.md](docs/SECURITY_REVIEW_0.7.0.md) for the findings of the latest
security review and what was done about them.

## Reporting a vulnerability

This is a pre-release milestone. If you find a security issue, please report it
privately to the maintainers rather than opening a public issue, and include reproduction steps
and affected versions. Do not include real secrets in reports.
