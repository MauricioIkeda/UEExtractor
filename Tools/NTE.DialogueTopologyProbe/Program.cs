using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Solicen.Localization.UE4;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NTE.DialogueTopologyProbe;

internal static class Program
{
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

            Console.WriteLine("NTE Dialogue Topology Probe");
            Console.WriteLine($"Packages: {packageTargets.Count}");
            Console.WriteLine($"Mappings: {(mappingsFile is null ? "<none>" : Path.GetFileName(mappingsFile))}");
            Console.WriteLine("Purpose: resolve DlgDialogue.Nodes + DlgNode.Children into explicit topology and target neighborhoods");
            Console.WriteLine();

            using var mappingScope = TemporaryFile.InstallCopy(gameRoot, "__nte_dialogue_topology_probe.usmap", mappingsFile);
            using var aesScope = TemporaryAesCompatibility.Install(gameRoot, aesKey);
            using var reader = CreateReaderWithoutLeakingAes(gameRoot);
            var provider = GetProvider(reader);

            var allNodes = new List<NodeRow>();
            var allEdges = new List<EdgeRow>();
            var allNeighborhoods = new List<TargetNeighborhoodRow>();
            var allParticipants = new List<ParticipantContextRow>();
            var allMismatches = new List<NamespaceMismatchRow>();
            var errors = new List<ProbeError>();

            foreach (var target in packageTargets.OrderBy(x => x.PackagePath, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"Inspecting: {target.PackagePath}");
                InspectPackage(provider, target, allNodes, allEdges, allNeighborhoods, allParticipants, allMismatches, errors);
            }

            WriteJsonl(Path.Combine(outputDir, "graph-nodes.jsonl"), allNodes);
            WriteJsonl(Path.Combine(outputDir, "graph-edges.jsonl"), allEdges);
            WriteJsonl(Path.Combine(outputDir, "target-neighborhoods.jsonl"), allNeighborhoods);
            WriteJsonl(Path.Combine(outputDir, "participant-context.jsonl"), allParticipants);
            WriteJsonl(Path.Combine(outputDir, "namespace-mismatches.jsonl"), allMismatches);
            WriteJsonl(Path.Combine(outputDir, "errors.jsonl"), errors);

            var summary = new ProbeSummary
            {
                PackageCount = packageTargets.Count,
                RequestedIdentityCount = packageTargets.SelectMany(x => x.TargetIdentities).Distinct(StringComparer.Ordinal).Count(),
                NodeCount = allNodes.Count,
                SpeechNodeCount = allNodes.Count(x => x.NodeType.Equals("DlgNode_Speech", StringComparison.OrdinalIgnoreCase)),
                EdgeCount = allEdges.Count,
                TargetNeighborhoodCount = allNeighborhoods.Count,
                ExactMatchedIdentityCount = allNeighborhoods.Select(x => x.Identity).Distinct(StringComparer.Ordinal).Count(),
                NamespaceMismatchCount = allMismatches.Count,
                ParticipantContextCount = allParticipants.Count,
                ErrorCount = errors.Count,
                MappingsProvided = mappingsFile is not null,
                ExactMatchedIdentities = allNeighborhoods.Select(x => x.Identity).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList()
            };

            File.WriteAllText(
                Path.Combine(outputDir, "summary.json"),
                System.Text.Json.JsonSerializer.Serialize(summary, JsonWriteIndented),
                new UTF8Encoding(false));

