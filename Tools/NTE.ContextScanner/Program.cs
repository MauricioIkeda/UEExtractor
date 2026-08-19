using Solicen.Localization.UE4;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NTE.ContextScanner;

internal static class Program
{
    private const string DefaultPathFilter = "HT/Content/";

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length < 3)
            {
                PrintUsage();
                return 2;
            }

            var gameRoot = Path.GetFullPath(args[0]);
            var keysFile = Path.GetFullPath(args[1]);
            var outputFile = Path.GetFullPath(args[2]);

            string? aesFile = null;
            string? aesConfig = null;
            var pathFilter = DefaultPathFilter;
            var deepScan = false;

            foreach (var arg in args.Skip(3))
            {
                if (arg.StartsWith("--aes-file=", StringComparison.OrdinalIgnoreCase))
                {
                    aesFile = Path.GetFullPath(arg["--aes-file=".Length..].Trim('"'));
                }
                else if (arg.StartsWith("--aes-config=", StringComparison.OrdinalIgnoreCase))
                {
                    aesConfig = Path.GetFullPath(arg["--aes-config=".Length..].Trim('"'));
                }
                else if (arg.StartsWith("--path=", StringComparison.OrdinalIgnoreCase))
                {
                    pathFilter = arg["--path=".Length..].Trim('"');
                    if (pathFilter == "*") pathFilter = string.Empty;
                }
                else if (arg.Equals("--deep", StringComparison.OrdinalIgnoreCase))
                {
                    deepScan = true;
                }
                else
                {
                    throw new ArgumentException($"Unknown argument: {arg}");
                }
            }

            if (!Directory.Exists(gameRoot))
                throw new DirectoryNotFoundException($"Game root not found: {gameRoot}");
            if (!File.Exists(keysFile))
                throw new FileNotFoundException("Keys file not found.", keysFile);
            if (aesFile is not null && aesConfig is not null)
                throw new ArgumentException("Use only one of --aes-file or --aes-config.");

            var targets = LoadTargets(keysFile);
            if (targets.Count == 0)
                throw new InvalidOperationException("The keys file does not contain any identities.");

            var aesKey = LoadAesKey(aesFile, aesConfig);
            UnrealLocres.FilterPath = pathFilter;

            Console.WriteLine("NTE Context Reference Scanner");
            Console.WriteLine($"Targets: {targets.Count}");
            Console.WriteLine($"Path filter: {(string.IsNullOrEmpty(pathFilter) ? "<none>" : pathFilter)}");
            Console.WriteLine($"Scan mode: {(deepScan ? "deep" : "prefiltered")}");
            Console.WriteLine("Provider mode: effective mounted asset view");
            Console.WriteLine();

            var references = new ConcurrentBag<ContextReference>();
            var matched = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            var seen = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

            using var aesScope = TemporaryAesFile.Install(gameRoot, aesKey);
            using var reader = CreateReaderWithoutLeakingAes(gameRoot);

            var candidates = FindCandidatePackages(reader, targets, deepScan);
            Console.WriteLine();
            Console.WriteLine($"Candidate packages: {candidates.Count}");

            foreach (var assetPath in candidates.OrderBy(x => x, StringComparer.Ordinal))
            {
                ScanFTextReferences(reader, assetPath, targets, references, matched, seen);
                ScanStringTableReferences(reader, assetPath, targets, references, matched, seen);
            }

            var ordered = references
                .OrderBy(x => x.Identity, StringComparer.Ordinal)
                .ThenBy(x => x.AssetPath, StringComparer.Ordinal)
                .ThenBy(x => x.ReferenceKind, StringComparer.Ordinal)
                .ThenBy(x => x.FTextIndex ?? -1)
                .ToList();

            WriteJsonl(outputFile, ordered);

            var missing = targets
                .Where(x => !matched.ContainsKey(x))
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();

            var missingFile = BuildMissingPath(outputFile);
            var missingDirectory = Path.GetDirectoryName(missingFile);
            if (!string.IsNullOrEmpty(missingDirectory))
                Directory.CreateDirectory(missingDirectory);
            File.WriteAllLines(missingFile, missing, new UTF8Encoding(false));

            Console.WriteLine();
            Console.WriteLine($"References found: {ordered.Count}");
            Console.WriteLine($"Matched identities: {matched.Count}/{targets.Count}");
            Console.WriteLine($"Missing identities: {missing.Count}");
            Console.WriteLine($"JSONL: {outputFile}");
            Console.WriteLine($"Missing list: {missingFile}");

            if (!deepScan && missing.Count > 0)
                Console.WriteLine("Tip: rerun with --deep if important identities remain missing; deep mode skips the raw-byte candidate prefilter.");

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }

    private static HashSet<string> LoadTargets(string path)
    {
        return File.ReadLines(path)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0 && !x.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string LoadAesKey(string? aesFile, string? aesConfig)
    {
        if (aesFile is null && aesConfig is null)
            return string.Empty;

        string raw;
        if (aesFile is not null)
        {
            if (!File.Exists(aesFile))
                throw new FileNotFoundException("AES file not found.", aesFile);
            raw = File.ReadAllText(aesFile).Trim();
        }
        else
        {
            if (!File.Exists(aesConfig!))
                throw new FileNotFoundException("AES config not found.", aesConfig);

            using var document = JsonDocument.Parse(File.ReadAllText(aesConfig!));
            if (!document.RootElement.TryGetProperty("aes_key", out var aesProperty))
                throw new InvalidDataException("The AES config does not contain an 'aes_key' property.");

            raw = aesProperty.GetString()?.Trim() ?? string.Empty;
        }

        if (!raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            raw = "0x" + raw;

        if (raw.Length != 66 || raw.Skip(2).Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("AES key must contain exactly 64 hexadecimal digits.");

        return raw;
    }

    private static UnrealArchiveReader CreateReaderWithoutLeakingAes(string gameRoot)
    {
        var originalOut = Console.Out;
        try
        {
            Console.SetOut(new AesRedactingTextWriter(originalOut));
            return new UnrealArchiveReader(gameRoot);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    private static HashSet<string> FindCandidatePackages(
        UnrealArchiveReader reader,
        HashSet<string> targets,
        bool deepScan)
    {
        var candidates = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var patterns = BuildSearchPatterns(targets);

        reader.ProcessAllAssets((assetPath, stream) =>
        {
            if (!IsPackagePayload(assetPath)) return;

            if (deepScan || ContainsAnyPattern(stream, patterns))
            {
                var packagePath = assetPath.EndsWith(".uexp", StringComparison.OrdinalIgnoreCase)
                    ? Path.ChangeExtension(assetPath, ".uasset")
                    : assetPath;
                candidates.TryAdd(packagePath, 0);
            }
        }, deepParse: false);

        return candidates.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static List<byte[]> BuildSearchPatterns(HashSet<string> targets)
    {
        var tokens = targets
            .Select(GetKeyToken)
            .Where(x => !string.IsNullOrEmpty(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var patterns = new List<byte[]>(tokens.Count * 2);
        foreach (var token in tokens)
        {
            patterns.Add(Encoding.UTF8.GetBytes(token));
            patterns.Add(Encoding.Unicode.GetBytes(token));
        }

        return patterns;
    }

    private static string GetKeyToken(string identity)
    {
        var separator = identity.IndexOf("::", StringComparison.Ordinal);
        return separator >= 0 ? identity[(separator + 2)..] : identity;
    }

    private static bool IsPackagePayload(string assetPath)
        => assetPath.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)
           || assetPath.EndsWith(".uexp", StringComparison.OrdinalIgnoreCase)
           || assetPath.EndsWith(".umap", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsAnyPattern(Stream stream, IReadOnlyList<byte[]> patterns)
    {
        if (patterns.Count == 0) return false;

        byte[] buffer;
        if (stream is MemoryStream memory && memory.TryGetBuffer(out var segment))
            buffer = segment.Array is not null
                ? segment.Array.AsSpan(segment.Offset, segment.Count).ToArray()
                : memory.ToArray();
        else
        {
            var originalPosition = stream.CanSeek ? stream.Position : 0;
            if (stream.CanSeek) stream.Position = 0;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            buffer = copy.ToArray();
            if (stream.CanSeek) stream.Position = originalPosition;
        }

        var span = buffer.AsSpan();
        foreach (var pattern in patterns)
        {
            if (pattern.Length > 0 && span.IndexOf(pattern) >= 0)
                return true;
        }

        return false;
    }

    private static void ScanFTextReferences(
        UnrealArchiveReader reader,
        string assetPath,
        HashSet<string> targets,
        ConcurrentBag<ContextReference> references,
        ConcurrentDictionary<string, byte> matched,
        ConcurrentDictionary<string, byte> seen)
    {
        try
        {
            reader.GetLocalizedStrings(assetPath, entries =>
            {
                for (var index = 0; index < entries.Count; index++)
                {
                    var entry = entries[index];
                    var identity = ComposeIdentity(entry.Namespace, entry.Key);
                    if (!targets.Contains(identity)) continue;

                    var uniqueness = $"FText\u001f{identity}\u001f{assetPath}\u001f{index}";
                    if (!seen.TryAdd(uniqueness, 0)) continue;

                    references.Add(new ContextReference
                    {
                        Identity = identity,
                        Namespace = entry.Namespace,
                        Key = entry.Key,
                        SourceString = entry.SourceString,
                        AssetPath = assetPath,
                        ReferenceKind = "FText",
                        ProviderView = "effective",
                        FTextIndex = index,
                        FTextCount = entries.Count,
                        Neighbors = BuildNeighbors(entries, index)
                    });
                    matched.TryAdd(identity, 0);
                }
            });
        }
        catch
        {
            // Asset-level parse failures are expected for some packages and are already
            // surfaced by the underlying reader during diagnostic runs.
        }
    }

    private static void ScanStringTableReferences(
        UnrealArchiveReader reader,
        string assetPath,
        HashSet<string> targets,
        ConcurrentBag<ContextReference> references,
        ConcurrentDictionary<string, byte> matched,
        ConcurrentDictionary<string, byte> seen)
    {
        try
        {
            reader.LoadStringTable(assetPath, (tableNamespace, entries) =>
            {
                foreach (var entry in entries)
                {
                    var identity = ComposeIdentity(tableNamespace, entry.Key);
                    if (!targets.Contains(identity)) continue;

                    var uniqueness = $"StringTable\u001f{identity}\u001f{assetPath}";
                    if (!seen.TryAdd(uniqueness, 0)) continue;

                    references.Add(new ContextReference
                    {
                        Identity = identity,
                        Namespace = tableNamespace,
                        Key = entry.Key,
                        SourceString = entry.Value,
                        AssetPath = assetPath,
                        ReferenceKind = "StringTable",
                        ProviderView = "effective",
                        StringTableEntryCount = entries.Count
                    });
                    matched.TryAdd(identity, 0);
                }
            });
        }
        catch
        {
            // Not every package is a StringTable; failures here are non-fatal.
        }
    }

    private static List<ContextNeighbor> BuildNeighbors(
        List<(string Namespace, string Key, string SourceString)> entries,
        int targetIndex)
    {
        const int radius = 3;
        var result = new List<ContextNeighbor>();
        var start = Math.Max(0, targetIndex - radius);
        var end = Math.Min(entries.Count - 1, targetIndex + radius);

        for (var i = start; i <= end; i++)
        {
            if (i == targetIndex) continue;
            var entry = entries[i];
            result.Add(new ContextNeighbor
            {
                Offset = i - targetIndex,
                Identity = ComposeIdentity(entry.Namespace, entry.Key),
                SourceString = entry.SourceString
            });
        }

        return result;
    }

    private static string ComposeIdentity(string @namespace, string key)
        => string.IsNullOrEmpty(@namespace) ? key : $"{@namespace}::{key}";

    private static void WriteJsonl(string path, IReadOnlyCollection<ContextReference> references)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        foreach (var reference in references)
            writer.WriteLine(JsonSerializer.Serialize(reference, options));
    }

    private static string BuildMissingPath(string outputFile)
    {
        var directory = Path.GetDirectoryName(outputFile) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(outputFile);
        return Path.Combine(directory, $"{name}.missing.txt");
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  NTE.ContextScanner <gameRoot> <keys.txt> <output.jsonl> [--aes-file=<path> | --aes-config=<path>] [--path=<virtual path>] [--deep]");
        Console.WriteLine();
        Console.WriteLine("keys.txt: one exact namespace::key identity per line; blank lines and # comments are ignored.");
        Console.WriteLine($"Default virtual path filter: {DefaultPathFilter}");
        Console.WriteLine("Use --path=* to disable the virtual path filter.");
        Console.WriteLine("Use --deep to deserialize every package under the filter instead of raw-byte candidate prefiltering.");
    }
}

internal sealed class TemporaryAesFile : IDisposable
{
    private readonly string? _path;
    private readonly bool _hadExisting;
    private readonly byte[]? _previousBytes;

    private TemporaryAesFile(string? path, bool hadExisting, byte[]? previousBytes)
    {
        _path = path;
        _hadExisting = hadExisting;
        _previousBytes = previousBytes;
    }

    public static TemporaryAesFile Install(string gameRoot, string aesKey)
    {
        if (string.IsNullOrEmpty(aesKey))
            return new TemporaryAesFile(null, false, null);

        var path = Path.Combine(gameRoot, "aes.txt");
        var hadExisting = File.Exists(path);
        var previousBytes = hadExisting ? File.ReadAllBytes(path) : null;
        File.WriteAllText(path, aesKey, Encoding.ASCII);
        return new TemporaryAesFile(path, hadExisting, previousBytes);
    }

    public void Dispose()
    {
        if (_path is null) return;

        if (_hadExisting && _previousBytes is not null)
            File.WriteAllBytes(_path, _previousBytes);
        else if (File.Exists(_path))
            File.Delete(_path);
    }
}

internal sealed class AesRedactingTextWriter : TextWriter
{
    private readonly TextWriter _inner;

    public AesRedactingTextWriter(TextWriter inner)
    {
        _inner = inner;
    }

    public override Encoding Encoding => _inner.Encoding;

    public override void Write(char value) => _inner.Write(value);
    public override void Write(string? value) => _inner.Write(value);

    public override void WriteLine(string? value)
    {
        if (value is not null && value.StartsWith("AES key loaded:", StringComparison.OrdinalIgnoreCase))
            _inner.WriteLine("AES key loaded [REDACTED]");
        else
            _inner.WriteLine(value);
    }
}

internal sealed class ContextReference
{
    public required string Identity { get; init; }
    public required string Namespace { get; init; }
    public required string Key { get; init; }
    public required string SourceString { get; init; }
    public required string AssetPath { get; init; }
    public required string ReferenceKind { get; init; }
    public required string ProviderView { get; init; }
    public int? FTextIndex { get; init; }
    public int? FTextCount { get; init; }
    public int? StringTableEntryCount { get; init; }
    public List<ContextNeighbor>? Neighbors { get; init; }
}

internal sealed class ContextNeighbor
{
    public required int Offset { get; init; }
    public required string Identity { get; init; }
    public required string SourceString { get; init; }
}
