# Encrypted sync (0.9.0)

Since 0.9.0 the desktop application can keep the vault on the BitosX server (`api.bitosx.se`)
and bring it to another device. This document says what goes up, what comes down, what the
server can and cannot do with it, how two devices that both changed are handled, and how it fails.
The account it stands on is described in [ACCOUNT.md](ACCOUNT.md).

## What goes up

**The vault file, as it is on disk.** The file is a JSON envelope: the Argon2id parameters and
salt, the wrapped vault key, two nonces and the AES-256-GCM ciphertext
([CRYPTOGRAPHY.md](CRYPTOGRAPHY.md)). Every field of it is either public or ciphertext. The
application sends those bytes in the body of `PUT /v1/sync/envelope`, Base64-encoded, together
with the **base revision**: the server revision this device last agreed with.

Never: the master password, the vault key, any item, any note. Nothing is decrypted to sync, and
the server has no way to decrypt what it stores: it holds the envelope as opaque bytes, with the
SHA-256 of those bytes as a fingerprint, a revision number, a size and a time. On the server side
this is `bitosx-api/docs/SECURITY_MODEL.md`, "Encrypted-envelope sync".

## Revisions, and why nothing is merged

The server keeps one envelope per account and numbers its versions: the first upload is revision
1, every accepted upload adds one. An upload names the revision it is based on, and the server
accepts it only if that is still the current revision; otherwise it answers 409 and keeps what it
had. Two devices that upload at the same time therefore produce exactly one new revision; the
other is told.

Two vaults cannot be merged without decrypting both, and the server cannot decrypt. So the
application does not merge, and does not pretend to. Each device keeps a small record next to its
vault (`sync-state.json`: the revision it last agreed with, and the fingerprint its own vault file
had then), and **Sync now** compares three things — this device's vault, that record, and what
the server holds — and does exactly one of:

| This device's vault | The server | Result |
|---|---|---|
| same bytes as the server's | — | **Up to date.** Nothing sent. |
| changed since last sync | unchanged since last sync | **Uploaded.** The server takes this device's vault. |
| unchanged since last sync | newer revision | **Server ahead.** Nothing changed; the download is offered. |
| changed | newer revision | **Conflict.** Nothing changed; both ways out are offered. |
| never synced here | holds a different vault | **Conflict.** There is no way to tell which is newer. |
| anything | holds nothing | **Uploaded** as revision 1. |

Nothing happens by itself. There is no background sync and no sync on save: every transfer is
the **Sync now** button, and every replacement is a checkbox and a button.

## What comes down, and how it is let in

**Taking the server's vault** replaces the vault on this device. It goes through the same path a
backup file goes through ([BACKUP_RESTORE.md](BACKUP_RESTORE.md)), in this order:

1. `GET /v1/sync/envelope`. The client checks that the bytes it received have the size and the
   SHA-256 the server states for them; a mismatch is refused as an unexpected answer.
2. The bytes are parsed as a vault envelope with the same fail-closed parser the vault file goes
   through. Anything that is not a well-formed envelope is refused before any password is used.
3. **The master password of the server's vault** is asked for — it may differ from this device's —
   and the envelope is opened with it, on this device, and its decrypted contents validated. A
   wrong password or an invalid vault fails closed. The server never sees the password; nothing
   decrypted is kept beyond the check.
4. A summary is shown (revision, format, item count, size, when stored) and the user ticks
   *"I understand that the vault on this device is replaced with the server's and locked"*.
5. Only then is the vault replaced: the current one is kept as a recovery copy
   (`vault.bitosx.pre-restore`), the new one is written atomically, the sync record is updated,
   and the vault locks so it is unlocked with the downloaded vault's master password.

The acknowledgement cannot be ticked before a download has been validated, and changing anything
— the password, cancelling, a new sync — clears both.

## The ways out of a conflict

- **Take the server's vault**, as above.
- **Replace the server's vault with this device's.** Behind a second acknowledgement
  (*"...changes the vault on the server for every device"*). Still compare-and-set against the
  revision just observed, so a device that uploads in between is not silently overwritten; the
  other devices then see a conflict of their own and choose in turn.
- **Remove the vault from the server**, behind the same acknowledgement. This device's vault is
  untouched, and its sync record is forgotten.

## What is on disk

- `vault.bitosx` — the vault, unchanged by this milestone (format v1).
- `sync-state.json`, next to it — `{ version: 1, revision, localFingerprint, syncedAt }`. Public
  metadata about ciphertext. Written atomically; anything that is not exactly this record reads as
  "never synced", which only makes the next sync more careful.
- `vault.bitosx.pre-restore` — the recovery copy a download leaves behind.

## How the client behaves

- Every sync call is an authenticated call of the account session ([ACCOUNT.md](ACCOUNT.md)):
  the bearer token, the refresh, the 401 that ends the session, the fixed failure messages.
- The download may be up to 16 MiB (the vault file's own limit, which the server mirrors); every
  other answer stays capped at 64 KiB. The upload refuses a vault above the limit before sending.
- One operation at a time; a second is refused, never queued.
- Logging: the outcome and the revision numbers (`ApiLog`); never a fingerprint of the vault,
  a password, or the server's text.

## What the server learns

The size of the vault and when it changes. Nothing about what is in it. A server that is
compromised can withhold the vault, serve an old revision, or serve bytes that are not the
vault — the client refuses the last by the server's own fingerprint and by the parser, and the
first two leave the user with the vault on the device. It cannot produce a vault that opens with
the user's master password without that password.

## Verification

Automated (`tests/BitosX.ApiClient.Tests`, `tests/BitosX.Storage.Tests`, `tests/BitosX.App.Tests`),
for every change:

- what each sync call sends and accepts; a fingerprint, size or revision the server states that
  does not match the bytes is refused; 409 is a conflict with nothing of the server's text in it;
- every row of the table above, over an in-memory server that enforces compare-and-set, with the
  record and the vault proven unchanged where the table says nothing changes; an upload another
  device beats to the server is a conflict that records nothing;
- a download replaces the vault before the record is updated, and a download that fails to write
  records nothing; bytes that are not a vault are refused before the password is used;
- the sync record: round trip, atomic write, every malformed or implausible file reads as never synced;
- the Sync card: no replacement without the acknowledgement, the acknowledgement impossible before
  a validated download, the master password cleared after every attempt, each failure's sentence;
- the whole application, without a display: the first sync uploads the vault file byte for byte, a
  conflict shows both ways out and uploads nothing; every control has a name for a screen reader;
  the card is drawn at both window sizes in all three states.

**NOT VERIFIED:** against the real server (`api.bitosx.se` is not publicly reachable yet;
`bitosx-api/docs/DEPLOYMENT.md`), and between two real devices. The contract implemented is
`bitosx-api/docs/API.md`, "Encrypted sync".
