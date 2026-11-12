# Authenticator codes (TOTP)

A login can hold one **authenticator key**: the secret a site hands out when two-step sign-in is
set up, from which an authenticator app makes a new six-digit code every 30 seconds (RFC 6238,
TOTP). BitosX keeps the key in the vault next to the password, shows the current code with the
seconds it has left, and copies it.

This document is the specification of that feature: every rule a second implementation needs to
store the same keys, read the same text into the same key, and show the same code at the same
moment. The desktop application (this repository) and the web vault (bitosx-manager) both follow
it, and the fixtures in [`tests/fixtures/totp`](../tests/fixtures/totp) hold the two to the same
results (§7). How keys are imported and exported is in [IMPORT_EXPORT.md](IMPORT_EXPORT.md).

Only time-based codes are supported. Counter-based codes (HOTP), Steam Guard and other schemes are
refused with a text that says so (§6). Nothing here touches the server or the vault's file format
(the v1 envelope); it changes the content schema inside the encryption (§2.2).

| What | Where |
|---|---|
| The key (`TotpKey`, `TotpAlgorithm`) and where a login holds it (`LoginItem.Totp`) | `src/BitosX.Core/Domain` |
| Its stored form, read strictly | `src/BitosX.Core/Serialization/TotpKeyJsonConverter.cs` |
| What a key may be | `VaultValidation.IsValidTotpKey` |
| Text → key (§3), the refusal codes and texts (§6) | `src/BitosX.Core/Authenticator/TotpKeyParser.cs`, `TotpRefusal.cs`, `TotpTexts.cs` |
| Key + time → code (§4) | `src/BitosX.Core/Authenticator/TotpGenerator.cs` |

## 1. Words

- **Trimmed**, **in lower case** (ASCII only) and **UTF-16 code units** mean what they mean in
  [IMPORT_EXPORT.md §2.1](IMPORT_EXPORT.md): the trim set is U+0009 to U+000D, U+0020, U+0085,
  U+00A0, U+1680, U+2000 to U+200A, U+2028, U+2029, U+202F, U+205F, U+3000, U+FEFF.
- **ASCII case-insensitive**: compared after `A`–`Z` are made `a`–`z`, and nothing else.
- **Base32** is RFC 4648's alphabet `ABCDEFGHIJKLMNOPQRSTUVWXYZ234567`, five bits to a character.

## 2. The stored form

### 2.1 The member

