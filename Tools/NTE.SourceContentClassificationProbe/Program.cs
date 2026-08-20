using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Localization;
using CUE4Parse.UE4.VirtualFileSystem;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NTE.SourceContentClassificationProbe;

internal static class Program
{
    private const int SamplesPerClass = 40;

    private static readonly Regex CjkLikeRegex = new(
        "[\\u3400-\\u4DBF\\u4E00-\\u9FFF\\uF900-\\uFAFF\\u3040-\\u30FF\\uAC00-\\uD7AF]",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex NumericOnlyRegex = new(
        @"^[\s\p{N}\p{P}\p{S}]+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex IdentifierLikeRegex = new(
        @"^[A-Za-z][A-Za-z0-9_:.\\/\-]*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex GuidRegex = new(
        @"^\{?[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex HexRegex = new(
        @"^(?:0x)?[0-9A-Fa-f]{16,}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex StructuralTokenRegex = new(
        @"\{[^{}\r\n]+\}|<[^>\r\n]+>|\[[^\]\r\n]+\]|%(?:\d+\$)?[A-Za-z]",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex GenderMarkerRegex = new(
        @"<male=>|<male>|<female=>|<female>",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly string[] ExactGenderMarkers = ["<male=>", "<male>", "<female=>", "<female>"];

    private static readonly string[] UiHints =
    [
        "ui", "setting", "option", "menu", "button", "hud", "widget", "panel", "screen",
        "window", "tooltip", "inventory", "profile", "avatar", "camera", "selfie", "map",
        "shop", "confirm", "cancel", "close", "back", "title", "guide", "language", "resolution"
    ];

    private static readonly string[] MessageHints =
    [
        "mail", "message", "msg", "sms", "chat", "phone", "contact", "inbox", "letter",
        "friend", "secret_test", "secrettest"
    ];

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
            var dialogueSummaryPath = Path.GetFullPath(args[1]);
            var outputDir = Path.GetFullPath(args[2]);
            string? aesConfig = null;
            string? aesFile = null;

            foreach (var arg in args.Skip(3))
            {
                if (arg.StartsWith("--aes-config=", StringComparison.OrdinalIgnoreCase))
                    aesConfig = Path.GetFullPath(arg["--aes-config=".Length..].Trim('"'));
                else if (arg.StartsWith("--aes-file=", StringComparison.OrdinalIgnoreCase))
                    aesFile = Path.GetFullPath(arg["--aes-file=".Length..].Trim('"'));
                else
                    throw new ArgumentException($"Unknown argument: {arg}");
            }

            ValidateInputs(gameRoot, dialogueSummaryPath, aesConfig, aesFile);
            Directory.CreateDirectory(outputDir);

            Console.WriteLine("NTE Source Content Classification Probe 020A");
            Console.WriteLine("Read-only: classify every effective ES/EN localization identity using evidence signals.");
            Console.WriteLine("Important: classifications/dispositions are provisional research output, not permanent Studio policy.");
            Console.WriteLine();

            var aesKey = LoadAesKey(aesConfig, aesFile);
            using var aesScope = TemporaryAesCompatibility.Install(gameRoot, aesKey);
            using var reader = CreateReaderWithoutLeakingAes(gameRoot);
            var provider = GetProvider(reader);

            var es = LoadCulture(provider, "es");
            var en = LoadCulture(provider, "en");
            var dialogue = ReadDialogueEvidence(dialogueSummaryPath);

            var esMap = es.Entries.ToDictionary(x => (x.Namespace, x.Key));
            var enMap = en.Entries.ToDictionary(x => (x.Namespace, x.Key));
            var missingInEn = esMap.Keys.Except(enMap.Keys).ToList();
            var missingInEs = enMap.Keys.Except(esMap.Keys).ToList();

            var rows = new List<ClassificationRow>(es.Entries.Count);
            foreach (var esEntry in es.Entries.OrderBy(x => x.Namespace, StringComparer.Ordinal).ThenBy(x => x.Key, StringComparer.Ordinal))
            {
                enMap.TryGetValue((esEntry.Namespace, esEntry.Key), out var enEntry);
                var identity = Identity(esEntry.Namespace, esEntry.Key);
                dialogue.TryGetValue(identity, out var dialogueEvidence);
                rows.Add(Classify(esEntry, enEntry?.Text ?? string.Empty, dialogueEvidence));
            }

            WriteJsonl(Path.Combine(outputDir, "classification.jsonl"), rows);
            WriteJsonl(Path.Combine(outputDir, "translate-candidates.jsonl"), rows.Where(x => x.ProvisionalDisposition == "translate_candidate"));
            WriteJsonl(Path.Combine(outputDir, "review-required.jsonl"), rows.Where(x => x.ProvisionalDisposition == "review_required"));
            WriteJsonl(Path.Combine(outputDir, "exclude-candidates.jsonl"), rows.Where(x => x.ProvisionalDisposition == "exclude_candidate"));

            var namespaceRows = rows
                .GroupBy(x => x.Namespace, StringComparer.Ordinal)
                .Select(g => new NamespaceSummary
                {
                    Namespace = g.Key,
                    IdentityCount = g.Count(),
                    NarrativeProvenCount = g.Count(x => x.DialogueProven),
                    TranslateCandidateCount = g.Count(x => x.ProvisionalDisposition == "translate_candidate"),
                    ReviewRequiredCount = g.Count(x => x.ProvisionalDisposition == "review_required"),
                    ExcludeCandidateCount = g.Count(x => x.ProvisionalDisposition == "exclude_candidate"),
                    CjkInEnCount = g.Count(x => x.CjkInEn),
                    CjkInEsCount = g.Count(x => x.CjkInEs),
                    StructuredCount = g.Count(x => x.HasRecognizedStructure),
                    PrimaryClassCounts = g.GroupBy(x => x.PrimaryClass, StringComparer.Ordinal)
                        .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal)
                })
                .OrderByDescending(x => x.IdentityCount)
                .ThenBy(x => x.Namespace, StringComparer.Ordinal)
                .ToList();
            WriteJsonl(Path.Combine(outputDir, "namespace-summary.jsonl"), namespaceRows);

            var samples = rows
                .GroupBy(x => x.PrimaryClass, StringComparer.Ordinal)
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderBy(x => x.Identity, StringComparer.Ordinal).Take(SamplesPerClass).Select(ToSample).ToList(),
                    StringComparer.Ordinal);
            WriteJson(Path.Combine(outputDir, "samples-by-primary-class.json"), samples);

            var signalCounts = rows
                .SelectMany(x => x.Signals)
                .GroupBy(x => x, StringComparer.Ordinal)
                .OrderByDescending(x => x.Count())
                .ThenBy(x => x.Key, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);

            var summary = new ProbeSummary
            {
                Success = missingInEn.Count == 0 && missingInEs.Count == 0 && rows.Count == es.Entries.Count,
                EsVirtualPath = es.VirtualPath,
                EsContainer = es.ContainerName,
                EsReadOrder = es.ReadOrder,
                EnVirtualPath = en.VirtualPath,
                EnContainer = en.ContainerName,
                EnReadOrder = en.ReadOrder,
                EsEntryCount = es.Entries.Count,
                EnEntryCount = en.Entries.Count,
                ClassifiedIdentityCount = rows.Count,
                MissingInEnCount = missingInEn.Count,
                MissingInEsCount = missingInEs.Count,
                DialogueEvidenceIdentityCount = dialogue.Count,
                DialogueJoinedIdentityCount = rows.Count(x => x.DialogueProven),
                PrimaryClassCounts = rows.GroupBy(x => x.PrimaryClass, StringComparer.Ordinal)
                    .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal),
                ProvisionalDispositionCounts = rows.GroupBy(x => x.ProvisionalDisposition, StringComparer.Ordinal)
                    .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal),
                SignalCounts = signalCounts,
                CjkInEnCount = rows.Count(x => x.CjkInEn),
                CjkInEsCount = rows.Count(x => x.CjkInEs),
                StructuredCount = rows.Count(x => x.HasRecognizedStructure),
                SourceAnomalySignalCount = rows.Count(x => x.SourceAnomalySignal),
                MethodNote = "020A is an inventory. Proven dialogue membership comes from Run 007 asset references. UI/message/technical classifications are heuristics. Provisional disposition is not a permanent exclusion/translation policy."
            };
            WriteJson(Path.Combine(outputDir, "summary.json"), summary);

            Console.WriteLine($"Effective ES: {es.ContainerName} readOrder={es.ReadOrder} entries={es.Entries.Count}");
            Console.WriteLine($"Effective EN: {en.ContainerName} readOrder={en.ReadOrder} entries={en.Entries.Count}");
            Console.WriteLine($"Alignment missing ES->EN / EN->ES: {missingInEn.Count} / {missingInEs.Count}");
            Console.WriteLine($"Dialogue evidence joined: {summary.DialogueJoinedIdentityCount}");
            Console.WriteLine();
            Console.WriteLine("Primary classes:");
            foreach (var pair in summary.PrimaryClassCounts.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal))
                Console.WriteLine($"- {pair.Key}: {pair.Value}");
            Console.WriteLine();
            Console.WriteLine("Provisional dispositions:");
            foreach (var pair in summary.ProvisionalDispositionCounts.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal))
                Console.WriteLine($"- {pair.Key}: {pair.Value}");
            Console.WriteLine();
            Console.WriteLine($"CJK-like in EN: {summary.CjkInEnCount}");
            Console.WriteLine($"CJK-like in ES: {summary.CjkInEsCount}");
            Console.WriteLine($"Recognized structured strings: {summary.StructuredCount}");
            Console.WriteLine($"Source-anomaly signals: {summary.SourceAnomalySignalCount}");
            Console.WriteLine($"Probe success: {summary.Success}");
            Console.WriteLine($"Reports: {outputDir}");
            return summary.Success ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex);
            return 1;
        }
    }

    private static ClassificationRow Classify(LocresEntry es, string enText, DialogueEvidence? dialogue)
    {
        var identity = Identity(es.Namespace, es.Key);
        var combinedId = (identity + " " + es.Namespace + " " + es.Key).ToLowerInvariant();
        var esText = es.Text ?? string.Empty;
        enText ??= string.Empty;
        var signals = new HashSet<string>(StringComparer.Ordinal);

        var emptyEs = string.IsNullOrWhiteSpace(esText);
        var emptyEn = string.IsNullOrWhiteSpace(enText);
        var cjkEs = CjkLikeRegex.IsMatch(esText);
        var cjkEn = CjkLikeRegex.IsMatch(enText);
        var sentinel = IsSentinel(esText) || IsSentinel(enText);
        var numericOnly = !emptyEs && NumericOnlyRegex.IsMatch(esText) && !esText.Any(char.IsLetter);
        var pathLike = IsPathLike(esText);
        var urlLike = IsUrlLike(esText);
        var guidLike = GuidRegex.IsMatch(esText.Trim());
        var hexLike = HexRegex.IsMatch(esText.Trim());
        var identifierLike = IsIdentifierLike(esText);
        var hasLetters = esText.Any(char.IsLetter) || enText.Any(char.IsLetter);
        var hasStructure = StructuralTokenRegex.IsMatch(esText);
        var sourceAnomaly = HasSourceAnomalySignal(esText);
        var uiLikely = IsUiLikely(combinedId);
        var messageLikely = IsMessageLikely(combinedId);
        var dialogueProven = dialogue is not null;

        if (emptyEs) signals.Add("empty_es");
        if (emptyEn) signals.Add("empty_en");
        if (cjkEs) signals.Add("cjk_in_es");
        if (cjkEn) signals.Add("cjk_in_en");
        if (sentinel) signals.Add("sentinel");
        if (numericOnly) signals.Add("numeric_only");
        if (pathLike) signals.Add("path_like");
        if (urlLike) signals.Add("url_like");
        if (guidLike) signals.Add("guid_like");
        if (hexLike) signals.Add("hex_like");
        if (identifierLike) signals.Add("identifier_like");
        if (hasStructure) signals.Add("recognized_structure");
        if (sourceAnomaly) signals.Add("source_anomaly");
        if (uiLikely) signals.Add("ui_likely");
        if (messageLikely) signals.Add("message_likely");
        if (dialogueProven) signals.Add("dialogue_proven");
        if (dialogue?.SpeechReferenceCount > 0) signals.Add("dialogue_speech");
        if (dialogue?.ChoiceReferenceCount > 0) signals.Add("dialogue_choice");
        if (string.Equals(esText, enText, StringComparison.Ordinal) && !emptyEs) signals.Add("es_equals_en");

        var primaryClass = DeterminePrimaryClass(
            emptyEs, sentinel, dialogueProven, messageLikely, uiLikely,
            pathLike, urlLike, guidLike, hexLike, numericOnly, identifierLike,
            cjkEn || cjkEs, hasLetters);

        var (disposition, confidence, reason) = DetermineDisposition(
            emptyEs, sentinel, pathLike, urlLike, guidLike, hexLike, numericOnly,
            sourceAnomaly, cjkEn, cjkEs, identifierLike, dialogueProven, messageLikely, uiLikely, hasLetters);

        return new ClassificationRow
        {
            Identity = identity,
            Namespace = es.Namespace,
            Key = es.Key,
            EsText = esText,
            EnText = enText,
            PrimaryClass = primaryClass,
            ProvisionalDisposition = disposition,
            DispositionConfidence = confidence,
            DispositionReason = reason,
            Signals = signals.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            DialogueProven = dialogueProven,
            SpeechReferenceCount = dialogue?.SpeechReferenceCount ?? 0,
            ChoiceReferenceCount = dialogue?.ChoiceReferenceCount ?? 0,
            OtherEdgeReferenceCount = dialogue?.OtherEdgeReferenceCount ?? 0,
            DialoguePackageCount = dialogue?.PackageCount ?? 0,
            CjkInEs = cjkEs,
            CjkInEn = cjkEn,
            HasRecognizedStructure = hasStructure,
            SourceAnomalySignal = sourceAnomaly,
            UiLikely = uiLikely,
            MessageLikely = messageLikely,
            NumericOnly = numericOnly,
            IdentifierLike = identifierLike,
            PathLike = pathLike,
            UrlLike = urlLike
        };
    }

    private static string DeterminePrimaryClass(
        bool emptyEs, bool sentinel, bool dialogue, bool message, bool ui,
        bool path, bool url, bool guid, bool hex, bool numeric, bool identifier,
        bool cjk, bool hasLetters)
    {
        if (emptyEs) return "empty";
        if (sentinel) return "sentinel";
        if (dialogue) return "narrative_proven";
        if (message) return "message_like";
        if (ui) return "ui_like";
        if (path || url || guid || hex || numeric) return "technical_high_confidence";
        if (cjk) return "cjk_or_unlocalized";
        if (identifier) return "identifier_like";
        if (hasLetters) return "human_unclassified";
        return "other_unclassified";
    }

    private static (string Disposition, string Confidence, string Reason) DetermineDisposition(
        bool emptyEs, bool sentinel, bool path, bool url, bool guid, bool hex, bool numeric,
        bool sourceAnomaly, bool cjkEn, bool cjkEs, bool identifier, bool dialogue, bool message, bool ui, bool hasLetters)
    {
        if (emptyEs) return ("exclude_candidate", "high", "empty_es");
        if (sentinel) return ("exclude_candidate", "high", "sentinel");
        if (path || url || guid || hex) return ("exclude_candidate", "high", "technical_literal");
        if (numeric) return ("exclude_candidate", "medium", "numeric_only");
        if (sourceAnomaly) return ("review_required", "high", "source_anomaly_signal");
        if (cjkEn) return ("review_required", "high", "cjk_present_in_english_anchor");
        if (cjkEs) return ("review_required", "medium", "cjk_present_in_spanish_source");
        if (identifier) return ("review_required", "medium", "identifier_like_text");
        if (dialogue) return ("translate_candidate", "high", "proven_dialogue_asset_reference");
        if (message) return ("translate_candidate", "medium", "message_surface_heuristic");
        if (ui) return ("translate_candidate", "medium", "ui_surface_heuristic");
        if (hasLetters) return ("translate_candidate", "low", "human_text_without_stronger_context");
        return ("review_required", "low", "unclassified_nonhuman_shape");
    }

    private static bool IsSentinel(string text)
    {
        var value = text.Trim();
        return value.Equals("<MISSING STRING TABLE ENTRY>", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("MISSING STRING TABLE ENTRY", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("<MISSING STRING>", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPathLike(string text)
    {
        var value = text.Trim();
        if (value.Length == 0 || value.Contains(' ')) return false;
        return value.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("../../../", StringComparison.Ordinal) ||
               value.Contains("/Content/", StringComparison.OrdinalIgnoreCase) ||
               value.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) ||
               value.EndsWith(".umap", StringComparison.OrdinalIgnoreCase) ||
               value.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ||
               value.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
               value.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUrlLike(string text)
    {
        var value = text.Trim();
        return value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIdentifierLike(string text)
    {
        var value = text.Trim();
        if (value.Length < 3 || value.Length > 180 || value.Contains(' ')) return false;
        if (!IdentifierLikeRegex.IsMatch(value)) return false;
        return value.Contains('_') || value.Contains("::", StringComparison.Ordinal) ||
               value.Contains('/') || value.Contains('\\') || value.Count(c => c == '.') >= 2;
    }

    private static bool HasSourceAnomalySignal(string text)
    {
        var genderMarkers = GenderMarkerRegex.Matches(text).Select(x => x.Value).ToArray();
        if (genderMarkers.Length > 0 && !genderMarkers.SequenceEqual(ExactGenderMarkers, StringComparer.Ordinal))
            return true;

        if (text.Contains("<male=>", StringComparison.OrdinalIgnoreCase) != text.Contains("<female=>", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static bool IsUiLikely(string combinedId) => UiHints.Any(h => combinedId.Contains(h, StringComparison.OrdinalIgnoreCase));
    private static bool IsMessageLikely(string combinedId) => MessageHints.Any(h => combinedId.Contains(h, StringComparison.OrdinalIgnoreCase));

    private static CultureSnapshot LoadCulture(DefaultFileProvider provider, string culture)
    {
        var suffix = $"/Content/Localization/Game/{culture}/Game.locres";
        var virtualPath = provider.Files.Keys
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(x => NormalizePath(x).EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"Effective {culture.ToUpperInvariant()} Game.locres virtual path was not found.");

        if (!provider.Files.TryGetValue(virtualPath, out var file) || file is null)
            throw new InvalidOperationException($"Provider could not resolve effective {culture.ToUpperInvariant()} LOCRES: {virtualPath}");

        var source = DescribeSource(file);
        var entries = new List<LocresEntry>();
        using var ar = file.CreateReader();
        var locres = new FTextLocalizationResource(ar);
        foreach (var (nsKey, values) in locres.Entries)
        {
            foreach (var (textKey, entry) in values)
            {
                entries.Add(new LocresEntry
                {
                    Namespace = nsKey.Str,
                    Key = textKey.Str,
                    Text = entry.LocalizedString ?? string.Empty
                });
            }
        }

        return new CultureSnapshot
        {
            Culture = culture,
            VirtualPath = virtualPath,
            ContainerName = source.ContainerName,
            ReadOrder = source.ReadOrder,
            Entries = entries
        };
    }

    private static Dictionary<string, DialogueEvidence> ReadDialogueEvidence(string path)
    {
        var result = new Dictionary<string, DialogueEvidence>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var row = JsonSerializer.Deserialize<DialogueEvidence>(line, JsonRead)
                ?? throw new InvalidDataException("Could not deserialize dialogue identity summary row.");
            if (string.IsNullOrWhiteSpace(row.Identity)) continue;
            result[row.Identity] = row;
        }
        return result;
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
            Console.SetOut(TextWriter.Null);
            return new UnrealArchiveReader(gameRoot);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    private static SourceDescription DescribeSource(GameFile file) => file is VfsEntry entry
        ? new SourceDescription(entry.Vfs.Name, entry.Vfs.ReadOrder)
        : new SourceDescription("<non-vfs>", 0);

    private static string LoadAesKey(string? aesConfig, string? aesFile)
    {
        if (aesFile is not null) return File.ReadAllText(aesFile).Trim();
        if (aesConfig is null) return string.Empty;
        using var doc = JsonDocument.Parse(File.ReadAllText(aesConfig));
        if (!doc.RootElement.TryGetProperty("aes_key", out var property))
            throw new InvalidDataException("AES config does not contain aes_key.");
        return property.GetString()?.Trim() ?? string.Empty;
    }

    private static void ValidateInputs(string gameRoot, string dialogueSummaryPath, string? aesConfig, string? aesFile)
    {
        if (!Directory.Exists(gameRoot)) throw new DirectoryNotFoundException(gameRoot);
        if (!File.Exists(dialogueSummaryPath)) throw new FileNotFoundException("Run 007 identity-summary.jsonl not found.", dialogueSummaryPath);
        if (aesConfig is not null && !File.Exists(aesConfig)) throw new FileNotFoundException("AES config not found.", aesConfig);
        if (aesFile is not null && !File.Exists(aesFile)) throw new FileNotFoundException("AES file not found.", aesFile);
        if (aesConfig is not null && aesFile is not null) throw new ArgumentException("Use either --aes-config or --aes-file, not both.");
    }

    private static SampleRow ToSample(ClassificationRow row) => new()
    {
        Identity = row.Identity,
        PrimaryClass = row.PrimaryClass,
        ProvisionalDisposition = row.ProvisionalDisposition,
        Signals = row.Signals,
        EsText = Compact(row.EsText),
        EnText = Compact(row.EnText)
    };

    private static string NormalizePath(string value) => value.Replace('\\', '/');
    private static string Identity(string ns, string key) => ns + "::" + key;
    private static string Compact(string value)
    {
        var single = value.Replace("\r", " ").Replace("\n", " ");
        return single.Length <= 240 ? single : single[..237] + "...";
    }

    private static void WriteJson(string path, object value) =>
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonIndented) + Environment.NewLine, new UTF8Encoding(false));

    private static void WriteJsonl<T>(string path, IEnumerable<T> rows)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        foreach (var row in rows) writer.WriteLine(JsonSerializer.Serialize(row, JsonLine));
    }

    private static readonly JsonSerializerOptions JsonRead = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions JsonIndented = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private static readonly JsonSerializerOptions JsonLine = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };

    private static void PrintUsage() =>
        Console.WriteLine("Usage: NTE.SourceContentClassificationProbe <gameRoot> <run007-identity-summary.jsonl> <outputDir> [--aes-config=<json> | --aes-file=<txt>]");
}

internal sealed record SourceDescription(string ContainerName, long ReadOrder);

internal sealed class LocresEntry
{
    public required string Namespace { get; init; }
    public required string Key { get; init; }
    public required string Text { get; init; }
}

internal sealed class CultureSnapshot
{
    public required string Culture { get; init; }
    public required string VirtualPath { get; init; }
    public required string ContainerName { get; init; }
    public long ReadOrder { get; init; }
    public required List<LocresEntry> Entries { get; init; }
}

internal sealed class DialogueEvidence
{
    public string Identity { get; set; } = string.Empty;
    public string Namespace { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public int SpeechReferenceCount { get; set; }
    public int ChoiceReferenceCount { get; set; }
    public int OtherEdgeReferenceCount { get; set; }
    public int PackageCount { get; set; }
    public int ReferenceCount { get; set; }
}

internal sealed class ClassificationRow
{
    public required string Identity { get; init; }
    public required string Namespace { get; init; }
    public required string Key { get; init; }
    public required string EsText { get; init; }
    public required string EnText { get; init; }
    public required string PrimaryClass { get; init; }
    public required string ProvisionalDisposition { get; init; }
    public required string DispositionConfidence { get; init; }
    public required string DispositionReason { get; init; }
    public required string[] Signals { get; init; }
    public bool DialogueProven { get; init; }
    public int SpeechReferenceCount { get; init; }
    public int ChoiceReferenceCount { get; init; }
    public int OtherEdgeReferenceCount { get; init; }
    public int DialoguePackageCount { get; init; }
    public bool CjkInEs { get; init; }
    public bool CjkInEn { get; init; }
    public bool HasRecognizedStructure { get; init; }
    public bool SourceAnomalySignal { get; init; }
    public bool UiLikely { get; init; }
    public bool MessageLikely { get; init; }
    public bool NumericOnly { get; init; }
    public bool IdentifierLike { get; init; }
    public bool PathLike { get; init; }
    public bool UrlLike { get; init; }
}

internal sealed class NamespaceSummary
{
    public required string Namespace { get; init; }
    public int IdentityCount { get; init; }
    public int NarrativeProvenCount { get; init; }
    public int TranslateCandidateCount { get; init; }
    public int ReviewRequiredCount { get; init; }
    public int ExcludeCandidateCount { get; init; }
    public int CjkInEnCount { get; init; }
    public int CjkInEsCount { get; init; }
    public int StructuredCount { get; init; }
    public required Dictionary<string, int> PrimaryClassCounts { get; init; }
}

internal sealed class SampleRow
{
    public required string Identity { get; init; }
    public required string PrimaryClass { get; init; }
    public required string ProvisionalDisposition { get; init; }
    public required string[] Signals { get; init; }
    public required string EsText { get; init; }
    public required string EnText { get; init; }
}

internal sealed class ProbeSummary
{
    public bool Success { get; init; }
    public required string EsVirtualPath { get; init; }
    public required string EsContainer { get; init; }
    public long EsReadOrder { get; init; }
    public required string EnVirtualPath { get; init; }
    public required string EnContainer { get; init; }
    public long EnReadOrder { get; init; }
    public int EsEntryCount { get; init; }
    public int EnEntryCount { get; init; }
    public int ClassifiedIdentityCount { get; init; }
    public int MissingInEnCount { get; init; }
    public int MissingInEsCount { get; init; }
    public int DialogueEvidenceIdentityCount { get; init; }
    public int DialogueJoinedIdentityCount { get; init; }
    public required Dictionary<string, int> PrimaryClassCounts { get; init; }
    public required Dictionary<string, int> ProvisionalDispositionCounts { get; init; }
    public required Dictionary<string, int> SignalCounts { get; init; }
    public int CjkInEnCount { get; init; }
    public int CjkInEsCount { get; init; }
    public int StructuredCount { get; init; }
    public int SourceAnomalySignalCount { get; init; }
    public required string MethodNote { get; init; }
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
