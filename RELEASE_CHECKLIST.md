# Release Checklist

Pre-release verification for the **encrypted local vault baseline**. Run every gate below and
confirm the expected result before pushing or tagging. Results shown are the last recorded run
on Windows 11 / .NET 10.0.12 (SDK 10.0.401). See [docs/VERIFICATION.md](docs/VERIFICATION.md) for
the full verification-status matrix and per-step expected GUI results.

> Legend for status: ✅ automated/verified · ⚠️ manual, **NOT YET DONE**.

## Automated gates

| # | Gate | Command | Expected / last result |
|---|---|---|---|
| 1 | Build Debug | `dotnet build BitosX.slnx -c Debug` | ✅ Build succeeded — 0 warnings, 0 errors |
| 2 | Build Release | `dotnet build BitosX.slnx -c Release` | ✅ Build succeeded — 0 warnings, 0 errors |
| 3 | Test Debug | `dotnet test BitosX.slnx -c Debug` | ✅ 112 passed, 0 failed, 0 skipped (Crypto 39, Core 53, Storage 9, Platform 11) |
| 4 | Test Release | `dotnet test BitosX.slnx -c Release` | ✅ 112 passed, 0 failed, 0 skipped |
| 5 | Plaintext-regression check | `dotnet test BitosX.slnx -c Release --filter "FullyQualifiedName~VaultPersistenceIntegrationTests"` | ✅ Pass — persisted vault file contains none of the distinctive markers for master password, login password, username, item name, notes, or URL host. **This is a regression check against accidental plaintext persistence, NOT a proof of cryptographic security.** |
| 6 | Argon2 benchmark | `dotnet run -c Release --project tools/BitosX.KdfBenchmark -- 12` | ✅ See "Argon2 benchmark result" below |

## Secret scan

```bash
# No private keys, tokens, API keys, certs, connection strings, or identities in tracked content.
git grep -nEi -- '-----BEGIN|PRIVATE KEY|AKIA[0-9A-Z]{16}|xox[baprs]-|ghp_[A-Za-z0-9]{20}|client[_-]?secret|Bearer [A-Za-z0-9._-]{20}'
```
Expected: **no matches**. (Test/benchmark files contain only obviously-fake password fixtures.) ✅ last run: clean.

## Repository clean check

```bash
git status --porcelain                       # expect: empty (clean working tree)
git ls-files | grep -Ei '(^|/)(bin|obj)/|\.bitosx|\.pfx|\.snk|\.trx|\.coverage|secrets\.json'   # expect: no output
```
Expected: clean tree; **no** build output, vault files (`*.bitosx`), key/cert files, or test output tracked. ✅ last run: clean (116 files tracked). `.gitignore` excludes `bin/`, `obj/`, `*.bitosx`, `*.pfx`, `*.snk`, `secrets.json`, test/coverage output.

## Argon2 benchmark result

Production profile (**not** weakened): Argon2id, 64 MiB / 3 iterations / parallelism 4 / 32-byte key.
Reference machine: 16 logical CPUs, .NET 10.0.12, Release, 12 measured runs + warm-up.

| Metric | Value |
|---|---|
| minimum | 42.7 ms |
| median | 46.1 ms |
| average | 52.5 ms |
| maximum | 130.1 ms (first-run warm effects) |

Comfortably interactive; on the low side for offline brute-force cost. **Do not retune from a single
machine** — a later milestone should add device-aware calibration and versioned migration. KDF cost
parameters are bounded (memory ≤ 1 GiB, iterations ≤ 64, parallelism ≤ 64) to stop a malicious vault
file from causing resource exhaustion on unlock.

## Manual GUI verification checklist — ⚠️ NOT YET DONE

Run `dotnet run --project src/BitosX.App`. Confirm each step (full expected results in
[docs/VERIFICATION.md](docs/VERIFICATION.md)). On a clean machine, ensure no `%APPDATA%\BitosX\vault.bitosx`
exists before step 1.

| # | Step | Expected result |
|---|---|---|
| 1 | First launch (no vault) | Opens on **Create Vault** screen |
| 2 | Create vault (master password ≥ 8) | Unlocks to empty main shell; `vault.bitosx` created |
| 3 | Restart app | Opens on **Unlock** screen |
| 4 | Wrong master password | Rejected with a single generic error; stays locked |
| 5 | Correct master password | Vault unlocks |
| 6 | Add login (name/user/pass/URL/notes) | Item appears; file changes |
| 7 | Edit login | Change shown and persists |
| 8 | Generate password | Random value; regenerates differently |
| 9 | Copy username | Status confirms; clipboard holds username |
| 10 | Copy password | Status confirms; clipboard holds password |
| 11 | Wait for clipboard timeout (~30 s) | Clipboard cleared; but content **you** copied afterward is **not** erased |
| 12 | Favorite / unfavorite | Indicator toggles; favorites sort to top; persists |
| 13 | Lock vault | Returns to Unlock screen |
| 14 | Verify secrets disappear | No item list/selection/password visible; no route back without re-auth |
| 15 | Unlock again | Items reappear unchanged |
| 16 | Delete item | Item gone; stays gone after save |
| 17 | Restart & verify persistence | Added/edited items present; deleted item gone |

## Known security limitations

- **Managed-memory wiping is best-effort.** Byte buffers (KEK, vault key, password bytes, plaintext)
  are zeroed; secrets held as `string` (UI passwords, clipboard compare value) cannot be reliably erased.
- **Decrypted content is not re-validated against `VaultValidation` bounds on load** (defense-in-depth
  only — content is AEAD-authenticated so requires the key; file is size-capped pre-decryption).
- **KDF profile is fast on high-end hardware** (~46 ms median) — interactive but modest brute-force cost.
- **No protection against a compromised device, malware/keyloggers, weak master password, OS clipboard
  history/sync, or a forgotten master password** (no recovery/backdoor). See
  [docs/SECURITY_MODEL.md](docs/SECURITY_MODEL.md).

## NOT VERIFIED items

- **End-to-end GUI behavior** — no manual click-through performed (the 17-step checklist above).
- **Secure erasure of `string`-held secrets** — not achievable on managed .NET (documented).
- **Cross-platform behavior on Linux/macOS** — code is cross-platform, but only Windows was exercised.

## Release commands — ⚠️ DO NOT RUN until every gate above is green

Replace `<REMOTE_URL>` with the real remote before running.

```bash
git remote add origin <REMOTE_URL>
git push -u origin main
git tag -a v0.1.0-local-vault -m "BitosX v0.1.0 - encrypted local vault baseline"
git push origin v0.1.0-local-vault
```
