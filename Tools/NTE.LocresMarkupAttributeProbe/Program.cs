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
        "(?<name>[A-Za-z_][A-Za-z0-9_:\\-]*)\\s*=\\s*(?:\"(?<dq>[^\"]*)\"|'(?<sq>[^']*)'|(?<bare>[^\\s]+))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length < 2) { PrintUsage(); return 2; }
            var gameRoot = Path.GetFullPath(args[0]);
            var outputDir = Path.GetFullPath(args[1]);
            string? aesConfig = null;
            string? aesFile = null;
            foreach (var arg in args.Skip(2))
            {
                if (arg.StartsWith("--aes-config=", StringComparison.OrdinalIgnoreCase)) aesConfig = Path.GetFullPath(arg[13..].Trim('"'));
                else if (arg.StartsWith("--aes-file=", StringComparison.OrdinalIgnoreCase)) aesFile = Path.GetFullPath(arg[11..].Trim('"'));
                else throw new ArgumentException($"Unknown argument: {arg}");
            }
            ValidateInputs(gameRoot, aesConfig, aesFile);
            Directory.CreateDirectory(outputDir);

            Console.WriteLine("NTE LOCRES Markup Attribute Probe 014");
            Console.WriteLine("Purpose: classify parameterized angle-tag attributes across aligned ES and EN identities");
            Console.WriteLine("Offline only; game archives are not modified.\n");

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
            var totalEsTags = 0; var totalEnTags = 0; var esParam = 0; var enParam = 0;
            var pairedTags = 0; var exactShapes = 0; var differentShapes = 0; var parseFailureCount = 0;

            foreach (var identity in shared)
            {
                var e = es[identity]; var n = en[identity];
                var eTags = ExtractTags(e.Text, "ES", identity, parseFailures, ref parseFailureCount);
                var nTags = ExtractTags(n.Text, "EN", identity, parseFailures, ref parseFailureCount);
                totalEsTags += eTags.Count; totalEnTags += nTags.Count;
                esParam += eTags.Count(x => x.Attributes.Count > 0); enParam += nTags.Count(x => x.Attributes.Count > 0);

                foreach (var tag in eTags)
                {
                    var s = GetTag(tagStats, tag.Name); s.EsOccurrenceCount++; if (tag.Attributes.Count > 0) s.EsParameterizedOccurrenceCount++;
                    foreach (var a in tag.Attributes) GetAttr(attrStats, tag.Name, a.Key).EsOccurrenceCount++;
                }
                foreach (var tag in nTags)
                {
                    var s = GetTag(tagStats, tag.Name); s.EnOccurrenceCount++; if (tag.Attributes.Count > 0) s.EnParameterizedOccurrenceCount++;
                    foreach (var a in tag.Attributes) GetAttr(attrStats, tag.Name, a.Key).EnOccurrenceCount++;
                }

                var eg = eTags.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
                var ng = nTags.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
                foreach (var tagName in eg.Keys.Union(ng.Keys, StringComparer.OrdinalIgnoreCase))
                {
                    eg.TryGetValue(tagName, out var el); ng.TryGetValue(tagName, out var nl); el ??= []; nl ??= [];
                    var ts = GetTag(tagStats, tagName);
                    if (el.Count > 0) ts.EsIdentityCount++; if (nl.Count > 0) ts.EnIdentityCount++; if (el.Count > 0 && nl.Count > 0) ts.BothIdentityCount++;
                    var count = Math.Min(el.Count, nl.Count); ts.PairedOccurrenceCount += count; pairedTags += count;
                    for (var i = 0; i < count; i++)
                    {
                        var et = el[i]; var nt = nl[i];
                        var eNames = et.Attributes.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                        var nNames = nt.Attributes.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                        var sameShape = eNames.SequenceEqual(nNames, StringComparer.OrdinalIgnoreCase) && et.IsClosing == nt.IsClosing && et.IsSelfClosing == nt.IsSelfClosing;
                        if (sameShape) { ts.ExactAttributeShapePairCount++; exactShapes++; } else { ts.DifferingAttributeShapePairCount++; differentShapes++; }

                        foreach (var attrName in et.Attributes.Keys.Union(nt.Attributes.Keys, StringComparer.OrdinalIgnoreCase))
                        {
                            var a = GetAttr(attrStats, tagName, attrName);
                            var hasE = et.Attributes.TryGetValue(attrName, out var ev); var hasN = nt.Attributes.TryGetValue(attrName, out var nv);
                            if (hasE && hasN)
                            {
                                a.PairedOccurrenceCount++;
                                if (string.Equals(ev, nv, StringComparison.Ordinal)) a.SameValueCount++;
                                else
                                {
                                    a.DifferentValueCount++;
                                    if (differences.Count < DifferenceSampleLimit) differences.Add(new AttributeDifferenceSample
                                    {
                                        Identity = identity, Namespace = e.Namespace, Key = e.Key, TagName = et.Name, AttributeName = attrName,
                                        EsValue = ev ?? string.Empty, EnValue = nv ?? string.Empty, EsToken = et.RawToken, EnToken = nt.RawToken,
                                        EsTextPreview = Preview(e.Text), EnTextPreview = Preview(n.Text)
                                    });
                                }
                            }
                            else if (hasE) a.EsOnlyInPairCount++; else if (hasN) a.EnOnlyInPairCount++;
                        }
                    }
                }
            }

            var attrRows = attrStats.Select(x =>
            {
                var rate = Rate(x.Value.SameValueCount, x.Value.PairedOccurrenceCount);
                return new AttributeParityRow
                {
                    TagName = x.Key.Tag, AttributeName = x.Key.Attribute,
                    EsOccurrenceCount = x.Value.EsOccurrenceCount, EnOccurrenceCount = x.Value.EnOccurrenceCount,
                    PairedOccurrenceCount = x.Value.PairedOccurrenceCount, SameValueCount = x.Value.SameValueCount,
                    DifferentValueCount = x.Value.DifferentValueCount, EsOnlyInPairCount = x.Value.EsOnlyInPairCount,
                    EnOnlyInPairCount = x.Value.EnOnlyInPairCount, SameValueRate = rate, SuggestedClass = SuggestClass(x.Value, rate)
                };
            }).OrderByDescending(x => x.EsOccurrenceCount + x.EnOccurrenceCount)
              .ThenBy(x => x.TagName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.AttributeName, StringComparer.OrdinalIgnoreCase).ToList();

            var tagRows = tagStats.Select(x => new TagShapeParityRow
            {
                TagName = x.Key, EsIdentityCount = x.Value.EsIdentityCount, EnIdentityCount = x.Value.EnIdentityCount, BothIdentityCount = x.Value.BothIdentityCount,
                EsOccurrenceCount = x.Value.EsOccurrenceCount, EnOccurrenceCount = x.Value.EnOccurrenceCount,
                EsParameterizedOccurrenceCount = x.Value.EsParameterizedOccurrenceCount, EnParameterizedOccurrenceCount = x.Value.EnParameterizedOccurrenceCount,
                PairedOccurrenceCount = x.Value.PairedOccurrenceCount, ExactAttributeShapePairCount = x.Value.ExactAttributeShapePairCount,
                DifferingAttributeShapePairCount = x.Value.DifferingAttributeShapePairCount,
                ExactAttributeShapeRate = Rate(x.Value.ExactAttributeShapePairCount, x.Value.PairedOccurrenceCount)
            }).OrderByDescending(x => x.EsParameterizedOccurrenceCount + x.EnParameterizedOccurrenceCount)
              .ThenBy(x => x.TagName, StringComparer.OrdinalIgnoreCase).ToList();

            WriteJsonl(Path.Combine(outputDir, "tag-attribute-parity.jsonl"), attrRows);
            WriteJsonl(Path.Combine(outputDir, "tag-shape-parity.jsonl"), tagRows);
            WriteJsonl(Path.Combine(outputDir, "attribute-difference-samples.jsonl"), differences);
            WriteJsonl(Path.Combine(outputDir, "markup-parse-failures.jsonl"), parseFailures);

            var summary = new ProbeSummary
            {
                EsVirtualPath = esPath, EnVirtualPath = enPath, EsContainerName = esSource.ContainerName, EnContainerName = enSource.ContainerName,
                EsReadOrder = esSource.ReadOrder, EnReadOrder = enSource.ReadOrder, EsEntryCount = es.Count, EnEntryCount = en.Count, SharedIdentityCount = shared.Count,
                TotalEsAngleTagInstances = totalEsTags, TotalEnAngleTagInstances = totalEnTags,
                EsParameterizedTagInstances = esParam, EnParameterizedTagInstances = enParam, PairedTagInstances = pairedTags,
                ExactAttributeShapePairCount = exactShapes, DifferingAttributeShapePairCount = differentShapes,
                DistinctTagNameCount = tagRows.Count, DistinctTagAttributePairCount = attrRows.Count,
                AttributePairsSuggestedInvariant = attrRows.Count(x => x.SuggestedClass == "likely_invariant"),
                AttributePairsSuggestedLocalizable = attrRows.Count(x => x.SuggestedClass == "likely_localizable_value"),
                AttributePairsMixedOrContextual = attrRows.Count(x => x.SuggestedClass == "mixed_or_contextual"),
                AttributePairsLowEvidence = attrRows.Count(x => x.SuggestedClass == "low_evidence"),
                ParseFailureCount = parseFailureCount, ParseFailureSamplesWritten = parseFailures.Count
            };
            WriteJson(Path.Combine(outputDir, "summary.json"), summary);

            Console.WriteLine($"ES/EN identities: {es.Count}/{en.Count}; aligned: {shared.Count}");
            Console.WriteLine($"Angle-tag instances ES/EN: {totalEsTags}/{totalEnTags}");
            Console.WriteLine($"Parameterized tag instances ES/EN: {esParam}/{enParam}");
            Console.WriteLine($"Paired tag instances: {pairedTags}; exact attribute shape: {exactShapes}");
            Console.WriteLine($"Distinct tag+attribute pairs: {attrRows.Count}");
            Console.WriteLine($"Suggested invariant/localizable/mixed/low-evidence: {summary.AttributePairsSuggestedInvariant}/{summary.AttributePairsSuggestedLocalizable}/{summary.AttributePairsMixedOrContextual}/{summary.AttributePairsLowEvidence}");
            Console.WriteLine($"Markup parse failures: {parseFailureCount}");
            Console.WriteLine($"Reports: {outputDir}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine($"ERROR: {ex}"); return 1; }
    }

    private static List<ParsedTag> ExtractTags(string text, string locale, string identity, List<ParseFailureSample> samples, ref int failureCount)
    {
        var result = new List<ParsedTag>();
        foreach (Match m in AngleRegex.Matches(text))
        {
            var parsed = TryParseTag(m.Value, out var error);
            if (parsed is not null) { result.Add(parsed); continue; }
            failureCount++;
            if (samples.Count < ParseFailureLimit) samples.Add(new ParseFailureSample { Locale = locale, Identity = identity, Token = m.Value, Error = error ?? "Unknown parse error" });
        }
        return result;
    }

    private static ParsedTag? TryParseTag(string token, out string? error)
    {
        error = null;
        if (token.Length < 3 || token[0] != '<' || token[^1] != '>') { error = "Not a complete angle token"; return null; }
        var inner = token[1..^1].Trim();
        if (inner.Length == 0) { error = "Empty angle token"; return null; }
        var closing = inner.StartsWith('/'); if (closing) inner = inner[1..].TrimStart();
        if (closing && inner.Length == 0) return new ParsedTag(token, "/", true, false, new(StringComparer.OrdinalIgnoreCase));
        var selfClosing = inner.EndsWith('/'); if (selfClosing) inner = inner[..^1].TrimEnd();
        if (inner.StartsWith('<')) { error = "Malformed nested leading angle bracket"; return null; }
        var nameEnd = inner.IndexOfAny([' ', '\t', '=']);
        var name = (nameEnd < 0 ? inner : inner[..nameEnd]).Trim();
        if (name.Length == 0) { error = "Missing tag name"; return null; }
        if (closing) return new ParsedTag(token, name, true, selfClosing, new(StringComparer.OrdinalIgnoreCase));
        var rest = nameEnd < 0 ? string.Empty : inner[nameEnd..].Trim();
        if (rest == "=") return new ParsedTag(token, name, false, selfClosing, new(StringComparer.OrdinalIgnoreCase));

        var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var consumed = new bool[rest.Length];
        foreach (Match m in AttributeRegex.Matches(rest))
        {
            var attr = m.Groups["name"].Value;
            var value = m.Groups["dq"].Success ? m.Groups["dq"].Value : m.Groups["sq"].Success ? m.Groups["sq"].Value : m.Groups["bare"].Value;
            attrs[attr] = value;
            for (var i = m.Index; i < m.Index + m.Length && i < consumed.Length; i++) consumed[i] = true;
        }
        for (var i = 0; i < rest.Length; i++)
        {
            if (char.IsWhiteSpace(rest[i]) || consumed[i]) continue;
            error = $"Unparsed attribute syntax near: {rest[i..Math.Min(rest.Length, i + 60)]}"; return null;
        }
        return new ParsedTag(token, name, false, selfClosing, attrs);
    }

    private static string SuggestClass(MutableAttributeStat s, double? rate)
    {
        if (s.PairedOccurrenceCount < 3) return "low_evidence";
        if (s.EsOnlyInPairCount > 0 || s.EnOnlyInPairCount > 0) return "mixed_or_contextual";
        if (rate >= 0.98) return "likely_invariant";
        if (s.DifferentValueCount >= 3 && rate <= 0.50) return "likely_localizable_value";
        return "mixed_or_contextual";
    }

    private static Dictionary<string, LocaleEntry> ParseLocale(GameFile file)
    {
        using var ar = file.CreateReader(); var r = new FTextLocalizationResource(ar); var d = new Dictionary<string, LocaleEntry>(StringComparer.Ordinal);
        foreach (var (ns, entries) in r.Entries) foreach (var (key, e) in entries) d[Identity(ns.Str, key.Str)] = new(ns.Str, key.Str, e.LocalizedString ?? string.Empty);
        return d;
    }

    private static MutableAttributeStat GetAttr(Dictionary<(string Tag, string Attribute), MutableAttributeStat> d, string tag, string attr)
    { var k = (tag, attr); if (!d.TryGetValue(k, out var v)) d[k] = v = new(); return v; }
    private static MutableTagStat GetTag(Dictionary<string, MutableTagStat> d, string tag)
    { if (!d.TryGetValue(tag, out var v)) d[tag] = v = new(); return v; }
    private static double? Rate(int n, int d) => d == 0 ? null : Math.Round((double)n / d, 6);
    private static string Identity(string ns, string key) => string.IsNullOrEmpty(ns) ? key : $"{ns}::{key}";
    private static string Preview(string text) { const int max = 500; var s = text.Replace("\r", "\\r").Replace("\n", "\\n"); return s.Length <= max ? s : s[..max] + "…"; }
    private static string NormalizePath(string path) => path.Replace('\\', '/');
    private static string FindLocres(DefaultFileProvider p, string suffix) => p.Files.Keys.Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault(x => NormalizePath(x).EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) ?? throw new FileNotFoundException($"LOCRES not found: {suffix}");
    private static GameFile Resolve(DefaultFileProvider p, string path) { if (!p.Files.TryGetValue(path, out var f) || f is null) throw new InvalidOperationException($"Could not resolve: {path}"); return f; }
    private static SourceDescription DescribeSource(GameFile file) => file is VfsEntry e ? new(e.Vfs.Name, e.Vfs.ReadOrder) : new("<non-vfs>", 0);
    private static DefaultFileProvider GetProvider(UnrealArchiveReader r) { var f = typeof(UnrealArchiveReader).GetField("_provider", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingFieldException(); return (DefaultFileProvider)(f.GetValue(r) ?? throw new InvalidOperationException()); }
    private static UnrealArchiveReader CreateReaderWithoutLeakingAes(string root) { var o = Console.Out; try { Console.SetOut(new AesRedactingTextWriter(o)); return new UnrealArchiveReader(root); } finally { Console.SetOut(o); } }

    private static string LoadAesKey(string? config, string? file)
    {
        if (config is null && file is null) return string.Empty; string raw;
        if (file is not null) raw = File.ReadAllText(file).Trim();
        else { using var doc = JsonDocument.Parse(File.ReadAllText(config!)); if (!doc.RootElement.TryGetProperty("aes_key", out var p)) throw new InvalidDataException("AES config missing aes_key"); raw = p.GetString()?.Trim() ?? string.Empty; }
        if (!raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) raw = "0x" + raw;
        if (raw.Length != 66 || raw.Skip(2).Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("AES key must contain 64 hex digits"); return raw;
    }
    private static void ValidateInputs(string root, string? config, string? file) { if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root); if (config is not null && file is not null) throw new ArgumentException("Use one AES source"); if (config is not null && !File.Exists(config)) throw new FileNotFoundException("AES config", config); if (file is not null && !File.Exists(file)) throw new FileNotFoundException("AES file", file); }
    private static void WriteJson<T>(string path, T v) => File.WriteAllText(path, JsonSerializer.Serialize(v, JsonIndented), new UTF8Encoding(false));
    private static void WriteJsonl<T>(string path, IEnumerable<T> values) { using var w = new StreamWriter(path, false, new UTF8Encoding(false)); foreach (var v in values) w.WriteLine(JsonSerializer.Serialize(v, JsonCompact)); }
    private static void PrintUsage() => Console.WriteLine("NTE.LocresMarkupAttributeProbe <gameRoot> <outputDir> [--aes-config=<path> | --aes-file=<path>]");
    private static readonly JsonSerializerOptions JsonIndented = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = true };
    private static readonly JsonSerializerOptions JsonCompact = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
}

