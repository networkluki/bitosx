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
- `BitosX.App.Tests` — view models, the content handed to the clipboard, the **real views run
  without a display** (`*UiTests`), the **whole application** with the manual checklists carried
  out on it (`ChecklistTests`), and the **layout of every screen** (`ViewAppearanceTests`). See
  below.
- `BitosX.Storage.Tests` — atomic file storage, malformed/oversized handling, what is specific to
  Unix (`UnixFilesTests`), and an end-to-end persistence integration test using the real crypto +
  file storage.

Use `Argon2Parameters.ForTesting` (a deliberately weak profile) in tests to keep them fast; never
use it for real vaults. The tests of the whole application are the exception: they use the
settings the application uses.

A test of something only one kind of system has is marked `[UnixFact]` or `[WindowsFact]`
(`tests/BitosX.Storage.Tests/PlatformFacts.cs`). Elsewhere it is reported as **skipped** — never
as passed, which would say that something was checked.

### Testing a view

`HeadlessUi` (in `BitosX.App.Tests`) runs a view on the headless platform of the UI framework: the
same XAML, styles, resources, fonts and view locator as the application, measured and drawn by
the same renderer, without a window on a screen. A test builds the view model on real services in
a temporary directory, shows the view, and then acts as a user would — `HeadlessUi.Type` (focus
and text input), `HeadlessUi.Press` (focus and Enter), `HeadlessUi.Click` (the mouse),
`HeadlessUi.Toggle` (focus and Space) — and reads what the view shows. Controls are found by
`Name`; every control in the views has one. The tests run one at a time: they share one
application, and with it one keyboard focus and one clipboard.

Text that is not ASCII is written with escapes in tests (`"lösenord"`), never typed into the
source file: the composed and decomposed forms of a letter look the same, and an editor or a
version control setting can turn one into the other.

### Testing the whole application

`WholeApplication` puts the application together the way `App` does — `ServiceConfiguration` and
`MainWindowSession`, which is also what `App` uses — in a data directory of its own, and shows its
main window without a display. What reaches outside the process is replaced: the session signals
of the operating system (so a test can lock the workstation), the clock that automatic locking
reads (so a test need not wait a minute), the file dialogs, and how long a copied value stays on
the clipboard.

`ChecklistTests` carries out the manual checklists of [VERIFICATION.md](VERIFICATION.md) on it.
When the application gains something a person would check by hand, add the check there.

### How the views look

`ViewAppearanceTests` lays out every screen at the size the window opens with and at the smallest
it can be given, and fails if a text is cut off, if a control is outside what is shown and cannot
be scrolled to, or if a text is shortened with an ellipsis and cannot be read in full somewhere
(`ViewLayout`). With the environment variable `BITOSX_VIEW_SNAPSHOTS` set to a directory, each
screen is also drawn to a PNG file there:

```bash
BITOSX_VIEW_SNAPSHOTS=artifacts/views dotnet test --filter "FullyQualifiedName~ViewAppearanceTests"
```

The build service keeps the images of each system for two weeks (the artifacts `views-…` of a
run). **Look at them when a view changes.** The checks find what can be measured; whether a screen
looks right is for someone to see.

### Writing a security regression test

A test for a defect has to be able to fail. Before relying on one, run it against the code from
before the fix — a second checkout (`git worktree add <dir> <commit>`) is the easiest way — and
confirm that it fails there. Where that is not possible, because the test uses something the old
code does not have, take the fix out and confirm that the test fails. This is how the tests of
0.5.1 and of 0.7.0 were checked; the second way found a test that passed with the code it tests
taken out.

`BitosX.Core` exposes its internals to `BitosX.Core.Tests`, and `BitosX.Storage` to
`BitosX.Storage.Tests` (`InternalsVisibleTo`). The only member that exists for tests is
`VaultService.HoldsKeyMaterial`, a read-only flag used to assert that no vault key is left in
memory.

### Live tests: the client against a real API

`tests/BitosX.LiveTests` runs the desktop client — `HttpBitosXApiClient`, `AccountSession` and
`VaultSyncService`, wired by the application's own `ServiceConfiguration`, with real vaults made by
`BitosX.Core` in data directories of their own — against a real BitosX API and its MariaDB
database: the local stack of bitosx-api (`tools/devstack/up.sh`). Registration, email verification
and turning on two-factor authentication, which the desktop does not do, go over plain HTTP.
Every test makes its own accounts (`e2e-<random>@example.test`) and never assumes an empty
database.

```bash
set -a; . ../bitosx-api/tools/devstack/.state/env; set +a   # BITOSX_LIVE_API_URL, BITOSX_LIVE_MAIL_CMD
dotnet test tests/BitosX.LiveTests -c Release
```

They are opt-in. With neither `BITOSX_LIVE_API_URL` nor `BITOSX_LIVE_MAIL_CMD` set, every test is
reported as skipped with the reason "not run: set BITOSX_LIVE_API_URL and BITOSX_LIVE_MAIL_CMD"
(`dotnet test` then prints `Skipped!` and exits with 0), which is how they run inside
`dotnet test BitosX.slnx` and in CI's build-and-test job. With only one of the two set they fail
and say so, and they refuse an API that is not on this machine (loopback): they create and delete
accounts. They run one at a time, because the stack may be shared with the web vault's live suite
and the server answers 503 when its queue of password hashes is full. bitosx-api's `e2e.yml` runs
them against the stack for every change to its `main` and every night.

