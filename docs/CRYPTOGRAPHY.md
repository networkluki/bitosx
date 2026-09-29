# Cryptography

This document describes the cryptographic design of the BitosX local vault. BitosX does **not**
invent cryptographic algorithms; it composes well-established, vetted primitives from the .NET
base class library and a maintained Argon2 library.

## Selected primitives (and why)

| Purpose | Choice | Source | Rationale |
|---|---|---|---|
| Password KDF | **Argon2id** | `Konscious.Security.Cryptography.Argon2` 1.3.1 (MIT) | Memory-hard KDF resistant to GPU/ASIC brute force. Not in the .NET BCL, so a verified, pure-managed (cross-platform, no native deps) package is used. |
| Authenticated encryption | **AES-256-GCM** | `System.Security.Cryptography.AesGcm` (.NET 10 BCL) | First-party, hardware-accelerated (AES-NI) AEAD with no extra crypto dependency. |
| CSPRNG | `RandomNumberGenerator` | .NET BCL | Cryptographically secure randomness for keys, salts, and nonces. |

### Why AES-256-GCM rather than XChaCha20-Poly1305

XChaCha20-Poly1305 (extended nonce) is **not** available in the .NET base class library and
would require a libsodium binding. AES-GCM is a first-party, hardware-accelerated primitive that
meets our needs with zero additional cryptographic dependencies. Because BitosX always uses a
fresh random nonce per encryption (and a fresh key/salt per vault creation), the 96-bit nonce
size of AES-GCM is not a limiting factor here.

## Key hierarchy

```
master password
      │  Argon2id(salt, m, t, p)
      ▼
key-encryption key (KEK, 256-bit)
      │  AES-256-GCM wrap  (key-wrap nonce, AAD = header)
      ▼
random vault key (256-bit, from CSPRNG)
      │  AES-256-GCM encrypt  (content nonce, AAD = content context)
      ▼
encrypted vault contents
```

- The **vault key** is random and independent of the password. The password only protects the
  wrapped vault key. This allows saving (re-encrypting content) without re-running Argon2, and
  enables changing the master password by **re-wrapping only** (see "Master password change").
- After unlock, only the **vault key** is kept in memory; the master password is not retained.
- The key is derived from the **canonical encoding** of the master password (every space
  character mapped to U+0020, then Unicode NFC, then UTF-8), so that the same password gives the
  same key however it is typed. See [MASTER_PASSWORD.md](MASTER_PASSWORD.md), including how vaults
  from before 0.6.0 keep opening.

## Default parameters

- Argon2id: **64 MiB** memory, **3** iterations, **parallelism 4**, 32-byte output
  (aligned with current OWASP guidance for interactive Argon2id; the second recommended option of
  RFC 9106). A deliberately weak profile is used **only** in automated tests to keep them fast.
  An existing vault can be moved to **256 MiB** or **1 GiB**; see
  [KEY_DERIVATION.md](KEY_DERIVATION.md).
- Salt: 16 bytes (128-bit), random per vault.
- Nonces: 12 bytes (96-bit), random per encryption operation.
- Authentication tag: 16 bytes (128-bit).

### Measured unlock latency

`tools/BitosX.KdfBenchmark` derives a key with the production profile and reports timing
statistics without ever printing the password or derived key. Run it with:

```
dotnet run -c Release --project tools/BitosX.KdfBenchmark -- 12
dotnet run -c Release --project tools/BitosX.KdfBenchmark -- 12 strong     # or: maximum
```

On one reference machine (16 logical CPUs, .NET 10, Release) the profile derived in ~46 ms
median (min ~43 ms, max ~130 ms including first-run warm effects). That is comfortably
interactive, but relatively fast for a memory-hard KDF: a stronger profile could raise
brute-force cost while remaining interactive. The default is intentionally **not** changed based
on a single machine — see "Parameter tuning and migration" below.

### KDF parameter bounds (untrusted input)

The KDF parameters (`memoryKiB`, `iterations`, `parallelism`, `keyLengthBytes`) are stored in the
vault header in cleartext and are therefore **attacker-controlled** on a stolen or tampered file.
Argon2 runs *before* the wrapped key is authenticated, so `Argon2Parameters.Validate()` enforces
both floors and **ceilings** to prevent a malicious file from requesting absurd cost and
exhausting the machine on unlock:

| Parameter | Minimum | Maximum |
|---|---|---|
| `memoryKiB` | 8 KiB (and ≥ 8 × parallelism) | 1 GiB (1,048,576 KiB) |
| `iterations` | 1 | 64 |
| `parallelism` | 1 | 64 |
| `keyLengthBytes` | must equal 32 | must equal 32 |

