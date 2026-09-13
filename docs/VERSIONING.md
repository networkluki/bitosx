# Versioning

BitosX uses a three-part version number: **`MAJOR.MINOR.PATCH`** (for example `1.3.2`).

| Position | Example digit in `1.3.2` | Name | Bump it for… |
|---|---|---|---|
| 1st | `1` | MAJOR | **Big changes** — breaking changes, a new on-disk vault format version, removing or redesigning features, anything that isn't backward-compatible. |
| 2nd | `3` | MINOR | **Medium changes** — new features added in a backward-compatible way (e.g. auto-lock, encrypted export). |
| 3rd | `2` | PATCH | **Small changes** — bug fixes, security hardening, docs, and other backward-compatible tweaks that add no new feature. |

This is the widely-used [Semantic Versioning](https://semver.org) convention.

## Rules of thumb

- Increment the **highest** position that applies, and reset the lower positions to `0`
  (e.g. a big change from `0.4.7` → `1.0.0`; a new feature from `1.0.3` → `1.1.0`; a bug fix from
  `1.1.0` → `1.1.1`).
- While the project is pre-1.0 (`0.x.y`), the API and vault format are still stabilizing; a `0.MINOR`
  bump may include larger changes than it would after 1.0.
- A change to the **encrypted vault on-disk format version** (`CryptoConstants.FormatVersion`) is a
  MAJOR change and must ship with an upgrade/migration path.

## Where the version lives

- The single source of truth is `<Version>` in **`Directory.Build.props`** (inherited by every
  project). Bump it there.
- Releases are marked with an annotated Git tag `vMAJOR.MINOR.PATCH` (optionally with a suffix, e.g.
  `v0.1.0-local-vault` for this milestone).

### Why .NET sometimes shows four numbers (e.g. `0.1.0.0`)

Our version is **three-part** (`MAJOR.MINOR.PATCH`). However, the .NET CLR and the Windows
file-version resource require the **AssemblyVersion** and **FileVersion** to have **four** numeric
components (`Major.Minor.Build.Revision`), so `0.1.0` is displayed there as `0.1.0.0`. That trailing
`.0` is platform padding only — BitosX never assigns meaning to a fourth level. The authoritative,
human-facing version stays three-part: the product/informational version is `0.1.0` (built as
`0.1.0+<git commit>`) and the release tag is `v0.1.0-...`.

## Current version

**`0.1.0`** — the encrypted local vault baseline. The first release tag will be `v0.1.0-local-vault`
(held until manual GUI verification passes; see [RELEASE_CHECKLIST.md](../RELEASE_CHECKLIST.md)).
