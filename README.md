# Solicen.UEExtractor

[**English**](/README.md) | [**Русский**](./docs/ru/README.ru.md)

Made with ❤️ for translators and translation developers.

UEExtractor is a **.NET 10 / C#** tool for extracting text from Unreal Engine games and rebuilding localization resources. It uses [CUE4Parse](https://github.com/FabianFG/CUE4Parse) to read `.pak`, `.utoc`, assets, string tables and compiled `.locres` files.

## Choose your goal

### I only need the approved NTE extractor used by the PT-BR pipeline

Do not compile a random branch or replace the binary with a different upstream release.

Use the dedicated manual:

**[UEExtractor NTE — approved binary, hashes, source setup and development on Windows](docs/NTE_MANUAL_WINDOWS.md)**

The pipeline-approved package is:

```text
UEExtractor NTE 1.0.8.4 (build 9d69362 / CUE4Parse 679f42b)
```

Approved provenance:

```text
UEExtractor commit: 9d6936204dc0180a3797ca59a5ed526ff51edff8
CUE4Parse commit:    679f42bc3e7b6970c28634ac7e1a1d07b9e8d8f5
```

The guide contains:

- the exact download used by the NTE Translation Studio;
- SHA-256 for the ZIP, `UEExtractor.exe` and `UEExtractor.dll`;
- the exact installation directory expected by the pipeline;
- the approved source branch and commits;
- .NET 10 SDK setup;
- CUE4Parse submodule setup;
- Debug and Release build commands;
- NTE extraction and patch examples;
- compatibility proofs required before replacing the approved build.

### I want to modify or rebuild UEExtractor

UEExtractor is a C# project targeting:

```xml
<TargetFramework>net10.0</TargetFramework>
```

Install the **.NET 10 SDK**. Visual Studio Community is optional; command-line builds work with `dotnet`. If you use Visual Studio, the relevant workload is **.NET desktop development**, not C++ development.

For the source currently used by the NTE-specific build, clone:

```powershell
git clone `
  --branch fix/nte-aes-submitkey `
  --recurse-submodules `
  https://github.com/MauricioIkeda/UEExtractor.git
```

Then follow [the complete NTE development guide](docs/NTE_MANUAL_WINDOWS.md#cenário-b--desenvolver-ou-recompilar-o-ueextractor).

## Main features

- extracts text from Unreal Engine `.pak` and `.utoc` containers;
- reads DataTables, StringTables and compiled LOCRES files;
- supports direct LOCRES extraction;
- exports localization CSV files;
- rebuilds LOCRES files from CSV;
- provides NTE patch mode using the original LOCRES as a structural template;
- preserves a `.locreshashes` sidecar for original game hashes;
- supports AES-protected archives;
- can restrict extraction to a known virtual path;
- includes automatic handling for several game-specific formats through CUE4Parse.

## LocresCSV structure

A normal exported row contains:

```csv
key,source,Translation
4A6FDB1549E45F6C5D8D739129686E2F,Default,
```

- `key`: localization identity;
- `source`: original game text;
- `Translation`: translated text to be applied.

Do not modify keys unless you fully understand the lookup format used by the target game.

## Preparation

### AES-protected games

Some games require an AES key.

You can provide it by one of the supported methods, such as:

```text
--aes=0x...
```

or a locally managed `aes.txt`, depending on your workflow.

Never commit real keys to GitHub.

### ZenLoader / IoStore games

Some games also require a `.usmap` mapping file. Place the correct file in the game directory or a supported subdirectory when required by the target title.

### Supported games

The repository includes automatic or game-specific handling for titles such as:

- Neverness To Everness;
- Ash Echoes;
- Wuthering Waves / KuroGames;
- inZOI;
- Marvel Rivals;
- Dead by Daylight;
- FragPunk;
- Infinity Nikki;
- Snowbreak: Containment Zone.

Auto-detection and implementation details can change by branch and CUE4Parse revision. For a reproducible NTE build, use the branch and submodule revision documented in the NTE guide.

## Basic use

### Extract from a game directory

```cmd
UEExtractor.exe <game_directory> [output_csv_or_directory] [arguments...]
```

Example:

```cmd
UEExtractor.exe "D:\Games\Example" "D:\Output\"
```

### Restrict extraction to a known virtual path

```cmd
UEExtractor.exe "D:\Games\Example" "D:\Output\" --path=Content/Localization
```

This avoids scanning unrelated assets.

### Create a LOCRES from CSV

```cmd
UEExtractor.exe <csv_path> <output_locres>
```

### Display help and version

```cmd
UEExtractor.exe --help
UEExtractor.exe --version
```

## Neverness To Everness patch mode

NTE uses a localization format where a LOCRES that appears valid can still fail in-game if namespaces, hashes, keys, order or compact-string structures are changed incorrectly.

The recommended patch flow uses the original `Game.locres` as a structural template:

```cmd
UEExtractor.exe <original.locres> <translations.csv> --version=GAME_NevernessToEverness --verbose
```

Expected output:

```text
<original>_patched.locres
```

Patch mode is intended to:

1. read the original game LOCRES;
2. preserve its structural identity;
3. replace only text values matched by the CSV;
4. write the NTE-compatible result;
5. preserve the sidecar hashes used by the pipeline.

For the exact PT-BR workflow, approved hashes and source branch, read [docs/NTE_MANUAL_WINDOWS.md](docs/NTE_MANUAL_WINDOWS.md).

## NTE extraction example

The PT-BR pipeline uses a command equivalent to:

```cmd
UEExtractor.exe "<NTE_ROOT>\Client\WindowsNoEditor\HT" "<OUTPUT>\" ^
  --path=HT/Content/Localization/Game/en/game.locres ^
  --version=GAME_NevernessToEverness ^
  --extract-locres ^
  --verbose
