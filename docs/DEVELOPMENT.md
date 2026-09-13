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
- `BitosX.Storage.Tests` — atomic file storage, malformed/oversized handling, and an end-to-end
  persistence integration test using the real crypto + file storage.

Use `Argon2Parameters.ForTesting` (a deliberately weak profile) in tests to keep them fast; never
use it for real vaults.

## Where data lives during development

The default vault path is the per-user application data directory (e.g.
`%APPDATA%\BitosX\vault.bitosx`). Delete that file to start from a clean "no vault" state. The
working directory is never used for permanent storage.
