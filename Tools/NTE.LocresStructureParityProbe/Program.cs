using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Localization;
using CUE4Parse.UE4.VirtualFileSystem;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NTE.LocresStructureParityProbe;

internal static class Program
{
    private const int MismatchSampleLimit = 250;

    private static readonly Regex AngleRegex = new(@"<[^>\r\n]+>", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BraceRegex = new(@"\{[^{}\r\n]+\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SquareRegex = new(@"\[[^\]\r\n]+\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PercentRegex = new(@"%(?:\d+\$)?[A-Za-z]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GenderMacroRegex = new(@"\{Gender\}\|gender\(", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

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

            Console.WriteLine("NTE LOCRES Structure Parity Probe 013");
            Console.WriteLine("Purpose: compare ES and EN special-token structure across aligned localization identities");
            Console.WriteLine("Offline only; game archives are not modified.");
            Console.WriteLine();

            var aesKey = LoadAesKey(aesConfig, aesFile);
            using var aesScope = TemporaryAesCompatibility.Install(gameRoot, aesKey);
            using var reader = CreateReaderWithoutLeakingAes(gameRoot);
            var provider = GetProvider(reader);

            var esPath = FindLocres(provider, "/Content/Localization/Game/es/Game.locres");
            var enPath = FindLocres(provider, "/Content/Localization/Game/en/game.locres");
            var esFileResolved = Resolve(provider, esPath);
            var enFileResolved = Resolve(provider, enPath);

            var es = ParseLocale(esFileResolved);
            var en = ParseLocale(enFileResolved);

            var esSource = DescribeSource(esFileResolved);
            var enSource = DescribeSource(enFileResolved);

            var shared = es.Keys.Intersect(en.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var esOnly = es.Keys.Except(en.Keys, StringComparer.Ordinal).ToList();
            var enOnly = en.Keys.Except(es.Keys, StringComparer.Ordinal).ToList();

            var tokenStats = new Dictionary<(string Family, string Token), MutableTokenStat>();
            var angleNameStats = new Dictionary<string, MutableTokenStat>(StringComparer.OrdinalIgnoreCase);
            var familyStats = new Dictionary<string, MutableFamilyStat>(StringComparer.Ordinal);
            var mismatches = new List<MismatchSample>();

            var esStructured = 0;
            var enStructured = 0;
            var eitherStructured = 0;
            var bothStructured = 0;
            var exactSignature = 0;
            var divergentSignature = 0;

            var esMaleFemale = 0;
            var enMaleFemale = 0;
            var bothMaleFemale = 0;
            var esOnlyMaleFemale = 0;
            var enOnlyMaleFemale = 0;
            var esGenderMacro = 0;
            var enGenderMacro = 0;
            var bothGenderMacro = 0;

            foreach (var identity in shared)
            {
                var esEntry = es[identity];
                var enEntry = en[identity];
                var ep = Tokenize(esEntry.Text);
                var np = Tokenize(enEntry.Text);

                if (ep.HasAny) esStructured++;
                if (np.HasAny) enStructured++;
                if (ep.HasAny || np.HasAny) eitherStructured++;
                if (ep.HasAny && np.HasAny) bothStructured++;

                var signatureEqual = ep.Signature.SequenceEqual(np.Signature, StringComparer.Ordinal);
                if (signatureEqual) exactSignature++;
                else if (ep.HasAny || np.HasAny) divergentSignature++;

                var eMF = HasMaleFemaleBranch(esEntry.Text);
                var nMF = HasMaleFemaleBranch(enEntry.Text);
                if (eMF) esMaleFemale++;
                if (nMF) enMaleFemale++;
                if (eMF && nMF) bothMaleFemale++;
                else if (eMF) esOnlyMaleFemale++;
                else if (nMF) enOnlyMaleFemale++;

                var eGM = GenderMacroRegex.IsMatch(esEntry.Text);
                var nGM = GenderMacroRegex.IsMatch(enEntry.Text);
                if (eGM) esGenderMacro++;
                if (nGM) enGenderMacro++;
                if (eGM && nGM) bothGenderMacro++;

                foreach (var family in TokenProfile.Families)
                {
                    var eTokens = ep.ByFamily[family];
                    var nTokens = np.ByFamily[family];
                    var eSet = eTokens.ToHashSet(StringComparer.Ordinal);
                    var nSet = nTokens.ToHashSet(StringComparer.Ordinal);
                    var stat = GetFamily(familyStats, family);
                    if (eTokens.Count > 0) stat.EsIdentityCount++;
                    if (nTokens.Count > 0) stat.EnIdentityCount++;
                    if (eTokens.Count > 0 || nTokens.Count > 0) stat.EitherIdentityCount++;
                    if (eTokens.SequenceEqual(nTokens, StringComparer.Ordinal)) stat.ExactSignatureIdentityCount++;
                    else if (eTokens.Count > 0 || nTokens.Count > 0) stat.DivergentIdentityCount++;

                    foreach (var token in eSet)
                    {
                        var t = GetToken(tokenStats, family, token);
                        t.EsIdentityCount++;
                        if (nSet.Contains(token)) t.BothSameIdentityCount++;
                    }
                    foreach (var token in nSet)
                        GetToken(tokenStats, family, token).EnIdentityCount++;
                }

                foreach (var name in ep.AngleNames.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var t = GetToken(angleNameStats, name);
                    t.EsIdentityCount++;
                    if (np.AngleNames.Contains(name, StringComparer.OrdinalIgnoreCase)) t.BothSameIdentityCount++;
                }
                foreach (var name in np.AngleNames.Distinct(StringComparer.OrdinalIgnoreCase))
                    GetToken(angleNameStats, name).EnIdentityCount++;

                if (!signatureEqual && (ep.HasAny || np.HasAny) && mismatches.Count < MismatchSampleLimit)
                {
                    mismatches.Add(new MismatchSample
                    {
                        Identity = identity,
                        Namespace = esEntry.Namespace,
                        Key = esEntry.Key,
                        EsText = Preview(esEntry.Text),
                        EnText = Preview(enEntry.Text),
                        EsTokens = ep.Signature,
                        EnTokens = np.Signature,
                        DivergentFamilies = TokenProfile.Families
                            .Where(f => !ep.ByFamily[f].SequenceEqual(np.ByFamily[f], StringComparer.Ordinal))
                            .ToArray()
                    });
                }
            }

            var tokenRows = tokenStats
                .Select(x => new TokenStatRow
                {
                    Family = x.Key.Family,
                    Token = x.Key.Token,
                    EsIdentityCount = x.Value.EsIdentityCount,
                    EnIdentityCount = x.Value.EnIdentityCount,
                    BothSameIdentityCount = x.Value.BothSameIdentityCount,
                    EsToEnSameIdentityRate = Rate(x.Value.BothSameIdentityCount, x.Value.EsIdentityCount),
                    EnToEsSameIdentityRate = Rate(x.Value.BothSameIdentityCount, x.Value.EnIdentityCount),
                    LikelySquareControl = x.Key.Family == "square" && x.Key.Token.EndsWith("/]", StringComparison.Ordinal)
                })
                .OrderBy(x => x.Family, StringComparer.Ordinal)
                .ThenByDescending(x => x.EsIdentityCount + x.EnIdentityCount)
                .ThenBy(x => x.Token, StringComparer.Ordinal)
                .ToList();

            var angleRows = angleNameStats
                .Select(x => new TokenStatRow
                {
                    Family = "angle_name",
                    Token = x.Key,
                    EsIdentityCount = x.Value.EsIdentityCount,
                    EnIdentityCount = x.Value.EnIdentityCount,
                    BothSameIdentityCount = x.Value.BothSameIdentityCount,
                    EsToEnSameIdentityRate = Rate(x.Value.BothSameIdentityCount, x.Value.EsIdentityCount),
                    EnToEsSameIdentityRate = Rate(x.Value.BothSameIdentityCount, x.Value.EnIdentityCount)
                })
                .OrderByDescending(x => x.EsIdentityCount + x.EnIdentityCount)
                .ThenBy(x => x.Token, StringComparer.OrdinalIgnoreCase)
                .ToList();

            WriteJsonl(Path.Combine(outputDir, "token-parity.jsonl"), tokenRows);
            WriteJsonl(Path.Combine(outputDir, "angle-tag-name-parity.jsonl"), angleRows);
            WriteJsonl(Path.Combine(outputDir, "structure-mismatch-samples.jsonl"), mismatches);

            var gender = new GenderStructureSummary
            {
                SharedIdentityCount = shared.Count,
                EsMaleFemaleBranchCount = esMaleFemale,
                EnMaleFemaleBranchCount = enMaleFemale,
                BothMaleFemaleBranchCount = bothMaleFemale,
                EsOnlyMaleFemaleBranchCount = esOnlyMaleFemale,
                EnOnlyMaleFemaleBranchCount = enOnlyMaleFemale,
                EsGenderMacroCount = esGenderMacro,
                EnGenderMacroCount = enGenderMacro,
                BothGenderMacroCount = bothGenderMacro
            };
            WriteJson(Path.Combine(outputDir, "gender-structure-summary.json"), gender);

            var familyReport = familyStats
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .ToDictionary(
                    x => x.Key,
                    x => new FamilyStatRow
                    {
                        EsIdentityCount = x.Value.EsIdentityCount,
                        EnIdentityCount = x.Value.EnIdentityCount,
                        EitherIdentityCount = x.Value.EitherIdentityCount,
                        ExactSignatureIdentityCount = x.Value.ExactSignatureIdentityCount,
                        DivergentIdentityCount = x.Value.DivergentIdentityCount
                    },
                    StringComparer.Ordinal);

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
                EsOnlyIdentityCount = esOnly.Count,
                EnOnlyIdentityCount = enOnly.Count,
                EsStructuredIdentityCount = esStructured,
                EnStructuredIdentityCount = enStructured,
                EitherStructuredIdentityCount = eitherStructured,
                BothStructuredIdentityCount = bothStructured,
                ExactOverallSignatureCountAcrossAllSharedIdentities = exactSignature,
                DivergentStructuredSignatureCount = divergentSignature,
                DistinctExactTokenCount = tokenRows.Count,
                DistinctAngleTagNameCount = angleRows.Count,
                FamilyStats = familyReport
            };
            WriteJson(Path.Combine(outputDir, "summary.json"), summary);

            Console.WriteLine($"ES identities: {es.Count}; EN identities: {en.Count}; aligned: {shared.Count}");
            Console.WriteLine($"ES structured: {esStructured}; EN structured: {enStructured}; either: {eitherStructured}");
            Console.WriteLine($"Divergent structured signatures: {divergentSignature}");
            Console.WriteLine($"ES <male>/<female>: {esMaleFemale}; EN: {enMaleFemale}; ES-only: {esOnlyMaleFemale}");
            Console.WriteLine($"Distinct exact tokens: {tokenRows.Count}; angle tag names: {angleRows.Count}");
            Console.WriteLine($"Reports: {outputDir}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex}");
            return 1;
        }
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

    private static TokenProfile Tokenize(string text)
    {
        var byFamily = TokenProfile.Families.ToDictionary(x => x, _ => new List<string>(), StringComparer.Ordinal);
        AddMatches(AngleRegex, text, byFamily["angle"]);
        AddMatches(BraceRegex, text, byFamily["brace"]);
        AddMatches(SquareRegex, text, byFamily["square"]);
        AddMatches(PercentRegex, text, byFamily["percent"]);
        if (GenderMacroRegex.IsMatch(text)) byFamily["gender_operator"].Add("|gender(");
        if (text.Contains("\\n", StringComparison.Ordinal)) byFamily["literal_backslash_n"].Add("\\n");

        foreach (var list in byFamily.Values)
            list.Sort(StringComparer.Ordinal);

        var signature = byFamily
            .SelectMany(x => x.Value.Select(v => $"{x.Key}:{v}"))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        var angleNames = byFamily["angle"]
            .Select(NormalizeAngleTagName)
            .Where(x => x.Length > 0)
            .ToArray();

        return new TokenProfile(byFamily, signature, angleNames);
    }

    private static void AddMatches(Regex regex, string text, List<string> destination)
    {
        foreach (Match match in regex.Matches(text)) destination.Add(match.Value);
    }

    private static string NormalizeAngleTagName(string token)
    {
        var inner = token.Trim();
        if (inner.Length < 3 || inner[0] != '<' || inner[^1] != '>') return string.Empty;
        inner = inner[1..^1].Trim();
        if (inner.StartsWith('/')) inner = inner[1..].TrimStart();
        if (inner.Length == 0) return string.Empty;
        var cut = inner.IndexOfAny([' ', '\t', '=', '/']);
        return (cut >= 0 ? inner[..cut] : inner).Trim();
    }

    private static bool HasMaleFemaleBranch(string text) =>
        text.Contains("<male=>", StringComparison.OrdinalIgnoreCase) &&
        text.Contains("<female=>", StringComparison.OrdinalIgnoreCase) &&
        text.Contains("<male>", StringComparison.OrdinalIgnoreCase) &&
        text.Contains("<female>", StringComparison.OrdinalIgnoreCase);

    private static MutableFamilyStat GetFamily(Dictionary<string, MutableFamilyStat> dictionary, string family)
    {
        if (!dictionary.TryGetValue(family, out var value))
            dictionary[family] = value = new MutableFamilyStat();
        return value;
    }

    private static MutableTokenStat GetToken(Dictionary<(string Family, string Token), MutableTokenStat> dictionary, string family, string token)
    {
        var key = (family, token);
        if (!dictionary.TryGetValue(key, out var value))
            dictionary[key] = value = new MutableTokenStat();
        return value;
    }

    private static MutableTokenStat GetToken(Dictionary<string, MutableTokenStat> dictionary, string token)
    {
        if (!dictionary.TryGetValue(token, out var value))
            dictionary[token] = value = new MutableTokenStat();
        return value;
    }

    private static double? Rate(int numerator, int denominator) => denominator == 0 ? null : Math.Round((double)numerator / denominator, 6);
    private static string Identity(string ns, string key) => string.IsNullOrEmpty(ns) ? key : $"{ns}::{key}";
    private static string Preview(string text)
    {
        const int max = 450;
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
        Console.WriteLine("  NTE.LocresStructureParityProbe <gameRoot> <outputDir> [--aes-config=<path> | --aes-file=<path>]");
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

internal sealed class TokenProfile
{
    public static readonly string[] Families = ["angle", "brace", "square", "percent", "gender_operator", "literal_backslash_n"];
    public Dictionary<string, List<string>> ByFamily { get; }
    public string[] Signature { get; }
    public string[] AngleNames { get; }
    public bool HasAny => Signature.Length > 0;
    public TokenProfile(Dictionary<string, List<string>> byFamily, string[] signature, string[] angleNames)
    {
        ByFamily = byFamily;
        Signature = signature;
        AngleNames = angleNames;
    }
}

internal sealed class MutableTokenStat
{
    public int EsIdentityCount { get; set; }
    public int EnIdentityCount { get; set; }
    public int BothSameIdentityCount { get; set; }
}

internal sealed class MutableFamilyStat
{
    public int EsIdentityCount { get; set; }
    public int EnIdentityCount { get; set; }
    public int EitherIdentityCount { get; set; }
    public int ExactSignatureIdentityCount { get; set; }
    public int DivergentIdentityCount { get; set; }
}

internal sealed class TokenStatRow
{
    public required string Family { get; init; }
    public required string Token { get; init; }
    public int EsIdentityCount { get; init; }
    public int EnIdentityCount { get; init; }
    public int BothSameIdentityCount { get; init; }
    public double? EsToEnSameIdentityRate { get; init; }
    public double? EnToEsSameIdentityRate { get; init; }
    public bool? LikelySquareControl { get; init; }
}

internal sealed class FamilyStatRow
{
    public int EsIdentityCount { get; init; }
    public int EnIdentityCount { get; init; }
    public int EitherIdentityCount { get; init; }
    public int ExactSignatureIdentityCount { get; init; }
    public int DivergentIdentityCount { get; init; }
}

internal sealed class MismatchSample
{
    public required string Identity { get; init; }
    public required string Namespace { get; init; }
    public required string Key { get; init; }
    public required string EsText { get; init; }
    public required string EnText { get; init; }
    public required string[] EsTokens { get; init; }
    public required string[] EnTokens { get; init; }
    public required string[] DivergentFamilies { get; init; }
}

internal sealed class GenderStructureSummary
{
    public int SharedIdentityCount { get; init; }
    public int EsMaleFemaleBranchCount { get; init; }
    public int EnMaleFemaleBranchCount { get; init; }
    public int BothMaleFemaleBranchCount { get; init; }
    public int EsOnlyMaleFemaleBranchCount { get; init; }
    public int EnOnlyMaleFemaleBranchCount { get; init; }
    public int EsGenderMacroCount { get; init; }
    public int EnGenderMacroCount { get; init; }
    public int BothGenderMacroCount { get; init; }
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
    public int EsOnlyIdentityCount { get; init; }
    public int EnOnlyIdentityCount { get; init; }
    public int EsStructuredIdentityCount { get; init; }
    public int EnStructuredIdentityCount { get; init; }
    public int EitherStructuredIdentityCount { get; init; }
    public int BothStructuredIdentityCount { get; init; }
    public int ExactOverallSignatureCountAcrossAllSharedIdentities { get; init; }
    public int DivergentStructuredSignatureCount { get; init; }
    public int DistinctExactTokenCount { get; init; }
    public int DistinctAngleTagNameCount { get; init; }
    public required Dictionary<string, FamilyStatRow> FamilyStats { get; init; }
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