Out-of-range values fail closed with `UnsupportedVaultFormatException` at parse time, before any
allocation or derivation. Validation runs in three places (the serializer, the key-derivation
call, and the cryptor), so no unlock path can bypass it.

### Parameter tuning and migration

The ceilings sit far above the interactive default to leave headroom for stronger profiles.
Because the parameters are persisted per vault, an existing vault always opens with the profile
it has; a stronger default would only affect newly created vaults. An existing vault is moved to
stronger settings explicitly, by its user, with a measurement from the device in front of them
(see [KEY_DERIVATION.md](KEY_DERIVATION.md)); the default itself has deliberately not been raised
from measurements on one machine. **Raising a profile above the current ceilings requires
bumping the on-disk format version and widening the bounds together**, so that older builds reject
files they cannot safely process rather than attempting an unbounded allocation.

## On-disk format (version 1)

The vault file is JSON with base64-encoded binary fields:

```json
{
  "version": 1,
  "kdf": "argon2id",
  "kdfParameters": { "memoryKiB": 65536, "iterations": 3, "parallelism": 4, "keyLengthBytes": 32 },
  "salt": "<base64, 16 bytes>",
  "algorithm": "AES-256-GCM",
  "keyWrapNonce": "<base64, 12 bytes>",
  "encryptedVaultKey": "<base64, 32-byte ciphertext + 16-byte tag>",
  "contentNonce": "<base64, 12 bytes>",
  "ciphertext": "<base64, content ciphertext + 16-byte tag>"
}
```

Notes:

- Ciphertext and tag are concatenated (`ciphertext || tag`) for both the wrapped key and the
  content.
- **Associated data (AAD) is not stored.** It is derived deterministically from the header
  (version, KDF parameters, salt for the key wrap; version and algorithm for the content). This
  binds the public header to the ciphertext without adding a separate tamperable field: modifying
  a bound header field changes the AAD and/or the derived KEK, so decryption fails closed.

## Master password change

Changing the master password **re-wraps the existing vault key** under a key derived from the new
password; it does **not** rotate the vault key and does **not** re-encrypt vault content.

1. The current password is verified by authenticating (unwrapping) the existing wrapped key — a
   wrong password fails closed (`VaultAuthenticationException`) before anything is written.
2. A **fresh salt** and **fresh key-wrap nonce** are generated; the vault's existing Argon2id cost
   parameters are kept.
3. The same vault key is wrapped under the new KEK. A self-check unwraps the freshly wrapped key and
   verifies it matches the original vault key before the result is used.
4. The new envelope carries the **unchanged** content nonce and ciphertext. This is sound because
   the content's associated data depends only on the format version and algorithm — not on the salt
   or KDF parameters — so the existing ciphertext still authenticates under the same vault key.
5. The re-wrapped vault is written through the normal **atomic** storage path; the in-memory header
   is updated only once the new vault is known to be on disk.

Because the content is carried over from the file while the session keeps its own key, the file
must be exactly the vault the session holds the key for. Before re-wrapping, the same
**consistency gate** as for key rotation applies (a SHA-256 fingerprint of the whole envelope); if
the file changed while the vault was open, the change fails closed with
`VaultChangedExternallyException` and writes nothing. Without it, a header wrapping a key the
session does not hold could be adopted, and the next save would produce a vault that no password
opens. The write itself uses the same cancellation boundary and write-failure reconciliation as
rotation.

Requirements and boundaries: the vault must be unlocked and the current password re-entered (so the
change cannot be made from an unattended unlocked session); the new password must differ from the
current one — compared in canonical form, so the same password typed on another keyboard does not
count as a new one. The on-disk **format is unchanged (v1)** — only the salt, key-wrap nonce, and wrapped
key differ. Limitations: a password change does **not** rotate the vault key (it re-protects the key
but does not invalidate a key that may already be compromised — use
[vault key rotation](KEY_ROTATION.md) for that), it does **not** change the key-derivation settings
(use [strengthening](KEY_DERIVATION.md) for that), and **pre-change copies/backups of the vault file
remain decryptable with the old password**.

## Strengthening key derivation

The same re-wrap, with the settings changing instead of the password: the vault key is unwrapped
with the current settings and wrapped again under a KEK derived from the same password with
**stronger settings** and a fresh salt. The content is carried over unchanged, the vault key is not
rotated, and the format stays v1. Settings can only be strengthened — no less memory and no fewer
passes, and more of one — and both derivations and the self-check run before anything is written,
so a vault cannot be moved to settings the device it is on cannot open. See
[KEY_DERIVATION.md](KEY_DERIVATION.md).

The new settings are bound to the wrapped key as associated data, exactly as the old ones were.
Writing weaker settings into a vault file therefore does not make it cheaper to attack; it makes
it fail to authenticate.

