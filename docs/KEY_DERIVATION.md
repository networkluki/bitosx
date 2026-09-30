# Strengthening Key Derivation

The key that protects your vault is wrapped under a key derived from your master password with
**Argon2id**. How expensive that derivation is decides how expensive every guess is for someone
who holds a copy of the vault file. Since 0.6.0 an existing vault can be moved to stronger
settings. Before that the settings were fixed when the vault was created, and neither a
master-password change nor a key rotation changed them.

The **on-disk format stays v1**, the **master password is unchanged**, and the **content is not
re-encrypted**.

## What it does

Recall the key hierarchy (see [CRYPTOGRAPHY.md](CRYPTOGRAPHY.md#key-hierarchy)):

```
master password --Argon2id(salt, settings)--> KEK
random 256-bit vault key --AES-256-GCM(KEK)--> wrapped vault key
plaintext --AES-256-GCM(vault key)--> ciphertext
```

Strengthening, given the **current master password** (which must be re-entered):

1. Derives the KEK with the vault's *current* settings and **authenticates** the password by
   unwrapping the vault key. A wrong password fails closed before anything new is produced.
2. Derives a new KEK from the same password with the **new settings** and a **fresh salt**.
3. Wraps the **same vault key** under the new KEK, with a fresh nonce. The new settings are bound
   to the wrapped key as associated data, as the old ones were.
4. Runs a **self-check**: the newly wrapped key must unwrap to exactly the vault key.
5. Writes the vault through the same consistency gate, cancellation boundary and write-failure
   reconciliation as every other operation that replaces the vault file, and only then adopts the
   new header in memory.

This is the same re-wrap a master-password change performs, with the settings changing instead of
the password.

## The settings on offer

| Profile | Memory | Passes | Lanes | |
|---|---|---|---|---|
| **Standard** | 64 MiB | 3 | 4 | What vaults were created with before 0.7.0. The second recommended option of RFC 9106. |
| **Strong** | 256 MiB | 3 | 4 | Four times the memory per guess. **What new vaults are created with** since 0.7.0. |
| **Maximum** | 1 GiB | 3 | 4 | The most format v1 allows (see [KDF parameter bounds](CRYPTOGRAPHY.md#kdf-parameter-bounds-untrusted-input)). |

The profiles differ in memory only, because memory is what makes Argon2 expensive to attack on
any hardware. Only profiles **stronger than the vault's own settings** are offered.

An existing vault is never moved by itself. **Measure on this device** runs the real derivation
with the selected settings and shows how long it took and how much memory it needs; the decision
is yours.

Measured on four kinds of machine (.NET 10, Release, pure-managed Argon2, median of five
derivations) — your device will differ:

| Machine | Logical CPUs | Standard | Strong | Maximum |
|---|---|---|---|---|
| Development machine, Windows 11 | 16 | about 0.05 s | about 0.2 s | about 0.85 s |
| Build service, macOS 15 | 5 | 0.14 – 0.18 s | 0.3 – 0.5 s | 1.3 – 1.6 s |
| Build service, Ubuntu 24.04 | 2 | 0.12 – 0.41 s | 0.5 – 1.6 s | 2.0 – 6.5 s |
| Build service, Windows Server 2025 | 2 | 0.26 – 0.61 s | 1.0 – 2.5 s | 4.3 – 6.9 s |

The machines of the build service are virtual, shared, and not the same from one run to the next;
the ranges are what four runs gave (2026-09-29 and 2026-09-30). They are measured again for every
change: see the step "Key derivation" of `.github/workflows/ci.yml`.

## What a new vault is created with

Until 0.7.0 a new vault was created with *Standard*, and the documentation said that this would
not be raised from measurements on one machine. It has now been measured on four kinds. On the
slowest of them, virtual machines with two processors that they share with others, *Strong* costs
between half a second and two and a half seconds per unlock: noticeable there, a fraction of a
second on an ordinary desktop, and four times the memory for every guess at the master password.
256 MiB is memory a desktop system has to spare. Since 0.7.0 a new vault is created with
**Strong**.

- **Existing vaults are not changed.** A vault created with *Standard* keeps opening as before.
  Settings says that it uses weaker settings than new vaults get, and offers to strengthen it.
- **A device that cannot spare 256 MiB** when the vault is created gets a vault with *Standard*
  instead. A vault is created on the device it will be opened on; settings that device cannot
  afford would produce a vault it cannot open. Settings then says the same as for an older vault.
  This is the only place where settings are chosen for the user, and it chooses the weaker of two.
- A vault created with *Strong* needs 256 MiB on **every** device it is opened on.

## Guarantees

- **Stronger only.** New settings must have at least as much memory *and* at least as many passes
  as the current ones, and more of one of them. Anything else is refused
  (`WeakerKeyDerivationException`) before any key is derived. Whoever holds a copy of the vault as
  it is today keeps the benefit of today's settings, so weakening could only help an attacker.
  Parallelism is not compared: it decides how the work is spread over threads, not how much work
  there is.
- **Tried out before it is written.** Both derivations and the self-check run before the first
  byte is written. If the device cannot provide the memory, the operation fails
  (`KeyDerivationResourceException`), the vault is untouched and the session stays usable. A vault
  can therefore not be moved to settings that the device it is on cannot open.
- **Requires the current master password**, not just an unlocked session.
- **Within the bounds of the format.** Settings outside the bounds are refused, so every version
  that reads format v1 can read the result.
- **Fails closed** like the other write operations: a vault that changed on disk while it was
  open is not overwritten; a write that throws is reconciled against the file; locking during the
  operation leaves no key in memory.

## What it does and does not protect

- It raises the cost of **guessing the master password** against a stolen copy of the vault
  **made after the change**.
- It is **not retroactive.** Copies and backups made before keep the settings they were made with.
  Restoring such a backup brings the older settings back; Settings shows the settings of the vault
  that is open and says when they are weaker than what new vaults get.
- It does **not rotate the vault key** and does not protect against a master password that is
  already known. Use [vault key rotation](KEY_ROTATION.md) and a master-password change for those.
- **Every device that opens the vault needs the memory.** A vault at *Maximum* cannot be opened on
  a device that cannot spare 1 GiB. The error then says so; it does not say the password is wrong.
  Nothing is lost: the same file opens on a device with more memory.
- No amount of key derivation rescues a master password that can be guessed in a few thousand
  attempts.

## How to

Settings → **Strengthen key derivation** → choose a profile → optionally **Measure on this
device** → enter your current master password → **Strengthen key derivation**. The vault stays
unlocked and your items are untouched.

As a side effect the vault moves to the canonical encoding of the master password (see
[MASTER_PASSWORD.md](MASTER_PASSWORD.md)), as it does on a master-password change or key rotation.

## Testing

Covered at every layer: `KeyDerivationChangeTests` (crypto), `VaultServiceKeyDerivationTests`
(service), `SettingsPageViewModelKeyDerivationTests` (view model) and `SettingsPageUiTests` (the
real Settings view, run without a display). What a new vault is created with, and what it falls
back to, is covered by `VaultServiceCreationSettingsTests`. What happens on a device without the
memory was also checked with the real application, its memory limited; see
[VERIFICATION.md](VERIFICATION.md).
