using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Localization;
using CUE4Parse.UE4.VirtualFileSystem;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NTE.RuntimeGenderContextProbe;

internal static class Program
{
    private const string SelfieNamespace = "ST_UI_J";
    private const string SelfieKey = "Selfie_117";

    private static readonly string[] Cultures = ["es", "en", "fr", "de", "ru"];

    private static readonly Regex FullGenderRegex = new(
        @"\A<male=>(?<male>.*?)<male><female=>(?<female>.*?)<female>\z",
        RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex GenderSequenceRegex = new(
        @"<male=>|<male>|<female=>|<female>",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

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
            var dialogueRefsPath = Path.GetFullPath(args[1]);
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

            ValidateInputs(gameRoot, dialogueRefsPath, aesConfig, aesFile);
            Directory.CreateDirectory(outputDir);

            Console.WriteLine("NTE Runtime Gender Context Probe 019A");
            Console.WriteLine("Read-only: compares gender syntax across official locales and joins ES gender branches to proven dialogue references.");
            Console.WriteLine();

            var aesKey = LoadAesKey(aesConfig, aesFile);
            using var aesScope = TemporaryAesCompatibility.Install(gameRoot, aesKey);
            using var reader = CreateReaderWithoutLeakingAes(gameRoot);
            var provider = GetProvider(reader);

            var snapshots = Cultures.ToDictionary(c => c, c => LoadCulture(provider, c), StringComparer.OrdinalIgnoreCase);
            var maps = snapshots.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Entries.ToDictionary(x => (x.Namespace, x.Key), x => x.Text),
                StringComparer.OrdinalIgnoreCase);

            var refs = ReadDialogueRefs(dialogueRefsPath);
            var refsByIdentity = refs
                .GroupBy(x => x.Identity, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

            var es = snapshots["es"];
            var esGenderEntries = es.Entries
                .Select(x => new { Entry = x, Match = FullGenderRegex.Match(x.Text) })
                .Where(x => x.Match.Success)
                .ToList();

            var candidates = new List<GenderDialogueCandidate>();
            foreach (var row in esGenderEntries)
            {
                var identity = Identity(row.Entry.Namespace, row.Entry.Key);
                if (!refsByIdentity.TryGetValue(identity, out var identityRefs) || identityRefs.Count == 0)
                    continue;

                var cultureEvidence = Cultures.ToDictionary(
                    c => c,
                    c => BuildCultureEvidence(c, maps[c].GetValueOrDefault((row.Entry.Namespace, row.Entry.Key)) ?? string.Empty),
                    StringComparer.OrdinalIgnoreCase);

                var roles = identityRefs.Select(x => x.Role).Distinct(StringComparer.Ordinal).OrderBy(x => x).ToArray();
                var packages = identityRefs.Select(x => x.PackagePath).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray();
                var owners = identityRefs.Select(x => x.OwnerName).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).OrderBy(x => x).ToArray();

                candidates.Add(new GenderDialogueCandidate
                {
                    Identity = identity,
                    Namespace = row.Entry.Namespace,
                    Key = row.Entry.Key,
                    EsText = row.Entry.Text,
                    EsMaleText = row.Match.Groups["male"].Value,
                    EsFemaleText = row.Match.Groups["female"].Value,
                    ReferenceCount = identityRefs.Count,
                    SpeechReferenceCount = identityRefs.Count(x => x.Role == "speech"),
                    ChoiceReferenceCount = identityRefs.Count(x => x.Role == "choice"),
                    PackageCount = packages.Length,
                    Roles = roles,
                    Packages = packages,
                    Owners = owners,
                    CultureEvidence = cultureEvidence,
                    Score = Score(identityRefs, row.Entry.Text, cultureEvidence)
                });
            }

            var ordered = candidates
                .OrderBy(x => x.Score)
                .ThenByDescending(x => x.SpeechReferenceCount)
                .ThenBy(x => x.Identity, StringComparer.Ordinal)
                .ToList();

            WriteJsonl(Path.Combine(outputDir, "gender-dialogue-candidates.jsonl"), ordered);
            WriteJson(Path.Combine(outputDir, "top-gender-dialogue-candidates.json"), ordered.Take(50).ToList());

            var selfie = BuildIdentityCrossLocale(SelfieNamespace, SelfieKey, maps);
            WriteJson(Path.Combine(outputDir, "selfie-117-cross-locale.json"), selfie);

            var summary = new ProbeSummary
            {
                Success = true,
                EsEntryCount = es.Entries.Count,
                EsFullGenderBranchCount = esGenderEntries.Count,
                DialogueReferenceRowCount = refs.Count,
                DialogueReferencedGenderIdentityCount = ordered.Count,
                SpeechReferencedGenderIdentityCount = ordered.Count(x => x.SpeechReferenceCount > 0),
                ChoiceReferencedGenderIdentityCount = ordered.Count(x => x.ChoiceReferenceCount > 0),
                SelfieIdentity = Identity(SelfieNamespace, SelfieKey),
                SelfieEsFullGenderShape = selfie.Cultures["es"].FullGenderShape,
                SelfieEsMarkerSequence = selfie.Cultures["es"].MarkerSequence,
                CultureSources = snapshots.ToDictionary(
                    x => x.Key,
                    x => new CultureSource(x.Value.ContainerName, x.Value.ReadOrder, x.Value.Entries.Count),
                    StringComparer.OrdinalIgnoreCase)
            };
            WriteJson(Path.Combine(outputDir, "summary.json"), summary);

            Console.WriteLine($"ES entries: {summary.EsEntryCount}");
            Console.WriteLine($"ES exact full gender branches: {summary.EsFullGenderBranchCount}");
            Console.WriteLine($"Dialogue reference rows loaded: {summary.DialogueReferenceRowCount}");
            Console.WriteLine($"Gender identities referenced by dialogue: {summary.DialogueReferencedGenderIdentityCount}");
            Console.WriteLine($"...with speech refs: {summary.SpeechReferencedGenderIdentityCount}");
            Console.WriteLine();
            Console.WriteLine("Selfie_117 cross-locale:");
            foreach (var culture in Cultures)
            {
                var ev = selfie.Cultures[culture];
                Console.WriteLine($"- {culture}: genderShape={ev.FullGenderShape} markers=[{string.Join(",", ev.MarkerSequence)}] text='{Compact(ev.Text)}'");
            }
            Console.WriteLine();
            Console.WriteLine("Top dialogue gender candidates:");
            foreach (var c in ordered.Take(12))
            {
                Console.WriteLine($"[{c.Score}] {c.Identity} speech={c.SpeechReferenceCount} choice={c.ChoiceReferenceCount} packages={c.PackageCount}");
                Console.WriteLine($"  ES male: {Compact(c.EsMaleText)}");
                Console.WriteLine($"  ES female: {Compact(c.EsFemaleText)}");
                if (c.Packages.Length > 0) Console.WriteLine($"  package: {c.Packages[0]}");
                if (c.Owners.Length > 0) Console.WriteLine($"  owner: {string.Join(", ", c.Owners)}");
            }

            Console.WriteLine();
            Console.WriteLine($"Reports: {outputDir}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex);
            return 1;
        }
    }