```

A successful extraction used by the pipeline must produce:

```text
CSV
original LOCRES
.locreshashes sidecar
```

The NTE Translation Studio then records source hashes and blocks the build if files from different extractions are mixed.

## Common arguments

| Argument | Purpose |
|---|---|
| `--version=<value>` / `-v` | selects an Unreal version or game profile |
| `--aes=<key>` | supplies an AES key locally |
| `--aes:auto` | extracts a supported game's AES key and uses it in memory for this run |
| `--path=<virtual_path>` / `-p` | limits scanning to a known path |
| `--verbose` | prints detailed diagnostics |
| `--extract-locres` | writes original LOCRES binaries to the output |
| `--locres` | requests LOCRES generation in supported flows |
| `--no-parallel` | disables parallel processing |
| `--skip-uexp` | skips `.uexp` processing |
| `--skip-uasset` | skips `.uasset` processing |
| `--all` | scans all folders |
| `--help` | displays the current executable's full argument list |
| `--version` | displays the executable version when used as the version command |

The executable's `--help` output is the source of truth for the exact branch you compiled.

## Merging previous CSV translations

When re-extracting into the same workflow, UEExtractor can reuse values from an existing Translation column. For the PT-BR project, long-term reuse, deduplication, validation and manual provenance are handled primarily by the SQLite memory in the NTE Translation Studio.

Do not rely on CSV merging as a substitute for the pipeline backup.

## Building from source

Minimum command-line requirements:

```text
Git
.NET 10 SDK
CUE4Parse submodule
```

Restore and build:

```powershell
dotnet restore .\UEExtractor\UEExtractor.csproj

dotnet build `
  .\UEExtractor\UEExtractor.csproj `
  --configuration Release
```

Output begins under:

```text
UEExtractor\bin\Release\net10.0\
```

For the NTE source, clone the approved branch recursively and follow every validation step in [the NTE manual](docs/NTE_MANUAL_WINDOWS.md).

## Replacing the approved NTE binary

A successful `dotnet build` is not sufficient evidence that a new binary is compatible with the game.

Before replacing the currently approved package, prove at minimum:

- real NTE extraction succeeds;
- CSV, LOCRES and sidecar are generated;
- source identity remains consistent;
- the complete round-trip passes;
- patch mode applies the expected number of translations;
- entry count and structural lookup information are preserved;
- the game loads the translated LOCRES;
- new ZIP/EXE/DLL hashes are recorded consistently in the private pipeline;
- clean-machine recovery downloads exactly the replacement package.

## Repository and submodule safety

The NTE branch uses a forked CUE4Parse submodule. After cloning:

```powershell
git submodule sync --recursive
git submodule update --init --recursive
git submodule status --recursive
```

Before committing, inspect both the main repository and the submodule pointer. A local submodule commit that was never pushed makes the parent repository impossible to reproduce elsewhere.

## Documentation

- [Complete NTE manual for Windows](docs/NTE_MANUAL_WINDOWS.md)
- [Russian documentation](docs/ru/README.ru.md)

## Contributions

Create a branch instead of committing directly to `main` or to the approved NTE branch.

Recommended checks:

```powershell
git status --short
git diff --check
git diff
git submodule status --recursive
dotnet restore .\UEExtractor\UEExtractor.csproj
dotnet build .\UEExtractor\UEExtractor.csproj --configuration Release
```

Changes that affect NTE LOCRES handling require real extraction, patch and in-game validation.

## Credits

- SolicentTEAM and project contributors;
- CUE4Parse contributors;
- community reverse-engineering and localization contributors;
- MauricioIkeda's NTE-specific source, packaging and validation work.
