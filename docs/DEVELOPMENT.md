# Development

## Prerequisites

- .NET SDK **10.0** (developed with 10.0.401). Check with `dotnet --version`.
- No other global tools are required. All dependencies are restored from NuGet.

## Common commands

```bash
dotnet restore                       # restore packages
dotnet build                         # build the whole solution (BitosX.slnx)
dotnet test                          # run all tests
dotnet run --project src/BitosX.App  # launch the desktop app
```

Run a single test project:

```bash
dotnet test tests/BitosX.Crypto.Tests/BitosX.Crypto.Tests.csproj
```

## Code quality

- `Nullable` reference types are enabled solution-wide (`Directory.Build.props`).
- .NET analyzers run in `Recommended` mode. **Warnings are not suppressed to make the build
  green.** The one scoped exception is `CA1707` (underscores in member names) for **test
  projects only**, because `Method_Scenario_Expectation` is the standard xUnit test naming
  convention (see `tests/Directory.Build.props`). Do not disable analyzers to hide real issues,
  and never weaken security-relevant rules.
- Logging uses source-generated `LoggerMessage` methods (`VaultLog`, `ClipboardLog`) for
  performance and to keep a fixed set of non-sensitive events.

## Project conventions

- `BitosX.Core` and `BitosX.Crypto` must not reference Avalonia or any UI type.
- No cryptographic operations in view code-behind; the UI talks to `IVaultService` and other
  services only.
- All source, comments, identifiers, logs, and docs are in English.
- Secrets (master password, item passwords, keys, decrypted contents) are never logged and never
  written to disk unencrypted.

## Adding a new vault item type

1. Add a record deriving from `VaultItem` in `BitosX.Core/Domain` and register it with a
   `[JsonDerivedType]` discriminator on `VaultItem`.
2. Extend `VaultValidation.Validate` with type-specific rules.
3. Add UI (view model + view) and CRUD as needed.
4. Add tests.

## Evolving the encrypted format

The format is versioned (`CryptoConstants.FormatVersion`). To introduce version 2:

1. Bump the version and add the new parameters/fields to `EncryptedVault` and the serializer.
2. Keep reading version 1 (add an upgrade path); never silently accept unknown versions.
3. Preserve fail-closed behavior and add tests covering both versions.

## Tests

- `BitosX.Crypto.Tests` — KDF, AEAD, format, tamper/fail-closed cases.
- `BitosX.Core.Tests` — vault CRUD/validation, password generator, security analyzer,
  `VaultService` lifecycle with an in-memory storage double.
- `BitosX.Platform.Tests` — clipboard policy (timed clear, retry, workstation lock / suspend) and
  the single-instance guard.
- `BitosX.App.Tests` — view models (the restore commit boundary, rotation, key derivation, failure
  handling), the content handed to the clipboard, and the **real views run without a display**
  (`*UiTests`, see below).
- `BitosX.Storage.Tests` — atomic file storage, malformed/oversized handling, and an end-to-end
  persistence integration test using the real crypto + file storage.

Use `Argon2Parameters.ForTesting` (a deliberately weak profile) in tests to keep them fast; never
use it for real vaults.

### Testing a view

`HeadlessUi` (in `BitosX.App.Tests`) runs a view on the headless platform of the UI framework: the
same XAML, styles, resources and view locator as the application, without a window on a screen.
A test builds the view model on real services in a temporary directory, shows the view, and then
acts as a user would — `HeadlessUi.Type` (focus and text input), `HeadlessUi.Press` (focus and
Enter), `HeadlessUi.Toggle` (focus and Space) — and reads what the view shows. Controls are found
by `Name`, so give a control a name when a test needs it.

This tests what a view shows and does. It does not test how it looks, and it is not a substitute
for the manual checklist.

Text that is not ASCII is written with escapes in tests (`"l�senord"`), never typed into the
source file: the composed and decomposed forms of a letter look the same, and an editor or a
version control setting can turn one into the other.

### Writing a security regression test

A test for a defect has to be able to fail. Before relying on one, run it against the code from
before the fix — a second checkout (`git worktree add <dir> <commit>`) is the easiest way — and
confirm that it fails there. This is how the tests of 0.5.1 were checked.

`BitosX.Core` exposes its internals to `BitosX.Core.Tests` (`InternalsVisibleTo`). The only member
that exists for tests is `VaultService.HoldsKeyMaterial`, a read-only flag used to assert that no
vault key is left in memory.

## Diagnostic tools

Neither is shipped.

- `tools/BitosX.KdfBenchmark` — Argon2id unlock latency, for the production profile or one of the
  stronger ones (`-- 12 strong`, `-- 12 maximum`). With the heap of the process limited
  (`DOTNET_GCHeapHardLimit`) it shows what happens on a device without the memory.
- `tools/BitosX.ClipboardProbe` — drives the production clipboard code against the **real**
  Windows clipboard and clipboard history and checks the result the way another application
  would see it. Windows-only, so it is **not part of `BitosX.slnx`**; build and run it on its own:

  ```bash
  dotnet run -c Release --project tools/BitosX.ClipboardProbe
  ```

  It uses the clipboard for about twenty seconds, places only its own marker strings there, puts
  back the text that was on it, and removes its markers from the clipboard history. Exit code 0
  means every check passed. Run it whenever clipboard code or the Avalonia version changes.

## Where data lives during development

The default vault path is the per-user application data directory (e.g.
`%APPDATA%\BitosX\vault.bitosx`). Delete that file to start from a clean "no vault" state. The
working directory is never used for permanent storage.
