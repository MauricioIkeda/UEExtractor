using Solicen.Localization.UE4;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NTE.ContextProbe;

internal static class Program
{
    private const string DefaultPathFilter = "HT/Content/";
    private const int ContextRadiusBytes = 192;

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
            var outputDir = Path.GetFullPath(args[2]);

            string? aesFile = null;
            string? aesConfig = null;
            string? mappingsFile = null;
            var pathFilter = DefaultPathFilter;

            foreach (var arg in args.Skip(3))
            {
                if (arg.StartsWith("--aes-file=", StringComparison.OrdinalIgnoreCase))
                    aesFile = Path.GetFullPath(arg["--aes-file=".Length..].Trim('"'));
                else if (arg.StartsWith("--aes-config=", StringComparison.OrdinalIgnoreCase))
                    aesConfig = Path.GetFullPath(arg["--aes-config=".Length..].Trim('"'));
                else if (arg.StartsWith("--mappings=", StringComparison.OrdinalIgnoreCase))
                    mappingsFile = Path.GetFullPath(arg["--mappings=".Length..].Trim('"'));
                else if (arg.StartsWith("--path=", StringComparison.OrdinalIgnoreCase))
                {
                    pathFilter = arg["--path=".Length..].Trim('"');
                    if (pathFilter == "*") pathFilter = string.Empty;
                }
                else
                    throw new ArgumentException($"Unknown argument: {arg}");
            }

            ValidateInputs(gameRoot, keysFile, aesFile, aesConfig, mappingsFile);
            Directory.CreateDirectory(outputDir);

            var targets = LoadTargets(keysFile);
            if (targets.Count == 0)
                throw new InvalidOperationException("The keys file does not contain any identities.");

            var aesKey = LoadAesKey(aesFile, aesConfig);
            UnrealLocres.FilterPath = pathFilter;

            Console.WriteLine("NTE Context Probe");
            Console.WriteLine($"Targets: {targets.Count}");
            Console.WriteLine($"Path filter: {(string.IsNullOrEmpty(pathFilter) ? "<none>" : pathFilter)}");
            Console.WriteLine($"Mappings: {(mappingsFile is null ? "<none>" : Path.GetFileName(mappingsFile))}");
            Console.WriteLine("Purpose: raw provenance + structured parse diagnostics");
            Console.WriteLine();

            using var aesScope = TemporaryFile.InstallText(gameRoot, "__nte_context_probe_aes.txt", aesKey);
            using var mappingScope = TemporaryFile.InstallCopy(gameRoot, "__nte_context_probe.usmap", mappingsFile);
            using var compatibilityAesScope = TemporaryAesCompatibility.Install(gameRoot, aesKey);
            using var reader = CreateReaderWithoutLeakingAes(gameRoot);

            var patterns = BuildPatterns(targets);
            var rawHits = new ConcurrentBag<RawHit>();

