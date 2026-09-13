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
  will allow master-password changes (re-wrap only) in a future milestone.
- After unlock, only the **vault key** is kept in memory; the master password is not retained.

## Default parameters

- Argon2id: **64 MiB** memory, **3** iterations, **parallelism 4**, 32-byte output
  (aligned with current OWASP guidance for interactive Argon2id). A deliberately weak profile is
  used **only** in automated tests to keep them fast.
- Salt: 16 bytes (128-bit), random per vault.
- Nonces: 12 bytes (96-bit), random per encryption operation.
- Authentication tag: 16 bytes (128-bit).

### Measured unlock latency

`tools/BitosX.KdfBenchmark` derives a key with the production profile and reports timing
statistics without ever printing the password or derived key. Run it with:

```
dotnet run -c Release --project tools/BitosX.KdfBenchmark -- 12
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

The ceilings sit far above the interactive default to leave headroom for future, stronger
device-aware profiles. Because the parameters are persisted per vault, an existing vault always
opens with the profile it was created under; a new, stronger default only affects newly created
vaults (or a future re-wrap/upgrade path). **Raising a profile above the current ceilings requires
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

## Security properties

- **Confidentiality + integrity** via AES-GCM. Any modification of ciphertext, tag, nonce, salt,
  wrapped key, or bound header fields causes authenticated decryption to fail.
- **Wrong password and tampering are indistinguishable**: both surface a single
  `VaultAuthenticationException` with an identical message, revealing nothing about which
  component failed.
- **Fail closed**: unknown version, unsupported KDF/algorithm, malformed structure, invalid
  base64, wrong field lengths, out-of-range KDF parameters, and oversized files are all rejected
  (`UnsupportedVaultFormatException`). There is no plaintext fallback.
- **Resource-exhaustion resistance**: the vault file is treated as untrusted input. The file size
  is capped (16 MiB) before it is read, and the KDF cost parameters are bounded (see "KDF
  parameter bounds") before any Argon2 work, so a malicious file cannot trivially exhaust memory
  or CPU.
- **Nonce uniqueness**: nonces come from the CSPRNG and a fresh nonce is used for every
  encryption. Vault creation also generates a fresh salt and vault key.

## Limitations

- Argon2id parameters trade off unlock latency vs. brute-force cost; the defaults are a
  reasonable interactive baseline, not a maximum.
- The strength of everything depends on the master password's entropy.
- Pure-managed Argon2 is slower than a native implementation; this is an intentional trade for
  cross-platform simplicity and no native dependencies.
- See [SECURITY_MODEL.md](SECURITY_MODEL.md) for memory-handling limitations.

## Testing

Cryptographic behavior is covered by `BitosX.Crypto.Tests`, including round-trip correctness,
wrong-key/wrong-password rejection, ciphertext/tag/AAD/nonce/salt tampering, unsupported version
and malformed input handling, independent per-vault keys, empty-input safety, rejection of weak
**and** resource-exhausting KDF parameters, and rejection of malicious KDF parameters in a vault
document before any derivation is attempted.
