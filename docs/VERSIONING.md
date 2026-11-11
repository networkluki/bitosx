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

**`0.14.1`** — one rule of an import. A web address with a backslash before the `@`
(`https://evil.example\@bank.example/`) is no longer taken for an address both clients read
alike: a browser ends the host at the backslash and goes to `evil.example`, while the text after
the `@` says `bank.example`. The desktop already refused such addresses through .NET's own rule,
and the web vault accepted them; the shared rule of [IMPORT_EXPORT.md](IMPORT_EXPORT.md) §7.3 now
refuses a backslash anywhere in the authority, in both. An entry with such an address is listed as
not imported, like one with any other address that is not valid. No expected file of the fixtures
changes. §15.3 of the specification now lists the differences between the two clients that
generating addresses brought out, and which of them are left as they are. The vault format, the
content schema and the server are unchanged. **Not verified:** as for `0.14.0` below.

Builds on `0.14.0` — import and export. Settings has a card "Import and export". **Import** reads a file
another password manager exported and adds its logins: a CSV file (one reader that finds its columns
by their names; the headers of Chrome and Edge, Firefox, Bitwarden, KeePassXC and KeePass 1.x are
recognised and named), Bitwarden's unencrypted JSON, KeePass 2 XML, and BitosX's own JSON export. A
preview shows what would be added, the duplicates, what cannot be imported and why, what is not a
login, and what BitosX has no field for (authenticator keys, folders, custom fields, addresses that
are not web addresses — left out, or copied into the notes if chosen). Nothing is written before it
is confirmed; then every login is added in one write, or none is. Nothing is ever cut short to make
it fit. **Export** writes the vault unencrypted, as CSV (Chrome's layout, one address per item) or
BitosX JSON (everything), after a warning, a box that has to be ticked, and the master password,
which is verified before anything is written; the file is written atomically and, on Unix, readable
by its owner only. Locking closes both and lets go of what they hold. Every file to import is
treated as hostile: size and entry limits, strict UTF-8, no document type declarations, bounded
nesting, one pass. The rules are a specification both clients follow, with fixtures that hold them
to the same results: [IMPORT_EXPORT.md](IMPORT_EXPORT.md). A vault file larger than BitosX can read
back (16 MiB) is no longer written, by an import or by anything else. The vault format, the content
schema and the server are unchanged. **Not verified:** the readers were written from the exporters'
source code, and no file produced by running those programs has been imported; 1Password's CSV
layout could not be verified at all; nothing of this was run on Windows or macOS, and not in a real
window (the GuiCheck does not cover it). See [VERIFICATION.md](VERIFICATION.md).

Builds on `0.13.1` — how a file is replaced on Windows. Every file BitosX keeps (the vault, the settings,
the sync record, a backup, the recovery copies) is written to a temporary file and then put in the
place of the old one. On Windows that last step was `File.Replace`, which is the Win32 `ReplaceFile`:
several steps in one call. Its documentation lists failures (`ERROR_UNABLE_TO_MOVE_REPLACEMENT`,
`ERROR_UNABLE_TO_MOVE_REPLACEMENT_2`) after which the file that was to be replaced is no longer under
its name, with the new content only in the temporary file — which a failed write then deleted. No
such loss has been observed; the documented behaviour was enough to stop relying on it. The step is
now one rename over the old file (`File.Move` with `overwrite`, i.e. `MoveFileEx` with
`MOVEFILE_REPLACE_EXISTING`; on Unix it always was `rename(2)`): if it fails, both files are what and
where they were. The price is that Windows refuses that rename while another program has the old file
open (its documented rule for a rename over an existing file; `ReplaceFile` may have been more
forgiving of some of them, which was not measured). It is tried again for up to 1.5 s, as before,
and then reported, with the vault in memory and on disk intact. The retry now
counts the pauses it asks for instead of the clock, and its tests hand in the waiting, after one of
them failed on a loaded build machine (the `main` build after 0.13.0). **Not verified:** what
Windows leaves behind when the power fails during the rename (NTFS journals it; this was not
tested), and the same for `ReplaceFile` before.

Builds on `0.13.0` — passkeys, as far as the desktop goes. Passkeys (WebAuthn) are added, renamed,
removed and used on vault.bitosx.se, on top of the authenticator code, never instead of it; this
application has no WebAuthn and keeps signing in with the authenticator code or a recovery code.
The Account page shows how many passkeys the account has, read-only (`GET /v1/account/passkeys`;
the line is hidden on a server without passkeys), and the second sign-in step says, when the server
lists `passkey` among its `methods`, that passkeys are used on the web vault and that here the
code is needed. The `methods` of the sign-in challenge are read tolerantly: no list means the code
step, as before; names this client does not know are ignored; a challenge that offers no code step
is refused as unexpected. The vault, its master password and the on-disk format (still v1) are
untouched. The live passkey test (`tests/BitosX.LiveTests`) passed against the local stack of
bitosx-api with passkeys on 2026-11-03. **Not verified:** `api.bitosx.se` itself, which is not
publicly reachable yet.

Builds on `0.12.0` — ready for the real server. BitosX has an icon of its own (a padlock on a
rounded square in its two accent colours), for its windows and for the program file on Windows,
drawn from its geometry by `tools/BitosX.IconGen` instead of the template's placeholder. The search
no longer builds the list again on every keystroke: it shows and hides the cards that are there
(with 5,000 logins, eight keystrokes took 109 s in the headless test before and take under 1 s
now). Failure texts say only what is known: the sync card no longer says "Nothing was changed"
after a permission error or any other failure that can follow an upload, a removal or a
replacement; a 403 on a call that sends no password no longer blames the password; and where no
answer came, "Sign out everywhere", the password change and the account deletion say that the
server did not confirm them instead of claiming either way. And the client has, for the first
time, run against a real API and database: `tests/BitosX.LiveTests` drives the account and the
sync of two devices through the application's own services against the local stack of bitosx-api
(opt-in; see [DEVELOPMENT.md](DEVELOPMENT.md)). The on-disk vault format is unchanged (still v1).
**Not verified:** the icon on a real Windows desktop (Explorer, the taskbar); `api.bitosx.se`
itself, which is not publicly reachable yet.

Builds on `0.11.0` — fine-tuning and the account itself. New on the Account page: change the account
password (every other device is signed out, this one stays; the master password is untouched),
delete the account (the vault file on the device is untouched), the two-factor state (read-only),
and "Forgot your password?" for the production server. Fixed: the account session ends on the
server when the vault locks by inactivity and when BitosX closes, not only on a manual lock; a
sign-in or refresh that completes after the lock no longer brings the session back; "Sign out
everywhere" no longer claims success when the server was not told; every call has a deadline that
covers the body (120 s for envelopes); a 403 is its own failure; the sync record is bound to the
server, the account, and the revision's fingerprint, so a removed and re-uploaded vault is never
taken for "up to date" (`sync-state.json` version 2); URLs are split on line breaks only; revealed
and generated passwords can no longer be selected past the clipboard protections; creating a vault
no longer freezes the window; and a list of smaller fixes in search, order, focus, screen-reader
names, Settings and recovery copies. The on-disk vault format is unchanged (still v1).
**Not verified:** against the real server; the password change, the code in account deletion and
revisions that never restart need the phase 8 server deployed.

Builds on `0.10.0` — second factor at sign-in: when the account has two-factor authentication (set up on
vault.bitosx.se), the server answers the password with a one-step challenge instead of a session,
and the Account page asks for the 6-digit code from the authenticator app or a recovery code. The
challenge token lives in memory for at most five minutes, nothing authenticated can be called before
the code is accepted, and a refused code can be retried or the sign-in started over. The vault, its
master password and the on-disk format (still v1) are untouched. See [ACCOUNT.md](ACCOUNT.md).
**Not verified:** against the real server.

Builds on `0.9.0` — encrypted sync: the vault file, as encrypted on the device, is stored on the BitosX
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
