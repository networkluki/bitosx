# Master Password Encoding

A password is text; a key is derived from bytes. This document describes how BitosX gets from one
to the other, and why that changed in 0.6.0.

## The problem

The same password can reach the application as different sequences of code points:

- **"ö"** is one code point (U+00F6) from most keyboards, and **"o" followed by a combining
  diaeresis** (U+006F U+0308) from some input methods, some operating systems, and text that has
  been through certain file systems or applications. They look identical.
- What looks like a **space** may be a no-break space (U+00A0), which some keyboard layouts
  produce with a modifier key.

Before 0.6.0 the password was encoded as UTF-8 exactly as typed. Two such sequences are different
bytes, give different keys, and the vault does not open — with the error for a wrong password.
Verified against 0.5.1: a vault created with one form did not open with the other, in either
direction.

## The canonical encoding

Since 0.6.0 the key is derived from the **canonical encoding** of the password, following the
rules RFC 8265 gives for passwords (the `OpaqueString` profile):

1. every space character (Unicode category Zs) becomes U+0020;
2. the result is normalized to **Unicode Normalization Form C**;
3. and encoded as UTF-8.

Nothing else is changed. In particular there is **no trimming, no case folding, and no
compatibility mapping** (NFKC): each of those would make different passwords equal, which reduces
what a password is worth. `ﬁ` stays different from `fi`, `Ａ` from `A`, `x²` from `x2`.

**A password made of ASCII characters has a single encoding. Nothing changes for it.**

A new password that is **not valid Unicode** (half of a surrogate pair) is rejected. It cannot be
normalized, and earlier versions encoded it with a replacement character, which made different
passwords equal. It cannot be typed on a keyboard.

The minimum and maximum length apply to the canonical form.

## Existing vaults keep opening

A vault created before 0.6.0 is keyed with the password as it was typed then. The vault file has
no field that says so, and adding one would change the format — so when a vault is opened, the
encodings it could have been keyed with are tried in turn:

1. the canonical encoding;
2. the password exactly as typed now;
3. its composed form (NFC);
4. its decomposed form (NFD).

Encodings that come out identical are tried once. For an ASCII password that is one attempt, as
before. For a password with accented letters typed the usual way it is two, and the second is
only reached when the first is rejected.

| Vault keyed with | Typed now | Opens |
|---|---|---|
| any form, by 0.6.0 or later | any form | yes |
| composed, by an earlier version | composed or decomposed | yes |
| decomposed, by an earlier version | composed or decomposed | yes |
| no-break spaces, by an earlier version | no-break spaces | yes |
| no-break spaces, by an earlier version | ordinary spaces | **no** — which spaces were typed cannot be guessed |

**Opening a vault never rewrites it.** A vault moves to the canonical encoding the next time its
key is wrapped anyway: on a **master-password change**, a **vault key rotation**, or when the
**key derivation is strengthened**. Until then Settings says that the vault is keyed the old way
and what that means. Backups are handled the same way when they are restored, so a backup made by
any earlier version can still be restored.

## Cost and security

- **A wrong password costs one key derivation per encoding tried** — one for ASCII, usually two
  otherwise, never more than four. This is the price of not locking anyone out. It does not
  help an attacker: the encodings are forms of one password, not additional guesses, and an
  offline attack against the file does not go through this code.
- **A right password on a canonical vault costs one**, however it is typed.
- Wrong password and tampering remain indistinguishable, with the same error as before.
- The encodings are byte buffers and are wiped after use. The normalized password passes through
  a managed string on the way, which — like the typed password itself — cannot be reliably wiped
  (see [SECURITY_MODEL.md](SECURITY_MODEL.md)).

## Limits

- Normalization is defined by the Unicode version of the runtime and the operating system. The
  normalized form of **assigned** characters is stable across Unicode versions by policy. A
  password containing code points that are unassigned today could normalize differently in a
  future version; the as-typed fallback above still opens such a vault if it is typed the same
  way.
- Characters that look alike but are different letters (Latin "a" and Cyrillic "а") are different
  passwords, as they should be.
- Tabs and line breaks are not space characters in the sense above and are left as they are.

## Testing

`MasterPasswordEncodingTests` (the encoding and the candidates), `VaultServicePasswordEncodingTests`
(new and earlier vaults, what a failed attempt costs, moving to the canonical encoding),
`VaultBackupServiceTests` (restoring earlier backups), and `VaultFlowUiTests` (typing each form
into the real Unlock view). Non-ASCII text in the tests is written with escapes, so the form under
test does not depend on how a source file is saved.
