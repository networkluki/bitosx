# Import and export

BitosX reads the logins another password manager exported, and writes its own vault out
unencrypted for another password manager to read. This document is the specification of both: every
rule a second implementation needs to arrive at exactly the same items. The desktop application
(this repository) and the web vault (bitosx-manager) both follow it, and the fixtures in
[`tests/fixtures/import`](../tests/fixtures/import) hold the two to the same results (§14).

Nothing here touches the server, the vault's file format (the v1 envelope) or the content schema
(version 1, `login` items only). An import adds ordinary items; an export writes a new file.

Where it is in the code:

| What | Where |
|---|---|
| Bytes in, logins out; the plan against a vault; the exporters. No file, no vault. | `src/BitosX.Core/ImportExport` |
| The three steps of an import, and an export behind the master password | `src/BitosX.Core/Services/VaultImportService.cs`, `VaultExportService.cs` |
| Adding several items in one write; verifying the master password | `IVaultService.AddLoginsAsync`, `VerifyMasterPasswordAsync` |
| Reading an import file within its size limit; writing an export atomically | `src/BitosX.Storage/FileImportExportFiles.cs` |

Sources are named where a rule comes from one, with the commit that was read; §15 lists them and
says what could **not** be verified. Reference copies of other programs' code were read as a
description of their formats only: none of it was copied.

## 1. Limits

| Limit | Value | What happens beyond it |
|---|---|---|
| Size of an import file | 16 MiB (16,777,216 bytes) | The file is refused (`too_large`). Checked before the file is read and again on the bytes actually read. |
| Entries in one file | 10,000 | The file is refused (`too_many_entries`); it is never read in part. Every record counts, whatever its kind (§3.5, §4.3, §5.4). |
| Items in the vault after the import | 10,000 | The import is refused as a whole (`vault_item_limit`). This is the web vault's limit on a vault; the desktop applies it to imports only. |
| Size of the vault file after the import | 16 MiB | The import is refused as a whole and nothing is written (§10). |
| Nesting of JSON | 64 arrays and objects inside one another | The file is refused (`invalid_json`). |
| Nesting of groups in a KeePass file | 64, the root group being the first | The file is refused (`nested_too_deeply`). |
| Nesting of elements in an XML file | 100, the root element being the first | The file is refused (`nested_too_deeply`). |
| Name | 1 to 200 | The entry is not imported (§7.6). |
| Username | at most 320 | |
| Password | at most 4096 | |
| Notes | at most 16384 | |
| One web address | at most 2048 | |
| Web addresses of one item | at most 64 | |

The limits on an item are the vault's own (`VaultValidation`; `LIMITS` in the web vault). **Lengths
are counted in UTF-16 code units**: what `string.Length` counts in .NET and `.length` in
JavaScript. A character outside the Basic Multilingual Plane counts as two.

**Nothing is ever cut short to make it fit.** An entry that breaks a limit is not imported, and is
listed with the reason.

## 2. Reading a file

A file is bytes. The steps, in this order:

1. More than 16,777,216 bytes: refused, `too_large`.
2. The first bytes are looked at for a byte order mark. `00 00 FE FF`, `FF FE 00 00`, `FE FF` or
   `FF FE` (UTF-32 and UTF-16, either byte order): refused, `unsupported_encoding`. `EF BB BF`
   (UTF-8) is removed, once.
3. The rest is decoded as UTF-8, strictly: an invalid sequence — an overlong form, half of a
   surrogate pair, a code point beyond U+10FFFF, a sequence cut short — is refused,
   `invalid_utf8`. Nothing is replaced.
4. The first character that is not a space (U+0020), a tab, a carriage return or a line feed
   decides the format. There is none: refused, `empty`.
   - `{` or `[`: JSON (§4).
   - `<`: XML (§5).
   - anything else: comma-separated values (§3).

The format is never taken from the file's name. A file that decodes but is none of the formats
ends in the refusal of the reader it was handed to (`no_recognised_columns`, `not_recognised`,
`invalid_json`, `invalid_xml`).

### 2.1 The text operations used below

These are written out because the two runtimes' own functions differ.

- **Trimmed** means: without these characters at either end — U+0009 to U+000D, U+0020, U+0085,
  U+00A0, U+1680, U+2000 to U+200A, U+2028, U+2029, U+202F, U+205F, U+3000, U+FEFF. (That is what
  .NET's `Trim` and JavaScript's `trim` remove, taken together; each alone leaves one of them.)
  **Blank** means: nothing is left after trimming.
- **In lower case** means: `A` to `Z` become `a` to `z`; every other character stays as it is. No
  rule of any language is applied.
- **Split into lines** means: split at every CR LF, at every LF, and at every CR that no LF
  follows. Nothing else ends a line.
- **A control character** is U+0000 to U+001F, or U+007F.
- Values are compared **ordinally**: code unit by code unit, nothing folded or normalised.

## 3. Comma-separated values

### 3.1 Records and values

RFC 4180, read leniently where that loses nothing and strictly where guessing could change a
password.

- Values are separated by commas. Records are separated by CR LF, LF or CR. The last record needs
  no line end; a line end at the end of the text does not start another record. A text that ends
  right after a comma ends with an empty value.
- A value whose **first character** is a double quote is a quoted value: it runs to the closing
  double quote and may contain commas and line ends, which are kept exactly as they are. Two double
  quotes inside it stand for one. After the closing quote only a comma, a line end or the end of
  the text may follow.
- Every other value is taken as it is up to the next comma or line end. A double quote that is not
  the first character is an ordinary character. A backslash is an ordinary character. **Nothing is
  trimmed.**
- A quoted value that is never closed, or a closing quote followed by anything else, makes the file
  refused: `invalid_csv`, with a line number. Lines are counted from 1; a line end is CR LF, LF or
  CR (a CR LF counts once), also inside quoted values. For a value that is never closed the line is
  the one its opening quote is on; for text after a closing quote it is the line that text is on.
- A record all of whose values are blank is not a record: it is passed over, is not counted, and
  cannot be the header. An empty line is the usual one.

Source: RFC 4180 as cited by the exporters themselves; the writers read are Chromium's
(`csv_writer.cc`: quotes a value that contains CR, LF, a quote or a comma, doubles quotes, and ends
lines with CR LF on Windows and LF elsewhere), Firefox's (`LoginExport.sys.mjs`: every value
quoted, quotes doubled, CR LF between lines, none after the last), KeePassXC's (`CsvExporter.cpp`:
every value quoted, quotes doubled, LF) and PapaParse's, which Bitwarden uses (`papaparse.js`,
`safe()`: quotes when needed, CR LF).

### 3.2 The header

The first record that is not blank is the header. Each of its values is trimmed and put in lower
case; that is the column's name. A column whose name is empty is ignored and has nothing to be
listed by.