    private static int Score(List<DialogueRef> refs, string esText, Dictionary<string, CultureEvidence> cultures)
    {
        var score = 1000;
        if (refs.Any(x => x.Role == "speech")) score -= 500;
        if (refs.Any(x => x.Role == "choice")) score -= 100;
        var packageCount = refs.Select(x => x.PackagePath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (packageCount == 1) score -= 100;
        else score += Math.Min(200, packageCount * 10);
        if (refs.Any(x => !string.IsNullOrWhiteSpace(x.OwnerName))) score -= 80;
        if (esText.Length <= 140) score -= 80;
        else if (esText.Length > 300) score += 120;
        if (!cultures["en"].FullGenderShape) score -= 30; // useful evidence of locale-introduced gender handling
        return score;
    }

    private static IdentityCrossLocale BuildIdentityCrossLocale(
        string ns,
        string key,
        Dictionary<string, Dictionary<(string Namespace, string Key), string>> maps)
    {
        var cultures = Cultures.ToDictionary(
            c => c,
            c => BuildCultureEvidence(c, maps[c].GetValueOrDefault((ns, key)) ?? string.Empty),
            StringComparer.OrdinalIgnoreCase);
        return new IdentityCrossLocale
        {
            Identity = Identity(ns, key),
            Namespace = ns,
            Key = key,
            Cultures = cultures
        };
    }

    private static CultureEvidence BuildCultureEvidence(string culture, string text)
    {
        var match = FullGenderRegex.Match(text);
        return new CultureEvidence
        {
            Culture = culture,
            Text = text,
            FullGenderShape = match.Success,
            MaleText = match.Success ? match.Groups["male"].Value : string.Empty,
            FemaleText = match.Success ? match.Groups["female"].Value : string.Empty,
            MarkerSequence = GenderSequenceRegex.Matches(text).Select(x => x.Value).ToArray(),
            Utf8Hex = Convert.ToHexString(Encoding.UTF8.GetBytes(text))
        };
    }

    private static List<DialogueRef> ReadDialogueRefs(string path)
    {
        var rows = new List<DialogueRef>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            rows.Add(new DialogueRef
            {
                Identity = GetString(root, "identity"),
                Namespace = GetString(root, "namespace"),
                Key = GetString(root, "key"),
                Role = GetString(root, "role"),
                PackagePath = GetString(root, "packagePath"),
                ExportName = GetString(root, "exportName"),
                OwnerName = GetString(root, "ownerName")
            });
        }
        return rows;
    }

    private static string GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

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
        foreach (var (textKey, entry) in values)
            entries.Add(new LocresEntry(nsKey.Str, textKey.Str, entry.LocalizedString ?? string.Empty));

