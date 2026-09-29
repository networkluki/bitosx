# Security Policy

## Overview

BitosX is designed to keep your secrets encrypted locally and to move toward a zero-knowledge
architecture. This document summarizes the security posture of the current local-vault
milestone and how to report issues. Detailed models live in [`docs/`](docs/):

- [docs/SECURITY_MODEL.md](docs/SECURITY_MODEL.md) — what BitosX protects against and what it does not.
- [docs/THREAT_MODEL.md](docs/THREAT_MODEL.md) — specific threats and BitosX's response to each.
- [docs/CRYPTOGRAPHY.md](docs/CRYPTOGRAPHY.md) — algorithms, parameters, and the encrypted format.

## Core principles

- **Local only (this milestone).** No network calls; nothing is sent to any server.
- **Zero-knowledge intent.** As server features are added, the master password, plaintext
  secrets, the raw vault key, and decrypted vault contents must never be sent to a server.
- **Fail closed.** Unknown formats, unsupported versions, corrupted data, and authentication
  failures are rejected. There is never a plaintext fallback.
- **No backdoors.** There is no universal decryption key, no hidden recovery access, and no
  telemetry of secret values.

## What is never logged

The master password, item passwords, secure notes, encryption keys, authentication secrets,
and decrypted vault contents are never written to logs. Logging is limited to non-sensitive
lifecycle events (see `BitosX.Core.Diagnostics.VaultLog`).

## Cryptography summary

- **Key derivation:** Argon2id (memory-hard) from the master password.
- **Encryption:** AES-256-GCM authenticated encryption.
- A random 256-bit vault key encrypts the vault and is itself wrapped by a key derived from
  the master password. See [docs/CRYPTOGRAPHY.md](docs/CRYPTOGRAPHY.md).

## Known limitations (honest disclosure)

- Managed .NET memory cannot be guaranteed to be wiped; secrets in `string` objects may persist
  in process memory. BitosX minimizes secret lifetime and wipes byte buffers on a best-effort basis.
- A copied secret is marked so that the operating system and clipboard managers leave it alone.
  On Windows this was verified against the real clipboard history; on macOS and Linux the markers
  are set but **not verified**. Nowhere can BitosX take back a copy that another application has
  already made of the clipboard.
- A weak master password undermines all protection, because it is the root of the key hierarchy.
  The strength rating shown in the application is a heuristic without a dictionary. Stronger
  key-derivation settings make every guess more expensive; they do not rescue a password that can
  be guessed in a few thousand attempts.
- Improving a vault's protection — a new master password, a rotated key, stronger key derivation —
  applies from then on. Copies and backups made earlier keep the protection they were made with.
- BitosX has only been run on Windows. macOS and Linux are **not verified**.

See [docs/SECURITY_MODEL.md](docs/SECURITY_MODEL.md) for the full list, and
[docs/SECURITY_REVIEW_0.5.1.md](docs/SECURITY_REVIEW_0.5.1.md) for the findings of the latest
security review and what was done about them.

## Reporting a vulnerability

This is a pre-release, local-only milestone. If you find a security issue, please report it
privately to the maintainers rather than opening a public issue, and include reproduction steps
and affected versions. Do not include real secrets in reports.