            Console.WriteLine();
            Console.WriteLine($"Nodes: {summary.NodeCount} ({summary.SpeechNodeCount} speech)");
            Console.WriteLine($"Edges: {summary.EdgeCount}");
            Console.WriteLine($"Target neighborhoods: {summary.TargetNeighborhoodCount}");
            Console.WriteLine($"Exact matched identities: {summary.ExactMatchedIdentityCount}/{summary.RequestedIdentityCount}");
            Console.WriteLine($"Namespace mismatches: {summary.NamespaceMismatchCount}");
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
        List<NodeRow> allNodes,
        List<EdgeRow> allEdges,
        List<TargetNeighborhoodRow> allNeighborhoods,
        List<ParticipantContextRow> allParticipants,
        List<NamespaceMismatchRow> allMismatches,
        List<ProbeError> errors)
    {
        try
        {
            var package = provider.LoadPackage(target.PackagePath);
            var exports = package.GetExports().ToList();
            var parsed = new List<ParsedExport>(exports.Count);

            for (var exportIndex = 0; exportIndex < exports.Count; exportIndex++)
            {
                var export = exports[exportIndex];
                try
                {
                    var json = JsonConvert.SerializeObject(export, Formatting.None);
                    parsed.Add(new ParsedExport(exportIndex, export, JObject.Parse(json)));
                }
                catch (Exception ex)
                {
                    errors.Add(ProbeError.From(target.PackagePath, $"SerializeOrParseExport[{exportIndex}]", ex));
                }
            }

            var dialogue = parsed.FirstOrDefault(x => x.Export.ExportType.Equals("DlgDialogue", StringComparison.OrdinalIgnoreCase));
            if (dialogue is null)
            {
                errors.Add(new ProbeError
                {
                    PackagePath = target.PackagePath,
                    Stage = "FindDlgDialogue",
                    ErrorType = "MissingDlgDialogue",
                    ErrorMessage = "No DlgDialogue export was found in the package."
                });
                return;
            }

            var rootProps = dialogue.Json["Properties"] as JObject ?? new JObject();
            var nodeRefs = rootProps["Nodes"] as JArray ?? new JArray();
            var startRefs = rootProps["StartNodes"] as JArray ?? new JArray();
            var graphIndexByExportName = BuildGraphIndexByExportName(nodeRefs);
            var startNames = startRefs
                .OfType<JObject>()
                .Select(ReferenceExportName)
                .Where(x => !string.IsNullOrEmpty(x))
                .ToHashSet(StringComparer.Ordinal);

            var packageNodes = new List<NodeRow>();

            foreach (var item in parsed)
            {
                var nodeType = item.Export.ExportType;
                if (!nodeType.StartsWith("DlgNode_", StringComparison.OrdinalIgnoreCase))
                    continue;

                var exportName = item.Export.Name.ToString();
                var props = item.Json["Properties"] as JObject ?? new JObject();
                var text = props["Text"] as JObject;
                var tableId = text?["TableId"]?.Value<string>() ?? string.Empty;
                var textNamespace = NamespaceFromTableId(tableId);
                var key = text?["Key"]?.Value<string>() ?? string.Empty;
                var identity = !string.IsNullOrEmpty(textNamespace) && !string.IsNullOrEmpty(key)
                    ? ComposeIdentity(textNamespace, key)
                    : null;

                graphIndexByExportName.TryGetValue(exportName, out var graphIndexValue);
                int? graphIndex = graphIndexByExportName.ContainsKey(exportName) ? graphIndexValue : null;

                var overrideSpeaker = props["OverrideSpeakerName"] as JObject;
                var sequenceNpcIds = (props["SequenceActorConfigs"] as JArray)?
                    .OfType<JObject>()
                    .Select(x => x["NpcId"]?.Value<string>() ?? string.Empty)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.Ordinal)
                    .ToList() ?? new List<string>();

                var node = new NodeRow
                {
                    PackagePath = target.PackagePath,
                    ExportIndex = item.ExportIndex,
                    ExportName = exportName,
                    NodeType = nodeType,
                    GraphIndex = graphIndex,
                    IsStartNode = startNames.Contains(exportName),
                    NodeGuid = props["NodeGuid"]?.Value<string>() ?? string.Empty,
                    TableId = tableId,
                    Namespace = textNamespace,
                    Key = key,
                    Identity = identity,
                    SourceString = text?["SourceString"]?.Value<string>() ?? string.Empty,
                    LocalizedString = text?["LocalizedString"]?.Value<string>() ?? string.Empty,
                    OwnerName = props["OwnerName"]?.Value<string>() ?? string.Empty,
                    IsSelfTalk = props["bIsSelfTalk"]?.Value<bool?>(),
                    OverrideSpeakerTableId = overrideSpeaker?["TableId"]?.Value<string>() ?? string.Empty,
                    OverrideSpeakerKey = overrideSpeaker?["Key"]?.Value<string>() ?? string.Empty,
                    OverrideSpeakerSourceString = overrideSpeaker?["SourceString"]?.Value<string>() ?? string.Empty,
                    SequenceNpcIds = sequenceNpcIds,
                    ChildCount = (props["Children"] as JArray)?.Count ?? 0
                };

                packageNodes.Add(node);
            }

            var nodeByExportName = packageNodes.ToDictionary(x => x.ExportName, StringComparer.Ordinal);
            var nodeByGraphIndex = packageNodes
                .Where(x => x.GraphIndex.HasValue)
                .GroupBy(x => x.GraphIndex!.Value)
                .ToDictionary(x => x.Key, x => x.First());

            var packageEdges = new List<EdgeRow>();

            foreach (var item in parsed)
            {
                var exportName = item.Export.Name.ToString();
                if (!nodeByExportName.TryGetValue(exportName, out var sourceNode))
                    continue;

                var props = item.Json["Properties"] as JObject;
                var children = props?["Children"] as JArray;
                if (children is null)
                    continue;

                for (var childIndex = 0; childIndex < children.Count; childIndex++)
                {
                    if (children[childIndex] is not JObject child)
                        continue;

                    var targetIndex = child["TargetIndex"]?.Value<int?>();
                    NodeRow? targetNode = null;
                    JObject? targetRef = null;

                    if (targetIndex.HasValue && targetIndex.Value >= 0 && targetIndex.Value < nodeRefs.Count)
                    {
                        targetRef = nodeRefs[targetIndex.Value] as JObject;
                        if (nodeByGraphIndex.TryGetValue(targetIndex.Value, out var resolvedByIndex))
                            targetNode = resolvedByIndex;
                        else
                        {
                            var targetExportName = targetRef is null ? string.Empty : ReferenceExportName(targetRef);
                            if (!string.IsNullOrEmpty(targetExportName))
                                nodeByExportName.TryGetValue(targetExportName, out targetNode);
                        }
                    }

                    var edgeText = child["Text"] as JObject;
                    packageEdges.Add(new EdgeRow
                    {
                        PackagePath = target.PackagePath,
                        SourceExportName = sourceNode.ExportName,
                        SourceNodeType = sourceNode.NodeType,
                        SourceGraphIndex = sourceNode.GraphIndex,
                        SourceIdentity = sourceNode.Identity,
                        ChildIndex = childIndex,
                        TargetIndex = targetIndex,
                        TargetExportName = targetNode?.ExportName ?? (targetRef is null ? string.Empty : ReferenceExportName(targetRef)),
                        TargetNodeType = targetNode?.NodeType ?? string.Empty,
                        TargetIdentity = targetNode?.Identity,
                        EdgeIndex = child["EdgeIndex"]?.Value<int?>(),
                        SpeakerState = child["SpeakerState"]?.Value<string>() ?? string.Empty,
                        EdgeNamespace = edgeText?["Namespace"]?.Value<string>() ?? NamespaceFromTableId(edgeText?["TableId"]?.Value<string>() ?? string.Empty),
                        EdgeKey = edgeText?["Key"]?.Value<string>() ?? string.Empty,
                        EdgeSourceString = edgeText?["SourceString"]?.Value<string>() ?? edgeText?["CultureInvariantString"]?.Value<string>() ?? string.Empty,
                        ConditionsJson = CompactJson(child["Conditions"]),
                        EdgeDataJson = CompactJson(child["EdgeData"])
                    });
                }
            }

            var requested = target.TargetIdentities.ToHashSet(StringComparer.Ordinal);
            var packageMismatches = FindNamespaceMismatches(packageNodes, target.TargetIdentities, target.PackagePath);
            allMismatches.AddRange(packageMismatches);

            foreach (var identity in requested)
            {
                foreach (var matched in packageNodes.Where(x => string.Equals(x.Identity, identity, StringComparison.Ordinal)))
                {
                    var incoming = packageEdges
                        .Where(x => string.Equals(x.TargetExportName, matched.ExportName, StringComparison.Ordinal))
                        .Select(x => EdgeSummary.From(x, nodeByExportName))
                        .ToList();

                    var outgoing = packageEdges
                        .Where(x => string.Equals(x.SourceExportName, matched.ExportName, StringComparison.Ordinal))
                        .Select(x => EdgeSummary.From(x, nodeByExportName))
                        .ToList();

                    allNeighborhoods.Add(new TargetNeighborhoodRow
                    {
                        Identity = identity,
                        PackagePath = target.PackagePath,
                        Node = NodeSummary.From(matched),
                        Incoming = incoming,
                        Outgoing = outgoing,
                        QuestTokens = ExtractQuestTokens(rootProps),
                        DialogueName = rootProps["Name"]?.Value<string>() ?? dialogue.Export.Name.ToString()
                    });
                }
            }

            allParticipants.Add(new ParticipantContextRow
            {
                PackagePath = target.PackagePath,
                DialogueName = rootProps["Name"]?.Value<string>() ?? dialogue.Export.Name.ToString(),
                DialogueGuid = rootProps["Guid"]?.Value<string>() ?? string.Empty,
                DialogueType = rootProps["DialogueType"]?.Value<string>() ?? string.Empty,
                StartNodeExportNames = startNames.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                ParticipantNames = ExtractParticipantNames(rootProps),
                ExtraParticipants = (rootProps["ExtraParticipants"] as JArray)?.Values<string>().Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? new List<string>(),
                QuestTokens = ExtractQuestTokens(rootProps),
                ParticipantsClassesJson = CompactJson(rootProps["ParticipantsClasses"]),
                ParticipantsDataJson = CompactJson(rootProps["ParticipantsData"])
            });

            allNodes.AddRange(packageNodes);
            allEdges.AddRange(packageEdges);
        }
        catch (Exception ex)
        {
            errors.Add(ProbeError.From(target.PackagePath, "LoadOrInspectPackage", ex));
        }
    }

    private static Dictionary<string, int> BuildGraphIndexByExportName(JArray nodeRefs)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < nodeRefs.Count; i++)
        {
            if (nodeRefs[i] is not JObject obj)
                continue;
            var name = ReferenceExportName(obj);
            if (!string.IsNullOrEmpty(name))
                result[name] = i;
        }
        return result;
    }

    private static string ReferenceExportName(JObject reference)
    {
        var objectName = reference["ObjectName"]?.Value<string>() ?? string.Empty;
        if (string.IsNullOrEmpty(objectName))
            return string.Empty;

        var end = objectName.LastIndexOf('\'');
        if (end < 0)
            end = objectName.Length;

        var colon = objectName.LastIndexOf(':', Math.Max(0, end - 1));
        if (colon >= 0 && colon + 1 < end)
            return objectName[(colon + 1)..end];

        var quote = objectName.IndexOf('\'');
        if (quote >= 0 && quote + 1 < end)
            return objectName[(quote + 1)..end];

        return objectName.Trim('\'');
    }

    private static List<NamespaceMismatchRow> FindNamespaceMismatches(
        IEnumerable<NodeRow> nodes,
        IEnumerable<string> requestedIdentities,
        string packagePath)
    {
        var result = new List<NamespaceMismatchRow>();
        foreach (var requested in requestedIdentities)
        {
            var (requestedNamespace, requestedKey) = SplitIdentity(requested);
            foreach (var node in nodes.Where(x => string.Equals(x.Key, requestedKey, StringComparison.Ordinal)))
            {
                if (string.IsNullOrEmpty(node.Namespace) || string.Equals(node.Namespace, requestedNamespace, StringComparison.Ordinal))
                    continue;

                result.Add(new NamespaceMismatchRow
                {
                    RequestedIdentity = requested,
                    ActualIdentity = node.Identity ?? ComposeIdentity(node.Namespace, node.Key),
                    PackagePath = packagePath,
                    ExportName = node.ExportName,
                    NodeType = node.NodeType,
                    TableId = node.TableId
                });
            }
        }
        return result;
    }

    private static List<string> ExtractParticipantNames(JObject rootProps)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (rootProps["ParticipantsClasses"] is JArray classes)
        {
            foreach (var entry in classes.OfType<JObject>())
            {
                var name = entry["ParticipantName"]?.Value<string>();
                if (!string.IsNullOrWhiteSpace(name))
                    result.Add(name);
            }
        }

        if (rootProps["ParticipantsData"] is JArray data)
        {
            foreach (var entry in data.OfType<JObject>())
            {
                var name = entry["Key"]?.Value<string>();
                if (!string.IsNullOrWhiteSpace(name))
                    result.Add(name);
            }
        }

        return result.OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    private static List<string> ExtractQuestTokens(JObject rootProps)
    {
        var text = CompactJson(rootProps["ParticipantsData"]);
        if (string.IsNullOrEmpty(text))
            return new List<string>();

        return Regex.Matches(text, @"(?<![A-Za-z0-9_])[A-Za-z]\d{6}(?:_\d+)?", RegexOptions.CultureInvariant)
            .Select(x => x.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string CompactJson(JToken? token)
        => token is null || token.Type == JTokenType.Null ? string.Empty : token.ToString(Formatting.None);

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
        return separator < 0 ? (string.Empty, identity) : (identity[..separator], identity[(separator + 2)..]);
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
            raw = File.ReadAllText(aesFile).Trim();
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

    private static void WriteJsonl<T>(string path, IEnumerable<T> values)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        foreach (var value in values)
            writer.WriteLine(System.Text.Json.JsonSerializer.Serialize(value, JsonWriteOptions));
    }

    private static readonly JsonSerializerOptions JsonReadOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };
    private static readonly JsonSerializerOptions JsonWriteIndented = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  NTE.DialogueTopologyProbe <gameRoot> <package-targets.jsonl> <outputDir> --mappings=<file.usmap> [--aes-config=<path> | --aes-file=<path>]");
    }
}

