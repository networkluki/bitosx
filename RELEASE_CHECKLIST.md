# Release Checklist

Pre-release verification. Run every gate below and confirm the expected result before tagging.
Results shown are the last recorded run, for 0.7.0 (2026-09-30): on the development machine
(Windows 11, .NET SDK 10.0.401) and on the machines of the build service (Ubuntu 24.04, macOS 15,
Windows Server 2025). See [docs/VERIFICATION.md](docs/VERIFICATION.md) for what each of them
covers and what none of them does.

> Legend for status: ✅ done for this release · ⚠️ **not done** for this release.

## The build service

Every pull request, and every change to `main`, runs `.github/workflows/ci.yml`. **A release is
tagged from a commit for which it is green.** It runs gates 1–5, 7, 9, 10 and 12–15 below, on
machines nobody sits at.

| Job | Result for 0.7.0 |
|---|---|
| Build and test — Ubuntu 24.04 | ✅ 818 passed, 1 skipped (what only Windows has) |
| Build and test — macOS 15 | ✅ 818 passed, 1 skipped (what only Windows has) |
| Build and test — Windows Server 2025 | ✅ 811 passed, 8 skipped (what only Unix has) |
| Clipboard managers — Ubuntu 24.04 | ✅ Klipper honors the marker; CopyQ 7.1.0 does not (reported, not a failure) |
| The real application — Windows Server 2025 | ✅ all 118 checks of the checklists passed; the clipboard probe: 16 passed, the 2 of the clipboard history left out (it is turned off there) |

## Gates

| # | Gate | Command | Expected / last result |
|---|---|---|---|
| 1 | Build Debug | `dotnet build BitosX.slnx -c Debug` | ✅ Build succeeded — 0 warnings, 0 errors |
| 2 | Build Release | `dotnet build BitosX.slnx -c Release -warnaserror` | ✅ Build succeeded — 0 warnings, 0 errors |
| 3 | Test Debug | `dotnet test BitosX.slnx -c Debug` | ✅ 811 passed, 0 failed, 8 skipped on Windows (Core 333, Crypto 152, App 232, Storage 59 + 8 skipped, Platform 35) |
| 4 | Test Release | `dotnet test BitosX.slnx -c Release` | ✅ 811 passed, 0 failed, 8 skipped on Windows |
| 5 | Plaintext-regression check | `dotnet test BitosX.slnx -c Release --filter "FullyQualifiedName~VaultPersistenceIntegrationTests"` | ✅ Pass — persisted vault file contains none of the distinctive markers for master password, login password, username, item name, notes, or URL host. **This is a regression check against accidental plaintext persistence, NOT a proof of cryptographic security.** |
| 6 | Argon2 benchmark | `dotnet run -c Release --project tools/BitosX.KdfBenchmark -- 12` (and `strong`, `maximum`) | ✅ See "What the key derivation costs" below |
| 7 | Known-answer tests | `dotnet test BitosX.slnx -c Release --filter "FullyQualifiedName~KnownAnswerTests"` | ✅ 12 passed — Argon2id and AES-256-GCM match the published reference outputs |
| 8 | Clipboard probe (Windows desktop) | `dotnet run -c Release --project tools/BitosX.ClipboardProbe` | ✅ 16 checks passed on Windows Server 2025 (build service), where the clipboard history is turned off — ⚠️ the 2 checks of the clipboard history were not made for 0.7.0; they were last made on a desktop for 0.6.0 (all 18 passed, 2026-09-29), and the code they exercise is unchanged. Uses the real clipboard for ~25 s and restores it. |
| 9 | Dependency audit | `dotnet list BitosX.slnx package --vulnerable --include-transitive` | ✅ no vulnerable packages (2026-09-30) |
| 10 | The whole application, without a display | `dotnet test BitosX.slnx -c Release --filter "FullyQualifiedName~ChecklistTests"` | ✅ 27 passed — the 17 steps, and the A-, H- and R-series |
| 11 | A device without the memory | `DOTNET_GCHeapHardLimit=0xC800000 dotnet run -c Release --project tools/BitosX.KdfBenchmark -- 3 strong` | ✅ reports the missing memory, exit code 3 |
| 12 | How the views look | `BITOSX_VIEW_SNAPSHOTS=artifacts/views dotnet test BitosX.slnx -c Release --filter "FullyQualifiedName~ViewAppearanceTests"`, then **look at the images** | ✅ 14 passed; 30 images, looked at by the assistant that made the changes — ⚠️ not by a person |
| 13 | The real application | `dotnet run -c Release --project tools/BitosX.GuiCheck` (Windows; about eight minutes, a window on screen, the clipboard in use) | ✅ all 118 checks passed on Windows Server 2025 (build service) |
| 14 | The clipboard of Linux and macOS | `dotnet run -c Release --project tools/BitosX.ClipboardCheck` (on Linux under `xvfb-run`) | ✅ all 10 checks passed on Ubuntu 24.04 and on macOS 15 (build service) |
| 15 | Clipboard managers | `xvfb-run -a dbus-run-session -- bash tools/ci/clipboard-managers.sh` (Linux) | ✅ Klipper 5.27.12 honors the marker. CopyQ 7.1.0 records a marked secret: known, documented |

## Secret scan

