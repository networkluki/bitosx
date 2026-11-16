# Secure notes

Since 0.16.0 (web: 0.10.0) a vault holds a second kind of item besides logins: a **secure note** —
a name and a text, nothing else. Both clients create, edit, view, search, favorite, delete, sync,
import and export notes. Logins are unchanged.

This document is the specification of that feature: every rule a second implementation needs to
store the same notes, read the same documents into the same items, and show a note the way the
other client does. The desktop application (this repository) and the web vault (bitosx-manager)
both follow it, and the fixture in [`tests/fixtures/notes`](../tests/fixtures/notes) holds the two to
the same results (§6.1), and an encrypted vault in [`tests/fixtures/web`](../tests/fixtures/web)
lets the web vault open a vault the desktop wrote (§6.2). How notes are imported and exported is in
[IMPORT_EXPORT.md](IMPORT_EXPORT.md).

Nothing here touches the server or the vault's file format (the v1 envelope); it changes the
content schema inside the encryption (§2.2).

| What | Where |
|---|---|
| The note (`NoteItem`, `VaultItemType.Note`) | `src/BitosX.Core/Domain` |
| The content schema version (`Vault.SchemaVersionWithNotes`, `RequiredSchemaVersion`) | `src/BitosX.Core/Domain/Vault.cs` |
| Its stored form, read strictly | `src/BitosX.Core/Serialization/VaultSerializer.cs` |
| What a note may be | `VaultValidation.Validate` |
| Adding one | `IVaultService.AddNoteAsync` (editing, deleting and the favorite mark as for every item) |

## 1. The text is as secret as a password

A note's text is what people keep there because it is secret: a recovery code, a PIN, a licence
key, the answers to security questions. It is treated as a password is:

- It exists in plain text only in memory while the vault is unlocked, and is stored only inside the
  encrypted vault.
- It is never written to a log, never in an exception's or an error's text, a tooltip, an
  accessibility name, a window title or a status text. A document that is refused is refused
  without quoting it (§2.3).
- It is hidden on screen until the user asks for it (§3), and leaves the application only through
  Copy, which uses the clipboard that marks it as sensitive and clears it again.
- Search never looks at it: a note is found by its name only (§3.3).
- Locking hides every text that was shown.

## 2. The stored form

### 2.1 The item