internal sealed record ParsedExport(int ExportIndex, UObject Export, JObject Json);

internal sealed class PackageTargetRow
{
    public required string PackagePath { get; init; }
    public List<string> TargetIdentities { get; init; } = new();
}

internal sealed class NodeRow
{
    public required string PackagePath { get; init; }
    public int ExportIndex { get; init; }
    public required string ExportName { get; init; }
    public required string NodeType { get; init; }
    public int? GraphIndex { get; init; }
    public bool IsStartNode { get; init; }
    public required string NodeGuid { get; init; }
    public required string TableId { get; init; }
    public required string Namespace { get; init; }
    public required string Key { get; init; }
    public string? Identity { get; init; }
    public required string SourceString { get; init; }
    public required string LocalizedString { get; init; }
    public required string OwnerName { get; init; }
    public bool? IsSelfTalk { get; init; }
    public required string OverrideSpeakerTableId { get; init; }
    public required string OverrideSpeakerKey { get; init; }
    public required string OverrideSpeakerSourceString { get; init; }
    public List<string> SequenceNpcIds { get; init; } = new();
    public int ChildCount { get; init; }
}

internal sealed class EdgeRow
{
    public required string PackagePath { get; init; }
    public required string SourceExportName { get; init; }
    public required string SourceNodeType { get; init; }
    public int? SourceGraphIndex { get; init; }
    public string? SourceIdentity { get; init; }
    public int ChildIndex { get; init; }
    public int? TargetIndex { get; init; }
    public required string TargetExportName { get; init; }
    public required string TargetNodeType { get; init; }
    public string? TargetIdentity { get; init; }
    public int? EdgeIndex { get; init; }
    public required string SpeakerState { get; init; }
    public required string EdgeNamespace { get; init; }
    public required string EdgeKey { get; init; }
    public required string EdgeSourceString { get; init; }
    public required string ConditionsJson { get; init; }
    public required string EdgeDataJson { get; init; }
}