For each field, the names it is read from, **in this order**. The first name of the list that the
header has is the field's column; if two columns have that name, the left one. Every other named
column — including one that carries another name of a field that already has its column — is an
*ignored column*: it is not read, and the preview names it (trimmed, in the header's own spelling,
in the header's order).

| Field | Column names, in order | Where each name comes from |
|---|---|---|
| name | `name`, `title`, `account` | `name`: Chromium, Bitwarden. `title`: KeePassXC. `account`: KeePass's "KeePass CSV (1.x)". |
| address | `url`, `urls`, `login_uri`, `uri`, `website`, `web site` | `url`: Chromium, Firefox, KeePassXC. `login_uri`: Bitwarden. `uri`: Bitwarden's importer reads it in place of `login_uri`. `website`: a name Chromium's importer accepts. `web site`: KeePass CSV (1.x). `urls`: not verified (§15). |
| username | `username`, `login_username`, `user name`, `login name` | `username`: Chromium, Firefox, KeePassXC. `login_username`: Bitwarden. `login name`: KeePass CSV (1.x). `user name`: not verified. |
| password | `password`, `login_password` | Chromium, Firefox, KeePassXC; Bitwarden. |
| notes | `note`, `notes`, `comments`, `extra` | `note`: Chromium. `notes`: KeePassXC, Bitwarden. `comments`: KeePass CSV (1.x). `extra`: not verified (LastPass, by two other importers). |
| favorite | `favorite`, `fav` | `favorite`: Bitwarden. `fav`: not verified (LastPass). |
| authenticator key | `totp`, `login_totp`, `otpauth` | `totp`: KeePassXC. `login_totp`: Bitwarden. `otpauth`: not verified (Safari, by Bitwarden's importer). |
| type | `type` | Bitwarden. |
| folder | `folder`, `group`, `grouping` | `folder`: Bitwarden. `group`: KeePassXC. `grouping`: not verified (LastPass). |
| custom fields | `fields` | Bitwarden. |

A file needs a **password** column and at least one of **name**, **address** and **username**.
Without them it is refused: `no_recognised_columns`.

### 3.3 The label

The label says which program the file looks like it came from. It is informational, with one
exception (§3.4). The header's column names — all of them, in order, trimmed and in lower case —
are compared with these lists; a header that equals none of them has the label `CSV`.

| Label | Header | Source |
|---|---|---|
| `Chrome or Edge (CSV)` | `name,url,username,password,note` | Chromium `main`, `password_csv_writer.cc` |
| | `name,url,username,password` | Chromium 90.0.4430.72 and 100.0.4896.60, before notes were exported |
| `Firefox (CSV)` | `url,username,password,httprealm,formactionorigin,guid,timecreated,timelastused,timepasswordchanged` | Firefox `main`, `LoginExport.sys.mjs` |
| `Bitwarden (CSV)` | `folder,favorite,type,name,notes,fields,reprompt,archiveddate,login_uri,login_username,login_password,login_totp` | Bitwarden `main`, an individual vault |
| | `collections,type,name,notes,fields,reprompt,archiveddate,login_uri,login_username,login_password,login_totp` | Bitwarden `main`, an organisation's vault |
| | the same two without `archiveddate` | Bitwarden `web-v2025.6.2` |
| `KeePassXC (CSV)` | `group,title,username,password,url,notes,totp,icon,last modified,created` | KeePassXC `develop`, and 2.6.6 to 2.7.10 |
| | `group,title,username,password,url,notes` | KeePassXC 2.3.4 to 2.6.0 |
| `KeePass 1.x (CSV)` | `account,login name,password,web site,comments` | KeePass 2.59, "KeePass CSV (1.x)" |

Edge is closed source; it is assumed to write what Chromium writes. There is no label for
1Password: its header could not be verified (§15), and its files are read as `CSV`.

### 3.4 KeePass CSV (1.x) quotes differently

KeePass's "KeePass CSV (1.x)" export does not double a quote inside a value: it writes `\"` for a
double quote and `\\` for a backslash (`KeePassCsv1.cs`, `WriteCsvString`). Read by the rules of
§3.1, a password with a backslash in it would come out with two.

So when the header is exactly the one of that label, the records **after** the header are read
with this change: inside a quoted value a backslash makes the character after it part of the value,
whatever that character is, and a double quote always closes the value (two in a row are an empty
value, or a fault). A backslash at the very end of the text is `invalid_csv`. Values that are not
quoted are read as always.

A file with the same column names in another order, or with one more column, is an ordinary CSV
file.

### 3.5 Rows

Every record after the header that is not blank is a row, and counts towards the 10,000. Rows are
numbered from 1 in the order of the file (*position*), whatever becomes of them.

- A row with fewer values than the header has columns has empty values where it ends.
- A row with a value that is **not empty** to the right of the header's last column is not
  imported: `row_has_extra_values`. That is what a comma inside a value that was not quoted looks
  like — every value after it has moved one column, and the password may be half of what it was.
  Empty values to the right of the last column mean nothing.
- **type**: if the row's type, trimmed and in lower case, is `note`, the row is a secure note. It
  is counted as unsupported (`secure_note`) and is not an entry. Every other value, and no value,
  is a login. (`note` is what Bitwarden writes for a secure note, and its importer treats
  everything else as a login. What other exporters put in a column of that name is not known.)
- **name**, **username**, **password**, **notes**: the value as it is.
- **favorite**: the value, trimmed and in lower case, is `1` or `true`. (Bitwarden writes `1` or
  nothing.)
- **authenticator key**, **folder**: the value as it is; trimmed later (§7.4).
- **address**: if the address column is `login_uri` or `uri`, the value is split at every comma
  (Bitwarden writes an item's addresses into one cell with commas between them: the cell is an
  array in `base-vault-export.service.ts`, and PapaParse writes an array as its `toString()`).
  Otherwise it is one value. An empty value is no address. Each value is then treated as §7.2
  says, which is where it is also split into lines.
- **custom fields**: the value is split into lines; a blank line is passed over; each other line
  is one field, split at the **last** `": "` (colon, space) into a name, which is trimmed, and a
  value, which is not. A line without `": "` is a field without a name whose value is the line.
  (Bitwarden writes `name: value`, one per line, and its importer splits at the last `": "`. It
  drops a line without one; BitosX keeps it, as the continuation of a value of several lines.)

## 4. JSON

### 4.1 The document

The JSON has to be strict JSON (RFC 8259): no comments, no trailing commas, nothing after the
value. Beyond that, each of these makes the file refused, `invalid_json`:

- more than 64 arrays and objects inside one another;
- the same name twice in one object, anywhere in the document, compared after escapes are
  resolved (`"a"` and `"\u0061"` are the same name);
- a name or a string, anywhere in the document, that holds half of a surrogate pair. The bytes are
  valid UTF-8 by now, so this can only be an escape: `\uD83D` that is not followed at once by an
  escape for a low surrogate, or an escape for a low surrogate that does not follow one for a high
  surrogate.

A document whose top level is not an object is refused, `not_recognised`. So is an object that is
neither of the two formats below.

What is read from the document is read **one member at a time, leniently**: a member that is
absent, is `null`, or is of another JSON type than the one named below counts as absent. A string
that is absent is the empty string; a boolean that is absent is `false`.

Which format it is, in this order:

1. `format` is the string `bitosx-export`: BitosX's own export (§6).
2. `encrypted` is `true`: an encrypted Bitwarden export; refused, `encrypted_export`.
3. `items` is an array: Bitwarden's unencrypted export (§4.2).
4. Otherwise: refused, `not_recognised`.

### 4.2 Bitwarden's unencrypted export

Source: Bitwarden `main`: the types in `bitwarden-json-export-types.ts`, the writer in
`individual-vault-export.service.ts` and `cipher.export.ts`, and Bitwarden's own reader
`bitwarden-json-importer.ts`.

Both of Bitwarden's encrypted exports carry `"encrypted": true`: the one encrypted with the
account's key (which still has `items`, their values encrypted) and the one protected by a password
(which has `data` and no `items`). Neither is read.

- `folders`: an array of objects with `id` (a string that is not empty) and `name` (a string). Of
  two folders with one `id`, the first counts.
- `items`: an array. More than 10,000 elements: refused, `too_many_entries`. Each element has a
  position, counted from 1.

For each element of `items`, in this order:

1. It is not an object: counted as unsupported, `other`.
2. It has a `deletedDate` that is not `null`: it is in the trash. Counted as such; nothing else is
   looked at.
3. `type` is not a number, or is absent: unsupported, `other`. Otherwise the number decides —
   written any way JSON allows (`1`, `1.0`, `1e0`):

   | `type` | Kind | Code |
   |---|---|---|
   | 1 | login | — |
   | 2 | secure note | `secure_note` |
   | 3 | card | `card` |
   | 4 | identity | `identity` |
   | 5 | SSH key | `ssh_key` |
   | 6 | bank account | `bank_account` |
   | 7 | driving licence | `drivers_license` |
   | 8 | passport | `passport` |
   | anything else | | `other` |

   (`cipher-type.ts`.) Everything but a login is counted by its kind and left out.
4. A login is an entry:
   - name: `name`. notes: `notes`. favorite: `favorite` is `true`.
   - username, password, authenticator key: `login.username`, `login.password`, `login.totp`.
   - addresses: for each element of `login.uris` that is an object, its `uri`, if it is a string
     that is not empty.
   - custom fields: for each element of `fields` that is an object, its `name` (trimmed) and its
     `value`. The field's `type` is not looked at; a linked field has no value, and is therefore
     dropped later (§7.4).
   - folder: the `name` of the folder whose `id` equals `folderId` exactly, if there is one.

An archived item (`archivedDate`) is an ordinary item.

**Not read:** `passwordHistory`, `login.fido2Credentials` (passkeys), the match setting of an
address, `reprompt`, `collectionIds` and `collections` (an organisation's export is read, without
them), attachments (which the JSON does not hold).

## 5. KeePass 2 XML

What KeePass 2 writes as "KeePass XML (2.x)" and KeePassXC writes as an XML export:
`KeePassFile/Root/Group…/Entry`. Sources: KeePass 2.59 (`KdbxFile.cs`, `KdbxFile.Write.cs`,
`PwDefs.cs`) and KeePassXC `develop` (`KdbxXmlWriter.cpp`, `KdbxXmlReader.cpp`, `KdbxWriter.cpp`,
`Group.cpp`).

### 5.1 Before parsing

- If the text contains the characters `<!DOCTYPE` anywhere, in any letter case, the file is
  refused: `doctype`. This is decided on the text, before any parser sees it — also for a
  declaration inside a comment or a CDATA section, which no export contains. With no document type
  declaration there are no entities to expand and nothing to fetch. (The desktop also configures
  its XML reader to prohibit one and to resolve nothing.)
- Spaces, tabs and line ends before the first `<` are removed.
- The bytes were decoded as UTF-8 (§2). The encoding an XML declaration names is not believed.

### 5.2 The document, in the order it is written

The file has to be well-formed XML 1.0. Namespaces mean nothing here: an element is known by its
name **as written**, compared exactly.

The document is read from its start, and **the first fault that is met decides** the refusal.
Whatever comes after it is not looked at:

- text that is not well-formed XML at that point: `invalid_xml` (this includes a reference to an
  entity other than XML's own five, and a character XML cannot hold);
- the root element is not `KeePassFile`: `not_recognised`, as soon as its start tag has been read;
- an element nested more than 100 deep, the root element being the first level:
  `nested_too_deeply`;
- an element — any element, read or passed over — with an attribute `Protected` whose value is
  `1`, or `true` in any letter case: `protected_values`. That mark means the value is encrypted
  with the database's inner key: the file is not a plain export. (`ProtectInMemory="True"` is what
  both applications put on a value that is in plain text in a plain export, and means nothing
  here.)
- a `Group` nested more than 64 deep, the group directly inside `Root` being the first:
  `nested_too_deeply`;
- the 10,001st `Entry` that is directly inside a `Group`: `too_many_entries`.

The root element is asked for its name before anything else. For every other start tag the checks
are in that order: depth, then `Protected`, then what the element is.

**The text of an element** is its own text and CDATA sections, one after the other, exactly as an
XML processor delivers them: character and entity references resolved, and line ends normalised as
XML 1.0 §2.11 requires (a CR LF and a lone CR become LF; a CR written as `&#13;` stays). An element
inside it is passed over. Nothing is trimmed unless said.

### 5.3 What is read

- `KeePassFile` → the first `Meta` → the first `RecycleBinUUID`: its text, trimmed. This names
  the recycle bin.
- `KeePassFile` → the first `Root` → every `Group` directly inside it is a root group.
- In a `Group`: the first `UUID` (text, trimmed), the first `Name` (text, trimmed), every `Entry`
  and every `Group` directly inside it. Everything else is passed over. The order of these inside
  the group does not matter.
- In an `Entry`: every `String` directly inside it. Everything else is passed over — in
  particular `History`, which holds the entry's earlier versions as `Entry` elements: they are
  neither read nor counted.
- In a `String`: the first `Key` and the first `Value`, as text. A `String` without a `Key` is
  passed over; one without a `Value` has the empty value.

The key says what the value is. Keys are compared exactly, and are case sensitive:

| Key | Field |
|---|---|
| `Title` | name |
| `UserName` | username |
| `Password` | password |
| `URL` | address (one value; §7.2) |
| `Notes` | notes |
| any other | a custom field, with the key (trimmed) as its name |

(`PwDefs.cs`; `EntryAttributes.cpp`.) If one of the five occurs more than once in an entry, the
entry is not imported: `field_twice`. (KeePassXC refuses such a file; KeePass keeps the last
value. The first value is the one a preview shows.) A custom field may occur any number of times.

An authenticator key is, in a KeePass file, just another string — `otp` or `TOTP Seed` in
KeePassXC, `TimeOtp-Secret…` in KeePass — and is therefore a custom field here. So is `KP2A_URL`,
where KeePassXC keeps an entry's further addresses.

**Folder:** the names of the groups the entry is in, from the one below its root group down to its
own, with `/` between them. The root group is the database itself and is left out; a group without
a name is left out.

**Recycle bin:** if `RecycleBinUUID` is not empty and is not `AAAAAAAAAAAAAAAAAAAAAA==` (sixteen
zero bytes: "none"), the group whose `UUID` equals it is the recycle bin, whatever
`RecycleBinEnabled` says and whatever the group is called. An entry in it, or in a group inside
it, is in the trash: counted as such, and not an entry. (KeePassXC, `Group::isRecycled`.) `Meta`
may come after `Root`.

### 5.4 Positions and the limit

Every `Entry` directly inside a `Group` has a position, counted from 1 in document order — those
in the recycle bin too. All of them count towards the 10,000.

## 6. BitosX's own export

The file §11.2 describes, read back.

- `version` has to be the number 1 (written any way JSON allows). Anything else, or none:
  refused, `unsupported_version`.
- `items` has to be an array; otherwise `not_recognised`. More than 10,000 elements:
  `too_many_entries`.
- An element that is not an object, or whose `type` is not the string `login`: unsupported,
  `other`.
- A login is an entry: `name`, `username`, `password`, `notes` (strings), `favorite` (`true`),
  and `urls`: each element that is a string is an address.

`exportedAt`, `createdAt` and `updatedAt` are not read: an imported item is a new item (§10).
Members this version does not know are ignored.

## 7. From an entry to an item

Every format arrives at the same thing: an entry with a name, a username, a password, notes, a
favorite mark, address values, an authenticator key, a folder and custom fields. What follows is
the same for all of them.

### 7.1 Name, username, password, notes

The name is trimmed. The username, the password and the notes are taken **exactly as they are**.

### 7.2 Addresses

Each address value is split into lines; each line is trimmed; an empty one is dropped. Each that
is left is told apart by the first of these that applies:

1. It contains a control character: it is an **other address**.
2. It starts with a scheme and `://` — `^[A-Za-z][A-Za-z0-9+.-]*://` — and the scheme is `http`
   or `https` in any letter case: it is a **web address**, as it is. Any other scheme
   (`android://…`, `ftp://…`, `chrome://…`): an other address.
3. It looks like a host: it is a **web address** with `https://` put in front. The whole value
   has to match

   ```
   ^(?:DOMAIN|IPV4)(?::[0-9]{1,5})?(?:[/?#][\s\S]*)?$

   DOMAIN = (?:LABEL\.)+TLD
   LABEL  = [A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?
   TLD    = [A-Za-z](?:[A-Za-z0-9-]*[A-Za-z0-9])?
   IPV4   = OCTET(?:\.OCTET){3}
   OCTET  = 25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9]
   ```

   A name needs a dot, and its last part has to start with a letter. `example.com`,
   `www.example.org/path?x=1` and `192.168.1.1:8080/admin` look like hosts; `localhost`,
   `localhost:3000`, `v1.2`, `user@example.com`, `mailto:x@example.com`, `http:example.com` and
   `My bank` do not.
4. Otherwise: an other address.

Web addresses stay in the order of the file; so do other addresses. The same address twice stays
twice.

An other address is not something BitosX can store as an address. It is an *extra* (§7.4); the
entry is imported without it.

### 7.3 A web address has to have a host both clients read the same way

The vault's rule for an address is each client's own: on the desktop `Uri.TryCreate` with an
absolute `http` or `https` result, in the web vault `new URL` with the protocol `http:` or
`https:`. The two differ about unusual hosts — the desktop refuses `https://a..b`, which the web
vault accepts, and accepts `https://example.123`, which the web vault refuses. An import therefore
asks a rule of its own first, and an address that fails it is not valid (§7.6, `url_invalid`):

1. The *authority* is what follows `://`, up to the first `/`, `?` or `#` (a backslash does not end
   it). It contains at most one `@`; what follows the `@` is host and port.
2. If that starts with `[`: there is a `]`; between the brackets is at least one character, and
   only hexadecimal digits, `:` and `.`; after the `]` comes nothing, or `:` and then only digits
   (none is allowed).
3. Otherwise the host ends at the first `:`, after which come only digits (none is allowed). The
   host is not empty. It consists only of ASCII letters and digits, `-`, `.`, `_`, and characters
   from U+0080 on. It does not start with `.` and contains no `..`.
4. Take the host without one `.` at its very end. If its last part (what follows its last `.`) is
   all digits, or is `0x` or `0X` followed by hexadecimal digits (none is allowed), then that host
   has to be an `IPV4` of §7.2.

An address that passes is then asked of the vault's own rule.

With this rule the two clients agreed on every address tried (§15.3) except one kind: a part of a
host that starts with `xn--` but is not valid Punycode is accepted by the desktop and refused by
the web vault. A host with letters outside ASCII is left to each client's own rule.

### 7.4 What BitosX has no field for

These are the *extras* of an entry:

| Kind | Code | What |
|---|---|---|
| authenticator key | `totp` | the value, trimmed, if it is not empty |
| folder | `folder` | the folder or group, trimmed, if it is not empty |
| other address | `other_address` | each address of §7.2 that is not a web address |
| custom field | `custom_field` | each custom field whose **value is not blank**; its name is trimmed, its value is as it is |

By default they are **not imported**. The preview counts them, by kind, over the entries that
will be added, and says that they are left out.

With the choice "Copy what BitosX has no field for into each item's notes" (off unless chosen),
they are appended to the notes as lines, in this order: the authenticator key, the folder, the
other addresses in their order, the custom fields in their order.

```
Authenticator key (TOTP): <value>
Folder: <value>
Other address: <value>
<name>: <value>
```

A custom field without a name has the label `Field`. Each line is label, colon, space, value; a
value of several lines is written as it is. The lines are joined with LF. If the entry's own notes
are not empty, they come first, followed by two LF (an empty line) and then the lines; if they are
empty, the lines are the notes. With no extras the notes are unchanged.

If the entry's own notes are within the limit and the notes with the lines are not, the entry is
not imported: `notes_too_long_with_extras`.

### 7.5 An entry with nothing in it, and an entry without a name

An entry is **empty** if its name is empty (after trimming), its username, password and notes are
blank, it has no address of either kind, no authenticator key, and no custom field with a value.
(A folder alone does not make an entry.) An empty entry is counted, and passed over.

An entry that is not empty but has no name is given one. The first of these that is 1 to 200
characters long is the name:

1. the host of its first web address, taken from the text: what follows `://` up to the first
   `/`, `?` or `#`; of that, what follows the last `@`; of that, if it starts with `[`,
   everything up to and including the first `]` (all of it, if there is no `]`), and otherwise
   everything before the last `:` if only digits (or nothing) follow that colon; in lower case;
   trimmed. The address is not handed to a URL parser for this, and nothing is decoded or
   converted: `https://Shop.Example:8443/x` gives `shop.example`, a host in Punycode stays in
   Punycode, and a host with letters outside ASCII keeps them as they are, capitals included;
2. its username, trimmed;
3. `Untitled`.

The preview counts the names that were derived.

### 7.6 The limits, and the reason an entry is not imported

An entry that is not empty is checked in this order. The first check that fails is its reason; it
is then **not imported**, and listed with its name and that reason.

| # | Check | Code |
|---|---|---|
| 1 | A CSV row with a value beyond the header's last column (§3.5) | `row_has_extra_values` |
| 2 | A KeePass entry with a standard field more than once (§5.3) | `field_twice` |
| 3 | The name is longer than 200 | `name_too_long` |
| 4 | The username is longer than 320 | `username_too_long` |
| 5 | The password is longer than 4096 | `password_too_long` |
| 6 | The notes of the file are longer than 16384 | `notes_too_long` |
| 7 | More than 64 web addresses | `too_many_urls` |
| 8 | A web address is longer than 2048 (with `https://`, if it was put in front) | `url_too_long` |
| 9 | A web address fails §7.3 or the vault's own rule | `url_invalid` |
| 10 | The notes with the extras copied in are longer than 16384 (§7.4) | `notes_too_long_with_extras` |

A name the file gives is never replaced by a derived one, even if it is too long. Only web
addresses count towards the 64.

## 8. Duplicates

An entry that passed §7.6 is a **duplicate** if an item of the vault, or an earlier entry of the
same file that itself passed §7.6, has

- the same name,
- the same username,
- the same password,
- and the same web addresses in the same order,

compared ordinally, where a value that is absent equals an empty one. For the comparison the name
and the addresses of an item of the vault are trimmed, as those of an entry are. Notes, the
favorite mark and extras are not compared.

The comparison is of what would be stored: a derived name and an added `https://` are part of it.
An entry that is not imported is nobody's duplicate, and nothing is a duplicate of it.

Duplicates are **skipped** by default. With the choice "Import duplicates too" (off unless chosen)
they are added like any other entry; they are still counted as duplicates.

## 9. The plan: what a preview shows

Reading a file and comparing it with a vault changes nothing. The result is a plan:

| | |
|---|---|
| format, label | `csv`, `bitwarden_json`, `keepass_xml`, `bitosx_json`; the label of §3.3, or `Bitwarden (JSON)`, `KeePass 2 (XML)`, `BitosX (JSON)` |
| records | every record of the file, whatever became of it |
| unsupported | how many records of each kind that is not a login |
| in the trash | Bitwarden items with a `deletedDate`; KeePass entries in the recycle bin |
| ignored columns | §3.2 |
| candidates | every entry that is not empty, in the order of the file, each with its position, its status (`add`, `duplicate`, `not_imported`), whether it is a duplicate, its reason, and its values as they would be stored |
| to add | candidates with the status `add` |
| duplicates | candidates that are duplicates, skipped or not |
| not imported | candidates with a reason |
| empty | entries with nothing in them |
| extras, `https://` added, names derived | counted over the candidates with the status `add` only |
| items before, items after, over the limit | the vault's items, that number plus *to add*, and whether that is more than 10,000 |

A preview shows names, counts and reasons. It never shows a password, an authenticator key, the
value of a custom field or notes. (The types that hold them are classes, not records, so that
nothing prints their content by accident.)

## 10. Applying an import (desktop)

Nothing is written before the user confirms. Then:

- The file is compared with the vault **again**, at the moment of the write and under the gate
  every change to the vault goes through. If the vault changed since the preview — an item was
  added by hand in between — what is added is what is right for the vault as it is then, and the
  result says what that was.
- If the vault would hold more than 10,000 items, the import is refused as a whole:
  `vault_item_limit`.
- All items are added in **one write** of the vault file (`IVaultService.AddLoginsAsync`): every
  item is stored, or none is. If the write fails, the vault and the session are as they were.
- If the vault file would be larger than 16 MiB, the write is refused before anything is written
  (`VaultTooLargeException`), and nothing was imported. 16 MiB is what this application, the web
  vault and the server read. The limit is on the encrypted file, which is about 44 % larger than
  the content: roughly 11 MiB of items.
- It is refused while the vault is locked.

Each item is new: a new identifier, never one from the file; created and last changed at the time
of the import; the favorite mark of the file, if the format has one. An empty username, password
or notes is stored the way the editor stores it, as none.

The bytes of the file are wiped once they are parsed. What was parsed holds the file's passwords
as text until the application lets go of it, which it does when the vault locks; text cannot be
wiped ([SECURITY_MODEL.md](SECURITY_MODEL.md)). Nothing from a file is logged: only that an import
added a number of items, and the code of a refusal.

The web vault adds the items to the open vault as unsaved changes; its *Save to server* uploads
them.

Once an import is done, both clients say, in these words:

> The file you imported still holds every password in plain text. Delete it when you no longer need it.

## 11. Export

Two formats, both unencrypted. Values are written exactly as they are (§11.3).

### 11.1 CSV

Chrome's layout, which most password managers read.

- UTF-8, no byte order mark.
- The first line is `name,url,username,password,note` — not quoted.
- Then one line for each item, in the vault's order: name, the item's **first** address (empty if
  it has none), username, password, notes. **Every value is in double quotes**, an empty one as
  `""`; a double quote inside a value is written twice; everything else, line ends included, is
  written as it is.
- CR LF ends every line, the last one included.

Only the first address of an item is in the file. The number of items with more than one is
known beforehand (`VaultExporter.CountItemsWithSeveralUrls`), and the dialog says that only JSON
keeps them all. Read back by §3, the file has the label `Chrome or Edge (CSV)`.

### 11.2 BitosX JSON

```json
{
  "format": "bitosx-export",
  "version": 1,
  "exportedAt": "2026-11-10T12:00:00Z",
  "items": [
    {
      "type": "login",
      "name": "Example",
      "username": "fake.user@example.test",
      "password": "FAKE-pw",
      "urls": [
        "https://example.com/login"
      ],
      "notes": "",
      "favorite": false,
      "createdAt": "2025-01-02T03:04:05.1234567Z",
      "updatedAt": "2026-02-03T04:05:06Z"
    }
  ]
}
```

- UTF-8, no byte order mark. The members in this order. Everything a vault holds is in it.
- A value an item does not have is the empty string; `urls` is an array, empty if there are none.
- Times are UTC and end in `Z`. `exportedAt` is to the second. `createdAt` and `updatedAt` keep
  their fraction of a second, up to seven digits, without trailing zeros, and without the point
  if there is none.
- The desktop indents by two spaces, ends lines with LF, ends the file with one, and writes most
  text as it is (`+`, `<`, `&` and ordinary letters outside ASCII are not escaped; a quote, a
  backslash, control characters, U+007F to U+00A0, characters outside the Basic Multilingual Plane
  and some others are written as escapes). **None of that is part of the format**: the two
  clients' files need not be the same bytes. Each client's files have to be read by both importers
  to the same items.

### 11.3 Nothing is put in front of a value

A value that starts with `=`, `+`, `-` or `@` is a formula to a spreadsheet program, and some of
those run what a formula asks for. The usual defence is to put a quote mark in front of such a
value. BitosX does not: the file is there to be read by another password manager, and a password
with a quote mark in front of it is another password.

Instead the user is told, before anything is written, not to open the file in a spreadsheet
program, with the same words in both clients:

> This file will contain every password in plain text. Anyone who can read it can sign in as you.
> Keep it only as long as you need it, and delete it afterwards. Do not open it in a spreadsheet
> program: such programs can run formulas found in files like this. To keep a copy of your vault,
> use the encrypted backup instead.

A box, "I understand that this file is not encrypted", has to be ticked before the file can be
written.

Text that is not valid Unicode — half of a surrogate pair — is written as U+FFFD, which is also
how the vault itself stores it.

### 11.4 Who may export, and how the file is written (desktop)

An unencrypted export is the costliest single thing someone at an unlocked vault can do, so the
unlocked session alone does not authorize it:

- The master password is asked for again and **verified before anything is written**
  (`IVaultService.VerifyMasterPasswordAsync`): the same check a master password change makes. The
  vault file has to be the one the session holds, and the password has to open it. A wrong
  password writes nothing and leaves a file that is at the destination as it is.
- What is written is what that check was for: the items of that moment.
- A lock at any time before the file is written stops the export.
- The file is written the way the vault is: to a new file that only its owner can read (mode 0600
  on Unix; on Windows it has the permissions of its folder), flushed to the disk, and moved into
  place in one step. An interrupted export leaves no file with part of the passwords under the
  name that was asked for.
- **What an interrupted export can leave behind.** The new file is a temporary file in the folder
  that was chosen, named `<name>.<32 hexadecimal digits>.tmp`. A write that fails deletes it. If
  the application or the machine dies during an export, it stays: a file with some or all of the
  passwords in plain text (readable by its owner only on Unix). Nothing removes it, unless a later
  export is written to the same name in the same folder more than ten minutes afterwards. BitosX
  does not look for such files when it starts, and does not remember where exports were written.
- The vault file itself is never written over.
- The suggested name is `BitosX-export-YYYY-MM-DD.csv` or `.json`, with the local date.
- The bytes of the export are wiped once they are written. The log says that an unencrypted
  export was written, in which format and with how many items — as a warning — and not where.

## 12. Threats, and what is done about each

An import file is controlled by whoever wrote it.

| Threat | What is done |
|---|---|
| A file sized to exhaust memory | 16 MiB, enforced before and while reading. |
| XML entity expansion ("billion laughs"), external entities, external DTDs | Any document type declaration is refused on the text itself, before parsing. The reader is configured to prohibit one and has no resolver. |
| XML nested millions deep | Counted while reading; refused at the 101st level. (The .NET reader keeps a record per open element, and has no limit of its own: 361 MiB for two million.) |
| JSON nested deeply | Refused beyond 64 levels by the parser. |
| A name twice in a JSON object, to make two readers see two values | Refused. |
| Quadratic parsing | Every reader makes one pass. Tests read a value of 15 MiB, a header of half a million columns, 600,000 records and 80,000 strings of one entry within fixed times. |
| Too many entries | Counted while reading; refused at the first one too many. |
| A file that is not what it seems (a database, an encrypted export, UTF-16) | Refused with a kind of its own, and a text that says what to do. |
| A CSV file in another dialect, read wrongly into other passwords | A fault in the quoting refuses the file; a row whose values moved is not imported. KeePass's own dialect is read as it is written. |
| Half a surrogate pair, which cannot be stored | Refused, in the whole document. |
| An import that makes the vault unreadable | The item limit, and the limit on the vault file, are checked before the write; the import is one atomic write. |
| What a file contains ending up in a log or an error text | Refusals and reasons are fixed texts. Parser messages are not passed on. |
| An unencrypted export made by someone who walks up to an unlocked vault | The master password is verified first. |
| An export that is left readable by other users, or half written | Mode 0600 on Unix; written atomically. |
| A spreadsheet program running a formula from an export | Not defended in the file (§11.3); the user is told. |

## 13. Codes and texts

The codes are what the fixtures spell. The texts are fixed, the same in both clients, and never
quote a file.

### 13.1 Refusals: a file that is not read

| Code | Text |
|---|---|
| `too_large` | The file is larger than 16 MiB. BitosX does not import files that large. |
| `empty` | The file is empty. |
| `unsupported_encoding` | The file is saved as UTF-16 or UTF-32 text. BitosX reads UTF-8: export the file again, or save it as UTF-8, and import that. |
| `invalid_utf8` | The file is not UTF-8 text. BitosX reads exports saved as CSV, JSON or XML text in UTF-8; it cannot read a database file, or a file that is encrypted or compressed. |
| `not_recognised` | BitosX does not recognise this file. It reads CSV files, Bitwarden's unencrypted JSON export, KeePass 2 XML and its own JSON export. |
| `invalid_csv` | The CSV file is damaged: a value in quotes is not closed, or is followed by something other than a comma or the end of the line. |
| `no_recognised_columns` | The file has no columns BitosX recognises. Its first line has to name a password column and at least one of name, url and username. |
| `invalid_json` | The file is not JSON that BitosX can read: it is damaged, nested too deeply, uses a name twice in one object, or holds an incomplete character. |
| `encrypted_export` | This Bitwarden export is encrypted. Export the vault from Bitwarden again as an unencrypted .json file, and import that. |
| `unsupported_version` | This BitosX export was written by a newer version of BitosX. Update BitosX, and import the file again. |
| `invalid_xml` | The file is not XML that BitosX can read: it is damaged or incomplete. |
| `doctype` | The file contains a document type declaration (DOCTYPE). BitosX does not read XML files that have one. |
| `protected_values` | The file holds values that are still encrypted (marked as protected). Export the database from KeePass as plain XML, and import that. |
| `nested_too_deeply` | The file nests its groups or elements more deeply than BitosX reads. |
| `too_many_entries` | The file holds more than 10,000 entries. BitosX imports at most 10,000 at a time: split the file, and import the parts. |
| `vault_item_limit` | The vault would hold more than 10,000 items after this import, which is the most a vault can hold. Nothing was imported. |

`invalid_csv` comes with a line number (§3.1).

### 13.2 Reasons: an entry that is not imported

| Code | Text |
|---|---|
| `row_has_extra_values` | The row has more values than the file has columns. |
| `field_twice` | The entry has the same field more than once. |
| `name_too_long` | The name is longer than 200 characters. |
| `username_too_long` | The username is longer than 320 characters. |
| `password_too_long` | The password is longer than 4096 characters. |
| `notes_too_long` | The notes are longer than 16384 characters. |
| `too_many_urls` | The entry has more than 64 web addresses. |
| `url_too_long` | A web address is longer than 2048 characters. |
| `url_invalid` | A web address is not a valid http or https address. |
| `notes_too_long_with_extras` | With the fields BitosX has no place for copied into them, the notes would be longer than 16384 characters. |

### 13.3 Kinds

| Extras | In a sentence |
|---|---|
| `totp` | authenticator keys (TOTP) |
| `folder` | folder or group names |
| `other_address` | addresses that are not web addresses |
| `custom_field` | custom fields |

| Unsupported | In a sentence |
|---|---|
| `secure_note` | secure notes |
| `card` | cards |
| `identity` | identities |
| `ssh_key` | SSH keys |
| `bank_account` | bank accounts |
| `drivers_license` | driver's licences |
| `passport` | passports |
| `other` | other items |

The name of an entry that has none and from which none can be derived is `Untitled`.

## 14. The fixtures

[`tests/fixtures/import`](../tests/fixtures/import) holds inputs and, next to each,
`<name>.expected.json`: what this library makes of it. The web vault copies the directory
unchanged and has to arrive at the same. Git does not convert line ends there (`.gitattributes`):
the files are compared byte for byte, and some are deliberately not text.

The expected files are **written by the tests**, never by hand
(`tests/BitosX.Core.Tests/ImportExport/ConformanceFixtures.cs`). A test fails if a committed file
differs from what the library produces. After an intended change:

```bash
BITOSX_UPDATE_IMPORT_FIXTURES=1 dotnet test tests/BitosX.Core.Tests --filter "FullyQualifiedName~ConformanceFixtureTests"
git diff tests/fixtures/import     # look at what changed, and say so here
```

A new fixture starts as an expected file that has its `input` and its `cases` and no `result`.

### 14.1 An expected file

```json
{
  "schema": 1,
  "input": { "file": "chrome.csv" },
  "cases": [
    {
      "name": "an empty vault, the choices as they are at first",
      "options": { "copyExtrasToNotes": false, "importDuplicates": false },
      "vault": [],
      "result": { }
    }
  ]
}
```

- `input` is `{ "file": "<a file in the directory>" }`, or
  `{ "parts": [ { "text": "…", "repeat": n }, … ] }` for an input too large or too dull to keep:
  the UTF-8 bytes of each text repeated that many times, one after the other.
- `vault` is the vault a case imports into: each item's `name`, `username`, `password` (empty
  strings if it has none) and `urls` — the four things a duplicate is told by.
- `result` of a file that is refused: `{ "refused": "<code>" }`, with `"line"` for `invalid_csv`.
- `result` otherwise:

  | Member | |
  |---|---|
  | `format`, `label` | §9 |
  | `records` | every record of the file |
  | `unsupported` | an object, code → number; a kind with none is absent |
  | `inTrash` | a number |
  | `ignoredColumns` | an array of names |
  | `counts` | `toAdd`, `duplicates`, `notImported`, `empty`, `httpsAdded`, `namesDerived`, `vaultItemsBefore`, `vaultItemsAfter`, `exceedsVaultItemLimit` |
  | `extras` | an object, code → number, over the candidates that are added; a kind with none is absent |
  | `candidates` | every entry that is not empty, in the order of the file |

  and each candidate: `position`, `status`, `duplicate`, `reason` (a code or `null`), `name`,
  `username`, `password`, `urls`, `notes` (as they would be stored: with the extras, if they are
  copied), `favorite`, `nameWasDerived`, `httpsAdded` (how many of its addresses), and `extras`:
  `totp`, `folder`, `otherAddresses`, `customFields` (each a `name` and a `value`).

There are no identifiers and no times in an expected file: an imported item gets both when it is
added.

### 14.2 What there is

Every value in the fixtures is made up.

| Fixture | What it shows |
|---|---|
| `chrome.csv` | Chrome's layout; a note of several lines, quotes and commas, text outside ASCII, an `android://` address, a duplicate inside the file, a password that is too long, a row whose values moved. Three cases: an empty vault; a vault that holds one of the logins; the same with duplicates imported. |
| `firefox.csv` | Firefox's layout, CR LF, every value quoted; no names, so all are derived; a `chrome://` address; an HTTP address with an IP host. |
| `bitwarden.csv` | Bitwarden's layout; a folder, a favorite, custom fields, two addresses in one cell, an authenticator key; a secure note; a row with nothing in it; notes that are too long, and notes that are too long only with the extras copied in. |
| `keepassxc.csv` | KeePassXC's layout; groups, an authenticator key, an entry that is only a note, an entry with nothing in it, a username that is too long. |
| `keepass1.csv` | KeePass CSV (1.x) with its backslashes. |
| `generic.csv` | A header no exporter is known by, with a byte order mark: other names for the columns, ignored columns, a name with spaces around it, hosts without a scheme, several addresses in one cell, and the four limits on names and addresses. |
| `addresses.csv` | 151 address values, one to a row: what is a web address, what is given `https://`, what is an other address, and what fails §7.3 or the vault's rule. |
| `columns.csv` | A header that offers more than one column for a field (§3.2): the first name of the field's list decides, not the first column; of two columns with one name, the left one; the others are ignored and named; a column without a name is not. A row whose column is empty does not fall back on another column, and a row that ends early has empty values. |
| `text-rules.csv` | The text operations of §2.1, where the two runtimes' own functions differ: every character that is trimmed, around a column name, a name, an address, an authenticator key and a folder, and two that are not; a lone CR between addresses, between custom fields and between records; a record of nothing but characters that are trimmed, and an entry of nothing but such values and a folder; a favorite mark and a type in capitals with such characters around them; a control character in an address; a name of 200 and one of 202 UTF-16 code units, both of characters outside the Basic Multilingual Plane; two names that differ only in how an accent is encoded, which are not duplicates. And the names that are derived (§7.5): from an address with a user name and a port, with an IPv6 host, with a bracket that is never closed, with a capital outside ASCII, in Punycode, with a port that is not a number, without a host, with a host longer than a name may be; from the first address that is a web address; from a host that was given `https://`; from a username, trimmed; and `Untitled` where the username is too long to be a name. Two cases: the choices as they are at first, and the extras copied into the notes. |
| `bitwarden.json` | Bitwarden's unencrypted export: a login with everything, one item of every other kind, an item in the trash, a duplicate, an item with only a name, hosts without a scheme, an item without a name, a passkey, an archived item. |
| `keepass.xml` | A KeePassXC export: nested groups, the recycle bin, a history, custom strings, values marked `ProtectInMemory`, CDATA and character references, a password of three spaces, a field twice, an entry with nothing in it, a duplicate in another group. |
| `keepass-line-ends.xml` | Line ends inside the values of a KeePass file (§5.2): written as they are — CR LF and a lone CR, which an XML processor turns into LF — written as character references, which stay what they are, and in a CDATA section. The file itself has CR LF line ends, as one written on Windows has. |
| `bitosx-export.csv`, `bitosx-export.json` | The desktop's own two exports of one vault of eight items (`ConformanceFixtures.ExportedVault`): quotes, commas, line ends, text outside ASCII, spaces that have to stay, values that look like formulas, backslashes, two addresses. Each is read back into an empty vault, and into the vault it came from: the JSON adds nothing; the CSV adds the item that has two addresses, because it holds only the first. |
| `refused-*` | One file for each refusal that a file of ordinary size can end in: both encrypted Bitwarden exports, a document type declaration (external, and with entities), protected values, XML that is damaged, XML that is not KeePass's, JSON that is damaged, has a name twice, has half a surrogate pair, or is neither format, a BitosX export of a newer version, UTF-16, bytes that are not UTF-8, a quote that is not closed, text after a closing quote, no known columns, no password column, an empty file, a file of white space. |
| `generated-*` | Inputs given as parts: 10,001 CSV rows, JSON items and KeePass entries; 16 MiB and 27 bytes; a value of 8 MiB in an ignored column; groups 64 and 65 deep; elements 101 deep; JSON 64 and 65 levels deep. |

The web vault's own exports of the vault in `bitosx-export.json` have to read back, with its
importer, to the candidates of `bitosx-export.json.expected.json` and
`bitosx-export.csv.expected.json`.

## 15. Sources, and what was not verified

### 15.1 What was read

Read on 2026-11-10, each at the commit named.

| Program | Where | What |
|---|---|---|
| Chromium | `github.com/chromium/chromium`, `main` at `4103299`, and the tags `90.0.4430.72`, `100.0.4896.60`, `110.0.5481.77` | `components/password_manager/core/browser/export/password_csv_writer.cc`, `csv_writer.cc`; `…/import/csv_password_sequence.cc`, `csv_field_parser.cc` |
| Firefox | `github.com/mozilla-firefox/firefox`, `main` at `5a3f639` | `toolkit/components/passwordmgr/LoginExport.sys.mjs`, `LoginCSVImport.sys.mjs` |
| Bitwarden | `github.com/bitwarden/clients`, `main` at `7d445be`, and the tag `web-v2025.6.2` | `libs/tools/export-vault-core/src/services/base-vault-export.service.ts`, `individual-vault-export.service.ts`, `org-vault-export.service.ts`, `…/types/bitwarden-json-export-types.ts`; `libs/common/src/models/export/cipher.export.ts`, `login.export.ts`, `login-uri.export.ts`, `field.export.ts`, `folder-with-id.export.ts`; `libs/common/src/vault/enums/cipher-type.ts`; `libs/importer/src/importers/base-importer.ts`, `bitwarden/bitwarden-csv-importer.ts`, `bitwarden/bitwarden-json-importer.ts`, `keepass2-xml-importer.ts` |
| PapaParse | `github.com/mholt/PapaParse`, `master` at `1fcb83c` | `papaparse.js`, `safe()`: how Bitwarden's CSV is quoted, and how an array becomes a cell |
| KeePassXC | `github.com/keepassxreboot/keepassxc`, `develop` at `6bcb901`, and the tags `2.3.4`, `2.5.4`, `2.6.0`, `2.6.6`, `2.7.0`, `2.7.10` | `src/format/CsvExporter.cpp`, `KdbxXmlWriter.cpp`, `KdbxXmlReader.cpp`, `KdbxWriter.cpp`; `src/core/Group.cpp`, `EntryAttributes.cpp`, `Totp.h` |
| KeePass 2 | `KeePass-2.59-Source.zip` from `sourceforge.net/projects/keepass` (SHA-256 `7ac27118…04a7b`) | `KeePass/DataExchange/Formats/KeePassCsv1.cs`; `KeePassLib/Serialization/KdbxFile.cs`, `KdbxFile.Write.cs`; `KeePassLib/PwDefs.cs`; `KeePass/Util/EntryUtil.cs` |

The full hashes: Chromium `410329972c7f59251c659a45f66cb5f05c9422bd`; Firefox
`5a3f6397e61554eda92b0ec63ddd0ff752a22da2`; Bitwarden `7d445bee64fae6907c82662a8a7b3a10c62ee1b3`;
PapaParse `1fcb83c64acf61ae636b556b0eadeebaca0aea33`; KeePassXC
`6bcb901aefdd6c7a525466d7dfe099c5ebc13c0e`; the KeePass archive
`7ac2711821836fc0198b6f19ecd787318c8bf06585a4a3aeb2e6bd5319604a7b`.

### 15.2 Not verified

- **1Password's CSV.** 1Password is closed source, and `support.1password.com` could not be
  reached from where this was written. Bitwarden's importer for it (`onepassword-csv-importer.ts`)
  shows what Bitwarden expects, not what 1Password writes. There is no label for it; its files
  are read by the names of §3.2.
- **Edge.** Closed source. Assumed to write what Chromium writes.
- **Safari's CSV, LastPass's CSV.** Known only from other programs' importers (Bitwarden's
  `safari-csv-importer.ts` and `lastpass-csv-importer.ts`; KeePass's `LastPassCsv2.cs`). The
  column names `otpauth`, `extra`, `fav` and `grouping` come from there. LastPass is said by both
  importers to write a secure note as a row whose address is `http://sn`; BitosX has no rule for
  that, and reads such a row as a login with that address.
- **The column names `urls` and `user name`.** No exporter was found that writes them (`urls` is a
  name Bitwarden's 1Password importer reads).
- **What other exporters write in a `type` column.**
- **Vendors' documentation.** `bitwarden.com`, `keepass.info`, `keepassxc.org`,
  `support.1password.com`, `support.apple.com`, `support.mozilla.org`, `support.google.com`,
  `support.lastpass.com`, `rfc-editor.org` and `w3.org` could not be reached. Nothing here rests
  on them.
- **How macOS and Windows answer for a device file** in the storage test of the size limit; that
  test runs on Linux only.

### 15.3 Where the two clients can differ

- **Addresses.** §7.3 was tried on 151 address values (`addresses.csv`), each asked of .NET 10's
  `Uri.TryCreate` and of Node 22's `URL`: after §7.3 the two agreed on all of them. So did they
  on every other address in the fixtures: what the expected files say of each was held against
  §7.3, written a second time in JavaScript from the text above, together with Node's `URL`. Two
  addresses that are not among the fixtures still differ: `https://xn--/` and
  `https://xn--a.example/`, which the desktop accepts and the web vault refuses. Hosts with
  letters outside ASCII were tried in a few ordinary cases only.
- **XML that is not well formed.** Which exotic documents an XML processor refuses is the
  processor's affair. The fixtures hold the cases that matter; beyond them the two clients may
  disagree about whether a damaged file is damaged.
- **An XML file with two faults.** §5.2 lets the first fault in the document decide, which the
  desktop can do because it reads the document forward. A client whose XML processor has parsed
  the whole document before the client looks at it can only answer `invalid_xml` for a file that
  has, say, a protected value and, further down, a tag that is never closed, where the desktop
  answers `protected_values`. Both refuse the file. No fixture has two faults.

## 16. What is not imported

Besides what §7.4 counts: an entry's history and earlier passwords, passkeys, attachments, tags,
icons, expiry dates, auto-type settings, the match setting of an address, the times an entry was
created and changed, and every item that is not a login. An authenticator key is never used by
BitosX, only kept as text if the user asks for it to be copied into the notes.

## 17. The desktop's user interface (0.14.0)

None of this is a rule for the other client; it says how the desktop carries out the rules above.
Settings has a card "Import and export" (`ImportExportViewModel`, `ImportPreview`), below Backup &
Restore.

**Import.** "Choose a file to import…" reads the file (§2) and shows the plan (§9); nothing is
written. A refusal is shown in its fixed text (§13.1), for `invalid_csv` followed by "The fault is
on line N of the file." The two choices, in the words of §7.4 and §8 and both off for every file,
ask for the plan again. The button that imports ("Import N logins") is disabled while the plan on
screen is not the one the choices ask for, while nothing would be added, and while the vault would
hold more than 10,000 items (shown with the text of `vault_item_limit`). Coming back to the page
from another page asks for the plan again, against the vault as it is then. Importing says what was
done (§10) — "N logins were added to the vault.", or, if the vault changed after the preview, that
the file was compared with it again and how many were added — and then the reminder of §10.

What the page is given is sentences, counts, and for each entry that cannot be imported its
position ("Entry 17"), its name and the fixed text of its reason (§13.2). It is given no password,
authenticator key, note, address or value of a custom field. Three things are decided here and not
by the specification:

- A name comes from the file. It is shown on one line (control characters and line separators as
  spaces), cut to 80 characters, in a text block of its own.
- An entry with the reason `row_has_extra_values` is listed **without a name**: its values moved
  by a column, so what stands where the name should may be any of them, the password included.
- At most 200 entries that cannot be imported are listed; the rest are counted ("… and N more.").
  At most 10 ignored columns are named, each cut to 40 characters.

**Export.** "Export unencrypted…" opens the warning and the box of §11.3, the choice of format
(CSV at first), the number of items with more than one address, and a box for the master password.
The button is disabled until the box is ticked and a password is typed. **The save dialog comes
first; then one call checks the password and writes the file** (`IVaultExportService.ExportAsync`).
So the key is derived once for an export and not at all for one that is called off in the dialog,
and what is written is what the check was for. A wrong password says "The master password is
incorrect. Nothing was exported.", and nothing more; the password box is emptied after every
attempt, and a new attempt starts with the save dialog again.

**Locking** — by hand, by the timer, by the workstation or the system going to sleep, after an
unexpected error, or because the vault was replaced by a backup or by the server's — closes both
and lets go of what they hold: the logins of the file, the plan, the file's name, what was typed.
An answer that arrives afterwards from a call that was still running is dropped. The next unlock
gets a new card.

**Words of failure** are fixed. No text of an exception reaches the page or the log, except the
texts of a refusal (§13.1): a parser's message can quote the file, and the file system's names a
path. A file that is gone is said to be gone. A file that cannot be read leaves the vault open.