```bash
# No private keys, tokens, API keys, certs, connection strings, or identities in tracked content.
git grep -nEi -- '-----BEGIN|PRIVATE KEY|AKIA[0-9A-Z]{16}|xox[baprs]-|ghp_[A-Za-z0-9]{20}|client[_-]?secret|Bearer [A-Za-z0-9._-]{20}'
```
Expected: **no matches** other than this file, which quotes the patterns. (Test/benchmark files contain only obviously-fake password fixtures.) ✅ last run: clean.

## Repository clean check

```bash
git status --porcelain                       # expect: empty (clean working tree)
git ls-files | grep -Ei '(^|/)(bin|obj)/|\.bitosx|\.pfx|\.snk|\.trx|\.coverage|secrets\.json'   # expect: no output
```
Expected: clean tree; **no** build output, vault files (`*.bitosx`), key/cert files, or test output tracked. ✅ last run: clean (218 files tracked). `.gitignore` excludes `bin/`, `obj/`, `artifacts/`, `*.bitosx`, `*.pfx`, `*.snk`, `secrets.json`, test/coverage output.

## What the key derivation costs

Argon2id, 3 iterations, parallelism 4, 32-byte key; .NET 10, Release; median per derivation. The
machines of the build service are virtual and shared; the ranges are what four runs gave.

| Machine | 64 MiB | 256 MiB (new vaults) | 1 GiB |
|---|---|---|---|
| Development machine — Windows 11, 16 logical CPUs | about 0.05 s | about 0.2 s | about 0.85 s |
| Build service — macOS 15, 5 logical CPUs | 0.14 – 0.18 s | 0.3 – 0.5 s | 1.3 – 1.6 s |
| Build service — Ubuntu 24.04, 2 logical CPUs | 0.12 – 0.41 s | 0.5 – 1.6 s | 2.0 – 6.5 s |
| Build service — Windows Server 2025, 2 logical CPUs | 0.26 – 0.61 s | 1.0 – 2.5 s | 4.3 – 6.9 s |

New vaults are created with 256 MiB since 0.7.0 (64 MiB on a device that cannot spare the
memory); see [docs/KEY_DERIVATION.md](docs/KEY_DERIVATION.md). KDF cost parameters are bounded
(memory ≤ 1 GiB, iterations ≤ 64, parallelism ≤ 64) to stop a malicious vault file from causing
resource exhaustion on unlock.

## The GUI checklist

The 17 steps below, and the A-, H- and R-series of [docs/VERIFICATION.md](docs/VERIFICATION.md),
are carried out by programs since 0.7.0: gate 10 on the whole application without a display, gate
13 on the real application in a real window. They were last gone through **by a person** on
2026-09-13 (the 17 steps, A, H) and 2026-09-14 (R) — ⚠️ not for 0.5.1, 0.6.0 or 0.7.0. That run
caught a real clipboard-clear defect, which is the argument for doing it again: a program does not
notice what it was not told to look for.

To go through them by hand, do not use the vault at the default location. Start the application
on a directory of its own: `dotnet run --project src/BitosX.App -- --data-directory <full path>`.

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
| 16 | Delete item, and confirm | Nothing is deleted until confirmed; then the item is gone, and stays gone after save |
| 17 | Restart & verify persistence | Added/edited items present; deleted item gone |

## Known security limitations

- **Managed-memory wiping is best-effort.** Byte buffers (KEK, vault key, password bytes, plaintext)
  are zeroed; secrets held as `string` (UI passwords, clipboard compare value) cannot be reliably erased.
- **The clipboard markers are requests.** They are on the real clipboard of Windows, Linux and
  macOS, and Windows and Klipper honor them — CopyQ 7.1.0 does not. Software that reads the
  clipboard directly still sees a copied value while it is there.
- **What the window shows, it tells the accessibility interface.** A password field tells it dots,
  but cannot tell it that it is a password field; a screen reader may speak what is typed.
- **The strength rating has no dictionary**; a good rating means no obvious weakness was found.
- **A vault created before 0.7.0 has 64 MiB** of key-derivation memory until its user strengthens
  it; Settings says so.
- **No protection against a compromised device, malware/keyloggers, weak master password, or a
  forgotten master password** (no recovery/backdoor). See
  [docs/SECURITY_MODEL.md](docs/SECURITY_MODEL.md).

## NOT VERIFIED items

- **Secure erasure of `string`-held secrets** — not achievable on managed .NET (documented).
- **A person looking at a real window.** The screens are drawn and checked without a display, and
  the real window is operated by a program.
- **The application itself on Linux and macOS.** Its tests run there for every change, including
  the whole application without a display; the application has not been started there.
- **Locking the workstation and putting the system to sleep, for real.** The real application is
  sent the messages Windows sends for them.
- **The clipboard on Wayland; clipboard managers for macOS and Windows; the clipboard history of
  macOS; the cloud clipboard of Windows.**
- See the "NOT VERIFIED" table of 0.7.0 in [docs/VERIFICATION.md](docs/VERIFICATION.md) for the
  full list.

## Releasing

The steps are taken one at a time, each when it is asked for: a branch, a pull request, the merge
to `main` (fast-forward), removing the branch, an annotated tag `v<version>-<slug>` with the
message `BitosX v<version> - <description>`, and a release on GitHub. ⚠️ Do not tag until every
gate above is green for the commit that is tagged.