What they cover: sign-in with the password, with a TOTP code and with a recovery code (a refused
code keeps the challenge; a used recovery code is refused); the session list at the server's cap
of 200, newest first, this session marked, one ended; refresh, and the whole session ended when a
spent refresh token is presented again; the session refreshing before its access token runs out;
the account password change (this session kept, the others signed out, the old password refused);
403 (`Forbidden`) for a wrong password or code with the session kept; deleting the account with a
code; two devices syncing a real vault byte for byte, with the sync record (format 2) bound to the
server and the account; compare-and-set conflicts (409) and both ways out; a removed vault whose
revisions never restart; every timestamp read as UTC.

## Continuous integration

`.github/workflows/ci.yml` runs for every pull request and for every change to `main`, on the
machines of the build service:

| Job | Where | What |
|---|---|---|
| Build and test | Ubuntu 24.04, macOS 15, Windows Server 2025 | Build with warnings as errors, all tests, the dependency audit, the icon files against their generator (Linux), the clipboard check (Linux, macOS), and what the key derivation costs on that machine |
| Clipboard managers | Ubuntu 24.04 | Asks CopyQ and Klipper what they make of a copied secret |
| The real application | Windows Server 2025 | The manual checklists, on the real application in a real window |

The actions it uses are pinned to a commit, not to a tag that can be moved.

## Diagnostic tools

None of them is shipped.

- `tools/BitosX.KdfBenchmark` — Argon2id unlock latency, for one of the profiles (`-- 12 standard`,
  `-- 12 strong`, `-- 12 maximum`). With the heap of the process limited
  (`DOTNET_GCHeapHardLimit`) it shows what happens on a device without the memory.
- `tools/BitosX.GuiCheck` — starts the **real application**, in a real window, on a data directory
  of its own, and carries out the manual checklists through the accessibility interface of
  Windows. Windows-only, so it is **not part of `BitosX.slnx`**:

  ```bash
  dotnet build -c Release
  dotnet run -c Release --project tools/BitosX.GuiCheck
  ```

  For about eight minutes a BitosX window is on screen and the clipboard is in use. It places
  only texts of its own there, puts back the text that was on it, never opens the vault at the
  default location, and checks at the end that that vault is byte for byte what it was.
  `--quick` leaves out what takes minutes; `--no-clipboard` leaves the clipboard alone.

  Someone sitting at the machine is part of the test whether they mean to be or not: a mouse
  moved over the window is activity, and the application counts it. The tool allows for that.
  The build service, where nobody sits, runs it for every change.
- `tools/BitosX.ClipboardProbe` — drives the production clipboard code against the **real**
  Windows clipboard and clipboard history and checks the result the way another application
  would see it. Windows-only, so it is **not part of `BitosX.slnx`**; build and run it on its own:

  ```bash
  dotnet run -c Release --project tools/BitosX.ClipboardProbe
  ```

  It uses the clipboard for about twenty seconds, places only its own marker strings there, puts
  back the text that was on it, and removes its markers from the clipboard history. Exit code 0
  means every check passed. Run it whenever clipboard code or the Avalonia version changes.
- `tools/BitosX.ClipboardCheck` — the counterpart for **Linux and macOS**: places a marked secret
  on the real clipboard with the application's clipboard code and reads the clipboard back with
  the tools of the system (`xclip`; the pasteboard of AppKit). On Linux it needs a display;
  `xvfb-run` provides one where there is none.

  ```bash
  dotnet run -c Release --project tools/BitosX.ClipboardCheck
  ```

  `tools/ci/clipboard-managers.sh` uses it to ask clipboard managers whether they honor the
  marker.

## The icon

The icon (a padlock with a keyhole on a rounded square in the two accent colours of
`App.axaml`) is drawn by `tools/BitosX.IconGen` from its geometry alone: rounded rectangles, a
ring and line segments, with no image library, font or artwork from anywhere else. It writes
`src/BitosX.App/Assets/bitosx.ico` (16 to 256 pixels; the window icon and, through
`ApplicationIcon`, the icon of the program file on Windows), `assets/icon/bitosx-<size>.png`
(16 to 512 pixels) and `assets/icon/bitosx.svg`. To change the icon, change the geometry in the
tool and draw the files again; CI fails if a file differs from what the tool draws.

```bash
dotnet run --project tools/BitosX.IconGen              # draws the files
dotnet run --project tools/BitosX.IconGen -- --check   # compares them, pixel for pixel
```

## Where data lives during development

The default vault path is the per-user application data directory (e.g.
`%APPDATA%\BitosX\vault.bitosx`). The working directory is never used for permanent storage.

**To try something out, do not use that vault.** Start the application on a data directory of
its own:

```bash
dotnet run --project src/BitosX.App -- --data-directory /full/path/to/a/directory
```

The vault and the settings are then kept there, the window title names the directory, and an
instance on the default location can run next to it. The directory is created when something is
first saved. The path must be a full path. An option that is given but cannot be used ends the
application with exit code 64 and a message on standard error — it never falls back to the
default location, where it would open a vault the caller did not ask for.