A login item (the decrypted vault content, PascalCase like the item's other members) gains one
optional member:

```json
"Totp": { "Secret": "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", "Algorithm": "SHA1", "Digits": 6, "Period": 30 }
```

- A login without a key has **no** `Totp` member. `"Totp": null` is refused when it is read.
- The four members are all required, each exactly once, with exactly these names (case-sensitive),
  and nothing else. The desktop writes them in this order, after `Urls`; the order is not part of
  the format.
- `Secret`: a JSON string, the **canonical base32** of the secret's bytes: only `A`–`Z` and
  `2`–`7`, no padding, no spaces; exactly ceil(8n/5) characters for n bytes, and the bits of its
  last character that carry no data are zero. In other words, the string is what encoding its own
  decoding gives. n is 10 to 128.
- `Algorithm`: a JSON string, exactly `SHA1`, `SHA256` or `SHA512` (`sha1` is refused).
- `Digits`: a JSON number, 6, 7 or 8.
- `Period`: a JSON number, the time step in seconds, 1 to 300.

A document that breaks any of this is refused with the vault's generic refusal for content that is
not valid — which never quotes the content.

### 2.2 The content schema version

The decrypted document has `schemaVersion`. From 0.15.0 on (web: 0.9.0):

- A vault is **written** with `schemaVersion: 1` when no login has a key, and with `2` when at
  least one has. A vault without keys is therefore byte for byte what 0.14 wrote.
- Versions 1 and 2 are **read**. A version 1 document in which a login has `Totp` is refused (the
  generic "invalid item" refusal). A version 2 document without any key is read, and written back
  as version 1. Every other version is refused ("unsupported schema version").
- Unknown members stay refused, as before.

**What this does to older clients.** Desktop 0.14.1 and older and the web vault 0.8.1 and older
read only version 1. They refuse a vault that holds a key with their existing "unsupported vault
schema version" error, and therefore never get far enough to save over it: nothing is lost, but
that vault cannot be opened by them until every key is removed with a newer client. A vault
without keys stays readable by them.

## 3. Reading a key from text

What the editor's field holds and what an import file holds are read the same way. The input is a
string; the result is a key (§2), a refusal code (§6), or **no key** (an empty text, which is not
an error). The checks are made in the order written here, and the first that fails is the result.

### 3.1 First steps

1. More than 2048 UTF-16 code units (counted before trimming): `too_long`.
2. Trim. Nothing left: no key.
3. Starts with `otpauth://` (ASCII case-insensitive): §3.2.
4. Starts with `steam://` (ASCII case-insensitive): `steam_not_supported`.
5. Otherwise the whole trimmed text is a base32 secret (§3.3), and the key is SHA1, 6 digits,
   30 seconds.

### 3.2 `otpauth://` links

The format is the one Google described as the *Key Uri Format*:
`otpauth://totp/LABEL?secret=…&issuer=…&algorithm=…&digits=…&period=…`.

1. **Type.** The text after `otpauth://` up to the first `/` is the type — wherever that `/` is,
   also after a `?`. No `/`: `invalid_link`. `hotp` (ASCII case-insensitive):
   `hotp_not_supported`. Anything but `totp` (ASCII case-insensitive): `invalid_link`.
2. **Fragment.** Of the text after that `/`, everything from the first `#` on is removed.
3. **Query.** What is left is the label and the query: the label (up to the first `?`) is not read;
   the query is everything after the first `?`. No `?`: `invalid_link`.
4. **Parameters**, from left to right. The query is split at every `&`; an empty part is skipped.
   - A part without `=` is `invalid_link`. Otherwise the name is what comes before the first `=`,
     and the value what comes after it.
   - The **name is not percent-decoded**; it is put in lower case (ASCII).
   - **Every value** — also of a parameter that is otherwise ignored — is **percent-decoded as
     UTF-8**: `%` followed by two hexadecimal digits (either case) is a byte; each run of such bytes
     has to be valid UTF-8 on its own (no half of a character, no overlong form, no surrogate).
     `%` followed by anything else, or bytes that are not UTF-8, is `invalid_link`. Every other
     character stands for itself; a `+` stays a `+`.
   - A name that was already seen (after lower-casing), known or not: `invalid_link`.
5. **`secret`**: absent, or present with an empty value: `missing_secret`. Otherwise its decoded
   value is read by §3.3 — so `%20` and `-` are removed there, and a `+` is `invalid_secret`.
6. **`algorithm`**: absent: SHA1. `SHA1`, `SHA256` or `SHA512` in ASCII case-insensitive spelling:
   that one. Anything else, the empty value included: `unsupported_algorithm`.
7. **`digits`**: absent: 6. Exactly `6`, `7` or `8`: that number. Anything else — `06`, `+6`,
   full-width digits, the empty value — `unsupported_digits`.
8. **`period`**: absent: 30. One to three ASCII digits, the first not `0`, with a value of 1 to
   300: that number. Anything else — `030`, `-30`, `30.0`, `3e1`, the empty value —
   `unsupported_period`.
9. Every other parameter (`issuer`, `image`, `counter`, `lock`, …) is ignored. So is the label,
   which is where an issuer and an account name usually are: the item already has a name and a
   username.

### 3.3 Base32 secrets

1. Remove every space (U+0020) and every `-`. Then remove the `=` characters at the end.
2. Every character left has to be `A`–`Z`, `a`–`z` or `2`–`7`; otherwise `invalid_secret`. (So a
   tab, a no-break space, a zero-width space, `0`, `1`, `8`, `9`, `+`, `=` in the middle and
   full-width letters are all `invalid_secret`.)
3. Decode five bits per character, most significant first: n characters give floor(5n/8) bytes.
   The bits left over at the end are dropped, whatever they are.
4. Fewer than 10 bytes (fewer than 16 characters): `secret_too_short`. More than 128 bytes (more
   than 206 characters): `secret_too_long`.
5. The stored `Secret` is the canonical base32 of those bytes (§2.1): upper case, no padding. For
   example `gezd gnbv gy3t qojq` and `GEZDGNBVGY3TQOJQ====` are both stored as `GEZDGNBVGY3TQOJQ`,
   and `GEZDGNBVGY3TQOJQGF` as `GEZDGNBVGY3TQOJQGE`.

## 4. Codes

RFC 6238 with RFC 4226's dynamic truncation, T0 = 0:

1. `unixSeconds` is the device's time in whole seconds since 1970-01-01T00:00:00Z (rounded down).
   Times before that are not defined.
2. T = floor(unixSeconds / Period). The message is T as eight bytes, big-endian. (T can exceed
   2^32: with a period of 1 it does from the year 2106; the fixtures hold such a case.)
3. HMAC-SHA1, HMAC-SHA256 or HMAC-SHA512 of the message, with the decoded secret as the key.
4. The low four bits of the last byte of the HMAC are an offset; the four bytes from there, read
   big-endian with the top bit cleared, are a number.
5. The code is that number modulo 10^Digits, written in decimal with zeros in front to Digits
   digits.
6. **Seconds left** = Period − (unixSeconds mod Period): 1 to Period.

**Display.** The first floor(Digits/2) digits, one space, the rest: `123 456`, `123 4567`,
`1234 5678`. **Copying** puts the digits alone on the clipboard, without the space.

**The clock.** Codes come from the device's clock. Where a code is shown, both clients say, in
these words:

> Codes depend on this device's clock. If they are refused, check that the clock is right.

## 5. Where the key is shown, and where it is not

The key is as secret as the password — more, since it is the second factor. It is never logged,
never part of an exception's or error's text, an accessibility name, a tooltip or a status text.
In the editor it is masked unless the user chooses to show it, like the password. The item's view
shows the code, never the key. Nothing is computed or shown while the vault is locked; locking
stops every timer and clears what was shown. See [THREAT_MODEL.md](THREAT_MODEL.md) for what it
means to keep both factors in one vault.

## 6. Codes and texts

The codes are what the fixtures spell. The texts are fixed, the same in both clients, shown under
the editor's field, and never quote what was entered.

| Code | Text |
|---|---|
| `too_long` | This is too long to be an authenticator key or an otpauth:// link. |
| `steam_not_supported` | Steam Guard codes are not supported. BitosX makes standard time-based codes (TOTP) only. |
| `invalid_link` | This otpauth:// link cannot be read. Copy the link again, or enter the key itself. |
| `hotp_not_supported` | This link is for counter-based codes (HOTP), which BitosX does not support. BitosX makes time-based codes (TOTP) only. |
| `missing_secret` | This otpauth:// link holds no key. |
| `unsupported_algorithm` | This link asks for an algorithm BitosX does not support. BitosX supports SHA1, SHA256 and SHA512. |
| `unsupported_digits` | This link asks for codes of a length BitosX does not support. BitosX makes codes of 6, 7 or 8 digits. |
| `unsupported_period` | This link asks for a time step BitosX does not support. BitosX supports 1 to 300 seconds. |
| `invalid_secret` | The key can hold only the letters A to Z and the digits 2 to 7. Spaces and dashes are ignored. |
| `secret_too_short` | The key is too short. An authenticator key has at least 16 characters. |
| `secret_too_long` | The key is too long to be an authenticator key. |

The editor's field:

| | Text |
|---|---|
| Label | Authenticator key |
| Hint | The key or the otpauth:// link a site shows when you set up two-step sign-in. |

## 7. The fixtures

[`tests/fixtures/totp`](../tests/fixtures/totp) holds two files. Git does not convert their line
ends (`.gitattributes`); the web vault copies them unchanged and has to arrive at the same results.
The results are **written by the tests**, never by hand
(`tests/BitosX.Core.Tests/Authenticator/TotpFixtures.cs`), and a test fails if a committed file
differs from what the library produces. A new case is added by hand with its `name` and its input
and no `result`; then:

```bash
BITOSX_UPDATE_TOTP_FIXTURES=1 dotnet test tests/BitosX.Core.Tests --filter "FullyQualifiedName~TotpFixtureTests"
git diff tests/fixtures/totp     # look at what changed, and say so here
```

Every secret in them is made up or published: the RFC 6238 seeds, the example of Google's Key Uri
Format (`JBSWY3DPEHPK3PXP`), runs of `A`.

### 7.1 `keys.json`: text → key

```json
{
  "schema": 1,
  "cases": [
    {
      "name": "base32 in lower case, in groups of four",
      "input": "gezd gnbv gy3t qojq gezd gnbv gy3t qojq",
      "result": { "key": { "secret": "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", "algorithm": "SHA1", "digits": 6, "period": 30 } }
    }
  ]
}
```

- `input` is the text, or `{ "parts": [ { "text": "…", "repeat": n }, … ] }`: each text repeated
  that many times, one after the other (for the long inputs).
- `result` is one of `{ "key": { "secret", "algorithm", "digits", "period" } }` (camelCase; the
  key as §2.1 stores it), `{ "refused": "<code>" }`, `{ "empty": true }` (no key).
- 91 cases: every rule of §3, every refusal code, every algorithm and number of digits.

### 7.2 `codes.json`: key and time → code

```json
{
  "schema": 1,
  "cases": [
    {
      "name": "RFC 6238 Appendix B: SHA1 at 59",
      "key": { "secret": "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", "algorithm": "SHA1", "digits": 8, "period": 30 },
      "unixSeconds": 59,
      "result": { "code": "94287082", "display": "9428 7082", "secondsLeft": 1 }
    }
  ]
}
```

- `key` is a stored key (§2.1). `unixSeconds` is at most 20,000,000,000 (a safe integer in
  JavaScript).
- 40 cases: the 18 of RFC 6238 Appendix B (8 digits; the 20-, 32- and 64-byte ASCII seeds that the
  RFC's reference code and table use for SHA1, SHA256 and SHA512); and our own: 6 and 7 digits,
  both edges of a step, periods of 1, 45, 60 and 300, a 10-, an 11- and two 128-byte secrets, more
  than 2^32 steps.

## 8. Import and export

An import reads an entry's authenticator key by §3; a key that is read is stored, and one that is
refused stays an *extra*, as before. BitosX's JSON export writes the key. See
[IMPORT_EXPORT.md](IMPORT_EXPORT.md) §6, §7.4, §11.2.

## 9. Sources, and what was not verified

- **RFC 6238** (TOTP) and **RFC 4226** (HOTP, dynamic truncation): `rfc-editor.org`,
  `datatracker.ietf.org` and `ietf.org` could not be reached from where this was written. The text
  of RFC 6238 was read from four copies kept in other projects on GitHub (`pingidentity/ldapsdk`
  `docs/specs/rfc6238.txt`, `smuellerDD/leancrypto` `otp/doc/rfc6238.txt`,
  `bdauvergne/python-oath` `doc/rfc6238.txt`, `sec51/twofactor` `rfc6238.txt`), all four
  byte-identical (SHA-256 `82947ed9064450850547f55959dc79d2de775f0fa33f7b3f9622fb6c93e69a7a`).
  Appendix B's table was copied from it into the tests. RFC 4226 was read from two such copies,
  for Appendix D's values (755224, 287082, 359152), which the tests also use.
- The RFC's text names one seed, `12345678901234567890`, for its table; the table was made with the
  reference code of its Appendix A, which uses that seed for SHA1 and longer ones of the same digits
  (32 and 64 bytes) for SHA256 and SHA512. The fixtures use what the code uses; with the one seed,
  the SHA256 and SHA512 rows do not come out.
- Every code in the fixtures was also computed by a second implementation written from this text
  in Python (`hmac`, `base64`), and every result of `keys.json` by a second reading of §3 in
  Python; both agreed with the library on every case.
- **Google's Key Uri Format** (`github.com/google/google-authenticator/wiki/Key-Uri-Format`) was
  not read again for this; the parameters and their defaults are the ones every authenticator app
  uses. What other apps do with an unusual link (a parameter twice, an encoded name) differs from
  app to app; §3.2 is BitosX's own rule.
- `HMACSHA1.HashData`, `HMACSHA256.HashData` and `HMACSHA512.HashData` (the span overloads) are
  .NET's; the RFC vectors are what shows they do what is needed.

### 9.1 Where the two clients can differ

- A stored `Digits` or `Period` written with a fraction or an exponent (`6.0`, `6e0`): the desktop
  refuses the document; a JavaScript reader sees the number 6 and may accept it. Neither client
  writes such a number.
- Codes of times before 1970: not defined (§4).
