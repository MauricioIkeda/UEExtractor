using CUE4Parse.FileProvider;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Solicen.Localization.UE4;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NTE.DialogueStructureProbe;

internal static class Program
{
    private const string DefaultPathFilter = "HT/Content/Dialogue/";
    private const int MaxContextJsonChars = 24000;
    private const int MaxHintValueChars = 1500;
    private const int MaxHintsPerHit = 80;

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

            string? aesConfig = null;
            string? aesFile = null;
            string? mappingsFile = null;
            var pathFilter = DefaultPathFilter;

            foreach (var arg in args.Skip(3))
            {
                if (arg.StartsWith("--aes-config=", StringComparison.OrdinalIgnoreCase))
                    aesConfig = Path.GetFullPath(arg["--aes-config=".Length..].Trim('"'));
                else if (arg.StartsWith("--aes-file=", StringComparison.OrdinalIgnoreCase))
                    aesFile = Path.GetFullPath(arg["--aes-file=".Length..].Trim('"'));
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

            ValidateInputs(gameRoot, keysFile, aesConfig, aesFile, mappingsFile);
            Directory.CreateDirectory(outputDir);

            var targets = LoadTargets(keysFile);
            if (targets.Count == 0)
                throw new InvalidOperationException("No localization identities were supplied.");

            var aesKey = LoadAesKey(aesConfig, aesFile);
            UnrealLocres.FilterPath = pathFilter;

            Console.WriteLine("NTE Dialogue Structure Probe");
            Console.WriteLine($"Targets: {targets.Count}");
            Console.WriteLine($"Path filter: {(string.IsNullOrEmpty(pathFilter) ? "<none>" : pathFilter)}");
            Console.WriteLine($"Mappings: {(mappingsFile is null ? "<none>" : Path.GetFileName(mappingsFile))}");
            Console.WriteLine("Purpose: inspect package exports/properties around raw localization-key references");
            Console.WriteLine();

            using var mappingScope = TemporaryFile.InstallCopy(gameRoot, "__nte_dialogue_structure_probe.usmap", mappingsFile);
            using var aesScope = TemporaryAesCompatibility.Install(gameRoot, aesKey);
            using var reader = CreateReaderWithoutLeakingAes(gameRoot);

            var packageTargets = FindCandidatePackages(reader, targets);
            var packageTargetRows = packageTargets
                .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => new PackageTargetRow
                {
                    PackagePath = x.Key,
                    TargetIdentities = x.Value.OrderBy(v => v, StringComparer.Ordinal).ToList()
                })
                .ToList();

            WriteJsonl(Path.Combine(outputDir, "package-targets.jsonl"), packageTargetRows);

            Console.WriteLine($"Candidate packages: {packageTargets.Count}");

            var exportRows = new List<ExportRow>();
            var propertyHits = new List<PropertyHit>();
            var ftextRows = new List<FTextRow>();
            var errors = new List<ProbeError>();

            var provider = GetProvider(reader);

            foreach (var pair in packageTargets.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                var packagePath = pair.Key;
                var expected = pair.Value;

                Console.WriteLine($"Inspecting: {packagePath}");

                CollectFTexts(reader, packagePath, ftextRows, errors);
                InspectPackage(provider, packagePath, expected, exportRows, propertyHits, errors);
            }

            WriteJsonl(Path.Combine(outputDir, "exports.jsonl"), exportRows);
            WriteJsonl(Path.Combine(outputDir, "property-hits.jsonl"), propertyHits);
            WriteJsonl(Path.Combine(outputDir, "ftexts.jsonl"), ftextRows);
            WriteJsonl(Path.Combine(outputDir, "errors.jsonl"), errors);