            Console.WriteLine("Scanning package payloads for exact key tokens...");
            var originalOut = Console.Out;
            try
            {
                Console.SetOut(TextWriter.Null);
                reader.ProcessAllAssets((assetPath, stream) =>
                {
                    if (!IsPackagePayload(assetPath)) return;
                    var bytes = ReadAllBytes(stream);
                    foreach (var pattern in patterns)
                    {
                        var (count, firstOffset) = CountOccurrences(bytes, pattern.Bytes);
                        if (count == 0) continue;

                        rawHits.Add(new RawHit
                        {
                            Identity = pattern.Identity,
                            KeyToken = pattern.KeyToken,
                            PayloadPath = assetPath,
                            PackageCandidates = BuildPackageCandidates(assetPath),
                            Encoding = pattern.EncodingName,
                            HitCount = count,
                            FirstByteOffset = firstOffset,
                            PayloadLength = bytes.Length,
                            Context = BuildContext(bytes, firstOffset, pattern.Bytes.Length, pattern.EncodingName)
                        });
                    }
                }, deepParse: false);
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            var orderedRawHits = rawHits
                .OrderBy(x => x.Identity, StringComparer.Ordinal)
                .ThenBy(x => x.PayloadPath, StringComparer.Ordinal)
                .ThenBy(x => x.Encoding, StringComparer.Ordinal)
                .ToList();

            WriteJsonl(Path.Combine(outputDir, "raw-hits.jsonl"), orderedRawHits);

            var packageTargets = BuildPackageTargetMap(orderedRawHits);
            Console.WriteLine($"Raw hits: {orderedRawHits.Count}");
            Console.WriteLine($"Candidate package paths: {packageTargets.Count}");

            var parseAttempts = new List<ParseAttempt>();
            var structuredRefs = new List<StructuredReference>();

            foreach (var pair in packageTargets.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                var packagePath = pair.Key;
                var expectedIdentities = pair.Value;

                ProbeFTexts(reader, packagePath, expectedIdentities, parseAttempts, structuredRefs);
                if (packagePath.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
                    ProbeStringTable(reader, packagePath, expectedIdentities, parseAttempts, structuredRefs);
            }

            WriteJsonl(Path.Combine(outputDir, "parse-attempts.jsonl"), parseAttempts);
            WriteJsonl(Path.Combine(outputDir, "structured-references.jsonl"), structuredRefs);

            var rawMatched = orderedRawHits
                .Select(x => x.Identity)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();

            var structuredMatched = structuredRefs
                .Select(x => x.Identity)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();

            var summary = new ProbeSummary
            {
                TargetCount = targets.Count,
                RawHitRecordCount = orderedRawHits.Count,
                RawMatchedIdentityCount = rawMatched.Count,
                RawMatchedIdentities = rawMatched,
                CandidatePackageCount = packageTargets.Count,
                ParseAttemptCount = parseAttempts.Count,
                ParseSuccessCount = parseAttempts.Count(x => x.Success),
                ParseFailureCount = parseAttempts.Count(x => !x.Success),
                StructuredReferenceCount = structuredRefs.Count,
                StructuredMatchedIdentityCount = structuredMatched.Count,
                StructuredMatchedIdentities = structuredMatched,
                MissingFromRawScan = targets.Except(rawMatched, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList(),
                MissingFromStructuredScan = targets.Except(structuredMatched, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList(),
                MappingsProvided = mappingsFile is not null,
                PathFilter = pathFilter
            };

            File.WriteAllText(
                Path.Combine(outputDir, "summary.json"),
                JsonSerializer.Serialize(summary, JsonOptionsIndented),
                new UTF8Encoding(false));

            Console.WriteLine();
            Console.WriteLine($"Raw matched identities: {summary.RawMatchedIdentityCount}/{summary.TargetCount}");
            Console.WriteLine($"Structured matched identities: {summary.StructuredMatchedIdentityCount}/{summary.TargetCount}");
            Console.WriteLine($"Parse attempts: {summary.ParseAttemptCount} ({summary.ParseSuccessCount} success, {summary.ParseFailureCount} failed)");
            Console.WriteLine($"Output: {outputDir}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex}");
            return 1;
        }
    }

    private static void ValidateInputs(
        string gameRoot,
        string keysFile,
        string? aesFile,
        string? aesConfig,
        string? mappingsFile)
    {
        if (!Directory.Exists(gameRoot))
            throw new DirectoryNotFoundException($"Game root not found: {gameRoot}");
        if (!File.Exists(keysFile))
            throw new FileNotFoundException("Keys file not found.", keysFile);
        if (aesFile is not null && aesConfig is not null)
            throw new ArgumentException("Use only one of --aes-file or --aes-config.");
        if (aesFile is not null && !File.Exists(aesFile))
            throw new FileNotFoundException("AES file not found.", aesFile);
        if (aesConfig is not null && !File.Exists(aesConfig))
            throw new FileNotFoundException("AES config not found.", aesConfig);
        if (mappingsFile is not null && !File.Exists(mappingsFile))
            throw new FileNotFoundException("Mappings file not found.", mappingsFile);
    }

    private static HashSet<string> LoadTargets(string path)
        => File.ReadLines(path)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0 && !x.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);

    private static string LoadAesKey(string? aesFile, string? aesConfig)
    {
        if (aesFile is null && aesConfig is null) return string.Empty;

        string raw;
        if (aesFile is not null)
            raw = File.ReadAllText(aesFile).Trim();
        else
        {
            using var document = JsonDocument.Parse(File.ReadAllText(aesConfig!));
            if (!document.RootElement.TryGetProperty("aes_key", out var aesProperty))
                throw new InvalidDataException("The AES config does not contain an 'aes_key' property.");
            raw = aesProperty.GetString()?.Trim() ?? string.Empty;
        }

        if (!raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) raw = "0x" + raw;
        if (raw.Length != 66 || raw.Skip(2).Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("AES key must contain exactly 64 hexadecimal digits.");
        return raw;
    }

    private static List<SearchPattern> BuildPatterns(HashSet<string> targets)
    {
        var result = new List<SearchPattern>(targets.Count * 2);
        foreach (var identity in targets)
        {
            var keyToken = GetKeyToken(identity);
            result.Add(new SearchPattern(identity, keyToken, "utf8", Encoding.UTF8.GetBytes(keyToken)));
            result.Add(new SearchPattern(identity, keyToken, "utf16le", Encoding.Unicode.GetBytes(keyToken)));
        }
        return result;
    }

    private static string GetKeyToken(string identity)
    {
        var separator = identity.IndexOf("::", StringComparison.Ordinal);
        return separator >= 0 ? identity[(separator + 2)..] : identity;
    }

    private static bool IsPackagePayload(string path)
        => path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".uexp", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase);

    private static byte[] ReadAllBytes(Stream stream)
    {
        if (stream is MemoryStream memory) return memory.ToArray();
        var original = stream.CanSeek ? stream.Position : 0;
        if (stream.CanSeek) stream.Position = 0;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        if (stream.CanSeek) stream.Position = original;
        return copy.ToArray();
    }

    private static (int Count, int FirstOffset) CountOccurrences(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return (0, -1);
        var count = 0;
        var first = -1;
        var offset = 0;
        while (offset <= haystack.Length - needle.Length)
        {
            var relative = haystack.AsSpan(offset).IndexOf(needle);
            if (relative < 0) break;
            var absolute = offset + relative;
            if (first < 0) first = absolute;
            count++;
            offset = absolute + Math.Max(1, needle.Length);
        }
        return (count, first);
    }

    private static List<string> BuildPackageCandidates(string payloadPath)
    {
        if (payloadPath.EndsWith(".uexp", StringComparison.OrdinalIgnoreCase))
        {
            var stem = Path.ChangeExtension(payloadPath, null)!;
            return new List<string> { stem + ".uasset", stem + ".umap" };
        }
        return new List<string> { payloadPath };
    }

    private static Dictionary<string, HashSet<string>> BuildPackageTargetMap(IEnumerable<RawHit> hits)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in hits)
        {
            foreach (var package in hit.PackageCandidates)
            {
                if (!result.TryGetValue(package, out var identities))
                {
                    identities = new HashSet<string>(StringComparer.Ordinal);
                    result[package] = identities;
                }
                identities.Add(hit.Identity);
            }
        }
        return result;
    }

    private static string BuildContext(byte[] bytes, int offset, int matchLength, string encodingName)
    {
        if (offset < 0) return string.Empty;
        var start = Math.Max(0, offset - ContextRadiusBytes);
        var end = Math.Min(bytes.Length, offset + matchLength + ContextRadiusBytes);
        var slice = bytes.AsSpan(start, end - start).ToArray();

        string text;
        try
        {
            text = encodingName == "utf16le"
                ? Encoding.Unicode.GetString(slice)
                : Encoding.UTF8.GetString(slice);
        }
        catch
        {
            return Convert.ToHexString(slice);
        }

        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\r' or '\n' or '\t') sb.Append(' ');
            else if (!char.IsControl(c)) sb.Append(c);
            else sb.Append('·');
        }
        return sb.ToString();
    }

    private static void ProbeFTexts(
        UnrealArchiveReader reader,
        string packagePath,
        HashSet<string> expected,
        List<ParseAttempt> attempts,
        List<StructuredReference> refs)
    {
        foreach (var argumentPath in PackageArgumentVariants(packagePath))
        {
            try
            {
                var called = false;
                var entryCount = 0;
                var matches = new List<string>();
                reader.GetLocalizedStrings(argumentPath, entries =>
                {
                    called = true;
                    entryCount = entries.Count;
                    for (var i = 0; i < entries.Count; i++)
                    {
                        var entry = entries[i];
                        var identity = ComposeIdentity(entry.Namespace, entry.Key);
                        if (!expected.Contains(identity)) continue;
                        matches.Add(identity);
                        refs.Add(new StructuredReference
                        {
                            Identity = identity,
                            PackagePath = packagePath,
                            ArgumentPath = argumentPath,
                            Kind = "FText",
                            Namespace = entry.Namespace,
                            Key = entry.Key,
                            SourceString = entry.SourceString,
                            Index = i,
                            TotalEntries = entries.Count
                        });
                    }
                });

                attempts.Add(new ParseAttempt
                {
                    PackagePath = packagePath,
                    ArgumentPath = argumentPath,
                    Method = "GetLocalizedStrings",
                    Success = true,
                    CallbackInvoked = called,
                    EntryCount = entryCount,
                    ExactMatches = matches
                });

                if (called) break;
            }
            catch (Exception ex)
            {
                attempts.Add(ParseAttempt.Failure(packagePath, argumentPath, "GetLocalizedStrings", ex));
            }
        }
    }

    private static void ProbeStringTable(
        UnrealArchiveReader reader,
        string packagePath,
        HashSet<string> expected,
        List<ParseAttempt> attempts,
        List<StructuredReference> refs)
    {
        try
        {
            var called = false;
            var entryCount = 0;
            var matches = new List<string>();
            reader.LoadStringTable(packagePath, (tableNamespace, entries) =>
            {
                called = true;
                entryCount = entries.Count;
                foreach (var entry in entries)
                {
                    var identity = ComposeIdentity(tableNamespace, entry.Key);
                    if (!expected.Contains(identity)) continue;
                    matches.Add(identity);
                    refs.Add(new StructuredReference
                    {
                        Identity = identity,
                        PackagePath = packagePath,
                        ArgumentPath = packagePath,
                        Kind = "StringTable",
                        Namespace = tableNamespace,
                        Key = entry.Key,
                        SourceString = entry.Value,
                        TotalEntries = entries.Count
                    });
                }
            });

            attempts.Add(new ParseAttempt
            {
                PackagePath = packagePath,
                ArgumentPath = packagePath,
                Method = "LoadStringTable",
                Success = true,
                CallbackInvoked = called,
                EntryCount = entryCount,
                ExactMatches = matches
            });
        }
        catch (Exception ex)
        {
            attempts.Add(ParseAttempt.Failure(packagePath, packagePath, "LoadStringTable", ex));
        }
    }

    private static IEnumerable<string> PackageArgumentVariants(string packagePath)
    {
        yield return packagePath;
        var extensionless = Path.ChangeExtension(packagePath, null);
        if (!string.IsNullOrEmpty(extensionless) && !extensionless.Equals(packagePath, StringComparison.OrdinalIgnoreCase))
            yield return extensionless;
    }

    private static string ComposeIdentity(string @namespace, string key)
        => string.IsNullOrEmpty(@namespace) ? key : $"{@namespace}::{key}";

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

    private static void WriteJsonl<T>(string path, IEnumerable<T> values)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        foreach (var value in values)
            writer.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private static readonly JsonSerializerOptions JsonOptionsIndented = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  NTE.ContextProbe <gameRoot> <keys.txt> <outputDir> [--aes-file=<path> | --aes-config=<path>] [--mappings=<file.usmap>] [--path=<virtual path>]");
        Console.WriteLine();
        Console.WriteLine("Produces raw-hits.jsonl, parse-attempts.jsonl, structured-references.jsonl and summary.json.");
    }
}

