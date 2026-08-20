using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Localization;
using CUE4Parse.UE4.VirtualFileSystem;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NTE.RuntimeStructureCandidateProbe;

internal static class Program
{
    private const int TopPerCategory = 30;

    private static readonly Regex BraceRegex = new(
        @"\{[^{}\r\n]+\}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SquareControlRegex = new(
        @"\[[A-Za-z][A-Za-z0-9_:\-]*/\]",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SimpleAngleRegex = new(
        @"<(?:NumGreen|Blue|Orange|red|Yellow|Grey|Green|Italic|Title|lv)>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly string[] UiKeyHints =
    [
        "setting", "audio", "sound", "volume", "music", "voice", "language",
        "display", "graphic", "resolution", "quality", "control", "keyboard",
        "mouse", "camera", "confirm", "cancel", "save", "apply", "close",
        "back", "return", "exit", "menu", "account", "title", "name"
    ];

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

            Console.WriteLine("NTE Runtime Structure Candidate Probe 018A");
            Console.WriteLine("Read-only: finds real ES structured identities that are good candidates for a visible runtime microtest.");
            Console.WriteLine("No game archives or mod files are modified.");
            Console.WriteLine();

            var aesKey = LoadAesKey(aesConfig, aesFile);
            using var aesScope = TemporaryAesCompatibility.Install(gameRoot, aesKey);
            using var reader = CreateReaderWithoutLeakingAes(gameRoot);
            var provider = GetProvider(reader);

            var es = LoadCulture(provider, "es");
            var en = LoadCulture(provider, "en");
            var enMap = en.Entries.ToDictionary(x => (x.Namespace, x.Key), x => x.Text);

            var candidates = new List<Candidate>();
            foreach (var entry in es.Entries)
            {
                var categories = Classify(entry.Text);
                if (categories.Count == 0) continue;

                var identity = (entry.Namespace, entry.Key);
                enMap.TryGetValue(identity, out var enText);
                foreach (var category in categories)
                {
                    candidates.Add(new Candidate
                    {
                        Category = category,
                        Identity = Identity(entry.Namespace, entry.Key),
                        Namespace = entry.Namespace,
                        Key = entry.Key,
                        EsText = entry.Text,
                        EnText = enText ?? string.Empty,
                        Score = Score(entry.Namespace, entry.Key, entry.Text, category),
                        UiLikely = IsUiNamespace(entry.Namespace),
                        RuntimeHint = BuildHint(entry.Namespace, entry.Key, category)
                    });
                }
            }

            var ordered = candidates
                .OrderBy(x => x.Category, StringComparer.Ordinal)
                .ThenBy(x => x.Score)
                .ThenBy(x => x.Identity, StringComparer.Ordinal)
                .ToList();

            WriteJsonl(Path.Combine(outputDir, "candidates.jsonl"), ordered);

            var top = ordered
                .GroupBy(x => x.Category, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => g.Take(TopPerCategory).ToList(),
                    StringComparer.Ordinal);
            WriteJson(Path.Combine(outputDir, "top-by-category.json"), top);

            var counts = ordered
                .GroupBy(x => x.Category, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            var uiCounts = ordered
                .Where(x => x.UiLikely)
                .GroupBy(x => x.Category, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

            var summary = new ProbeSummary
            {
                Success = true,
                EsVirtualPath = es.VirtualPath,
                EsContainer = es.ContainerName,
                EsReadOrder = es.ReadOrder,
                EnVirtualPath = en.VirtualPath,
                EnContainer = en.ContainerName,
                EnReadOrder = en.ReadOrder,
                EsEntryCount = es.Entries.Count,
                EnEntryCount = en.Entries.Count,
                CandidateRowCount = ordered.Count,
                CategoryCounts = counts,
                UiLikelyCategoryCounts = uiCounts,
                TopPerCategory = TopPerCategory
            };
            WriteJson(Path.Combine(outputDir, "summary.json"), summary);

            Console.WriteLine($"Effective ES: {es.ContainerName} (readOrder {es.ReadOrder}) | entries={es.Entries.Count}");
            Console.WriteLine($"Effective EN: {en.ContainerName} (readOrder {en.ReadOrder}) | entries={en.Entries.Count}");
            Console.WriteLine($"Structured candidate rows: {ordered.Count}");
            Console.WriteLine();

            foreach (var category in top.Keys.OrderBy(x => x, StringComparer.Ordinal))
            {
                var rows = top[category];
                Console.WriteLine($"== {category} | total={counts[category]} | uiLikely={uiCounts.GetValueOrDefault(category)} ==");
                foreach (var row in rows.Take(8))
                {
                    Console.WriteLine($"[{row.Score}] {row.Identity}");
                    Console.WriteLine($"  ES: {Compact(row.EsText)}");
                    if (!string.IsNullOrWhiteSpace(row.EnText))
                        Console.WriteLine($"  EN: {Compact(row.EnText)}");
                }
                Console.WriteLine();
            }

            Console.WriteLine($"Reports: {outputDir}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex);
            return 1;
        }
    }

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

    private static HashSet<string> Classify(string text)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (text.Contains("<male=>", StringComparison.OrdinalIgnoreCase) &&
            text.Contains("<male>", StringComparison.OrdinalIgnoreCase) &&
            text.Contains("<female=>", StringComparison.OrdinalIgnoreCase) &&
            text.Contains("<female>", StringComparison.OrdinalIgnoreCase))
            result.Add("gender_branch");

        if (BraceRegex.IsMatch(text)) result.Add("brace_placeholder");
        if (text.Contains("<NumGreen>", StringComparison.OrdinalIgnoreCase)) result.Add("numgreen");
        if (text.Contains("<TypingTitle", StringComparison.OrdinalIgnoreCase)) result.Add("typing_title");
        if (text.Contains("<TypingImg", StringComparison.OrdinalIgnoreCase)) result.Add("typing_img");
        if (Regex.IsMatch(text, @"<img\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) result.Add("img");
        if (Regex.IsMatch(text, @"<hot\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) result.Add("hot");
        if (SquareControlRegex.IsMatch(text)) result.Add("square_control");
        if (SimpleAngleRegex.IsMatch(text)) result.Add("simple_angle");
        return result;
    }

    private static int Score(string ns, string key, string text, string category)
    {
        var score = 500;
        if (string.Equals(ns, "ST_Ui", StringComparison.Ordinal)) score -= 350;
        else if (IsUiNamespace(ns)) score -= 250;
        else if (ns.Contains("System", StringComparison.OrdinalIgnoreCase) || ns.Contains("Common", StringComparison.OrdinalIgnoreCase)) score -= 100;

        for (var i = 0; i < UiKeyHints.Length; i++)
        {
            if (key.Contains(UiKeyHints[i], StringComparison.OrdinalIgnoreCase))
            {
                score -= Math.Max(20, 120 - i * 3);
                break;
            }
        }

        if (text.Length <= 80) score -= 50;
        else if (text.Length <= 160) score -= 20;
        else score += Math.Min(200, text.Length / 4);

        if (category == "typing_title") score -= 25;
        if (category == "numgreen") score -= 15;
        if (category == "brace_placeholder") score -= 10;
        if (category == "gender_branch") score += IsUiNamespace(ns) ? 0 : 80;

        return score;
    }

    private static bool IsUiNamespace(string ns) =>
        string.Equals(ns, "ST_Ui", StringComparison.Ordinal) ||
        ns.Contains("Ui", StringComparison.OrdinalIgnoreCase) ||
        ns.Contains("UI", StringComparison.Ordinal);

    private static string BuildHint(string ns, string key, string category)
    {
        if (string.Equals(ns, "ST_Ui", StringComparison.Ordinal))
            return $"UI candidate; key={key}; inspect settings/menus matching the ES/EN text.";
        if (category == "gender_branch")
            return "Gender-branch candidate; likely needs narrative/context lookup before runtime selection.";
        return "Structured candidate; use namespace/key plus ES/EN text to locate runtime surface.";
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

    private static SourceDescription DescribeSource(GameFile file)
    {
        return file is VfsEntry entry
            ? new SourceDescription(entry.Vfs.Name, entry.Vfs.ReadOrder)
            : new SourceDescription("<non-vfs>", 0);
    }

    private static string LoadAesKey(string? aesConfig, string? aesFile)
    {
        if (aesFile is not null)
            return File.ReadAllText(aesFile).Trim();
        if (aesConfig is null)
            return string.Empty;

        using var doc = JsonDocument.Parse(File.ReadAllText(aesConfig));
        if (!doc.RootElement.TryGetProperty("aes_key", out var property))
            throw new InvalidDataException("AES config does not contain aes_key.");
        return property.GetString()?.Trim() ?? string.Empty;
    }

    private static void ValidateInputs(string gameRoot, string? aesConfig, string? aesFile)
    {
        if (!Directory.Exists(gameRoot))
            throw new DirectoryNotFoundException(gameRoot);
        if (aesConfig is not null && !File.Exists(aesConfig))
            throw new FileNotFoundException("AES config not found.", aesConfig);
        if (aesFile is not null && !File.Exists(aesFile))
            throw new FileNotFoundException("AES file not found.", aesFile);
        if (aesConfig is not null && aesFile is not null)
            throw new ArgumentException("Use either --aes-config or --aes-file, not both.");
    }

    private static string NormalizePath(string value) => value.Replace('\\', '/');
    private static string Identity(string ns, string key) => ns + "::" + key;
    private static string Compact(string value)
    {
        var single = value.Replace("\r", " ").Replace("\n", " ");
        return single.Length <= 180 ? single : single[..177] + "...";
    }

    private static void WriteJson(string path, object value) =>
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonIndented) + Environment.NewLine, new UTF8Encoding(false));

    private static void WriteJsonl<T>(string path, IEnumerable<T> rows)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        foreach (var row in rows)
            writer.WriteLine(JsonSerializer.Serialize(row, JsonLine));
    }

    private static readonly JsonSerializerOptions JsonIndented = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly JsonSerializerOptions JsonLine = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static void PrintUsage() =>
        Console.WriteLine("Usage: NTE.RuntimeStructureCandidateProbe <gameRoot> <outputDir> [--aes-config=<json> | --aes-file=<txt>]");
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

internal sealed class Candidate
{
    public required string Category { get; init; }
    public required string Identity { get; init; }
    public required string Namespace { get; init; }
    public required string Key { get; init; }
    public required string EsText { get; init; }
    public required string EnText { get; init; }
    public int Score { get; init; }
    public bool UiLikely { get; init; }
    public required string RuntimeHint { get; init; }
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
    public int CandidateRowCount { get; init; }
    public required Dictionary<string, int> CategoryCounts { get; init; }
    public required Dictionary<string, int> UiLikelyCategoryCounts { get; init; }
    public int TopPerCategory { get; init; }
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
