# Versioning

BitosX uses a three-part version number: **`MAJOR.MINOR.PATCH`** (for example `1.3.2`).

| Position | Example digit in `1.3.2` | Name | Bump it for… |
|---|---|---|---|
| 1st | `1` | MAJOR | **Big changes** — breaking changes, a new on-disk vault format version, removing or redesigning features, anything that isn't backward-compatible. |
| 2nd | `3` | MINOR | **Medium changes** — new features added in a backward-compatible way (e.g. auto-lock, encrypted export). |
| 3rd | `2` | PATCH | **Small changes** — bug fixes, security hardening, docs, and other backward-compatible tweaks that add no new feature. |

This is the widely-used [Semantic Versioning](https://semver.org) convention.

## Rules of thumb

- Increment the **highest** position that applies, and reset the lower positions to `0`
  (e.g. a big change from `0.4.7` → `1.0.0`; a new feature from `1.0.3` → `1.1.0`; a bug fix from
  `1.1.0` → `1.1.1`).
- While the project is pre-1.0 (`0.x.y`), the API and vault format are still stabilizing; a `0.MINOR`
  bump may include larger changes than it would after 1.0.
- A change to the **encrypted vault on-disk format version** (`CryptoConstants.FormatVersion`) is a
  MAJOR change and must ship with an upgrade/migration path.

## Where the version lives

- The single source of truth is `<Version>` in **`Directory.Build.props`** (inherited by every
  project). Bump it there.
- Releases are marked with an annotated Git tag `vMAJOR.MINOR.PATCH` (optionally with a suffix, e.g.
  `v0.1.0-local-vault` for this milestone).

### Why .NET sometimes shows four numbers (e.g. `0.1.0.0`)

Our version is **three-part** (`MAJOR.MINOR.PATCH`). However, the .NET CLR and the Windows
file-version resource require the **AssemblyVersion** and **FileVersion** to have **four** numeric
components (`Major.Minor.Build.Revision`), so `0.1.0` is displayed there as `0.1.0.0`. That trailing
`.0` is platform padding only — BitosX never assigns meaning to a fourth level. The authoritative,
human-facing version stays three-part: the product/informational version is `0.1.0` (built as
`0.1.0+<git commit>`) and the release tag is `v0.1.0-...`.

## Current version

**`0.9.0`** — encrypted sync: the vault file, as encrypted on the device, is stored on the BitosX
server through the account and brought to another device. The server holds opaque bytes and a
fingerprint, never a key; revisions are compare-and-set; nothing is merged, and when both a device
and the server have changed the user is shown both ways out and nothing happens until they choose.
A downloaded vault is opened with its master password and validated on the device, behind an
acknowledgement, before it replaces the local vault, with a recovery copy kept. A small sync record
(`sync-state.json`) lives next to the vault. See [SYNC.md](SYNC.md). The on-disk vault format is
unchanged (still v1). **Not verified:** against the real server, or between two real devices.

Builds on `0.8.0` — the account: the application signs in to the BitosX server (`api.bitosx.se`)
with the account email and account password, shows the account and its sessions, and can end
them. Nothing syncs yet, and nothing from the vault is sent: the server has no vault endpoints
and the client has no method that could carry vault content. Tokens are held in memory only
and dropped when the vault locks. `--api-server` chooses another server or `none`. See
[ACCOUNT.md](ACCOUNT.md). The on-disk vault format is unchanged (still v1). **Not verified:**
against the real server, which is not publicly reachable yet.

Builds on `0.7.0` (tag `v0.7.0-verification`), which was about verifying what 0.6.0 had left unverified, and fixing what that turned up. The tests
now run on Linux and macOS as well as on Windows, for every change; the manual checklists are
carried out on the whole application and on the real application in a real window; every screen
is drawn and checked; and the clipboard markers were verified on the real clipboard of Linux and
macOS. That found, among other things, that a typed master password could be read by other
programs through the accessibility interface, that an empty password field could end the
application, and that Settings could silently put settings back
([SECURITY_REVIEW_0.7.0.md](SECURITY_REVIEW_0.7.0.md)). New in this version: new vaults are created
with 256 MiB of key-derivation memory instead of 64 MiB
([KEY_DERIVATION.md](KEY_DERIVATION.md)), deleting an item is confirmed, and the application can be
run on a data directory of its own (`--data-directory`). No new cryptographic primitive, and the
on-disk vault format is unchanged (still v1): every existing vault and backup opens as before, with
the settings it has. **Not verified:** a person looking at a real window, and the application
itself on Linux or macOS.

Builds on `0.6.0` master password encoding and stronger key derivation (tag
`v0.6.0-password-key-derivation`, see [MASTER_PASSWORD.md](MASTER_PASSWORD.md)), `0.5.1` security
hardening (tag `v0.5.1-security-hardening`, see
[SECURITY_REVIEW_0.5.1.md](SECURITY_REVIEW_0.5.1.md)), `0.5.0` vault key rotation (tag
`v0.5.0-key-rotation`, see [KEY_ROTATION.md](KEY_ROTATION.md)), `0.4.0` encrypted backup/restore
(tag `v0.4.0-backup-restore`), `0.3.0` master password change (tag `v0.3.0-master-password`),
`0.2.0` automatic locking (tag `v0.2.0-auto-lock`), and the `0.1.0` encrypted local vault baseline
(tag `v0.1.0-local-vault`).