            var summary = new ProbeSummary
            {
                TargetCount = targets.Count,
                CandidatePackageCount = packageTargets.Count,
                ExportCount = exportRows.Count,
                PropertyHitCount = propertyHits.Count,
                PropertyMatchedIdentityCount = propertyHits.Select(x => x.Identity).Distinct(StringComparer.Ordinal).Count(),
                FTextCount = ftextRows.Count,
                ExactFTextIdentityCount = ftextRows.Count(x => targets.Contains(x.Identity)),
                ErrorCount = errors.Count,
                MappingsProvided = mappingsFile is not null,
                PathFilter = pathFilter,
                PropertyMatchedIdentities = propertyHits.Select(x => x.Identity).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList()
            };

            File.WriteAllText(
                Path.Combine(outputDir, "summary.json"),
                System.Text.Json.JsonSerializer.Serialize(summary, JsonOptionsIndented),
                new UTF8Encoding(false));

            Console.WriteLine();
            Console.WriteLine($"Exports: {summary.ExportCount}");
            Console.WriteLine($"FTexts: {summary.FTextCount}");
            Console.WriteLine($"Property hits: {summary.PropertyHitCount}");
            Console.WriteLine($"Property matched identities: {summary.PropertyMatchedIdentityCount}/{summary.TargetCount}");
            Console.WriteLine($"Errors: {summary.ErrorCount}");
            Console.WriteLine($"Output: {outputDir}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex}");
            return 1;
        }
    }

    private static void ValidateInputs(string gameRoot, string keysFile, string? aesConfig, string? aesFile, string? mappingsFile)
    {
        if (!Directory.Exists(gameRoot))
            throw new DirectoryNotFoundException($"Game root not found: {gameRoot}");
        if (!File.Exists(keysFile))
            throw new FileNotFoundException("Keys file not found.", keysFile);
        if (aesConfig is not null && aesFile is not null)
            throw new ArgumentException("Use only one of --aes-config or --aes-file.");
        if (aesConfig is not null && !File.Exists(aesConfig))
            throw new FileNotFoundException("AES config not found.", aesConfig);
        if (aesFile is not null && !File.Exists(aesFile))
            throw new FileNotFoundException("AES file not found.", aesFile);
        if (mappingsFile is not null && !File.Exists(mappingsFile))
            throw new FileNotFoundException("Mappings file not found.", mappingsFile);
    }

    private static HashSet<string> LoadTargets(string path)
        => File.ReadLines(path)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0 && !x.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);

    private static string LoadAesKey(string? aesConfig, string? aesFile)
    {
        if (aesConfig is null && aesFile is null)
            return string.Empty;

        string raw;
        if (aesFile is not null)
        {
            raw = File.ReadAllText(aesFile).Trim();
        }
        else
        {
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

    private static Dictionary<string, HashSet<string>> FindCandidatePackages(UnrealArchiveReader reader, HashSet<string> targets)
    {
        var byPackage = new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>(StringComparer.OrdinalIgnoreCase);
        var patterns = targets.Select(x => new TargetPattern(x, GetKeyToken(x), Encoding.UTF8.GetBytes(GetKeyToken(x)))).ToList();

        var originalOut = Console.Out;
        try
        {
            Console.SetOut(TextWriter.Null);
            reader.ProcessAllAssets((assetPath, stream) =>
            {
                if (!IsPackagePayload(assetPath))
                    return;

                var bytes = ReadAllBytes(stream);
                foreach (var pattern in patterns)
                {
                    if (bytes.AsSpan().IndexOf(pattern.Bytes) < 0)
                        continue;

                    foreach (var packagePath in BuildPackageCandidates(assetPath))
                    {
                        var identities = byPackage.GetOrAdd(packagePath, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
                        identities.TryAdd(pattern.Identity, 0);
                    }
                }
            }, deepParse: false);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return byPackage.ToDictionary(
            x => x.Key,
            x => x.Value.Keys.ToHashSet(StringComparer.Ordinal),
            StringComparer.OrdinalIgnoreCase);
    }

    private static void CollectFTexts(UnrealArchiveReader reader, string packagePath, List<FTextRow> rows, List<ProbeError> errors)
    {
        try
        {
            reader.GetLocalizedStrings(packagePath, entries =>
            {
                for (var i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    rows.Add(new FTextRow
                    {
                        PackagePath = packagePath,
                        Index = i,
                        Namespace = entry.Namespace,
                        Key = entry.Key,
                        Identity = ComposeIdentity(entry.Namespace, entry.Key),
                        SourceString = entry.SourceString
                    });
                }
            });
        }
        catch (Exception ex)
        {
            errors.Add(ProbeError.From(packagePath, "GetLocalizedStrings", ex));
        }
    }

    private static void InspectPackage(
        DefaultFileProvider provider,
        string packagePath,
        HashSet<string> expected,
        List<ExportRow> exportRows,
        List<PropertyHit> propertyHits,
        List<ProbeError> errors)
    {
        try
        {
            var package = provider.LoadPackage(packagePath);
            var exports = package.GetExports();

            for (var exportIndex = 0; exportIndex < exports.Count; exportIndex++)
            {
                var export = exports[exportIndex];
                string json;

                try
                {
                    json = JsonConvert.SerializeObject(export, Formatting.None);
                }
                catch (Exception ex)
                {
                    errors.Add(ProbeError.From(packagePath, $"SerializeExport[{exportIndex}]", ex));
                    continue;
                }

                var exportName = export.Name.ToString();
                var exportType = export.ExportType;
                var before = propertyHits.Count;

                try
                {
                    var root = JToken.Parse(json);
                    FindTargetValues(root, expected, packagePath, exportIndex, exportName, exportType, propertyHits);
                }
                catch (Exception ex)
                {
                    errors.Add(ProbeError.From(packagePath, $"ParseExportJson[{exportIndex}]", ex));
                }

                exportRows.Add(new ExportRow
                {
                    PackagePath = packagePath,
                    ExportIndex = exportIndex,
                    ExportName = exportName,
                    ExportType = exportType,
                    SerializedJsonLength = json.Length,
                    TargetPropertyHitCount = propertyHits.Count - before
                });
            }
        }
        catch (Exception ex)
        {
            errors.Add(ProbeError.From(packagePath, "LoadPackage", ex));
        }
    }

    private static void FindTargetValues(
        JToken root,
        HashSet<string> expected,
        string packagePath,
        int exportIndex,
        string exportName,
        string exportType,
        List<PropertyHit> results)
    {
        foreach (var valueToken in root.DescendantsAndSelf().OfType<JValue>())
        {
            if (valueToken.Type != JTokenType.String)
                continue;

            var value = valueToken.Value<string>() ?? string.Empty;
            if (value.Length == 0)
                continue;

            foreach (var identity in expected)
            {
                var keyToken = GetKeyToken(identity);
                var position = value.IndexOf(keyToken, StringComparison.Ordinal);
                if (position < 0)
                    continue;

                var contextObject = FindNearestObject(valueToken);
                var contextJson = contextObject is null ? string.Empty : Truncate(contextObject.ToString(Formatting.None), MaxContextJsonChars);

                results.Add(new PropertyHit
                {
                    Identity = identity,
                    KeyToken = keyToken,
                    PackagePath = packagePath,
                    ExportIndex = exportIndex,
                    ExportName = exportName,
                    ExportType = exportType,
                    JsonPath = valueToken.Path,
                    ParentProperty = GetParentPropertyName(valueToken),
                    MatchKind = value.Equals(keyToken, StringComparison.Ordinal) ? "exact" : "contains",
                    MatchedValue = Truncate(value, MaxContextJsonChars),
                    ContextObjectPath = contextObject?.Path ?? string.Empty,
                    ContextObjectJson = contextJson,
                    InterestingProperties = contextObject is null ? new List<PropertyHint>() : ExtractInterestingProperties(contextObject)
                });
            }
        }
    }

    private static JObject? FindNearestObject(JToken token)
    {
        JToken? current = token;
        while (current is not null)
        {
            if (current is JObject obj)
                return obj;
            current = current.Parent;
        }
        return null;
    }

    private static string GetParentPropertyName(JToken token)
    {
        if (token.Parent is JProperty property)
            return property.Name;
        if (token.Parent?.Parent is JProperty grandProperty)
            return grandProperty.Name;
        return string.Empty;
    }

    private static List<PropertyHint> ExtractInterestingProperties(JObject context)
    {
        var result = new List<PropertyHint>();
        foreach (var property in context.DescendantsAndSelf().OfType<JProperty>())
        {
            if (result.Count >= MaxHintsPerHit)
                break;

            if (!IsInterestingPropertyName(property.Name))
                continue;

            string value;
            if (property.Value is JValue scalar)
                value = scalar.ToString();
            else
                value = property.Value.ToString(Formatting.None);

            result.Add(new PropertyHint
            {
                Path = property.Path,
                Name = property.Name,
                Value = Truncate(value, MaxHintValueChars)
            });
        }
        return result;
    }

    private static bool IsInterestingPropertyName(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.Contains("next")
            || lower.Contains("edge")
            || lower.Contains("dlg")
            || lower.Contains("dialog")
            || lower.Contains("speaker")
            || lower.Contains("actor")
            || lower.Contains("character")
            || lower.Contains("npc")
            || lower.Contains("player")
            || lower.Contains("quest")
            || lower.Contains("scene")
            || lower.Contains("node")
            || lower.Contains("choice")
            || lower.Contains("branch")
            || lower.Contains("text")
            || lower.Contains("key")
            || lower.Contains("name")
            || lower.EndsWith("id", StringComparison.Ordinal);
    }

    private static DefaultFileProvider GetProvider(UnrealArchiveReader reader)
    {
        var field = typeof(UnrealArchiveReader).GetField("_provider", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(UnrealArchiveReader).FullName, "_provider");

        return field.GetValue(reader) as DefaultFileProvider
            ?? throw new InvalidOperationException("UnrealArchiveReader._provider is not a DefaultFileProvider.");
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

    private static byte[] ReadAllBytes(Stream stream)
    {
        if (stream is MemoryStream memory)
            return memory.ToArray();

        var original = stream.CanSeek ? stream.Position : 0;
        if (stream.CanSeek)
            stream.Position = 0;

        using var copy = new MemoryStream();
        stream.CopyTo(copy);

        if (stream.CanSeek)
            stream.Position = original;

        return copy.ToArray();
    }

    private static bool IsPackagePayload(string path)
        => path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".uexp", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase);

    private static List<string> BuildPackageCandidates(string payloadPath)
    {
        if (!payloadPath.EndsWith(".uexp", StringComparison.OrdinalIgnoreCase))
            return new List<string> { payloadPath };

        var stem = Path.ChangeExtension(payloadPath, null)!;
        return new List<string> { stem + ".uasset", stem + ".umap" };
    }

    private static string GetKeyToken(string identity)
    {
        var separator = identity.IndexOf("::", StringComparison.Ordinal);
        return separator >= 0 ? identity[(separator + 2)..] : identity;
    }

    private static string ComposeIdentity(string @namespace, string key)
        => string.IsNullOrEmpty(@namespace) ? key : $"{@namespace}::{key}";

    private static string Truncate(string value, int maxChars)
        => value.Length <= maxChars ? value : value[..maxChars] + "...[TRUNCATED]";

    private static void WriteJsonl<T>(string path, IEnumerable<T> values)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        foreach (var value in values)
            writer.WriteLine(System.Text.Json.JsonSerializer.Serialize(value, JsonOptions));
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
        Console.WriteLine("  NTE.DialogueStructureProbe <gameRoot> <keys.txt> <outputDir> --mappings=<file.usmap> [--aes-config=<path> | --aes-file=<path>] [--path=<virtual path>]");
    }
}

internal sealed record TargetPattern(string Identity, string KeyToken, byte[] Bytes);

internal sealed class PackageTargetRow
{
    public required string PackagePath { get; init; }
    public List<string> TargetIdentities { get; init; } = new();
}

internal sealed class ExportRow
{
    public required string PackagePath { get; init; }
    public int ExportIndex { get; init; }
    public required string ExportName { get; init; }
    public required string ExportType { get; init; }
    public int SerializedJsonLength { get; init; }
    public int TargetPropertyHitCount { get; init; }
}

internal sealed class FTextRow
{
    public required string PackagePath { get; init; }
    public int Index { get; init; }
    public required string Namespace { get; init; }
    public required string Key { get; init; }
    public required string Identity { get; init; }
    public required string SourceString { get; init; }
}

internal sealed class PropertyHit
{
    public required string Identity { get; init; }
    public required string KeyToken { get; init; }
    public required string PackagePath { get; init; }
    public int ExportIndex { get; init; }
    public required string ExportName { get; init; }
    public required string ExportType { get; init; }
    public required string JsonPath { get; init; }
    public required string ParentProperty { get; init; }
    public required string MatchKind { get; init; }
    public required string MatchedValue { get; init; }
    public required string ContextObjectPath { get; init; }
    public required string ContextObjectJson { get; init; }
    public List<PropertyHint> InterestingProperties { get; init; } = new();
}

internal sealed class PropertyHint
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required string Value { get; init; }
}

internal sealed class ProbeError
{
    public required string PackagePath { get; init; }
    public required string Stage { get; init; }
    public required string ErrorType { get; init; }
    public required string ErrorMessage { get; init; }

    public static ProbeError From(string packagePath, string stage, Exception ex)
        => new()
        {
            PackagePath = packagePath,
            Stage = stage,
            ErrorType = ex.GetType().FullName ?? ex.GetType().Name,
            ErrorMessage = ex.Message
        };
}

internal sealed class ProbeSummary
{
    public int TargetCount { get; init; }
    public int CandidatePackageCount { get; init; }
    public int ExportCount { get; init; }
    public int PropertyHitCount { get; init; }
    public int PropertyMatchedIdentityCount { get; init; }
    public int FTextCount { get; init; }
    public int ExactFTextIdentityCount { get; init; }
    public int ErrorCount { get; init; }
    public bool MappingsProvided { get; init; }
    public string PathFilter { get; init; } = string.Empty;
    public List<string> PropertyMatchedIdentities { get; init; } = new();
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

    public static TemporaryFile InstallCopy(string directory, string name, string? sourcePath)
    {
        if (string.IsNullOrEmpty(sourcePath))
            return new TemporaryFile(null, false, null);

        var path = Path.Combine(directory, name);
        var hadExisting = File.Exists(path);
        var previous = hadExisting ? File.ReadAllBytes(path) : null;
        File.Copy(sourcePath, path, true);
        return new TemporaryFile(path, hadExisting, previous);
    }

    public void Dispose()
    {
        if (_path is null)
            return;

        if (_hadExisting && _previous is not null)
            File.WriteAllBytes(_path, _previous);
        else if (File.Exists(_path))
            File.Delete(_path);
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
        if (string.IsNullOrEmpty(aesKey))
            return new TemporaryAesCompatibility(null, false, null);

        var path = Path.Combine(gameRoot, "aes.txt");
        var hadExisting = File.Exists(path);
        var previous = hadExisting ? File.ReadAllBytes(path) : null;
        File.WriteAllText(path, aesKey, Encoding.ASCII);
        return new TemporaryAesCompatibility(path, hadExisting, previous);
    }

    public void Dispose()
    {
        if (_path is null)
            return;

        if (_hadExisting && _previous is not null)
            File.WriteAllBytes(_path, _previous);
        else if (File.Exists(_path))
            File.Delete(_path);
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
