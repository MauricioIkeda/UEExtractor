# NTE Context Reference Scanner

Diagnostic-only utility for the NTE Localization Studio research phase.

It does **not** modify game archives. It mounts the NTE content through the existing `UnrealArchiveReader`, receives a small list of exact localization identities (`namespace::key`) and records which effective assets expose those identities through FText or StringTable data.

## Why this exists

The normal UEExtractor pipeline aggregates localization values for extraction. That is appropriate for building CSV/LOCRES data, but it loses provenance that the Localization Studio research needs.

This scanner answers a narrower question first:

> Where is this localization identity referenced in the mounted asset view?

The first version deliberately avoids claiming that it already understands dialogue chronology, speaker semantics or quest graph semantics.

## Input

A UTF-8 text file with one exact identity per line:

```text
ST_MainDialogue::MU01_03_NPC017_038
ST_Dialogue_Side::SU001_NPC007A_L_54
```

Blank lines and lines beginning with `#` are ignored.

## Run

```powershell
dotnet run --project .\Tools\NTE.ContextScanner\NTE.ContextScanner.csproj -- `
  "G:\EpicGamesLibrary\NTENevernesstoEvernezYbAx\Client\WindowsNoEditor\HT" `
  ".\context-keys.txt" `
  ".\context-references.jsonl" `
  "--aes-config=C:\path\to\nte.config.json" `
  "--path=HT/Content/"
```

Supported optional arguments:

- `--aes-config=<path>`: reads `aes_key` from a JSON config without placing the AES value on the command line;
- `--aes-file=<path>`: reads a raw AES key from a file;
- `--path=<virtual path>`: limits the mounted asset scan; default is `HT/Content/`;
- `--path=*`: disables the virtual path filter;
- `--deep`: disables the raw-byte candidate prefilter and attempts to deserialize every package under the path filter.

When an AES is supplied through the scanner, it is written to a temporary `aes.txt` only for compatibility with the current reader, then the previous file is restored (or the temporary file is removed). Console output redacts the partial AES message emitted by the current reader.

## Two-stage scan

The default mode is intentionally conservative with CPU/time:

1. scan raw `.uasset`, `.uexp` and `.umap` payloads for the key tokens requested in `keys.txt` (UTF-8 and UTF-16LE);
2. deserialize only candidate packages and inspect their FText/StringTable data.

This avoids serializing the entire NTE asset set just to investigate a handful of identities.

The raw prefilter is an optimization, not a correctness proof. If an important key remains missing, rerun with `--deep`; that mode is slower but bypasses the candidate filter.

## Output

The main result is UTF-8 JSONL. Each record preserves one found reference instead of collapsing by key.

Fields currently include:

- `identity`;
- `namespace`;
- `key`;
- `sourceString` found in the asset;
- `assetPath`;
- `referenceKind` (`FText` or `StringTable`);
- `providerView` (`effective`);
- flattened FText index/count when available;
- up to three FText neighbors before and after the target inside the same parsed asset;
- StringTable entry count when applicable.

A sibling `<output-name>.missing.txt` contains requested identities for which no asset reference was recovered.

## Important limitations

This is research instrumentation, not the final Context Engine.

- `providerView=effective` means the scan follows the mounted provider view; it does not yet report all duplicate base/patch copies of an asset.
- FText neighbors are only local serialization neighbors inside the same parsed asset. They must **not** be interpreted as dialogue chronology without further evidence.
- The current UEExtractor API does not expose export/property JSON paths through `GetLocalizedStrings`, so the first scanner version records asset-level provenance. If this proves useful, the next increment can preserve export/property paths instead of redesigning the Studio prematurely.
- Some packages may fail to deserialize because mappings/type information are incomplete. Missing references therefore require investigation, not an automatic conclusion that the key is unused.
- A key token can occur in an asset for reasons other than the exact target FText; candidate selection is only a prefilter. Exact `namespace::key` matching happens after deserialization.

## Research success criterion

Run the scanner against a small, diverse set of real NTE identities. If the resulting asset paths reveal stable families of dialogue/DataTable/StringTable assets, deepen the scanner around those asset types. If not, reassess before building permanent context infrastructure.
