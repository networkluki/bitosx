# Encrypted Backup & Restore

BitosX can export an **encrypted backup** of your vault and restore from one. The backup is the
existing encrypted vault envelope copied **byte-for-byte** (Approach A) — there is **no separate
backup crypto format and no new cryptographic primitive**. A backup therefore has exactly the same
security properties as the live vault: AES-256-GCM content under a random vault key wrapped by an
Argon2id-derived key. **A backup never contains plaintext**, the master password, the vault key, or
the KEK. Vault format stays **v1**.

## Export
- Requires an **unlocked** session (UI authorization; the file is ciphertext, so this is a policy
  gate, not a cryptographic one).
- Copies the current `vault.bitosx` byte-for-byte to a chosen file, **atomically** (temp file +
  replace), after validating that the current vault parses. The active vault is **never modified**.
- Default file name: `BitosX-backup-YYYY-MM-DD.bitosx-backup` — it contains no email, user name,
  item names, or secrets.

## Restore (destructive — replaces the current vault)
Restore treats the backup file as **fully attacker-controlled** and authenticates it **before** any
replacement. Flow:
1. Choose file → read with a **16 MiB size cap enforced before the full read**.
2. **Strict parse** (`EncryptedVaultSerializer`): version, KDF/algorithm identifiers, base64,
   field lengths, **KDF parameter bounds (before any Argon2 work)**, and **duplicate JSON
   properties are rejected**.
3. Enter the **backup's** master password → Argon2id **within validated bounds** → **authenticated**
   unwrap/decrypt (wrong password or tampering fails closed).
4. **Validate the decrypted model** (schema, unique ids, per-item `VaultValidation`).
5. Show a safe summary (format version, item count [computed locally after decryption], file name,
   size) — **never decrypted item content**.
6. On explicit confirmation: preserve the current vault as a **recovery copy**
   (`vault.bitosx.pre-restore`), then **atomically replace** the vault, then lock so you re-unlock
   with the backup's password.

Restore is **replace-only** in this milestone — it never merges vaults. Also required: the **current
session must be unlocked** (authorizes the destructive replace) in addition to the backup password.

## Recovery / rollback
- Any failure in steps 1–5 leaves the live vault **byte-for-byte unchanged** (nothing is written).
- Before replacement the current vault is copied to `vault.bitosx.pre-restore`.
- The replacement itself is atomic (`File.Replace`), so a crash leaves **either** the old **or** the
  restored valid vault — never a partial file.

## Compatibility
- Backups from 0.1.0 / 0.2.0 / 0.3.0 (all v1 envelopes) restore into 0.4.0.
- A future **v2** backup fails safely on a v1-only client (unsupported-format error, before any
  crypto). Supported existing formats restore; unknown future versions fail closed.

## Master-password history (important)
A backup is encrypted with whatever master password was active **when it was made**. A backup made
**before** a master-password change requires the **old** password; one made **after** requires the
**new** password. BitosX does **not** upgrade old backups and **cannot recover a forgotten backup
password**.

## What a backup does and does not protect
- It is safe to copy to USB / external disk / NAS / cloud: the contents are ciphertext.
- Its security depends on the **strength of the master password** it was made with.
- Encryption is **not** a substitute for storage hygiene: cloud providers may retain deleted copies
  or version history; old backups remain valid indefinitely unless you delete them.

## Verification status
| Item | Status |
|---|---|
| Byte-for-byte export; active vault untouched; no plaintext markers | AUTOMATED TESTED (`VaultBackupServiceTests`) |
| Restore replace + recovery copy; unlock with backup password | AUTOMATED TESTED |
| Wrong password / corrupted / unsupported version / malicious KDF / oversized / malformed all fail closed, live vault unchanged | AUTOMATED TESTED |
| Duplicate JSON property rejected | AUTOMATED TESTED (`EncryptedVaultSerializerTests`) |
| Pre/post master-password-change backups need the respective password; foreign-vault replace-only (no merge) | AUTOMATED TESTED |
| Destructive-confirmation hardening — Enter/Space at the preview do not restore; focused Cancel; Esc cancels; acknowledgement required and not retained after Cancel; enabling Replace writes nothing; commit only on explicit click | **MANUAL VERIFIED** 2026-09-13 (H1–H6, Windows 11) |
| End-to-end GUI (file picker, confirmation, restart, recovery copy) | **MANUAL VERIFIED** 2026-09-13 (Windows 11) |
| Linux/macOS file picker & behavior | **NOT VERIFIED** |

Manual H1–H6 method and full results are recorded in
[VERIFICATION.md](VERIFICATION.md#manual-restore-hardening-verification--h1h6-2026-09-13-windows-11-real-avalonia-ui):
each destructive-input attempt was checked against the on-disk encrypted vault by SHA-256, and no
attempt other than the explicit "Replace my vault" click ever wrote the vault.