        return new CultureSnapshot(culture, virtualPath, source.ContainerName, source.ReadOrder, entries);
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
        try { Console.SetOut(TextWriter.Null); return new UnrealArchiveReader(gameRoot); }
        finally { Console.SetOut(originalOut); }
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

    private static void ValidateInputs(string gameRoot, string refs, string? aesConfig, string? aesFile)
    {
        if (!Directory.Exists(gameRoot)) throw new DirectoryNotFoundException(gameRoot);
        if (!File.Exists(refs)) throw new FileNotFoundException("Dialogue identity references not found.", refs);
        if (aesConfig is not null && !File.Exists(aesConfig)) throw new FileNotFoundException("AES config not found.", aesConfig);
        if (aesFile is not null && !File.Exists(aesFile)) throw new FileNotFoundException("AES file not found.", aesFile);
        if (aesConfig is not null && aesFile is not null) throw new ArgumentException("Use either --aes-config or --aes-file, not both.");
    }

    private static string NormalizePath(string value) => value.Replace('\\', '/');
    private static string Identity(string ns, string key) => ns + "::" + key;
    private static string Compact(string value)
    {
        var s = value.Replace("\r", " ").Replace("\n", " ");
        return s.Length <= 150 ? s : s[..147] + "...";
    }

    private static void WriteJson(string path, object value) =>
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonIndented) + Environment.NewLine, new UTF8Encoding(false));
    private static void WriteJsonl<T>(string path, IEnumerable<T> rows)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        foreach (var row in rows) writer.WriteLine(JsonSerializer.Serialize(row, JsonLine));
    }

    private static readonly JsonSerializerOptions JsonIndented = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private static readonly JsonSerializerOptions JsonLine = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };

    private static void PrintUsage() => Console.WriteLine("Usage: NTE.RuntimeGenderContextProbe <gameRoot> <identity-references.jsonl> <outputDir> [--aes-config=<json> | --aes-file=<txt>]");
}

internal sealed record SourceDescription(string ContainerName, long ReadOrder);
internal sealed record LocresEntry(string Namespace, string Key, string Text);
internal sealed record CultureSnapshot(string Culture, string VirtualPath, string ContainerName, long ReadOrder, List<LocresEntry> Entries);
internal sealed record CultureSource(string ContainerName, long ReadOrder, int EntryCount);

internal sealed class DialogueRef
{
    public required string Identity { get; init; }
    public required string Namespace { get; init; }
    public required string Key { get; init; }
    public required string Role { get; init; }
    public required string PackagePath { get; init; }
    public required string ExportName { get; init; }
    public required string OwnerName { get; init; }
}

internal sealed class CultureEvidence
{
    public required string Culture { get; init; }
    public required string Text { get; init; }
    public bool FullGenderShape { get; init; }
    public required string MaleText { get; init; }
    public required string FemaleText { get; init; }
    public required string[] MarkerSequence { get; init; }
    public required string Utf8Hex { get; init; }
}

internal sealed class IdentityCrossLocale
{
    public required string Identity { get; init; }
    public required string Namespace { get; init; }
    public required string Key { get; init; }
    public required Dictionary<string, CultureEvidence> Cultures { get; init; }
}

internal sealed class GenderDialogueCandidate
{
    public required string Identity { get; init; }
    public required string Namespace { get; init; }
    public required string Key { get; init; }
    public required string EsText { get; init; }
    public required string EsMaleText { get; init; }
    public required string EsFemaleText { get; init; }
    public int ReferenceCount { get; init; }
    public int SpeechReferenceCount { get; init; }
    public int ChoiceReferenceCount { get; init; }
    public int PackageCount { get; init; }
    public required string[] Roles { get; init; }
    public required string[] Packages { get; init; }
    public required string[] Owners { get; init; }
    public required Dictionary<string, CultureEvidence> CultureEvidence { get; init; }
    public int Score { get; init; }
}

internal sealed class ProbeSummary
{
    public bool Success { get; init; }
    public int EsEntryCount { get; init; }
    public int EsFullGenderBranchCount { get; init; }
    public int DialogueReferenceRowCount { get; init; }
    public int DialogueReferencedGenderIdentityCount { get; init; }
    public int SpeechReferencedGenderIdentityCount { get; init; }
    public int ChoiceReferencedGenderIdentityCount { get; init; }
    public required string SelfieIdentity { get; init; }
    public bool SelfieEsFullGenderShape { get; init; }
    public required string[] SelfieEsMarkerSequence { get; init; }
    public required Dictionary<string, CultureSource> CultureSources { get; init; }
}

internal sealed class TemporaryAesCompatibility : IDisposable
{
    private readonly string? _path;
    private readonly bool _hadExisting;
    private readonly byte[]? _previous;
    private TemporaryAesCompatibility(string? path, bool hadExisting, byte[]? previous) { _path = path; _hadExisting = hadExisting; _previous = previous; }
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