## Vault key rotation

Rotation **replaces the vault key** and **re-encrypts all content** under the new key (in contrast to a
master-password change, which re-wraps the same key). It requires an unlocked vault and re-entry of the
current password; a wrong password or any tampering fails closed before any new material is generated.
Fresh material is produced throughout — new vault key, salt, key-wrap nonce, and content nonce — while
the master password, on-disk **format (v1)**, KDF identifier/algorithm, and Argon2 cost parameters are
unchanged. The new key is wrapped under the canonical encoding of the master password. A self-check confirms the rotated envelope unwraps to the new key and decrypts to the exact
original plaintext before it is persisted through the atomic storage path.

Because rotation is destructive it is additionally guarded by an on-disk/in-memory consistency gate (a
SHA-256 fingerprint of the whole envelope that catches both different-key and same-key external
changes), a cancellation boundary that cannot leave `disk = rotated / memory = old key`, and a
write-failure reconciliation whose terminal state is always `OLD disk + OLD session`,
`NEW disk + NEW session`, or `UNKNOWN disk + LOCKED session`. Rotation is **not retroactive**:
pre-rotation copies/backups keep the old key material and remain decryptable with the master password.
See [KEY_ROTATION.md](KEY_ROTATION.md) for the full design.

## Security properties

- **Confidentiality + integrity** via AES-GCM. Any modification of ciphertext, tag, nonce, salt,
  wrapped key, or bound header fields causes authenticated decryption to fail.
- **Wrong password and tampering are indistinguishable**: both surface a single
  `VaultAuthenticationException` with an identical message, revealing nothing about which
  component failed.
- **Fail closed**: unknown version, unsupported KDF/algorithm, malformed structure, missing or
  `null` fields, unknown or duplicated properties, invalid UTF-8, invalid base64, wrong field
  lengths, out-of-range KDF parameters, and oversized files are all rejected
  (`UnsupportedVaultFormatException`). There is no plaintext fallback.
- **Resource-exhaustion resistance**: the vault file is treated as untrusted input. The file size
  is capped (16 MiB) — enforced on the bytes actually read, not only on the size reported
  beforehand — and the KDF cost parameters are bounded (see "KDF parameter bounds") before any
  Argon2 work, so a malicious file cannot trivially exhaust memory or CPU.
- **The decrypted content is validated too.** Authentication proves the content was written by
  someone holding the key, not that it is well formed; see [SECURITY_MODEL.md](SECURITY_MODEL.md).
- **Nonce uniqueness**: nonces come from the CSPRNG and a fresh nonce is used for every
  encryption. Vault creation also generates a fresh salt and vault key.

## Limitations

- Argon2id parameters trade off unlock latency vs. brute-force cost; the defaults are a
  reasonable interactive baseline, not a maximum. A vault can be moved to stronger settings, not
  beyond 1 GiB of memory: that is the ceiling of format v1.
- Every device that opens a vault needs the memory its settings ask for. Where it is missing the
  derivation fails with `KeyDerivationResourceException` — before anything is decrypted, and with
  an error that is not the one for a wrong password.
- The strength of everything depends on the master password's entropy.
- Pure-managed Argon2 is slower than a native implementation; this is an intentional trade for
  cross-platform simplicity and no native dependencies.
- See [SECURITY_MODEL.md](SECURITY_MODEL.md) for memory-handling limitations.

## Testing

Cryptographic behavior is covered by `BitosX.Crypto.Tests`, including round-trip correctness,
wrong-key/wrong-password rejection, ciphertext/tag/AAD/nonce/salt tampering, unsupported version
and malformed input handling, independent per-vault keys, empty-input safety, rejection of weak
**and** resource-exhausting KDF parameters, rejection of malicious KDF parameters in a vault
document before any derivation is attempted, and re-wrapping under stronger settings and under
another encoding of the same password (`KeyDerivationChangeTests`).

**Known-answer tests** (`KnownAnswerTests`) check the primitives against published reference
outputs rather than against themselves — a round trip cannot tell a correct implementation from
one that is consistently wrong:

| Primitive | Vectors |
|---|---|
| Argon2id, through the vault's own wrapper | Seven vectors from the test suite of the reference implementation (phc-winner-argon2, version 0x13) |
| Argon2id, the library directly | RFC 9106, section 5.3 (with secret and associated data) |
| AES-256-GCM, including the `ciphertext ‖ tag` layout | Test cases 13, 14 and 16 of the GCM specification |
| Associated data of format v1 | An envelope assembled from the primitives alone, as documented above, must be accepted by the real cryptor |

The last one matters because the associated data is derived rather than stored: a change to how it
is built would make every existing vault fail to authenticate, and no round-trip test would notice.