internal sealed class NodeSummary
{
    public required string ExportName { get; init; }
    public required string NodeType { get; init; }
    public int? GraphIndex { get; init; }
    public string? Identity { get; init; }
    public required string SourceString { get; init; }
    public required string OwnerName { get; init; }
    public bool? IsSelfTalk { get; init; }
    public required string OverrideSpeakerKey { get; init; }
    public required string OverrideSpeakerSourceString { get; init; }
    public List<string> SequenceNpcIds { get; init; } = new();

    public static NodeSummary From(NodeRow node) => new()
    {
        ExportName = node.ExportName,
        NodeType = node.NodeType,
        GraphIndex = node.GraphIndex,
        Identity = node.Identity,
        SourceString = node.SourceString,
        OwnerName = node.OwnerName,
        IsSelfTalk = node.IsSelfTalk,
        OverrideSpeakerKey = node.OverrideSpeakerKey,
        OverrideSpeakerSourceString = node.OverrideSpeakerSourceString,
        SequenceNpcIds = node.SequenceNpcIds
    };
}

internal sealed class EdgeSummary
{
    public required string SourceExportName { get; init; }
    public string? SourceIdentity { get; init; }
    public required string SourceText { get; init; }
    public required string TargetExportName { get; init; }
    public string? TargetIdentity { get; init; }
    public required string TargetText { get; init; }
    public required string EdgeKey { get; init; }
    public required string EdgeSourceString { get; init; }
    public required string ConditionsJson { get; init; }

