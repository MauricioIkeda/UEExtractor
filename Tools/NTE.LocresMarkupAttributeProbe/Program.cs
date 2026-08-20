using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Localization;
using CUE4Parse.UE4.VirtualFileSystem;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NTE.LocresMarkupAttributeProbe;

internal static class Program
{
    private const int DifferenceSampleLimit = 300;
    private const int ParseFailureLimit = 200;

    private static readonly Regex AngleRegex = new(@"<[^>\r\n]+>", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AttributeRegex = new(
        @"(?<name>[A-Za-z_][A-Za-z0-9_:\-]*)\s*=\s*(?:\"(?<dq>[^\"]*)\"|'(?<sq>[^']*)'|(?<bare>[^\s]+))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

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

            foreach (var arg in args.Skip(2))
            {
                if (arg.StartsWith("--aes-config=", StringComparison.OrdinalIgnoreCase))
                    aesConfig = Path.GetFullPath(arg["--aes-config=".Length..].Trim('"'));
                else if (arg.StartsWith("--aes-file=", StringComparison.OrdinalIgnoreCase))
                    aesFile = Path.GetFullPath(arg["--aes-file=".Length..].Trim('"'));
                else
                    throw new ArgumentException($"Unknown argument: {arg}");
            }

            ValidateInputs(gameRoot, aesConfig, aesFile);
            Directory.CreateDirectory(outputDir);

            Console.WriteLine("NTE LOCRES Markup Attribute Probe 014");
            Console.WriteLine("Purpose: classify parameterized angle-tag attributes across aligned ES and EN identities");
            Console.WriteLine("Offline only; game archives are not modified.");
            Console.WriteLine();

            var aesKey = LoadAesKey(aesConfig, aesFile);
            using var aesScope = TemporaryAesCompatibility.Install(gameRoot, aesKey);
            using var reader = CreateReaderWithoutLeakingAes(gameRoot);
            var provider = GetProvider(reader);

            var esPath = FindLocres(provider, "/Content/Localization/Game/es/Game.locres");
            var enPath = FindLocres(provider, "/Content/Localization/Game/en/game.locres");
            var esFile = Resolve(provider, esPath);
            var enFile = Resolve(provider, enPath);
            var es = ParseLocale(esFile);
            var en = ParseLocale(enFile);
            var esSource = DescribeSource(esFile);
            var enSource = DescribeSource(enFile);

            var shared = es.Keys.Intersect(en.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var attrStats = new Dictionary<(string Tag, string Attribute), MutableAttributeStat>(TagAttributeComparer.Instance);
            var tagStats = new Dictionary<string, MutableTagStat>(StringComparer.OrdinalIgnoreCase);
            var differences = new List<AttributeDifferenceSample>();
            var parseFailures = new List<ParseFailureSample>();

            var totalEsTagInstances = 0;
            var totalEnTagInstances = 0;
            var esParameterizedInstances = 0;
            var enParameterizedInstances = 0;
            var pairedTagInstances = 0;
            var exactAttributeShapePairs = 0;
            var differingAttributeShapePairs = 0;
            var parseFailureCount = 0;

            foreach (var identity in shared)
            {
                var esEntry = es[identity];
                var enEntry = en[identity];
                var esTags = ExtractTags(esEntry.Text, "ES", identity, parseFailures, ref parseFailureCount);
                var enTags = ExtractTags(enEntry.Text, "EN", identity, parseFailures, ref parseFailureCount);

                totalEsTagInstances += esTags.Count;
                totalEnTagInstances += enTags.Count;
                esParameterizedInstances += esTags.Count(x => x.Attributes.Count > 0);
                enParameterizedInstances += enTags.Count(x => x.Attributes.Count > 0);

                foreach (var tag in esTags)
                {
                    GetTag(tagStats, tag.Name).EsOccurrenceCount++;
                    if (tag.Attributes.Count > 0) GetTag(tagStats, tag.Name).EsParameterizedOccurrenceCount++;
                    foreach (var attr in tag.Attributes)
                        GetAttr(attrStats, tag.Name, attr.Key).EsOccurrenceCount++;
                }

                foreach (var tag in enTags)
                {
                    GetTag(tagStats, tag.Name).EnOccurrenceCount++;
                    if (tag.Attributes.Count > 0) GetTag(tagStats, tag.Name).EnParameterizedOccurrenceCount++;
                    foreach (var attr in tag.Attributes)
                        GetAttr(attrStats, tag.Name, attr.Key).EnOccurrenceCount++;
                }

                var esGroups = esTags.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
                var enGroups = enTags.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

                foreach (var tagName in esGroups.Keys.Union(enGroups.Keys, StringComparer.OrdinalIgnoreCase))
                {
                    esGroups.TryGetValue(tagName, out var eList);
                    enGroups.TryGetValue(tagName, out var nList);
                    eList ??= [];
                    nList ??= [];

                    var tagStat = GetTag(tagStats, tagName);
                    if (eList.Count > 0) tagStat.EsIdentityCount++;
                    if (nList.Count > 0) tagStat.EnIdentityCount++;
                    if (eList.Count > 0 && nList.Count > 0) tagStat.BothIdentityCount++;

                    var pairCount = Math.Min(eList.Count, nList.Count);
                    tagStat.PairedOccurrenceCount += pairCount;
                    pairedTagInstances += pairCount;

                    for (var i = 0; i < pairCount; i++)
                    {
                        var eTag = eList[i];
                        var nTag = nList[i];
                        var eNames = eTag.Attributes.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                        var nNames = nTag.Attributes.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                        var shapeEqual = eNames.SequenceEqual(nNames, StringComparer.OrdinalIgnoreCase) &&
                                         eTag.IsSelfClosing == nTag.IsSelfClosing &&
                                         eTag.IsClosing == nTag.IsClosing;
                        if (shapeEqual)
                        {
                            tagStat.ExactAttributeShapePairCount++;
                            exactAttributeShapePairs++;
                        }
                        else
                        {
                            tagStat.DifferingAttributeShapePairCount++;
                            differingAttributeShapePairs++;
                        }

                        foreach (var attrName in eTag.Attributes.Keys.Union(nTag.Attributes.Keys, StringComparer.OrdinalIgnoreCase))
                        {
                            var stat = GetAttr(attrStats, tagName, attrName);
                            var hasE = eTag.Attributes.TryGetValue(attrName, out var eValue);
                            var hasN = nTag.Attributes.TryGetValue(attrName, out var nValue);
                            if (hasE && hasN)
                            {
                                stat.PairedOccurrenceCount++;
                                if (string.Equals(eValue, nValue, StringComparison.Ordinal))
                                    stat.SameValueCount++;
                                else
                                {
                                    stat.DifferentValueCount++;
                                    if (differences.Count < DifferenceSampleLimit)
                                    {
                                        differences.Add(new AttributeDifferenceSample
                                        {
                                            Identity = identity,
                                            Namespace = esEntry.Namespace,
                                            Key = esEntry.Key,
                                            TagName = eTag.Name,
                                            AttributeName = attrName,
                                            EsValue = eValue ?? string.Empty,
                                            EnValue = nValue ?? string.Empty,
                                            EsToken = eTag.RawToken,
                                            EnToken = nTag.RawToken,
                                            EsTextPreview = Preview(esEntry.Text),
                                            EnTextPreview = Preview(enEntry.Text)
                                        });
                                    }
                                }
                            }
                            else if (hasE) stat.EsOnlyInPairCount++;
                            else if (hasN) stat.EnOnlyInPairCount++;
                        }
                    }
                }
            }

            var attributeRows = attrStats.Select(x =>
            {
                var paired = x.Value.PairedOccurrenceCount;
                var sameRate = Rate(x.Value.SameValueCount, paired);
                return new AttributeParityRow
                {
                    TagName = x.Key.Tag,
                    AttributeName = x.Key.Attribute,
                    EsOccurrenceCount = x.Value.EsOccurrenceCount,
                    EnOccurrenceCount = x.Value.EnOccurrenceCount,
                    PairedOccurrenceCount = paired,
                    SameValueCount = x.Value.SameValueCount,
                    DifferentValueCount = x.Value.DifferentValueCount,
                    EsOnlyInPairCount = x.Value.EsOnlyInPairCount,
                    EnOnlyInPairCount = x.Value.EnOnlyInPairCount,
                    SameValueRate = sameRate,
                    SuggestedClass = SuggestClass(x.Value, sameRate)
                };
            })
            .OrderByDescending(x => x.EsOccurrenceCount + x.EnOccurrenceCount)
            .ThenBy(x => x.TagName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.AttributeName, StringComparer.OrdinalIgnoreCase)
            .ToList();

            var tagRows = tagStats.Select(x => new TagShapeParityRow
            {
                TagName = x.Key,
                EsIdentityCount = x.Value.EsIdentityCount,
                EnIdentityCount = x.Value.EnIdentityCount,
                BothIdentityCount = x.Value.BothIdentityCount,
                EsOccurrenceCount = x.Value.EsOccurrenceCount,
                EnOccurrenceCount = x.Value.EnOccurrenceCount,
                EsParameterizedOccurrenceCount = x.Value.EsParameterizedOccurrenceCount,
                EnParameterizedOccurrenceCount = x.Value.EnParameterizedOccurrenceCount,
                PairedOccurrenceCount = x.Value.PairedOccurrenceCount,
                ExactAttributeShapePairCount = x.Value.ExactAttributeShapePairCount,
                DifferingAttributeShapePairCount = x.Value.DifferingAttributeShapePairCount,
                ExactAttributeShapeRate = Rate(x.Value.ExactAttributeShapePairCount, x.Value.PairedOccurrenceCount)
            })
            .OrderByDescending(x => x.EsParameterizedOccurrenceCount + x.EnParameterizedOccurrenceCount)
            .ThenBy(x => x.TagName, StringComparer.OrdinalIgnoreCase)
            .ToList();

            WriteJsonl(Path.Combine(outputDir, "tag-attribute-parity.jsonl"), attributeRows);
            WriteJsonl(Path.Combine(outputDir, "tag-shape-parity.jsonl"), tagRows);
            WriteJsonl(Path.Combine(outputDir, "attribute-difference-samples.jsonl"), differences);
            WriteJsonl(Path.Combine(outputDir, "markup-parse-failures.jsonl"), parseFailures);

            var summary = new ProbeSummary
            {
                EsVirtualPath = esPath,
                EnVirtualPath = enPath,
                EsContainerName = esSource.ContainerName,
                EnContainerName = enSource.ContainerName,
                EsReadOrder = esSource.ReadOrder,
                EnReadOrder = enSource.ReadOrder,
                EsEntryCount = es.Count,
                EnEntryCount = en.Count,
                SharedIdentityCount = shared.Count,
                TotalEsAngleTagInstances = totalEsTagInstances,
                TotalEnAngleTagInstances = totalEnTagInstances,
                EsParameterizedTagInstances = esParameterizedInstances,
                EnParameterizedTagInstances = enParameterizedInstances,
                PairedTagInstances = pairedTagInstances,
                ExactAttributeShapePairCount = exactAttributeShapePairs,
                DifferingAttributeShapePairCount = differingAttributeShapePairs,
                DistinctTagNameCount = tagRows.Count,
                DistinctTagAttributePairCount = attributeRows.Count,
                AttributePairsSuggestedInvariant = attributeRows.Count(x => x.SuggestedClass == "likely_invariant"),
                AttributePairsSuggestedLocalizable = attributeRows.Count(x => x.SuggestedClass == "likely_localizable_value"),
                AttributePairsMixedOrContextual = attributeRows.Count(x => x.SuggestedClass == "mixed_or_contextual"),
                AttributePairsLowEvidence = attributeRows.Count(x => x.SuggestedClass == "low_evidence"),
                ParseFailureCount = parseFailureCount,
                ParseFailureSamplesWritten = parseFailures.Count
            };
            WriteJson(Path.Combine(outputDir, "summary.json"), summary);

            Console.WriteLine($"ES/EN identities: {es.Count}/{en.Count}; aligned: {shared.Count}");
            Console.WriteLine($"Angle-tag instances ES/EN: {totalEsTagInstances}/{totalEnTagInstances}");
            Console.WriteLine($"Parameterized tag instances ES/EN: {esParameterizedInstances}/{enParameterizedInstances}");
            Console.WriteLine($"Paired tag instances: {pairedTagInstances}; exact attribute shape: {exactAttributeShapePairs}");
            Console.WriteLine($"Distinct tag+attribute pairs: {attributeRows.Count}");
            Console.WriteLine($"Suggested invariant/localizable/mixed/low-evidence: {summary.AttributePairsSuggestedInvariant}/{summary.AttributePairsSuggestedLocalizable}/{summary.AttributePairsMixedOrContextual}/{summary.AttributePairsLowEvidence}");
            Console.WriteLine($"Markup parse failures: {parseFailureCount}");
            Console.WriteLine($"Reports: {outputDir}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex}");
            return 1;
        }
    }

    private static List<ParsedTag> ExtractTags(
        string text,
        string locale,
        string identity,
        List<ParseFailureSample> samples,
        ref int failureCount)
    {
        var result = new List<ParsedTag>();
        foreach (Match match in AngleRegex.Matches(text))
        {
            var parsed = TryParseTag(match.Value, out var error);
            if (parsed is not null)
            {
                result.Add(parsed);
                continue;
            }

            failureCount++;
            if (samples.Count < ParseFailureLimit)
            {
                samples.Add(new ParseFailureSample
                {
                    Locale = locale,
                    Identity = identity,
                    Token = match.Value,
                    Error = error ?? "Unknown parse error"
                });
            }
        }
        return result;
    }

    private static ParsedTag? TryParseTag(string token, out string? error)
    {
        error = null;
        if (token.Length < 3 || token[0] != '<' || token[^1] != '>')
        {
            error = "Not a complete angle token";
            return null;
        }

        var inner = token[1..^1].Trim();
        if (inner.Length == 0)
        {
            error = "Empty angle token";
            return null;
        }

        var closing = inner.StartsWith('/');
        if (closing) inner = inner[1..].TrimStart();
        var selfClosing = inner.EndsWith('/');
        if (selfClosing) inner = inner[..^1].TrimEnd();

        if (inner.StartsWith('<'))
        {
            error = "Malformed nested leading angle bracket";
            return null;
        }

        var nameEnd = inner.IndexOfAny([' ', '\t', '=']);
        var name = (nameEnd < 0 ? inner : inner[..nameEnd]).Trim();
        if (name.Length == 0)
        {
            error = "Missing tag name";
            return null;
        }

        if (closing)
            return new ParsedTag(token, name, true, selfClosing, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        var rest = nameEnd < 0 ? string.Empty : inner[nameEnd..].Trim();
        if (rest.StartsWith('=') && !rest.Contains(' '))
        {
            // Runtime gender markers such as <male=> are structural but are not attribute syntax.
            return new ParsedTag(token, name, false, selfClosing, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var consumed = new bool[rest.Length];
        foreach (Match m in AttributeRegex.Matches(rest))
        {
            var attrName = m.Groups["name"].Value;
            var value = m.Groups["dq"].Success ? m.Groups["dq"].Value
                : m.Groups["sq"].Success ? m.Groups["sq"].Value
                : m.Groups["bare"].Value;
            attrs[attrName] = value;
            for (var i = m.Index; i < m.Index + m.Length && i < consumed.Length; i++) consumed[i] = true;
        }

        for (var i = 0; i < rest.Length; i++)
        {
            if (char.IsWhiteSpace(rest[i]) || consumed[i]) continue;
            error = $"Unparsed attribute syntax near: {rest[i..Math.Min(rest.Length, i + 60)]}";
            return null;
        }

        return new ParsedTag(token, name, false, selfClosing, attrs);
    }

    private static string SuggestClass(MutableAttributeStat stat, double? sameRate)
    {
        if (stat.PairedOccurrenceCount < 3) return "low_evidence";
        if (stat.EsOnlyInPairCount > 0 || stat.EnOnlyInPairCount > 0) return "mixed_or_contextual";
        if (sameRate >= 0.98) return "likely_invariant";
        if (stat.DifferentValueCount >= 3 && sameRate <= 0.50) return "likely_localizable_value";
        return "mixed_or_contextual";
    }

    private static Dictionary<string, LocaleEntry> ParseLocale(GameFile file)
    {
        using var ar = file.CreateReader();
        var resource = new FTextLocalizationResource(ar);
        var result = new Dictionary<string, LocaleEntry>(StringComparer.Ordinal);
        foreach (var (nsKey, entries) in resource.Entries)
        {
            foreach (var (textKey, entry) in entries)
            {
                var identity = Identity(nsKey.Str, textKey.Str);
                result[identity] = new LocaleEntry(nsKey.Str, textKey.Str, entry.LocalizedString ?? string.Empty);
            }
        }
        return result;
    }

    private static MutableAttributeStat GetAttr(Dictionary<(string Tag, string Attribute), MutableAttributeStat> dictionary, string tag, string attribute)
    {
        var key = (tag, attribute);
        if (!dictionary.TryGetValue(key, out var value))
            dictionary[key] = value = new MutableAttributeStat();
        return value;
    }

    private static MutableTagStat GetTag(Dictionary<string, MutableTagStat> dictionary, string tag)
    {
        if (!dictionary.TryGetValue(tag, out var value))
            dictionary[tag] = value = new MutableTagStat();
        return value;
    }

    private static double? Rate(int numerator, int denominator) => denominator == 0 ? null : Math.Round((double)numerator / denominator, 6);
    private static string Identity(string ns, string key) => string.IsNullOrEmpty(ns) ? key : $"{ns}::{key}";
    private static string Preview(string text)
    {
        const int max = 500;
        var normalized = text.Replace("\r", "\\r").Replace("\n", "\\n");
        return normalized.Length <= max ? normalized : normalized[..max] + "…";
    }

    private static string FindLocres(DefaultFileProvider provider, string suffix) => provider.Files.Keys
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault(x => NormalizePath(x).EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        ?? throw new FileNotFoundException($"LOCRES not found for suffix: {suffix}");

    private static GameFile Resolve(DefaultFileProvider provider, string path)
    {
        if (!provider.Files.TryGetValue(path, out var file) || file is null)
            throw new InvalidOperationException($"Provider could not resolve effective file: {path}");
        return file;
    }

    private static SourceDescription DescribeSource(GameFile file)
    {
        if (file is VfsEntry entry)
            return new SourceDescription(entry.Vfs.Name, entry.Vfs.ReadOrder);
        return new SourceDescription("<non-vfs>", 0);
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
                throw new InvalidDataException("AES config does not contain an 'aes_key' property.");
            raw = aesProperty.GetString()?.Trim() ?? string.Empty;
        }
        if (!raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) raw = "0x" + raw;
        if (raw.Length != 66 || raw.Skip(2).Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("AES key must contain exactly 64 hexadecimal digits.");
        return raw;
    }

    private static void ValidateInputs(string gameRoot, string? aesConfig, string? aesFile)
    {
        if (!Directory.Exists(gameRoot)) throw new DirectoryNotFoundException($"Game root not found: {gameRoot}");
        if (aesConfig is not null && aesFile is not null) throw new ArgumentException("Use only one of --aes-config or --aes-file.");
        if (aesConfig is not null && !File.Exists(aesConfig)) throw new FileNotFoundException("AES config not found.", aesConfig);
        if (aesFile is not null && !File.Exists(aesFile)) throw new FileNotFoundException("AES file not found.", aesFile);
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    private static void WriteJson<T>(string path, T value) => File.WriteAllText(
        path,
        JsonSerializer.Serialize(value, JsonIndented),
        new UTF8Encoding(false));

    private static void WriteJsonl<T>(string path, IEnumerable<T> values)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        foreach (var value in values)
            writer.WriteLine(JsonSerializer.Serialize(value, JsonCompact));
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  NTE.LocresMarkupAttributeProbe <gameRoot> <outputDir> [--aes-config=<path> | --aes-file=<path>]");
    }

    private static readonly JsonSerializerOptions JsonIndented = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private static readonly JsonSerializerOptions JsonCompact = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

internal sealed record LocaleEntry(string Namespace, string Key, string Text);
internal sealed record SourceDescription(string ContainerName, long ReadOrder);
internal sealed record ParsedTag(string RawToken, string Name, bool IsClosing, bool IsSelfClosing, Dictionary<string, string> Attributes);

internal sealed class TagAttributeComparer : IEqualityComparer<(string Tag, string Attribute)>
{
    public static readonly TagAttributeComparer Instance = new();
    public bool Equals((string Tag, string Attribute) x, (string Tag, string Attribute) y) =>
        string.Equals(x.Tag, y.Tag, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(x.Attribute, y.Attribute, StringComparison.OrdinalIgnoreCase);
    public int GetHashCode((string Tag, string Attribute) obj) => HashCode.Combine(
        StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Tag),
        StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Attribute));
}

internal sealed class MutableAttributeStat
{
    public int EsOccurrenceCount { get; set; }
    public int EnOccurrenceCount { get; set; }
    public int PairedOccurrenceCount { get; set; }
    public int SameValueCount { get; set; }
    public int DifferentValueCount { get; set; }
    public int EsOnlyInPairCount { get; set; }
    public int EnOnlyInPairCount { get; set; }
}

internal sealed class MutableTagStat
{
    public int EsIdentityCount { get; set; }
    public int EnIdentityCount { get; set; }
    public int BothIdentityCount { get; set; }
    public int EsOccurrenceCount { get; set; }
    public int EnOccurrenceCount { get; set; }
    public int EsParameterizedOccurrenceCount { get; set; }
    public int EnParameterizedOccurrenceCount { get; set; }
    public int PairedOccurrenceCount { get; set; }
    public int ExactAttributeShapePairCount { get; set; }
    public int DifferingAttributeShapePairCount { get; set; }
}

internal sealed class AttributeParityRow
{
    public required string TagName { get; init; }
    public required string AttributeName { get; init; }
    public int EsOccurrenceCount { get; init; }
    public int EnOccurrenceCount { get; init; }
    public int PairedOccurrenceCount { get; init; }
    public int SameValueCount { get; init; }
    public int DifferentValueCount { get; init; }
    public int EsOnlyInPairCount { get; init; }
    public int EnOnlyInPairCount { get; init; }
    public double? SameValueRate { get; init; }
    public required string SuggestedClass { get; init; }
}

internal sealed class TagShapeParityRow
{
    public required string TagName { get; init; }
    public int EsIdentityCount { get; init; }
    public int EnIdentityCount { get; init; }
    public int BothIdentityCount { get; init; }
    public int EsOccurrenceCount { get; init; }
    public int EnOccurrenceCount { get; init; }
    public int EsParameterizedOccurrenceCount { get; init; }
    public int EnParameterizedOccurrenceCount { get; init; }
    public int PairedOccurrenceCount { get; init; }
    public int ExactAttributeShapePairCount { get; init; }
    public int DifferingAttributeShapePairCount { get; init; }
    public double? ExactAttributeShapeRate { get; init; }
}

internal sealed class AttributeDifferenceSample
{
    public required string Identity { get; init; }
    public required string Namespace { get; init; }
    public required string Key { get; init; }
    public required string TagName { get; init; }
    public required string AttributeName { get; init; }
    public required string EsValue { get; init; }
    public required string EnValue { get; init; }
    public required string EsToken { get; init; }
    public required string EnToken { get; init; }
    public required string EsTextPreview { get; init; }
    public required string EnTextPreview { get; init; }
}

internal sealed class ParseFailureSample
{
    public required string Locale { get; init; }
    public required string Identity { get; init; }
    public required string Token { get; init; }
    public required string Error { get; init; }
}

internal sealed class ProbeSummary
{
    public required string EsVirtualPath { get; init; }
    public required string EnVirtualPath { get; init; }
    public required string EsContainerName { get; init; }
    public required string EnContainerName { get; init; }
    public long EsReadOrder { get; init; }
    public long EnReadOrder { get; init; }
    public int EsEntryCount { get; init; }
    public int EnEntryCount { get; init; }
    public int SharedIdentityCount { get; init; }
    public int TotalEsAngleTagInstances { get; init; }
    public int TotalEnAngleTagInstances { get; init; }
    public int EsParameterizedTagInstances { get; init; }
    public int EnParameterizedTagInstances { get; init; }
    public int PairedTagInstances { get; init; }
    public int ExactAttributeShapePairCount { get; init; }
    public int DifferingAttributeShapePairCount { get; init; }
    public int DistinctTagNameCount { get; init; }
    public int DistinctTagAttributePairCount { get; init; }
    public int AttributePairsSuggestedInvariant { get; init; }
    public int AttributePairsSuggestedLocalizable { get; init; }
    public int AttributePairsMixedOrContextual { get; init; }
    public int AttributePairsLowEvidence { get; init; }
    public int ParseFailureCount { get; init; }
    public int ParseFailureSamplesWritten { get; init; }
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
