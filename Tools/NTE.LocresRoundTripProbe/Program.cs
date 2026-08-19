using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Localization;
using CUE4Parse.UE4.VirtualFileSystem;
using LocresWriter;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NTE.LocresRoundTripProbe;

internal static class Program
{
    private const int SamplesPerCategory = 12;

    private static readonly Regex GenderMacroRegex = new(
        @"\{Gender\}\|gender\(",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex BraceTokenRegex = new(
        @"\{[^{}\r\n]+\}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AngleTokenRegex = new(
        @"<[^>\r\n]+>",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SquareTokenRegex = new(
        @"\[[^\]\r\n]+\]",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex PercentTokenRegex = new(
        @"%(?:\d+\$)?[A-Za-z]",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

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
            var artifactsDir = Path.Combine(outputDir, "artifacts-local-only");
            Directory.CreateDirectory(artifactsDir);

            var aesKey = LoadAesKey(aesConfig, aesFile);

            Console.WriteLine("NTE LOCRES Round-Trip Probe 009");
            Console.WriteLine("Purpose: inventory real ES structures and test a no-op rewrite without modifying the game");
            Console.WriteLine();

            using var aesScope = TemporaryAesCompatibility.Install(gameRoot, aesKey);
            using var reader = CreateReaderWithoutLeakingAes(gameRoot);
            var provider = GetProvider(reader);

            var esVirtualPath = provider.Files.Keys
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(x => NormalizePath(x).EndsWith(
                    "/Content/Localization/Game/es/Game.locres",
                    StringComparison.OrdinalIgnoreCase));

            if (esVirtualPath is null)
                throw new FileNotFoundException("Effective ES Game.locres virtual path was not found in the mounted provider.");

            if (!provider.Files.TryGetValue(esVirtualPath, out var effectiveEs) || effectiveEs is null)
                throw new InvalidOperationException($"Provider could not resolve the effective ES locres: {esVirtualPath}");

            var source = DescribeSource(effectiveEs);
            var originalBytes = effectiveEs.Read();
            var originalSha = Sha256(originalBytes);
            var originalPath = Path.Combine(artifactsDir, "effective-es-original.locres");
            var noopPath = Path.Combine(artifactsDir, "effective-es-noop-roundtrip.locres");
            File.WriteAllBytes(originalPath, originalBytes);

            Console.WriteLine($"ES virtual path: {esVirtualPath}");
            Console.WriteLine($"Effective container: {source.ContainerName} (readOrder {source.ReadOrder})");
            Console.WriteLine($"Original SHA-256: {originalSha}");
            Console.WriteLine();

            var structureCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var angleTagFrequency = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var braceTokenFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
            var squareTokenFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
            var percentTokenFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
            var samples = new List<StructureSample>();
            var sampleCountByCategory = new Dictionary<string, int>(StringComparer.Ordinal);

            var parsedEntryCount = 0;
            var entriesWithRecognizedStructure = 0;

            using (var ar = effectiveEs.CreateReader())
            {
                var locres = new FTextLocalizationResource(ar);
                foreach (var (nsKey, entries) in locres.Entries)
                {
                    foreach (var (textKey, entry) in entries)
                    {
                        parsedEntryCount++;
                        var text = entry.LocalizedString ?? string.Empty;
                        var identity = string.IsNullOrEmpty(nsKey.Str)
                            ? textKey.Str
                            : $"{nsKey.Str}::{textKey.Str}";

                        var categories = Classify(
                            text,
                            angleTagFrequency,
                            braceTokenFrequency,
                            squareTokenFrequency,
                            percentTokenFrequency);

                        if (categories.Count > 0)
                            entriesWithRecognizedStructure++;

                        foreach (var category in categories)
                        {
                            Increment(structureCounts, category);
                            if (sampleCountByCategory.GetValueOrDefault(category) < SamplesPerCategory)
                            {
                                samples.Add(new StructureSample
                                {
                                    Category = category,
                                    Identity = identity,
                                    Namespace = nsKey.Str,
                                    Key = textKey.Str,
                                    Text = text
                                });
                                sampleCountByCategory[category] = sampleCountByCategory.GetValueOrDefault(category) + 1;
                            }
                        }
                    }
                }
            }

            WriteJsonl(
                Path.Combine(outputDir, "structure-samples.jsonl"),
                samples.OrderBy(x => x.Category, StringComparer.Ordinal).ThenBy(x => x.Identity, StringComparer.Ordinal));
            WriteFrequency(Path.Combine(outputDir, "angle-tag-frequency.jsonl"), angleTagFrequency);
            WriteFrequency(Path.Combine(outputDir, "brace-token-frequency.jsonl"), braceTokenFrequency);
            WriteFrequency(Path.Combine(outputDir, "square-token-frequency.jsonl"), squareTokenFrequency);
            WriteFrequency(Path.Combine(outputDir, "percent-token-frequency.jsonl"), percentTokenFrequency);

            var structureSummary = new StructureInventorySummary
            {
                ParsedEntryCount = parsedEntryCount,
                EntriesWithRecognizedStructure = entriesWithRecognizedStructure,
                CategoryCounts = structureCounts.OrderBy(x => x.Key, StringComparer.Ordinal)
                    .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
                DistinctAngleTagNames = angleTagFrequency.Count,
                DistinctBraceTokens = braceTokenFrequency.Count,
                DistinctSquareTokens = squareTokenFrequency.Count,
                DistinctPercentTokens = percentTokenFrequency.Count,
                SamplesPerCategoryLimit = SamplesPerCategory
            };

            File.WriteAllText(
                Path.Combine(outputDir, "structure-summary.json"),
                JsonSerializer.Serialize(structureSummary, JsonIndented),
                new UTF8Encoding(false));

            Console.WriteLine("Running no-op template patch...");
            LocresCompactWriter.Patch(originalPath, null, noopPath);

            var noopBytes = File.ReadAllBytes(noopPath);
            var noopSha = Sha256(noopBytes);
            var originalLayout = InspectRawLayout(originalBytes);
            var noopLayout = InspectRawLayout(noopBytes);
            var binaryComparison = CompareBytes(originalBytes, noopBytes);

            var roundTrip = new RoundTripReport
            {
                OriginalSha256 = originalSha,
                RoundTripSha256 = noopSha,
                OriginalSize = originalBytes.LongLength,
                RoundTripSize = noopBytes.LongLength,
                ByteIdentical = binaryComparison.ByteIdentical,
                FirstDifferentOffset = binaryComparison.FirstDifferentOffset,
                DifferingByteCount = binaryComparison.DifferingByteCount,
                PrefixThroughKeySectionByteIdentical = originalLayout.StringTableOffset == noopLayout.StringTableOffset &&
                    originalLayout.PrefixThroughKeySectionSha256 == noopLayout.PrefixThroughKeySectionSha256,
                OriginalLayout = originalLayout,
                RoundTripLayout = noopLayout
            };

            File.WriteAllText(
                Path.Combine(outputDir, "roundtrip.json"),
                JsonSerializer.Serialize(roundTrip, JsonIndented),
                new UTF8Encoding(false));

            var summary = new ProbeSummary
            {
                VirtualPath = esVirtualPath,
                ContainerName = source.ContainerName,
                ContainerPath = source.ContainerPath,
                ReadOrder = source.ReadOrder,
                EffectiveEsSha256 = originalSha,
                ParsedEntryCount = parsedEntryCount,
                EntriesWithRecognizedStructure = entriesWithRecognizedStructure,
                NoOpRoundTripByteIdentical = roundTrip.ByteIdentical,
                NoOpRoundTripPrefixByteIdentical = roundTrip.PrefixThroughKeySectionByteIdentical,
                OriginalRefCounts = originalLayout.RefCountHistogram,
                RoundTripRefCounts = noopLayout.RefCountHistogram,
                LocalArtifactsDirectory = artifactsDir
            };

            File.WriteAllText(
                Path.Combine(outputDir, "summary.json"),
                JsonSerializer.Serialize(summary, JsonIndented),
                new UTF8Encoding(false));

            Console.WriteLine();
            Console.WriteLine($"Parsed ES entries: {parsedEntryCount}");
            Console.WriteLine($"Entries with recognized structure: {entriesWithRecognizedStructure}");
            Console.WriteLine($"No-op byte-identical: {roundTrip.ByteIdentical}");
            Console.WriteLine($"Header + key section identical: {roundTrip.PrefixThroughKeySectionByteIdentical}");
            if (!roundTrip.ByteIdentical)
                Console.WriteLine($"First byte difference: {roundTrip.FirstDifferentOffset}; differing bytes: {roundTrip.DifferingByteCount}");
            Console.WriteLine($"Reports: {outputDir}");
            Console.WriteLine($"Raw .locres artifacts remain local only: {artifactsDir}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex}");
            return 1;
        }
    }

    private static HashSet<string> Classify(
        string text,
        Dictionary<string, int> angleTags,
        Dictionary<string, int> braceTokens,
        Dictionary<string, int> squareTokens,
        Dictionary<string, int> percentTokens)
    {
        var categories = new HashSet<string>(StringComparer.Ordinal);

        var hasMaleOpen = text.Contains("<male=>", StringComparison.OrdinalIgnoreCase);
        var hasFemaleOpen = text.Contains("<female=>", StringComparison.OrdinalIgnoreCase);
        if (hasMaleOpen && hasFemaleOpen) categories.Add("male_female_branch");
        else if (hasMaleOpen || hasFemaleOpen) categories.Add("partial_gender_branch_marker");

        if (GenderMacroRegex.IsMatch(text)) categories.Add("gender_macro");
        if (text.Contains("<!>", StringComparison.Ordinal)) categories.Add("internal_marker_bang");
        if (text.Contains("\\n", StringComparison.Ordinal)) categories.Add("literal_backslash_n");

        var braceMatches = BraceTokenRegex.Matches(text);
        if (braceMatches.Count > 0)
        {
            categories.Add("brace_token");
            foreach (Match match in braceMatches)
                Increment(braceTokens, match.Value);
        }

        var angleMatches = AngleTokenRegex.Matches(text);
        if (angleMatches.Count > 0)
        {
            categories.Add("angle_markup");
            foreach (Match match in angleMatches)
            {
                var name = NormalizeAngleTagName(match.Value);
                if (!string.IsNullOrEmpty(name)) Increment(angleTags, name);
            }
        }

        var squareMatches = SquareTokenRegex.Matches(text);
        if (squareMatches.Count > 0)
        {
            categories.Add("square_markup");
            foreach (Match match in squareMatches)
                Increment(squareTokens, match.Value);
        }

        var percentMatches = PercentTokenRegex.Matches(text);
        if (percentMatches.Count > 0)
        {
            categories.Add("percent_format_token");
            foreach (Match match in percentMatches)
                Increment(percentTokens, match.Value);
        }

        if (text.Contains("<Typing", StringComparison.OrdinalIgnoreCase)) categories.Add("typing_markup");
        if (text.Contains("<hot", StringComparison.OrdinalIgnoreCase)) categories.Add("hot_markup");
        if (text.Contains("<NumGreen", StringComparison.OrdinalIgnoreCase)) categories.Add("numgreen_markup");

        return categories;
    }

    private static string NormalizeAngleTagName(string token)
    {
        var inner = token.Trim();
        if (inner.Length < 3 || inner[0] != '<' || inner[^1] != '>') return string.Empty;
        inner = inner[1..^1].Trim();
        if (inner.StartsWith('/')) inner = inner[1..].TrimStart();
        if (inner.Length == 0) return string.Empty;

        var cut = inner.IndexOfAny([' ', '\t', '=', '/']);
        var name = cut >= 0 ? inner[..cut] : inner;
        return name.Trim();
    }

    private static RawLocresLayout InspectRawLayout(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        var magic = Convert.ToHexString(r.ReadBytes(16));
        var ueVersion = r.ReadByte();
        var nteVersion = r.ReadInt32();
        var encryptedFlag = r.ReadInt32();
        var stringTableOffset = r.ReadInt64();

        var totalEntries = r.ReadUInt32();
        var namespaceCount = r.ReadUInt32();
        for (var i = 0u; i < namespaceCount; i++)
        {
            r.ReadUInt32();
            SkipFString(r);
            var keyCount = r.ReadUInt32();
            for (var j = 0u; j < keyCount; j++)
            {
                r.ReadUInt32();
                SkipFString(r);
                r.ReadInt32();
                r.ReadInt32();
            }
        }

        var parsedKeySectionEnd = ms.Position;
        if (stringTableOffset < 0 || stringTableOffset > bytes.LongLength)
            throw new InvalidDataException($"Invalid string table offset: {stringTableOffset}");

        var prefixHash = Sha256(bytes.AsSpan(0, checked((int)stringTableOffset)));

        ms.Seek(stringTableOffset, SeekOrigin.Begin);
        var stringCount = r.ReadUInt32();
        var refCounts = new Dictionary<int, int>();
        for (var i = 0u; i < stringCount; i++)
        {
            SkipFString(r);
            var refCount = r.ReadInt32();
            Increment(refCounts, refCount);
        }

        return new RawLocresLayout
        {
            MagicHex = magic,
            UeVersion = ueVersion,
            NteVersion = nteVersion,
            IsEncrypted = encryptedFlag != 0,
            StringTableOffset = stringTableOffset,
            TotalEntries = totalEntries,
            NamespaceCount = namespaceCount,
            ParsedKeySectionEnd = parsedKeySectionEnd,
            ParsedKeySectionEndsAtDeclaredOffset = parsedKeySectionEnd == stringTableOffset,
            PrefixThroughKeySectionSha256 = prefixHash,
            StringCount = stringCount,
            RefCountHistogram = refCounts.OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Value),
            EndOffset = ms.Position,
            TrailingByteCount = bytes.LongLength - ms.Position
        };
    }

    private static void SkipFString(BinaryReader r)
    {
        var length = r.ReadInt32();
        if (length == 0) return;
        long byteCount = length > 0 ? length : -(long)length * 2L;
        if (byteCount < 0 || r.BaseStream.Position + byteCount > r.BaseStream.Length)
            throw new InvalidDataException($"Invalid FString byte length {byteCount} at offset {r.BaseStream.Position - 4}.");
        r.BaseStream.Seek(byteCount, SeekOrigin.Current);
    }

    private static BinaryComparison CompareBytes(byte[] left, byte[] right)
    {
        var commonLength = Math.Min(left.LongLength, right.LongLength);
        long differing = 0;
        long? first = null;
        for (long i = 0; i < commonLength; i++)
        {
            if (left[i] == right[i]) continue;
            differing++;
            first ??= i;
        }

        if (left.LongLength != right.LongLength)
        {
            first ??= commonLength;
            differing += Math.Abs(left.LongLength - right.LongLength);
        }

        return new BinaryComparison(differing == 0, first, differing);
    }

    private static SourceDescription DescribeSource(GameFile file)
    {
        if (file is VfsEntry entry)
            return new SourceDescription(entry.Vfs.Name, entry.Vfs.Path, entry.Vfs.ReadOrder);
        return new SourceDescription("<non-vfs>", string.Empty, 0);
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static void Increment<TKey>(Dictionary<TKey, int> dictionary, TKey key) where TKey : notnull
        => dictionary[key] = dictionary.GetValueOrDefault(key) + 1;

    private static void WriteFrequency(string path, Dictionary<string, int> values)
    {
        var rows = values
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => new FrequencyRow { Token = x.Key, Count = x.Value });
        WriteJsonl(path, rows);
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
                throw new InvalidDataException("The AES config does not contain an 'aes_key' property.");
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
            writer.WriteLine(JsonSerializer.Serialize(value, JsonCompact));
    }

    private static readonly JsonSerializerOptions JsonCompact = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private static readonly JsonSerializerOptions JsonIndented = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  NTE.LocresRoundTripProbe <gameRoot> <outputDir> [--aes-config=<path> | --aes-file=<path>]");
    }
}