    public static EdgeSummary From(EdgeRow edge, IReadOnlyDictionary<string, NodeRow> nodes)
    {
        nodes.TryGetValue(edge.SourceExportName, out var source);
        nodes.TryGetValue(edge.TargetExportName, out var target);
        return new EdgeSummary
        {
            SourceExportName = edge.SourceExportName,
            SourceIdentity = edge.SourceIdentity,
            SourceText = source?.SourceString ?? string.Empty,
            TargetExportName = edge.TargetExportName,
            TargetIdentity = edge.TargetIdentity,
            TargetText = target?.SourceString ?? string.Empty,
            EdgeKey = edge.EdgeKey,
            EdgeSourceString = edge.EdgeSourceString,
            ConditionsJson = edge.ConditionsJson
        };
    }
}

internal sealed class TargetNeighborhoodRow
{
    public required string Identity { get; init; }
    public required string PackagePath { get; init; }
    public required string DialogueName { get; init; }
    public required NodeSummary Node { get; init; }
    public List<EdgeSummary> Incoming { get; init; } = new();
    public List<EdgeSummary> Outgoing { get; init; } = new();
    public List<string> QuestTokens { get; init; } = new();
}

internal sealed class ParticipantContextRow
{
    public required string PackagePath { get; init; }
    public required string DialogueName { get; init; }
    public required string DialogueGuid { get; init; }
    public required string DialogueType { get; init; }
    public List<string> StartNodeExportNames { get; init; } = new();
    public List<string> ParticipantNames { get; init; } = new();
    public List<string> ExtraParticipants { get; init; } = new();
    public List<string> QuestTokens { get; init; } = new();
    public required string ParticipantsClassesJson { get; init; }
    public required string ParticipantsDataJson { get; init; }
}

internal sealed class NamespaceMismatchRow
{
    public required string RequestedIdentity { get; init; }
    public required string ActualIdentity { get; init; }
    public required string PackagePath { get; init; }
    public required string ExportName { get; init; }
    public required string NodeType { get; init; }
    public required string TableId { get; init; }
}

internal sealed class ProbeError
{
    public required string PackagePath { get; init; }
    public required string Stage { get; init; }
    public required string ErrorType { get; init; }
    public required string ErrorMessage { get; init; }

    public static ProbeError From(string packagePath, string stage, Exception ex) => new()
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
    public int NodeCount { get; init; }
    public int SpeechNodeCount { get; init; }
    public int EdgeCount { get; init; }
    public int TargetNeighborhoodCount { get; init; }
    public int ExactMatchedIdentityCount { get; init; }
    public int NamespaceMismatchCount { get; init; }
    public int ParticipantContextCount { get; init; }
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
