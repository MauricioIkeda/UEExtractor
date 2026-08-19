using CUE4Parse.FileProvider;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Solicen.Localization.UE4;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NTE.DialogueGraphProbe;

internal static class Program
{
    private const int MaxExportJsonChars = 120000;
    private const int MaxHintValueChars = 3000;
    private const int MaxHintsPerExport = 300;

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
            var packageTargetsPath = Path.GetFullPath(args[1]);
            var outputDir = Path.GetFullPath(args[2]);

            string? aesConfig = null;
            string? aesFile = null;
            string? mappingsFile = null;

            foreach (var arg in args.Skip(3))
            {
                if (arg.StartsWith("--aes-config=", StringComparison.OrdinalIgnoreCase))
                    aesConfig = Path.GetFullPath(arg["--aes-config=".Length..].Trim('"'));
                else if (arg.StartsWith("--aes-file=", StringComparison.OrdinalIgnoreCase))
                    aesFile = Path.GetFullPath(arg["--aes-file=".Length..].Trim('"'));
                else if (arg.StartsWith("--mappings=", StringComparison.OrdinalIgnoreCase))
                    mappingsFile = Path.GetFullPath(arg["--mappings=".Length..].Trim('"'));
                else
                    throw new ArgumentException($"Unknown argument: {arg}");
            }

            ValidateInputs(gameRoot, packageTargetsPath, aesConfig, aesFile, mappingsFile);
            Directory.CreateDirectory(outputDir);

            var packageTargets = LoadPackageTargets(packageTargetsPath);
            var aesKey = LoadAesKey(aesConfig, aesFile);

            Console.WriteLine("NTE Dialogue Graph Probe");
            Console.WriteLine($"Packages: {packageTargets.Count}");
            Console.WriteLine($"Mappings: {(mappingsFile is null ? "<none>" : Path.GetFileName(mappingsFile))}");
            Console.WriteLine("Purpose: validate namespace+key and inspect full DlgNode/DlgDialogue export structure");
            Console.WriteLine();

            using var mappingScope = TemporaryFile.InstallCopy(gameRoot, "__nte_dialogue_graph_probe.usmap", mappingsFile);
            using var aesScope = TemporaryAesCompatibility.Install(gameRoot, aesKey);
            using var reader = CreateReaderWithoutLeakingAes(gameRoot);
            var provider = GetProvider(reader);

            var nodeRows = new List<NodeRow>();
            var dialogueRoots = new List<DialogueRootRow>();
            var mismatchRows = new List<NamespaceMismatchRow>();
            var errors = new List<ProbeError>();

            foreach (var target in packageTargets.OrderBy(x => x.PackagePath, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"Inspecting: {target.PackagePath}");
                InspectPackage(provider, target, nodeRows, dialogueRoots, mismatchRows, errors);
            }

            WriteJsonl(Path.Combine(outputDir, "target-nodes.jsonl"), nodeRows);
            WriteJsonl(Path.Combine(outputDir, "dialogue-roots.jsonl"), dialogueRoots);
            WriteJsonl(Path.Combine(outputDir, "namespace-mismatches.jsonl"), mismatchRows);
            WriteJsonl(Path.Combine(outputDir, "errors.jsonl"), errors);

            var summary = new ProbeSummary
            {
                PackageCount = packageTargets.Count,
                RequestedIdentityCount = packageTargets.SelectMany(x => x.TargetIdentities).Distinct(StringComparer.Ordinal).Count(),
                ExactNodeCount = nodeRows.Count,
                ExactMatchedIdentityCount = nodeRows.Select(x => x.Identity).Distinct(StringComparer.Ordinal).Count(),
                NamespaceMismatchCount = mismatchRows.Count,
                DialogueRootCount = dialogueRoots.Count,
                ErrorCount = errors.Count,
                MappingsProvided = mappingsFile is not null,
                ExactMatchedIdentities = nodeRows.Select(x => x.Identity).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList()
            };