internal sealed record SearchPattern(string Identity, string KeyToken, string EncodingName, byte[] Bytes);

internal sealed class RawHit
{
    public required string Identity { get; init; }
    public required string KeyToken { get; init; }
    public required string PayloadPath { get; init; }
    public required List<string> PackageCandidates { get; init; }
    public required string Encoding { get; init; }
    public int HitCount { get; init; }
    public int FirstByteOffset { get; init; }
    public int PayloadLength { get; init; }
    public required string Context { get; init; }
}

internal sealed class StructuredReference
{
    public required string Identity { get; init; }
    public required string PackagePath { get; init; }
    public required string ArgumentPath { get; init; }
    public required string Kind { get; init; }
    public required string Namespace { get; init; }
    public required string Key { get; init; }
    public required string SourceString { get; init; }
    public int? Index { get; init; }
    public int TotalEntries { get; init; }
}

internal sealed class ParseAttempt
{
    public required string PackagePath { get; init; }
    public required string ArgumentPath { get; init; }
    public required string Method { get; init; }
    public bool Success { get; init; }
    public bool CallbackInvoked { get; init; }
    public int EntryCount { get; init; }
    public List<string> ExactMatches { get; init; } = new();
    public string? ErrorType { get; init; }
    public string? ErrorMessage { get; init; }

