# NTE Context Probe

Second diagnostic step for the NTE Localization Studio Milestone 001 asset-context investigation.

The first `NTE.ContextScanner` run on NTE 1.3 successfully mounted 640,226 virtual files and found 62 candidate packages for seven known localization identities, but structured FText/StringTable parsing returned zero references. The run also reported that no `.usmap` mapping file was available.

This probe is designed to separate three questions that the first scanner combined:

1. **Raw provenance** — which exact package payload contains each localization key token?
2. **Parser capability** — can CUE4Parse deserialize that candidate package, and if not, what exact exception occurs?
3. **Structured reference** — when deserialization succeeds, does the package expose the exact `namespace::key` as FText or StringTable data?

## Output

The output directory contains:

- `raw-hits.jsonl` — one record per identity/payload/encoding hit, including payload path, package candidates, first byte offset, hit count and a small raw context window;
- `parse-attempts.jsonl` — explicit success/failure diagnostics for `GetLocalizedStrings` and `LoadStringTable`, including exception type/message;
- `structured-references.jsonl` — exact structured identity matches when available;
- `summary.json` — aggregate counts and missing identities.

The probe deliberately records raw hits even when structured parsing fails. A raw hit is already useful evidence that the key token is physically present in an asset payload.

## Usage

```powershell
dotnet run --project .\Tools\NTE.ContextProbe\NTE.ContextProbe.csproj -c Release -- -- `
  "<NTE_ROOT>" `
  ".\Tools\NTE.ContextScanner\samples\nte-1.3-context-keys.txt" `
  "<OUTPUT_DIR>" `
  --aes-config="<LOCAL_CONFIG_WITH_AES>" `
  --path=HT/Content/
```

Optional mappings:

```text
--mappings=C:\path\to\current-nte.usmap
```

When provided, the mapping file is copied temporarily into the game root for the lifetime of the reader because the current `UnrealArchiveReader` discovers mappings from local `.usmap` files before package deserialization. The original file is not modified.

## Important

Do not assume an old NTE mapping remains valid for a new game build. UE5 unversioned-property mappings can change between game updates. The mapping option exists so a mapping validated for the current build can be tested when available.