internal sealed record SourceDescription(string ContainerName, string ContainerPath, long ReadOrder);
internal sealed record BinaryComparison(bool ByteIdentical, long? FirstDifferentOffset, long DifferingByteCount);

internal sealed class StructureSample
{
    public required string Category { get; init; }
    public required string Identity { get; init; }
    public required string Namespace { get; init; }
    public required string Key { get; init; }
    public required string Text { get; init; }
}

internal sealed class FrequencyRow
{
    public required string Token { get; init; }
    public int Count { get; init; }
}

internal sealed class StructureInventorySummary
{
    public int ParsedEntryCount { get; init; }
    public int EntriesWithRecognizedStructure { get; init; }
    public Dictionary<string, int> CategoryCounts { get; init; } = new();
    public int DistinctAngleTagNames { get; init; }
    public int DistinctBraceTokens { get; init; }
    public int DistinctSquareTokens { get; init; }
    public int DistinctPercentTokens { get; init; }
    public int SamplesPerCategoryLimit { get; init; }
}

internal sealed class RawLocresLayout
{
    public required string MagicHex { get; init; }
    public byte UeVersion { get; init; }
    public int NteVersion { get; init; }
    public bool IsEncrypted { get; init; }
    public long StringTableOffset { get; init; }
    public uint TotalEntries { get; init; }
    public uint NamespaceCount { get; init; }
    public long ParsedKeySectionEnd { get; init; }
    public bool ParsedKeySectionEndsAtDeclaredOffset { get; init; }
    public required string PrefixThroughKeySectionSha256 { get; init; }
    public uint StringCount { get; init; }
    public Dictionary<int, int> RefCountHistogram { get; init; } = new();
    public long EndOffset { get; init; }
    public long TrailingByteCount { get; init; }
}