    public static ParseAttempt Failure(string packagePath, string argumentPath, string method, Exception ex)
        => new()
        {
            PackagePath = packagePath,
            ArgumentPath = argumentPath,
            Method = method,
            Success = false,
            ErrorType = ex.GetType().FullName,
            ErrorMessage = ex.Message
        };
}

internal sealed class ProbeSummary
{
    public int TargetCount { get; init; }
    public int RawHitRecordCount { get; init; }
    public int RawMatchedIdentityCount { get; init; }
    public List<string> RawMatchedIdentities { get; init; } = new();
    public int CandidatePackageCount { get; init; }
    public int ParseAttemptCount { get; init; }
    public int ParseSuccessCount { get; init; }
    public int ParseFailureCount { get; init; }
    public int StructuredReferenceCount { get; init; }
    public int StructuredMatchedIdentityCount { get; init; }
    public List<string> StructuredMatchedIdentities { get; init; } = new();
    public List<string> MissingFromRawScan { get; init; } = new();
    public List<string> MissingFromStructuredScan { get; init; } = new();
    public bool MappingsProvided { get; init; }
    public string PathFilter { get; init; } = string.Empty;
}

internal sealed class TemporaryFile : IDisposable
{
    private readonly string? _path;
    private readonly bool _hadExisting;
    private readonly byte[]? _previous;

