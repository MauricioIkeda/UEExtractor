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

namespace NTE.DialogueCoverageProbe;

internal static class Program
{
    private const string DefaultPathPrefix = "HT/Content/Dialogue/";

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length < 2)
            {
                PrintUsage();
                return 2;
            }

            var gameRoot = Path.GetFullPath(args[0]);
            var outputDir = Path.GetFullPath(args[1]);

            string? aesConfig = null;
            string? aesFile = null;
            string? mappingsFile = null;
            var pathPrefix = DefaultPathPrefix;

            foreach (var arg in args.Skip(2))
            {
                if (arg.StartsWith("--aes-config=", StringComparison.OrdinalIgnoreCase))
                    aesConfig = Path.GetFullPath(arg["--aes-config=".Length..].Trim('"'));
                else if (arg.StartsWith("--aes-file=", StringComparison.OrdinalIgnoreCase))
                    aesFile = Path.GetFullPath(arg["--aes-file=".Length..].Trim('"'));
                else if (arg.StartsWith("--mappings=", StringComparison.OrdinalIgnoreCase))
                    mappingsFile = Path.GetFullPath(arg["--mappings=".Length..].Trim('"'));
                else if (arg.StartsWith("--path=", StringComparison.OrdinalIgnoreCase))
                    pathPrefix = arg["--path=".Length..].Trim('"');
                else
                    throw new ArgumentException($"Unknown argument: {arg}");
            }

            ValidateInputs(gameRoot, aesConfig, aesFile, mappingsFile);
            Directory.CreateDirectory(outputDir);

            var aesKey = LoadAesKey(aesConfig, aesFile);

            Console.WriteLine("NTE Dialogue Coverage Probe");
            Console.WriteLine($"Path prefix: {pathPrefix}");
            Console.WriteLine($"Mappings: {(mappingsFile is null ? "<none>" : Path.GetFileName(mappingsFile))}");
            Console.WriteLine("Purpose: quantify structured dialogue-context coverage across the effective mounted asset view");
            Console.WriteLine();

            using var mappingScope = TemporaryFile.InstallCopy(gameRoot, "__nte_dialogue_coverage_probe.usmap", mappingsFile);
            using var aesScope = TemporaryAesCompatibility.Install(gameRoot, aesKey);
            using var reader = CreateReaderWithoutLeakingAes(gameRoot);
            var provider = GetProvider(reader);

            var packagePaths = provider.Files.Keys
                .Where(x => x.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase))
                .Where(x => x.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Console.WriteLine($"Candidate .uasset packages: {packagePaths.Count}");

            var packageRows = new List<PackageSummaryRow>();
            var references = new List<IdentityReferenceRow>();
            var errors = new List<ProbeError>();

            var parsedPackageCount = 0;
            var failedPackageCount = 0;

            for (var packageIndex = 0; packageIndex < packagePaths.Count; packageIndex++)
            {
                var packagePath = packagePaths[packageIndex];
                if (packageIndex % 50 == 0 || packageIndex == packagePaths.Count - 1)
                    Console.WriteLine($"[{packageIndex + 1}/{packagePaths.Count}] {packagePath}");

                try
                {
                    var row = InspectPackage(provider, packagePath, references);
                    packageRows.Add(row);
                    parsedPackageCount++;
                }
                catch (Exception ex)
                {
                    failedPackageCount++;
                    errors.Add(ProbeError.From(packagePath, "LoadOrInspectPackage", ex));
                    packageRows.Add(PackageSummaryRow.Failed(packagePath, ex));
                }
            }

            var identityRows = references
                .GroupBy(x => x.Identity, StringComparer.Ordinal)
                .Select(group =>
                {
                    var first = group.First();
                    return new IdentitySummaryRow
                    {
                        Identity = group.Key,
                        Namespace = first.Namespace,
                        Key = first.Key,
                        SpeechReferenceCount = group.Count(x => x.Role == "speech"),
                        ChoiceReferenceCount = group.Count(x => x.Role == "choice"),
                        OtherEdgeReferenceCount = group.Count(x => x.Role == "edge"),
                        PackageCount = group.Select(x => x.PackagePath).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                        ReferenceCount = group.Count()
                    };
                })
                .OrderBy(x => x.Identity, StringComparer.Ordinal)
                .ToList();

            var namespaceRows = references
                .GroupBy(x => x.Namespace, StringComparer.Ordinal)
                .Select(group => new NamespaceSummaryRow
                {
                    Namespace = group.Key,
                    DistinctIdentityCount = group.Select(x => x.Identity).Distinct(StringComparer.Ordinal).Count(),
                    SpeechIdentityCount = group.Where(x => x.Role == "speech").Select(x => x.Identity).Distinct(StringComparer.Ordinal).Count(),
                    ChoiceIdentityCount = group.Where(x => x.Role == "choice").Select(x => x.Identity).Distinct(StringComparer.Ordinal).Count(),
                    ReferenceCount = group.Count()
                })
                .OrderByDescending(x => x.ReferenceCount)
                .ThenBy(x => x.Namespace, StringComparer.Ordinal)
                .ToList();

            WriteJsonl(Path.Combine(outputDir, "package-summary.jsonl"), packageRows);
            WriteJsonl(Path.Combine(outputDir, "identity-references.jsonl"), references);
            WriteJsonl(Path.Combine(outputDir, "identity-summary.jsonl"), identityRows);
            WriteJsonl(Path.Combine(outputDir, "namespace-summary.jsonl"), namespaceRows);
            WriteJsonl(Path.Combine(outputDir, "errors.jsonl"), errors);

            var dialoguePackages = packageRows.Where(x => x.HasDlgDialogue).ToList();
            var speechRefs = references.Where(x => x.Role == "speech").ToList();
            var choiceRefs = references.Where(x => x.Role == "choice").ToList();

            var summary = new CoverageSummary
            {
                VirtualFileCount = provider.Files.Count,
                CandidateUassetCount = packagePaths.Count,
                ParsedPackageCount = parsedPackageCount,
                FailedPackageCount = failedPackageCount,
                DlgDialoguePackageCount = dialoguePackages.Count,
                NonDialoguePackageCount = packageRows.Count(x => x.ParseSucceeded && !x.HasDlgDialogue),
                TotalNodeCount = dialoguePackages.Sum(x => x.NodeCount),
                SpeechNodeCount = dialoguePackages.Sum(x => x.SpeechNodeCount),
                EndNodeCount = dialoguePackages.Sum(x => x.EndNodeCount),
                StartNodeCount = dialoguePackages.Sum(x => x.StartNodeCount),
                EdgeCount = dialoguePackages.Sum(x => x.EdgeCount),
                GenericNextEdgeCount = dialoguePackages.Sum(x => x.GenericNextEdgeCount),
                GenericFinishEdgeCount = dialoguePackages.Sum(x => x.GenericFinishEdgeCount),
                UnlabeledEdgeCount = dialoguePackages.Sum(x => x.UnlabeledEdgeCount),
                LocalizedChoiceEdgeCount = dialoguePackages.Sum(x => x.LocalizedChoiceEdgeCount),
                SpeechIdentityReferenceCount = speechRefs.Count,
                DistinctSpeechIdentityCount = speechRefs.Select(x => x.Identity).Distinct(StringComparer.Ordinal).Count(),
                ChoiceIdentityReferenceCount = choiceRefs.Count,
                DistinctChoiceIdentityCount = choiceRefs.Select(x => x.Identity).Distinct(StringComparer.Ordinal).Count(),
                DistinctContextIdentityCount = references.Select(x => x.Identity).Distinct(StringComparer.Ordinal).Count(),
                MultiReferenceIdentityCount = identityRows.Count(x => x.ReferenceCount > 1),
                MultiPackageIdentityCount = identityRows.Count(x => x.PackageCount > 1),
                DisconnectedNodeCount = dialoguePackages.Sum(x => x.DisconnectedNodeCount),
                DisconnectedLocalizedSpeechCount = dialoguePackages.Sum(x => x.DisconnectedLocalizedSpeechCount),
                SpeechWithOwnerNameCount = dialoguePackages.Sum(x => x.SpeechWithOwnerNameCount),
                SpeechWithOverrideSpeakerCount = dialoguePackages.Sum(x => x.SpeechWithOverrideSpeakerCount),
                SpeechWithSelfTalkTrueCount = dialoguePackages.Sum(x => x.SpeechWithSelfTalkTrueCount),
                SpeechWithSequenceNpcCount = dialoguePackages.Sum(x => x.SpeechWithSequenceNpcCount),
                ErrorCount = errors.Count,
                MappingsProvided = mappingsFile is not null,
                PathPrefix = pathPrefix
            };

            File.WriteAllText(
                Path.Combine(outputDir, "summary.json"),
                System.Text.Json.JsonSerializer.Serialize(summary, JsonWriteIndented),
                new UTF8Encoding(false));

            Console.WriteLine();
            Console.WriteLine($"Dialogue packages: {summary.DlgDialoguePackageCount}/{summary.CandidateUassetCount}");
            Console.WriteLine($"Parse failures: {summary.FailedPackageCount}");
            Console.WriteLine($"Nodes: {summary.TotalNodeCount} ({summary.SpeechNodeCount} speech)");
            Console.WriteLine($"Edges: {summary.EdgeCount}");
            Console.WriteLine($"Speech identities: {summary.DistinctSpeechIdentityCount} distinct / {summary.SpeechIdentityReferenceCount} refs");
            Console.WriteLine($"Choice identities: {summary.DistinctChoiceIdentityCount} distinct / {summary.ChoiceIdentityReferenceCount} refs");
            Console.WriteLine($"All contextual identities: {summary.DistinctContextIdentityCount}");
            Console.WriteLine($"Multi-package identities: {summary.MultiPackageIdentityCount}");
            Console.WriteLine($"Disconnected localized speech nodes: {summary.DisconnectedLocalizedSpeechCount}");
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

    private static PackageSummaryRow InspectPackage(DefaultFileProvider provider, string packagePath, List<IdentityReferenceRow> references)
    {
        var package = provider.LoadPackage(packagePath);
        var exports = package.GetExports().ToList();

        var dialogueExport = exports.FirstOrDefault(x => x.ExportType.Equals("DlgDialogue", StringComparison.OrdinalIgnoreCase));
        if (dialogueExport is null)
        {
            return new PackageSummaryRow
            {
                PackagePath = packagePath,
                ParseSucceeded = true,
                HasDlgDialogue = false
            };
        }

        var parsedNodes = new List<ParsedNode>();
        JObject? dialogueRoot = null;

        foreach (var export in exports)
        {
            if (export.ExportType.Equals("DlgDialogue", StringComparison.OrdinalIgnoreCase))
            {
                if (ReferenceEquals(export, dialogueExport))
                    dialogueRoot = JObject.Parse(JsonConvert.SerializeObject(export, Formatting.None));
                continue;
            }

            if (!export.ExportType.StartsWith("DlgNode_", StringComparison.OrdinalIgnoreCase))
                continue;

            var json = JObject.Parse(JsonConvert.SerializeObject(export, Formatting.None));
            parsedNodes.Add(new ParsedNode(export.Name.ToString(), export.ExportType, json));
        }

        dialogueRoot ??= JObject.Parse(JsonConvert.SerializeObject(dialogueExport, Formatting.None));
        var rootProps = dialogueRoot["Properties"] as JObject ?? new JObject();
        var nodeRefs = rootProps["Nodes"] as JArray ?? new JArray();
        var startRefs = rootProps["StartNodes"] as JArray ?? new JArray();
        var graphIndexByExportName = BuildGraphIndexByExportName(nodeRefs);
        var startNames = startRefs
            .OfType<JObject>()
            .Select(ReferenceExportName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.Ordinal);

        var nodeByName = parsedNodes.ToDictionary(x => x.ExportName, StringComparer.Ordinal);
        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var edgeCount = 0;
        var nextCount = 0;
        var finishCount = 0;
        var unlabeledCount = 0;
        var choiceCount = 0;

        var speechNodeCount = 0;
        var endNodeCount = 0;
        var startNodeCount = 0;
        var localizedSpeechCount = 0;
        var speechWithOwner = 0;
        var speechWithOverride = 0;
        var speechSelfTalkTrue = 0;
        var speechWithSequenceNpc = 0;

        foreach (var node in parsedNodes)
        {
            if (node.NodeType.Equals("DlgNode_Speech", StringComparison.OrdinalIgnoreCase)) speechNodeCount++;
            else if (node.NodeType.Equals("DlgNode_End", StringComparison.OrdinalIgnoreCase)) endNodeCount++;
            else if (node.NodeType.Equals("DlgNode_Start", StringComparison.OrdinalIgnoreCase)) startNodeCount++;

            var props = node.Json["Properties"] as JObject ?? new JObject();
            graphIndexByExportName.TryGetValue(node.ExportName, out var graphIndexValue);
            int? graphIndex = graphIndexByExportName.ContainsKey(node.ExportName) ? graphIndexValue : null;

            if (node.NodeType.Equals("DlgNode_Speech", StringComparison.OrdinalIgnoreCase))
            {
                var text = props["Text"] as JObject;
                var tableId = text?["TableId"]?.Value<string>() ?? string.Empty;
                var ns = NamespaceFromTableId(tableId);
                var key = text?["Key"]?.Value<string>() ?? string.Empty;

                if (!string.IsNullOrEmpty(ns) && !string.IsNullOrEmpty(key))
                {
                    localizedSpeechCount++;
                    var owner = props["OwnerName"]?.Value<string>() ?? string.Empty;
                    var overrideSpeaker = props["OverrideSpeakerName"] as JObject;
                    var overrideKey = overrideSpeaker?["Key"]?.Value<string>() ?? string.Empty;
                    var selfTalk = props["bIsSelfTalk"]?.Value<bool?>();
                    var sequenceNpcIds = (props["SequenceActorConfigs"] as JArray)?
                        .OfType<JObject>()
                        .Select(x => x["NpcId"]?.Value<string>() ?? string.Empty)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.Ordinal)
                        .ToList() ?? new List<string>();

                    if (!string.IsNullOrEmpty(owner)) speechWithOwner++;
                    if (!string.IsNullOrEmpty(overrideKey)) speechWithOverride++;
                    if (selfTalk is true) speechSelfTalkTrue++;
                    if (sequenceNpcIds.Count > 0) speechWithSequenceNpc++;

                    references.Add(new IdentityReferenceRow
                    {
                        Identity = ComposeIdentity(ns, key),
                        Namespace = ns,
                        Key = key,
                        Role = "speech",
                        PackagePath = packagePath,
                        ExportName = node.ExportName,
                        GraphIndex = graphIndex,
                        OwnerName = owner,
                        OverrideSpeakerKey = overrideKey,
                        IsSelfTalk = selfTalk,
                        SequenceNpcIds = sequenceNpcIds
                    });
                }
            }

            var children = props["Children"] as JArray;
            if (children is null)
                continue;

            foreach (var child in children.OfType<JObject>())
            {
                edgeCount++;
                var targetIndex = child["TargetIndex"]?.Value<int?>();
                var targetName = ResolveTargetExportName(targetIndex, nodeRefs);
                if (!string.IsNullOrEmpty(targetName))
                {
                    if (!adjacency.TryGetValue(node.ExportName, out var targets))
                    {
                        targets = new List<string>();
                        adjacency[node.ExportName] = targets;
                    }
                    targets.Add(targetName);
                }

                var edgeText = child["Text"] as JObject;
                var edgeTableId = edgeText?["TableId"]?.Value<string>() ?? string.Empty;
                var edgeNs = edgeText?["Namespace"]?.Value<string>() ?? NamespaceFromTableId(edgeTableId);
                var edgeKey = edgeText?["Key"]?.Value<string>() ?? string.Empty;
                var edgeSource = edgeText?["SourceString"]?.Value<string>() ?? edgeText?["CultureInvariantString"]?.Value<string>() ?? string.Empty;

                if (edgeNs == "DlgSystem" && edgeKey == "edge_next")
                    nextCount++;
                else if (edgeNs == "DlgSystem" && edgeKey == "edge_finish")
                    finishCount++;
                else if (string.IsNullOrEmpty(edgeNs) && string.IsNullOrEmpty(edgeKey) && string.IsNullOrEmpty(edgeSource))
                    unlabeledCount++;
                else if (!string.IsNullOrEmpty(edgeNs) && !string.IsNullOrEmpty(edgeKey) && !edgeNs.Equals("DlgSystem", StringComparison.Ordinal))
                {
                    choiceCount++;
                    references.Add(new IdentityReferenceRow
                    {
                        Identity = ComposeIdentity(edgeNs, edgeKey),
                        Namespace = edgeNs,
                        Key = edgeKey,
                        Role = "choice",
                        PackagePath = packagePath,
                        ExportName = node.ExportName,
                        GraphIndex = graphIndex,
                        TargetExportName = targetName
                    });
                }
                else if (!string.IsNullOrEmpty(edgeNs) && !string.IsNullOrEmpty(edgeKey))
                {
                    references.Add(new IdentityReferenceRow
                    {
                        Identity = ComposeIdentity(edgeNs, edgeKey),
                        Namespace = edgeNs,
                        Key = edgeKey,
                        Role = "edge",
                        PackagePath = packagePath,
                        ExportName = node.ExportName,
                        GraphIndex = graphIndex,
                        TargetExportName = targetName
                    });
                }
            }
        }

        var reachable = ComputeReachable(startNames, adjacency);
        var disconnectedNodes = parsedNodes.Where(x => !reachable.Contains(x.ExportName)).ToList();
        var disconnectedLocalizedSpeech = disconnectedNodes.Count(node =>
        {
            if (!node.NodeType.Equals("DlgNode_Speech", StringComparison.OrdinalIgnoreCase)) return false;
            var text = (node.Json["Properties"] as JObject)?["Text"] as JObject;
            return !string.IsNullOrEmpty(NamespaceFromTableId(text?["TableId"]?.Value<string>() ?? string.Empty))
                && !string.IsNullOrEmpty(text?["Key"]?.Value<string>() ?? string.Empty);
        });

        return new PackageSummaryRow
        {
            PackagePath = packagePath,
            ParseSucceeded = true,
            HasDlgDialogue = true,
            DialogueName = rootProps["Name"]?.Value<string>() ?? dialogueExport.Name.ToString(),
            DialogueType = rootProps["DialogueType"]?.Value<string>() ?? string.Empty,
            NodeCount = parsedNodes.Count,
            SpeechNodeCount = speechNodeCount,
            EndNodeCount = endNodeCount,
            StartNodeCount = startNodeCount,
            EdgeCount = edgeCount,
            GenericNextEdgeCount = nextCount,
            GenericFinishEdgeCount = finishCount,
            UnlabeledEdgeCount = unlabeledCount,
            LocalizedChoiceEdgeCount = choiceCount,
            LocalizedSpeechNodeCount = localizedSpeechCount,
            DisconnectedNodeCount = disconnectedNodes.Count,
            DisconnectedLocalizedSpeechCount = disconnectedLocalizedSpeech,
            SpeechWithOwnerNameCount = speechWithOwner,
            SpeechWithOverrideSpeakerCount = speechWithOverride,
            SpeechWithSelfTalkTrueCount = speechSelfTalkTrue,
            SpeechWithSequenceNpcCount = speechWithSequenceNpc,
            ParticipantNames = ExtractParticipantNames(rootProps),
            QuestTokens = ExtractQuestTokens(rootProps)
        };
    }

    private static Dictionary<string, int> BuildGraphIndexByExportName(JArray nodeRefs)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < nodeRefs.Count; i++)
        {
            if (nodeRefs[i] is not JObject obj) continue;
            var name = ReferenceExportName(obj);
            if (!string.IsNullOrEmpty(name)) result[name] = i;
        }
        return result;
    }

    private static string ResolveTargetExportName(int? targetIndex, JArray nodeRefs)
    {
        if (!targetIndex.HasValue || targetIndex.Value < 0 || targetIndex.Value >= nodeRefs.Count)
            return string.Empty;
        return nodeRefs[targetIndex.Value] is JObject reference ? ReferenceExportName(reference) : string.Empty;
    }

    private static string ReferenceExportName(JObject reference)
    {
        var objectName = reference["ObjectName"]?.Value<string>() ?? string.Empty;
        if (string.IsNullOrEmpty(objectName)) return string.Empty;

        var withOuter = Regex.Match(objectName, @":([^:']+)'$");
        if (withOuter.Success) return withOuter.Groups[1].Value;

        var withoutOuter = Regex.Match(objectName, @"'([^']+)'$");
        return withoutOuter.Success ? withoutOuter.Groups[1].Value : objectName;
    }

    private static HashSet<string> ComputeReachable(IEnumerable<string> startNames, IReadOnlyDictionary<string, List<string>> adjacency)
    {
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var start in startNames)
        {
            if (reachable.Add(start)) queue.Enqueue(start);
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!adjacency.TryGetValue(current, out var targets)) continue;
            foreach (var target in targets)
            {
                if (reachable.Add(target)) queue.Enqueue(target);
            }
        }
        return reachable;
    }

    private static List<string> ExtractParticipantNames(JObject rootProps)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (rootProps["ParticipantsClasses"] is JArray classes)
        {
            foreach (var entry in classes.OfType<JObject>())
            {
                var name = entry["ParticipantName"]?.Value<string>();
                if (!string.IsNullOrWhiteSpace(name)) result.Add(name);
            }
        }
        if (rootProps["ParticipantsData"] is JArray data)
        {
            foreach (var entry in data.OfType<JObject>())
            {
                var name = entry["Key"]?.Value<string>();
                if (!string.IsNullOrWhiteSpace(name)) result.Add(name);
            }
        }
        return result.OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    private static List<string> ExtractQuestTokens(JObject rootProps)
    {
        var text = rootProps["ParticipantsData"]?.ToString(Formatting.None) ?? string.Empty;
        if (string.IsNullOrEmpty(text)) return new List<string>();
        return Regex.Matches(text, @"(?<![A-Za-z0-9_])[A-Za-z]\d{6}(?:_\d+)?", RegexOptions.CultureInvariant)
            .Select(x => x.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NamespaceFromTableId(string tableId)
    {
        if (string.IsNullOrWhiteSpace(tableId)) return string.Empty;
        var slash = tableId.LastIndexOf('/');
        var leaf = slash >= 0 ? tableId[(slash + 1)..] : tableId;
        var dot = leaf.IndexOf('.');
        return dot >= 0 ? leaf[..dot] : leaf;
    }

    private static string ComposeIdentity(string ns, string key)
        => string.IsNullOrEmpty(ns) ? key : $"{ns}::{key}";

    private static string LoadAesKey(string? aesConfig, string? aesFile)
    {
        if (aesConfig is null && aesFile is null) return string.Empty;
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

    private static void ValidateInputs(string gameRoot, string? aesConfig, string? aesFile, string? mappings)
    {
        if (!Directory.Exists(gameRoot)) throw new DirectoryNotFoundException($"Game root not found: {gameRoot}");
        if (aesConfig is not null && aesFile is not null) throw new ArgumentException("Use only one of --aes-config or --aes-file.");
        if (aesConfig is not null && !File.Exists(aesConfig)) throw new FileNotFoundException("AES config not found.", aesConfig);
        if (aesFile is not null && !File.Exists(aesFile)) throw new FileNotFoundException("AES file not found.", aesFile);
        if (mappings is not null && !File.Exists(mappings)) throw new FileNotFoundException("Mappings file not found.", mappings);
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
        Console.WriteLine("  NTE.DialogueCoverageProbe <gameRoot> <outputDir> --mappings=<file.usmap> [--aes-config=<path> | --aes-file=<path>] [--path=HT/Content/Dialogue/]");
    }
}

internal sealed record ParsedNode(string ExportName, string NodeType, JObject Json);

internal sealed class PackageSummaryRow
{
    public required string PackagePath { get; init; }
    public bool ParseSucceeded { get; init; }
    public bool HasDlgDialogue { get; init; }
    public string DialogueName { get; init; } = string.Empty;
    public string DialogueType { get; init; } = string.Empty;
    public int NodeCount { get; init; }
    public int SpeechNodeCount { get; init; }
    public int EndNodeCount { get; init; }
    public int StartNodeCount { get; init; }
    public int EdgeCount { get; init; }
    public int GenericNextEdgeCount { get; init; }
    public int GenericFinishEdgeCount { get; init; }
    public int UnlabeledEdgeCount { get; init; }
    public int LocalizedChoiceEdgeCount { get; init; }
    public int LocalizedSpeechNodeCount { get; init; }
    public int DisconnectedNodeCount { get; init; }
    public int DisconnectedLocalizedSpeechCount { get; init; }
    public int SpeechWithOwnerNameCount { get; init; }
    public int SpeechWithOverrideSpeakerCount { get; init; }
    public int SpeechWithSelfTalkTrueCount { get; init; }
    public int SpeechWithSequenceNpcCount { get; init; }
    public List<string> ParticipantNames { get; init; } = new();
    public List<string> QuestTokens { get; init; } = new();
    public string ErrorType { get; init; } = string.Empty;
    public string ErrorMessage { get; init; } = string.Empty;

    public static PackageSummaryRow Failed(string packagePath, Exception ex) => new()
    {
        PackagePath = packagePath,
        ParseSucceeded = false,
        HasDlgDialogue = false,
        ErrorType = ex.GetType().FullName ?? ex.GetType().Name,
        ErrorMessage = ex.Message
    };
}

internal sealed class IdentityReferenceRow
{
    public required string Identity { get; init; }
    public required string Namespace { get; init; }
    public required string Key { get; init; }
    public required string Role { get; init; }
    public required string PackagePath { get; init; }
    public required string ExportName { get; init; }
    public int? GraphIndex { get; init; }
    public string TargetExportName { get; init; } = string.Empty;
    public string OwnerName { get; init; } = string.Empty;
    public string OverrideSpeakerKey { get; init; } = string.Empty;
    public bool? IsSelfTalk { get; init; }
    public List<string> SequenceNpcIds { get; init; } = new();
}

internal sealed class IdentitySummaryRow
{
    public required string Identity { get; init; }
    public required string Namespace { get; init; }
    public required string Key { get; init; }
    public int SpeechReferenceCount { get; init; }
    public int ChoiceReferenceCount { get; init; }
    public int OtherEdgeReferenceCount { get; init; }
    public int PackageCount { get; init; }
    public int ReferenceCount { get; init; }
}

internal sealed class NamespaceSummaryRow
{
    public required string Namespace { get; init; }
    public int DistinctIdentityCount { get; init; }
    public int SpeechIdentityCount { get; init; }
    public int ChoiceIdentityCount { get; init; }
    public int ReferenceCount { get; init; }
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

internal sealed class CoverageSummary
{
    public int VirtualFileCount { get; init; }
    public int CandidateUassetCount { get; init; }
    public int ParsedPackageCount { get; init; }
    public int FailedPackageCount { get; init; }
    public int DlgDialoguePackageCount { get; init; }
    public int NonDialoguePackageCount { get; init; }
    public int TotalNodeCount { get; init; }
    public int SpeechNodeCount { get; init; }
    public int EndNodeCount { get; init; }
    public int StartNodeCount { get; init; }
    public int EdgeCount { get; init; }
    public int GenericNextEdgeCount { get; init; }
    public int GenericFinishEdgeCount { get; init; }
    public int UnlabeledEdgeCount { get; init; }
    public int LocalizedChoiceEdgeCount { get; init; }
    public int SpeechIdentityReferenceCount { get; init; }
    public int DistinctSpeechIdentityCount { get; init; }
    public int ChoiceIdentityReferenceCount { get; init; }
    public int DistinctChoiceIdentityCount { get; init; }
    public int DistinctContextIdentityCount { get; init; }
    public int MultiReferenceIdentityCount { get; init; }
    public int MultiPackageIdentityCount { get; init; }
    public int DisconnectedNodeCount { get; init; }
    public int DisconnectedLocalizedSpeechCount { get; init; }
    public int SpeechWithOwnerNameCount { get; init; }
    public int SpeechWithOverrideSpeakerCount { get; init; }
    public int SpeechWithSelfTalkTrueCount { get; init; }
    public int SpeechWithSequenceNpcCount { get; init; }
    public int ErrorCount { get; init; }
    public bool MappingsProvided { get; init; }
    public string PathPrefix { get; init; } = string.Empty;
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
