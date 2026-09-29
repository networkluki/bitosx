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
| 3 | Test Debug | `dotnet test BitosX.slnx -c Debug` | ✅ 684 passed, 0 failed, 0 skipped (Core 318, Crypto 142, App 133, Storage 56, Platform 35) |
| 4 | Test Release | `dotnet test BitosX.slnx -c Release` | ✅ 684 passed, 0 failed, 0 skipped |
| 5 | Plaintext-regression check | `dotnet test BitosX.slnx -c Release --filter "FullyQualifiedName~VaultPersistenceIntegrationTests"` | ✅ Pass — persisted vault file contains none of the distinctive markers for master password, login password, username, item name, notes, or URL host. **This is a regression check against accidental plaintext persistence, NOT a proof of cryptographic security.** |
| 6 | Argon2 benchmark | `dotnet run -c Release --project tools/BitosX.KdfBenchmark -- 12` | ✅ See "Argon2 benchmark result" below |
| 7 | Known-answer tests | `dotnet test BitosX.slnx -c Release --filter "FullyQualifiedName~KnownAnswerTests"` | ✅ 12 passed — Argon2id and AES-256-GCM match the published reference outputs |
| 8 | Clipboard probe (Windows) | `dotnet run -c Release --project tools/BitosX.ClipboardProbe` | ✅ all 18 checks passed (2026-09-29). Uses the real clipboard for ~25 s and restores it. |
| 9 | Dependency audit | `dotnet list BitosX.slnx package --vulnerable --include-transitive` | ✅ no vulnerable packages (2026-09-29) |
| 10 | Views, run without a display | `dotnet test BitosX.slnx -c Release --filter "FullyQualifiedName~UiTests"` | ✅ 22 passed — what the views show and do, not how they look |
| 11 | Stronger key-derivation profiles | `dotnet run -c Release --project tools/BitosX.KdfBenchmark -- 5 strong` (and `maximum`) | ✅ about 0.2 s and 0.85 s on the reference machine |
| 12 | A device without the memory | `DOTNET_GCHeapHardLimit=0x19000000 dotnet run -c Release --project tools/BitosX.KdfBenchmark -- 3 maximum` | ✅ reports the missing memory, exit code 3 |

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

## Manual GUI verification checklist — ✅ DONE 2026-09-13 (all 17 steps PASS)

Run `dotnet run --project src/BitosX.App`. Confirm each step (full expected results in
[docs/VERIFICATION.md](docs/VERIFICATION.md)). On a clean machine, ensure no `%APPDATA%\BitosX\vault.bitosx`
exists before step 1. **Completed 2026-09-13 on Windows 11 / .NET 10.0.12 — all 17 steps passed.**
This run caught a real clipboard-clear defect (copied secrets were not cleared after the timeout
because Avalonia clipboard ops must run on the UI thread); it was fixed and step 11 re-verified.
Re-run this checklist for each future release.

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
- **Clipboard markers are verified on Windows only**; on macOS and Linux they are set but untested.
  Everywhere they are requests: software that reads the clipboard
  directly still sees a copied value while it is there.
- **The strength rating has no dictionary**; a good rating means no obvious weakness was found.
- **KDF profile is fast on high-end hardware** (~46 ms median) — interactive but modest brute-force cost.
- **No protection against a compromised device, malware/keyloggers, weak master password, OS clipboard
  history/sync, or a forgotten master password** (no recovery/backdoor). See
  [docs/SECURITY_MODEL.md](docs/SECURITY_MODEL.md).

## NOT VERIFIED items

- **Secure erasure of `string`-held secrets** — not achievable on managed .NET (documented).
- **0.6.0: everything on macOS and Linux**, including the clipboard markers and the file
  permissions; **how the views look** (the headless tests check what they show). See the
  "NOT VERIFIED" table in [docs/VERIFICATION.md](docs/VERIFICATION.md).
- **0.5.1: the new Settings texts, clearing the clipboard on workstation lock in the running
  application, and Unix file permissions** — see the "NOT VERIFIED" table in
  [docs/VERIFICATION.md](docs/VERIFICATION.md). The manual GUI checklist below was **not re-run for
  0.5.1 or 0.6.0**; re-run it before tagging.
- **Cross-platform behavior on Linux/macOS** — code is cross-platform, but only Windows was exercised
  (note: the clipboard UI-thread fix uses `Dispatcher.UIThread`, which is cross-platform in Avalonia,
  but the clipboard clear has been manually confirmed only on Windows).

## Release commands — ⚠️ DO NOT RUN until every gate above is green

Replace `<REMOTE_URL>` with the real remote before running.

```bash
git remote add origin <REMOTE_URL>
git push -u origin main
git tag -a v0.1.0-local-vault -m "BitosX v0.1.0 - encrypted local vault baseline"
git push origin v0.1.0-local-vault
```