            File.WriteAllText(
                Path.Combine(outputDir, "summary.json"),
                System.Text.Json.JsonSerializer.Serialize(summary, JsonOptionsIndented),
                new UTF8Encoding(false));

            Console.WriteLine();
            Console.WriteLine($"Exact nodes: {summary.ExactNodeCount}");
            Console.WriteLine($"Exact matched identities: {summary.ExactMatchedIdentityCount}/{summary.RequestedIdentityCount}");
            Console.WriteLine($"Namespace mismatches: {summary.NamespaceMismatchCount}");
            Console.WriteLine($"Dialogue roots: {summary.DialogueRootCount}");
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

    private static void InspectPackage(
        DefaultFileProvider provider,
        PackageTargetRow target,
        List<NodeRow> nodeRows,
        List<DialogueRootRow> dialogueRoots,
        List<NamespaceMismatchRow> mismatchRows,
        List<ProbeError> errors)
    {
        try
        {
            var package = provider.LoadPackage(target.PackagePath);
            var exports = package.GetExports().ToList();

            for (var exportIndex = 0; exportIndex < exports.Count; exportIndex++)
            {
                var export = exports[exportIndex];
                string json;
                JObject root;

                try
                {
                    json = JsonConvert.SerializeObject(export, Formatting.None);
                    root = JObject.Parse(json);
                }
                catch (Exception ex)
                {
                    errors.Add(ProbeError.From(target.PackagePath, $"SerializeOrParseExport[{exportIndex}]", ex));
                    continue;
                }

                var exportName = export.Name.ToString();
                var exportType = export.ExportType;

                if (exportType.Equals("DlgDialogue", StringComparison.OrdinalIgnoreCase))
                {
                    dialogueRoots.Add(new DialogueRootRow
                    {
                        PackagePath = target.PackagePath,
                        ExportIndex = exportIndex,
                        ExportName = exportName,
                        ExportType = exportType,
                        ExportJson = Truncate(json, MaxExportJsonChars),
                        InterestingProperties = ExtractInterestingProperties(root)
                    });
                }

                foreach (var requestedIdentity in target.TargetIdentities)
                {
                    var (requestedNamespace, requestedKey) = SplitIdentity(requestedIdentity);

                    foreach (var textObject in EnumerateObjects(root))
                    {
                        var key = textObject["Key"]?.Value<string>();
                        if (!string.Equals(key, requestedKey, StringComparison.Ordinal))
                            continue;

                        var tableId = textObject["TableId"]?.Value<string>() ?? string.Empty;
                        var actualNamespace = NamespaceFromTableId(tableId);
                        var sourceString = textObject["SourceString"]?.Value<string>() ?? string.Empty;
                        var localizedString = textObject["LocalizedString"]?.Value<string>() ?? string.Empty;

                        if (!string.IsNullOrEmpty(actualNamespace) &&
                            !string.Equals(actualNamespace, requestedNamespace, StringComparison.Ordinal))
                        {
                            mismatchRows.Add(new NamespaceMismatchRow
                            {
                                RequestedIdentity = requestedIdentity,
                                ActualIdentity = ComposeIdentity(actualNamespace, requestedKey),
                                PackagePath = target.PackagePath,
                                ExportIndex = exportIndex,
                                ExportName = exportName,
                                ExportType = exportType,
                                TableId = tableId,
                                JsonPath = textObject.Path
                            });
                            continue;
                        }

                        if (string.IsNullOrEmpty(actualNamespace))
                            continue;

                        nodeRows.Add(new NodeRow
                        {
                            Identity = requestedIdentity,
                            PackagePath = target.PackagePath,
                            ExportIndex = exportIndex,
                            ExportName = exportName,
                            ExportType = exportType,
                            TableId = tableId,
                            TextObjectPath = textObject.Path,
                            SourceString = sourceString,
                            LocalizedString = localizedString,
                            ExportJson = Truncate(json, MaxExportJsonChars),
                            InterestingProperties = ExtractInterestingProperties(root),
                            NearbyExports = GetNearbyExports(exports, exportIndex)
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add(ProbeError.From(target.PackagePath, "LoadPackage", ex));
        }
    }

    private static List<ExportNeighbor> GetNearbyExports(IReadOnlyList<CUE4Parse.UE4.Assets.Exports.UObject> exports, int index)
    {
        var result = new List<ExportNeighbor>();
        var start = Math.Max(0, index - 3);
        var end = Math.Min(exports.Count - 1, index + 3);
        for (var i = start; i <= end; i++)
        {
            result.Add(new ExportNeighbor
            {
                Offset = i - index,
                ExportIndex = i,
                ExportName = exports[i].Name.ToString(),
                ExportType = exports[i].ExportType
            });
        }
        return result;
    }

    private static IEnumerable<JObject> EnumerateObjects(JToken root)
    {
        if (root is JObject self)
            yield return self;

        foreach (var child in root.Children())
        {
            foreach (var nested in EnumerateObjects(child))
                yield return nested;
        }
    }

    private static List<PropertyHint> ExtractInterestingProperties(JObject root)
    {
        var result = new List<PropertyHint>();

        foreach (var token in EnumerateTokens(root))
        {
            if (result.Count >= MaxHintsPerExport)
                break;

            if (token is not JProperty property || !IsInterestingPropertyName(property.Name))
                continue;

            var value = property.Value is JValue scalar
                ? scalar.ToString()
                : property.Value.ToString(Formatting.None);

            result.Add(new PropertyHint
            {
                Path = property.Path,
                Name = property.Name,
                Value = Truncate(value, MaxHintValueChars)
            });
        }

        return result;
    }

    private static IEnumerable<JToken> EnumerateTokens(JToken root)
    {
        yield return root;
        foreach (var child in root.Children())
        {
            foreach (var nested in EnumerateTokens(child))
                yield return nested;
        }
    }

    private static bool IsInterestingPropertyName(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.Contains("next")
            || lower.Contains("edge")
            || lower.Contains("dlg")
            || lower.Contains("dialog")
            || lower.Contains("speaker")
            || lower.Contains("participant")
            || lower.Contains("actor")
            || lower.Contains("character")
            || lower.Contains("npc")
            || lower.Contains("player")
            || lower.Contains("quest")
            || lower.Contains("scene")
            || lower.Contains("node")
            || lower.Contains("choice")
            || lower.Contains("branch")
            || lower.Contains("condition")
            || lower.Contains("text")
            || lower.Contains("key")
            || lower.Contains("name")
            || lower.Contains("guid")
            || lower.Contains("target")
            || lower.Contains("parent")
            || lower.Contains("child")
            || lower.Contains("sequence")
            || lower.EndsWith("id", StringComparison.Ordinal);
    }

    private static List<PackageTargetRow> LoadPackageTargets(string path)
    {
        var result = new List<PackageTargetRow>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var row = System.Text.Json.JsonSerializer.Deserialize<PackageTargetRow>(line, JsonReadOptions)
                ?? throw new InvalidDataException("Invalid package-targets JSONL row.");
            result.Add(row);
        }
        return result;
    }

    private static (string Namespace, string Key) SplitIdentity(string identity)
    {
        var separator = identity.IndexOf("::", StringComparison.Ordinal);
        return separator < 0
            ? (string.Empty, identity)
            : (identity[..separator], identity[(separator + 2)..]);
    }

    private static string NamespaceFromTableId(string tableId)
    {
        if (string.IsNullOrWhiteSpace(tableId))
            return string.Empty;

        var slash = tableId.LastIndexOf('/');
        var leaf = slash >= 0 ? tableId[(slash + 1)..] : tableId;
        var dot = leaf.IndexOf('.');
        return dot >= 0 ? leaf[..dot] : leaf;
    }

    private static string ComposeIdentity(string ns, string key)
        => string.IsNullOrEmpty(ns) ? key : $"{ns}::{key}";

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

    private static void ValidateInputs(string gameRoot, string packageTargets, string? aesConfig, string? aesFile, string? mappings)
    {
        if (!Directory.Exists(gameRoot))
            throw new DirectoryNotFoundException($"Game root not found: {gameRoot}");
        if (!File.Exists(packageTargets))
            throw new FileNotFoundException("package-targets.jsonl not found.", packageTargets);
        if (aesConfig is not null && aesFile is not null)
            throw new ArgumentException("Use only one of --aes-config or --aes-file.");
        if (aesConfig is not null && !File.Exists(aesConfig))
            throw new FileNotFoundException("AES config not found.", aesConfig);
        if (aesFile is not null && !File.Exists(aesFile))
            throw new FileNotFoundException("AES file not found.", aesFile);
        if (mappings is not null && !File.Exists(mappings))
            throw new FileNotFoundException("Mappings file not found.", mappings);
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

    private static string Truncate(string value, int maxChars)
        => value.Length <= maxChars ? value : value[..maxChars] + "...[TRUNCATED]";

    private static void WriteJsonl<T>(string path, IEnumerable<T> values)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        foreach (var value in values)
            writer.WriteLine(System.Text.Json.JsonSerializer.Serialize(value, JsonWriteOptions));
    }

    private static readonly JsonSerializerOptions JsonReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions JsonWriteOptions = new()
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
        Console.WriteLine("  NTE.DialogueGraphProbe <gameRoot> <package-targets.jsonl> <outputDir> --mappings=<file.usmap> [--aes-config=<path> | --aes-file=<path>]");
    }
}

internal sealed class PackageTargetRow
{
    public required string PackagePath { get; init; }
    public List<string> TargetIdentities { get; init; } = new();
}

internal sealed class NodeRow
{
    public required string Identity { get; init; }
    public required string PackagePath { get; init; }
    public int ExportIndex { get; init; }
    public required string ExportName { get; init; }
    public required string ExportType { get; init; }
    public required string TableId { get; init; }
    public required string TextObjectPath { get; init; }
    public required string SourceString { get; init; }
    public required string LocalizedString { get; init; }
    public required string ExportJson { get; init; }
    public List<PropertyHint> InterestingProperties { get; init; } = new();
    public List<ExportNeighbor> NearbyExports { get; init; } = new();
}

internal sealed class DialogueRootRow
{
    public required string PackagePath { get; init; }
    public int ExportIndex { get; init; }
    public required string ExportName { get; init; }
    public required string ExportType { get; init; }
    public required string ExportJson { get; init; }
    public List<PropertyHint> InterestingProperties { get; init; } = new();
}

internal sealed class NamespaceMismatchRow
{
    public required string RequestedIdentity { get; init; }
    public required string ActualIdentity { get; init; }
    public required string PackagePath { get; init; }
    public int ExportIndex { get; init; }
    public required string ExportName { get; init; }
    public required string ExportType { get; init; }
    public required string TableId { get; init; }
    public required string JsonPath { get; init; }
}

internal sealed class ExportNeighbor
{
    public int Offset { get; init; }
    public int ExportIndex { get; init; }
    public required string ExportName { get; init; }
    public required string ExportType { get; init; }
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
    public int PackageCount { get; init; }
    public int RequestedIdentityCount { get; init; }
    public int ExactNodeCount { get; init; }
    public int ExactMatchedIdentityCount { get; init; }
    public int NamespaceMismatchCount { get; init; }
    public int DialogueRootCount { get; init; }
    public int ErrorCount { get; init; }
    public bool MappingsProvided { get; init; }
    public List<string> ExactMatchedIdentities { get; init; } = new();
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
