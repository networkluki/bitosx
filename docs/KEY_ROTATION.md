# Vault Key Rotation

Vault key rotation replaces the random 256-bit **vault key** with a brand-new one and **re-encrypts
all content under it**, retiring the previous key. Unlike a master-password change (which only
re-wraps the same vault key), rotation changes the key that actually protects your data — closing the
limitation noted in [CRYPTOGRAPHY.md](CRYPTOGRAPHY.md#master-password-change) that a password change
does not invalidate a possibly-compromised vault key.

The **master password is unchanged**, and the **on-disk format stays v1** (no schema change).

## What rotation does

Recall the key hierarchy (see [CRYPTOGRAPHY.md](CRYPTOGRAPHY.md#key-hierarchy)):

```
master password --Argon2id(salt)--> KEK
random 256-bit vault key --AES-256-GCM(KEK)--> wrapped vault key
plaintext --AES-256-GCM(vault key)--> ciphertext
```

Rotation, given the **current master password** (which must be re-entered):

1. Derives the KEK from the current password + existing salt and **authenticates** the vault by
   unwrapping the old vault key and decrypting the content. A wrong password or any tampering fails
   closed (`VaultAuthenticationException`) **before** any new material is generated.
2. Generates **fresh cryptographic material**: a new random vault key, a new salt, a new key-wrap
   nonce, and a new content nonce. The Argon2id cost parameters are preserved.
3. **Re-encrypts the entire content** under the new vault key and wraps the new vault key under a KEK
   derived from the current password and the new salt.
4. Runs a **self-check** — the assembled envelope must unwrap to exactly the new vault key *and*
   decrypt back to the exact original plaintext — so an unreadable/lossy vault can never be produced.
5. Writes the rotated vault through the normal **atomic** storage path and only then adopts the new
   key/header in memory.

After rotation the same master password unlocks the vault (the password did not change), the item
contents are identical, and the old vault key can no longer decrypt the content.

## Authorization and boundaries

- **Requires an unlocked session AND re-entry of the current master password.** An unlocked session
  alone is not sufficient authorization — the KEK is not retained after unlock, and re-deriving it to
  wrap the new key requires the password. This also re-authorizes a sensitive operation.
- **Replace-only, in place.** Rotation does not merge or branch; it produces one new generation of the
  same vault.
- **Format unchanged (v1).** Only field *values* change (salt, key-wrap nonce, wrapped key, content
  nonce, ciphertext); the envelope schema, KDF identifier, algorithm, and Argon2 parameters are the
  same.

## Consistency and safety guarantees

Rotation is destructive (it replaces the vault file), so it is guarded on several fronts:

- **On-disk / in-memory consistency gate.** Before rotating, the service verifies that the on-disk
  vault still matches the state the current session loaded or last persisted, using a SHA-256
  **fingerprint of the whole encrypted envelope** (see `EncryptedVaultFingerprint`). This detects
  **both** a different cryptographic identity (a different key/header) **and** a same-key external
  content change (a different content nonce/ciphertext under the same key). If the file changed
  externally (e.g. another instance edited or rotated it), rotation **fails closed** with
  `VaultChangedExternallyException`, writes nothing, and leaves both disk and session untouched. The
  fingerprint is content-based; it never relies on file timestamps.
- **Cancellation boundary.** All expensive work (decrypt, re-encrypt, self-check) happens before any
  write, with a final cancellation check immediately before commit. The commit write is issued with a
  non-cancellable token and the new key/header are adopted with no awaited step in between, so
  cancellation can never leave `disk = rotated vault` while `memory = old key`.
- **Write-failure reconciliation.** If the commit write throws, the service re-reads disk and
  reconciles: if disk still holds the old vault it keeps the old key and surfaces the failure; if disk
  already holds the rotated vault it adopts the new key (success); if disk matches neither (or cannot
  be read) it **locks the session** and fails closed (`VaultInconsistentStateException`). The terminal
  state is always one of `OLD disk + OLD session`, `NEW disk + NEW session`, or
  `UNKNOWN disk + LOCKED session` — never `NEW disk + OLD session`.
- **Atomic write.** The underlying storage writes to a temp file and atomically replaces the vault, so
  a crash leaves either the old or the new vault, never a partial file. On any pre-commit failure the
  live vault is left **byte-for-byte unchanged**.

Since 0.5.1 the consistency gate, the cancellation boundary and the write-failure reconciliation
are no longer specific to rotation: **every** operation that replaces the vault file — saving a
change and changing the master password too — goes through the same gate and the same commit
routine. Rotation was the only one guarded before, which left the other two able to overwrite a
rotated vault with the retired key (see [SECURITY_REVIEW_0.5.1.md](SECURITY_REVIEW_0.5.1.md),
findings 2 and 3).

- **Locking during a rotation.** `Lock()` takes effect immediately and never waits. A rotation that
  is overtaken by a lock still completes on disk, but its new key is wiped instead of being adopted
  into the locked service; the vault then opens with the same master password.

## What rotation does and does not protect

- Rotation **retires the vault key for the current vault file**. If the old vault key (or the
  decrypted content) had been exposed, the current file is no longer protected by that key.
- Rotation is **not retroactive**. Copies and backups made **before** rotation still contain the old
  key material and remain decryptable with the master password — rotation does not reach into or
  invalidate them. Delete old backups if that history matters to you.
- Rotation addresses **vault-key** exposure, **not** a weak or compromised **master password** (the
  KEK is still derived from the same password). To change the password, use
  [master password change](CRYPTOGRAPHY.md#master-password-change); the two are independent.
- As elsewhere, decrypted item values live in managed memory and cannot be reliably wiped (see
  [SECURITY_MODEL.md](SECURITY_MODEL.md)).

## How to rotate

Settings → **Rotate vault key** → re-enter your current master password → **Rotate vault key**. The
action is only available while the vault is unlocked. On success the app stays unlocked with your data
intact under the new key.

## Testing

Rotation is covered by automated tests across the crypto, service, and view-model layers
(`VaultKeyRotationTests`, `EncryptedVaultFingerprintTests`, `VaultServiceRotationConsistencyTests`,
the rotation cases in `VaultServiceTests`, and `SettingsPageViewModelRotationTests`): new-key/plaintext
preservation, old key can no longer decrypt, format/parameters preserved, wrong-password and tamper
fail closed, external same-key and different-key change detection, the cancellation boundary, and the
write-failure reconciliation terminal states. Manual GUI verification on Windows 11 is recorded in
[VERIFICATION.md](VERIFICATION.md).