internal sealed record LocaleEntry(string Namespace, string Key, string Text);
internal sealed record SourceDescription(string ContainerName, long ReadOrder);
internal sealed record ParsedTag(string RawToken, string Name, bool IsClosing, bool IsSelfClosing, Dictionary<string, string> Attributes);
internal sealed class TagAttributeComparer : IEqualityComparer<(string Tag, string Attribute)>
{ public static readonly TagAttributeComparer Instance = new(); public bool Equals((string Tag, string Attribute) x, (string Tag, string Attribute) y) => string.Equals(x.Tag, y.Tag, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Attribute, y.Attribute, StringComparison.OrdinalIgnoreCase); public int GetHashCode((string Tag, string Attribute) o) => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(o.Tag), StringComparer.OrdinalIgnoreCase.GetHashCode(o.Attribute)); }
internal sealed class MutableAttributeStat { public int EsOccurrenceCount { get; set; } public int EnOccurrenceCount { get; set; } public int PairedOccurrenceCount { get; set; } public int SameValueCount { get; set; } public int DifferentValueCount { get; set; } public int EsOnlyInPairCount { get; set; } public int EnOnlyInPairCount { get; set; } }
internal sealed class MutableTagStat { public int EsIdentityCount { get; set; } public int EnIdentityCount { get; set; } public int BothIdentityCount { get; set; } public int EsOccurrenceCount { get; set; } public int EnOccurrenceCount { get; set; } public int EsParameterizedOccurrenceCount { get; set; } public int EnParameterizedOccurrenceCount { get; set; } public int PairedOccurrenceCount { get; set; } public int ExactAttributeShapePairCount { get; set; } public int DifferingAttributeShapePairCount { get; set; } }
internal sealed class AttributeParityRow { public required string TagName { get; init; } public required string AttributeName { get; init; } public int EsOccurrenceCount { get; init; } public int EnOccurrenceCount { get; init; } public int PairedOccurrenceCount { get; init; } public int SameValueCount { get; init; } public int DifferentValueCount { get; init; } public int EsOnlyInPairCount { get; init; } public int EnOnlyInPairCount { get; init; } public double? SameValueRate { get; init; } public required string SuggestedClass { get; init; } }
internal sealed class TagShapeParityRow { public required string TagName { get; init; } public int EsIdentityCount { get; init; } public int EnIdentityCount { get; init; } public int BothIdentityCount { get; init; } public int EsOccurrenceCount { get; init; } public int EnOccurrenceCount { get; init; } public int EsParameterizedOccurrenceCount { get; init; } public int EnParameterizedOccurrenceCount { get; init; } public int PairedOccurrenceCount { get; init; } public int ExactAttributeShapePairCount { get; init; } public int DifferingAttributeShapePairCount { get; init; } public double? ExactAttributeShapeRate { get; init; } }
internal sealed class AttributeDifferenceSample { public required string Identity { get; init; } public required string Namespace { get; init; } public required string Key { get; init; } public required string TagName { get; init; } public required string AttributeName { get; init; } public required string EsValue { get; init; } public required string EnValue { get; init; } public required string EsToken { get; init; } public required string EnToken { get; init; } public required string EsTextPreview { get; init; } public required string EnTextPreview { get; init; } }
internal sealed class ParseFailureSample { public required string Locale { get; init; } public required string Identity { get; init; } public required string Token { get; init; } public required string Error { get; init; } }
internal sealed class ProbeSummary { public required string EsVirtualPath { get; init; } public required string EnVirtualPath { get; init; } public required string EsContainerName { get; init; } public required string EnContainerName { get; init; } public long EsReadOrder { get; init; } public long EnReadOrder { get; init; } public int EsEntryCount { get; init; } public int EnEntryCount { get; init; } public int SharedIdentityCount { get; init; } public int TotalEsAngleTagInstances { get; init; } public int TotalEnAngleTagInstances { get; init; } public int EsParameterizedTagInstances { get; init; } public int EnParameterizedTagInstances { get; init; } public int PairedTagInstances { get; init; } public int ExactAttributeShapePairCount { get; init; } public int DifferingAttributeShapePairCount { get; init; } public int DistinctTagNameCount { get; init; } public int DistinctTagAttributePairCount { get; init; } public int AttributePairsSuggestedInvariant { get; init; } public int AttributePairsSuggestedLocalizable { get; init; } public int AttributePairsMixedOrContextual { get; init; } public int AttributePairsLowEvidence { get; init; } public int ParseFailureCount { get; init; } public int ParseFailureSamplesWritten { get; init; } }
internal sealed class TemporaryAesCompatibility : IDisposable { private readonly string? _path; private readonly bool _had; private readonly byte[]? _previous; private TemporaryAesCompatibility(string? p, bool h, byte[]? b) { _path = p; _had = h; _previous = b; } public static TemporaryAesCompatibility Install(string root, string key) { if (string.IsNullOrEmpty(key)) return new(null, false, null); var p = Path.Combine(root, "aes.txt"); var h = File.Exists(p); var b = h ? File.ReadAllBytes(p) : null; File.WriteAllText(p, key, Encoding.ASCII); return new(p, h, b); } public void Dispose() { if (_path is null) return; if (_had && _previous is not null) File.WriteAllBytes(_path, _previous); else if (File.Exists(_path)) File.Delete(_path); } }
internal sealed class AesRedactingTextWriter : TextWriter { private readonly TextWriter _inner; public AesRedactingTextWriter(TextWriter i) => _inner = i; public override Encoding Encoding => _inner.Encoding; public override void Write(char v) => _inner.Write(v); public override void Write(string? v) => _inner.Write(v); public override void WriteLine(string? v) { if (v is not null && v.StartsWith("AES key loaded:", StringComparison.OrdinalIgnoreCase)) _inner.WriteLine("AES key loaded [REDACTED]"); else _inner.WriteLine(v); } }