internal sealed class RoundTripReport
{
    public required string OriginalSha256 { get; init; }
    public required string RoundTripSha256 { get; init; }
    public long OriginalSize { get; init; }
    public long RoundTripSize { get; init; }
    public bool ByteIdentical { get; init; }
    public long? FirstDifferentOffset { get; init; }
    public long DifferingByteCount { get; init; }
    public bool PrefixThroughKeySectionByteIdentical { get; init; }
    public required RawLocresLayout OriginalLayout { get; init; }
    public required RawLocresLayout RoundTripLayout { get; init; }
}

internal sealed class ProbeSummary
{
    public required string VirtualPath { get; init; }
    public required string ContainerName { get; init; }
    public required string ContainerPath { get; init; }
    public long ReadOrder { get; init; }
    public required string EffectiveEsSha256 { get; init; }
    public int ParsedEntryCount { get; init; }
    public int EntriesWithRecognizedStructure { get; init; }
    public bool NoOpRoundTripByteIdentical { get; init; }
    public bool NoOpRoundTripPrefixByteIdentical { get; init; }
    public Dictionary<int, int> OriginalRefCounts { get; init; } = new();
    public Dictionary<int, int> RoundTripRefCounts { get; init; } = new();
    public required string LocalArtifactsDirectory { get; init; }
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