    private TemporaryFile(string? path, bool hadExisting, byte[]? previous)
    {
        _path = path;
        _hadExisting = hadExisting;
        _previous = previous;
    }

    public static TemporaryFile InstallText(string directory, string name, string content)
    {
        if (string.IsNullOrEmpty(content)) return new TemporaryFile(null, false, null);
        var path = Path.Combine(directory, name);
        var hadExisting = File.Exists(path);
        var previous = hadExisting ? File.ReadAllBytes(path) : null;
        File.WriteAllText(path, content, Encoding.ASCII);
        return new TemporaryFile(path, hadExisting, previous);
    }

    public static TemporaryFile InstallCopy(string directory, string name, string? sourcePath)
    {
        if (string.IsNullOrEmpty(sourcePath)) return new TemporaryFile(null, false, null);
        var path = Path.Combine(directory, name);
        var hadExisting = File.Exists(path);
        var previous = hadExisting ? File.ReadAllBytes(path) : null;
        File.Copy(sourcePath, path, true);
        return new TemporaryFile(path, hadExisting, previous);
    }

    public void Dispose()
    {
        if (_path is null) return;
        if (_hadExisting && _previous is not null) File.WriteAllBytes(_path, _previous);
        else if (File.Exists(_path)) File.Delete(_path);
    }
}

internal sealed class TemporaryAesCompatibility : IDisposable
{
    private readonly string? _path;
    private readonly bool _hadExisting;
    private readonly byte[]? _previous;

    private TemporaryAesCompatibility(string? path, bool hadExisting, byte[]? previous)
    {
        _path = path;
        _hadExisting = hadExisting;
        _previous = previous;
    }

    public static TemporaryAesCompatibility Install(string gameRoot, string aesKey)
    {
        if (string.IsNullOrEmpty(aesKey)) return new TemporaryAesCompatibility(null, false, null);
        var path = Path.Combine(gameRoot, "aes.txt");
        var hadExisting = File.Exists(path);
        var previous = hadExisting ? File.ReadAllBytes(path) : null;
        File.WriteAllText(path, aesKey, Encoding.ASCII);
        return new TemporaryAesCompatibility(path, hadExisting, previous);
    }

    public void Dispose()
    {
        if (_path is null) return;
        if (_hadExisting && _previous is not null) File.WriteAllBytes(_path, _previous);
        else if (File.Exists(_path)) File.Delete(_path);
    }
}

internal sealed class AesRedactingTextWriter : TextWriter
{
    private readonly TextWriter _inner;
    public AesRedactingTextWriter(TextWriter inner) => _inner = inner;
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