An item of the decrypted vault content (PascalCase, like a login's members) with the type
discriminator `"note"`:

```json
{"type":"note","Id":"11111111-1111-4111-8111-111111111111","Name":"Wi-Fi at home","Notes":"Network: FAKE\nKey: FAKE-key","Favorite":false,"CreatedAt":"2026-11-01T09:00:00+00:00","UpdatedAt":"2026-11-02T09:00:00+00:00"}
```

- `type` is `note`, exactly (`Note` and `secure_note` are refused, as every unknown type is). As
  for a login, it is the **first** member.
- The members are exactly the ones every item has: `Id`, `Name`, `Notes`, `Favorite`,
  `CreatedAt`, `UpdatedAt`. The note's text is `Notes`. A note has **no** `Username`, `Password`,
  `Urls` or `Totp`: any of them, or any other member, makes the document refused, as an unknown
  member always does. So does a member twice, or in another letter case.
- The desktop writes them in the order above (the order in which it writes the members every item
  has), and leaves `Notes` out when the note has no text; the order is not part of the format.
- `Name`: 1 to 200 UTF-16 code units, not blank (as for every item).
- `Notes`: a string of at most 16,384 UTF-16 code units, or absent, or `null`, or empty: a note
  with only a name is a note. (A character outside the Basic Multilingual Plane counts as two.)
- `Favorite`, `CreatedAt`, `UpdatedAt`, `Id`: as for every item.

### 2.2 The content schema version

The decrypted document has `schemaVersion`. From 0.16.0 on (web: 0.10.0):

- A vault is **written** with the lowest version that can hold it:
  - `1` when it holds no note and no login has an authenticator key;
  - `2` when at least one login has an authenticator key and it holds no note ([TOTP.md](TOTP.md) §2.2);
  - `3` when it holds at least one note, with or without keys.

  A vault without notes is therefore byte for byte what 0.15.0 wrote (and, without keys, what
  0.14 wrote).
- Versions 1, 2 and 3 are **read**. A note in a version 1 or 2 document is refused; an
  authenticator key in a version 1 document stays refused. A version 3 document without a note is
  read, and written back as version 2 or 1. Every other version (0, 4 and above) is refused.
- Unknown members and unknown types stay refused, as before.

**What this does to older clients.** Desktop 0.15.0 and older and the web vault 0.9.0 and older
read versions 1 and 2 only. They refuse a vault that holds a note with their existing "unsupported
vault schema version" error, and therefore never get far enough to save over it: nothing is lost,
but that vault cannot be opened by them until every note is deleted with a newer client. A vault
without notes stays readable by them.

### 2.3 Refusals

A document that breaks any of §2.1 or §2.2 is refused, the vault is not opened, and the refusal
never quotes the content: the desktop says "The vault contains an invalid item." for an item that
breaks a rule (a note in version 1 or 2 included), "The decrypted vault content is not valid JSON."
for an unknown member or type, and "Unsupported vault schema version N." for a version it does not
read. The shared fixture (§6) tells apart only two kinds: `unsupported_version` and `invalid`.

## 3. Behaviour

What follows is how the desktop does it; the web vault follows it, each in its own way of laying
out a page. The words are the same in both.

### 3.1 Creating

The vault page has "+ Add login" and, next to it, "+ Add note". The editor of a note is titled
"Add note" ("Edit note" for an existing one) and has:

- **Name \*** (required; the accessible name `Name`; the placeholder `e.g. Wi-Fi at home`). Saved
  trimmed. Save is disabled while it is blank.
- **Text**: a box of several lines (the accessible name `Text`), **masked until "Show"** is pressed
  next to it, for a new note as for an existing one, as the password box of a login is. "Show"
  becomes "Hide"; the tooltip is "Show or hide the text". Nothing remembers the choice: the next
  editor starts masked again. While masked, the box tells the accessibility interface only the
  mask (the desktop's `SecretBox`), never the text.
- **Favorite** (a check box).
- Cancel and Save.

The text is stored exactly as typed — nothing is trimmed, line ends are kept — except that a text
that is empty or blank (only white space) is stored as no text (`Notes` absent), as the notes of a
login are. A text longer than 16,384 UTF-16 code units cannot be saved: Save is disabled and the
editor says "The text is longer than 16384 characters." (never quoting it).

### 3.2 The list, and viewing a note

Notes and logins are in one list, each a card.

- A note's card has the favorite star (the same accessible names as a login's: "Add NAME to
  favorites" / "Remove NAME from favorites"), the name, and under it the label **Secure note**,
  which is text and therefore read by a screen reader with the card. A login's card has no label,
  as before.
- Under it, if the note has a text, the text **hidden** as `••••••••••` (always ten, whatever its
  length), until "Show" is pressed. Shown, it is plain text of several lines, wrapped, never markup
  (nothing in it is interpreted: `<b>` is the three characters `<b>`), and not selectable: it
  leaves the application only through Copy. "Show" becomes "Hide".
- The actions: **Show** / **Hide**, **Copy text**, **Edit**, **Delete**. A note without a text has
  neither Show nor Copy text. Their accessible names carry the note's name, never its text:
  "Show the text of NAME" / "Hide the text of NAME", "Copy the text of NAME", "Edit NAME",
  "Delete NAME".
- **Copy text** copies the stored text exactly through the clipboard that clears itself, and says
  "Text copied. Clipboard clears in 30s." (the delay is the clipboard's), or "Text could not be
  copied. Please try again.".
- **Delete** asks first: "Delete this note? This cannot be undone.", with **Keep** and **Delete**;
  done, it says "Item deleted.". Saving says "Saved.".
- A text that was shown is hidden again whenever the list is built again (after a save, a delete,
  a change of the favorite mark) and when the vault locks.

### 3.3 Search, favorites, order

- The search box matches a note by its **name only** — never its text — in the same way it matches
  a login's name (case-insensitive, a part of the name). Its placeholder stays "Search by name,
  username, or URL".
- Favorites first, then by name in the order of the user's language, logins and notes together.
- With nothing in the vault: "No logins or notes yet. Click “Add login” or “Add note” to create
  your first entry." With nothing that matches: "No logins or notes match “TERM”."

### 3.4 The security overview

The Security Center's checks (reused, weak passwords, duplicates, insecure addresses) look at logins
only. A note is never a finding and never counted.

### 3.5 Locking

Locking — by hand, by the timer, by the system — hides every text that was shown, and the page is
let go of; nothing of a note is on screen while the vault is locked.

## 4. Import and export

[IMPORT_EXPORT.md](IMPORT_EXPORT.md) holds the rules; in short:

- Bitwarden's secure notes — `type` 2 in its JSON (§4.2), `type` `note` in its CSV (§3.5) — and the
  notes of BitosX's own JSON export (§6) are imported as notes: the name, the text and the favorite
  mark; the folder and custom fields are extras, as for a login (§7.4). A note without a name is
  named after the first line of its text that is not blank, or `Untitled` (§7.7). KeePass entries
  and the rows of any other CSV file are logins, as before.
- A note is a duplicate of a note with the same name and text, never of a login (§8). The preview
  counts the notes to add apart from the logins (§9).
- BitosX's JSON export writes every note (`"type": "note"`, §11.2); the CSV export cannot hold them,
  leaves them out and says how many (§11.1).
- An older client (desktop 0.15.0, web 0.9.0) importing a JSON export that holds notes counts them as
  unsupported (`other`) and imports the logins only.

## 5. Sync

A note is part of the vault content, which is encrypted before it leaves the device; the server
sees the envelope only. A vault with a note is uploaded and downloaded as every vault is
([SYNC.md](SYNC.md)); a device with an older client refuses to open it (§2.2).

## 6. The fixtures

### 6.1 `tests/fixtures/notes/content.json`: the content schema

[`tests/fixtures/notes/content.json`](../tests/fixtures/notes/content.json) holds decrypted vault
documents and what is read from each. Git does not convert its line ends (`.gitattributes`); the
web vault copies it unchanged and has to arrive at the same results. The results are **written by
the tests**, never by hand (`tests/BitosX.Core.Tests/Notes/NoteFixtures.cs`), and a test fails if
the committed file differs from what the library produces. A new case is added by hand with its
`name` and its `input` and no `result`; then:

```bash
BITOSX_UPDATE_NOTE_FIXTURES=1 dotnet test tests/BitosX.Core.Tests --filter "FullyQualifiedName~NoteFixtureTests"
git diff tests/fixtures/notes     # look at what changed, and say so here
```

```json
{
  "schema": 1,
  "cases": [
    {
      "name": "version 3: a note",
      "input": "{\"schemaVersion\":3,\"items\":[{\"type\":\"note\",…}]}",
      "result": {
        "schemaVersion": 3,
        "items": [ { "type": "note", "id": "…", "name": "FAKE note", "notes": "FAKE note text", "favorite": false } ],
        "writtenSchemaVersion": 3
      }
    }
  ]
}
```

- `input` is the decrypted document as text, or `{ "parts": [ { "text": "…", "repeat": n }, … ] }`:
  each text repeated that many times, one after the other (for the long ones).
- `result` is either `{ "refused": "unsupported_version" | "invalid" }`, or what is read: the
  version it was read with, its items in order (`type`, `id`, `name`, `notes` — the text, or `null`
  when absent or `null` —, `favorite`, and for a login `hasTotp`), and the version the vault is
  written back with.
- 41 cases: every version read and written, a note in versions 1 and 2, versions 0 and 4, a note
  with an empty, absent and `null` text, a text of several lines with text outside ASCII and what
  looks like markup, the limits of the name and the text (also in characters outside the Basic
  Multilingual Plane), every login member and an unknown one on a note, a member twice or in lower
  case, a type in capitals and `secure_note`, a note and a login with one id.

Every value in it is made up.

### 6.2 `tests/fixtures/web/notes.vault.json`: an encrypted vault

An encrypted vault (envelope v1) for the web vault's tests, **written by this repository's own
code** (`VaultCryptor`, `EncryptedVaultSerializer`, `VaultSerializer`, `MasterPasswordEncoding`) in
the way `bitosx-manager/tests/fixtures/README.md` describes for the others: the test profile
(Argon2id 1 MiB, t=1, p=1), the password `correct horse battery`, content schema version 3, two
notes — a favorite one whose text has several lines (CR LF and LF), text outside ASCII, an emoji, a
tab and what looks like markup (`<script>`, `**…**`); one with only a name (no `Notes` member) — and
two logins, one with an authenticator key and one without. `notes.meta.json` has the members of the
other `*.meta.json` files and one more, `items`: each item as the desktop reads it, in order (`id`,
`type`, `name`, `notes` or `null`, `favorite`, and `hasTotp` for a login). The web vault copies both
files unchanged. A test here (`NotesWebVaultFixtureTests`) opens the vault with the desktop's code
and checks every member of the meta file, so the two cannot drift apart. The generator itself is not
kept: the envelope has random nonces and a random salt, and is never written again for a change
that does not need it.

## 7. Where the two clients can differ

- The desktop refuses an item whose `type` is not its first member (for logins too, since 0.1).
  The fixture has no such case.
