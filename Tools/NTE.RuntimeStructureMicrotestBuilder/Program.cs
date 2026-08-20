using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Localization;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.VirtualFileSystem;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NTE.RuntimeStructureMicrotestBuilder;

internal static class Program
{
    private const string SplitMarker = "HottaLocresSplit";
    private static readonly byte[] NteLocresKey = Convert.FromHexString(
        "396d4330686f704b4e6a5377694364684e56375974435765754476484c513238");

    private static readonly Regex StructuralTokenRegex = new(
        @"\{[^{}\r\n]+\}|<[^>\r\n]+>",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly TestCase[] Cases =
    [
        new(
            "placeholder_settings",
            "ST_Common",
            "ui_setting_option_fullscreen",
            "{0} PTBR018 PLACEHOLDER",
            "Settings > display/window mode; source is the Full-screen option label."),
        new(
            "title_markup_mint",
            "Mint_SkillDes",
            "GA_Mint_Melee1_name",
            "<Title>PTBR018 TITLE</>",
            "Mint character > skill/basic attack name; Title markup must render without leaking raw tags."),
        new(
            "numgreen_markup_mint",
            "Mint_SkillDes",
            "GA_Mint_Melee1_des",
            "PTBR018 NUMGREEN: realiza hasta <NumGreen>5</> ataques e inflige <Ling>daño de ánima</>.",
            "Mint character > basic attack description; NumGreen/Ling markup must remain functional."),
        new(
            "gender_selfie",
            "ST_UI_J",
            "Selfie_117",
            "<male=>PTBR018 MASCULINO<male><female=>PTBR018 FEMININO<female>",
            "Selfie/photo expression list; source is Confundido/Confundida. Runtime should choose one branch and hide markers.")
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
            var artifactsDir = Path.Combine(outputDir, "artifacts-local-only");
            Directory.CreateDirectory(artifactsDir);

            Console.WriteLine("NTE Runtime Structure Microtest Builder 018B");
            Console.WriteLine("Purpose: mutate four known ES identities while preserving source-relative structural tokens.");
            Console.WriteLine("In-place only: every selected identity must own a unique RefCount=1 string record.");
            Console.WriteLine("This builder does not install anything into the game.");
            Console.WriteLine();

            var aesKey = LoadAesKey(aesConfig, aesFile);
            using var aesScope = TemporaryAesCompatibility.Install(gameRoot, aesKey);
            using var reader = CreateReaderWithoutLeakingAes(gameRoot);
            var provider = GetProvider(reader);

            var source = LoadEffectiveEs(provider);
            var raw = ParseRaw(source.Bytes);
            var baseline = ParseSemantic(source.Bytes, source.VirtualPath);
            if (!baseline.Success)
                throw new InvalidOperationException("ES baseline semantic parse failed: " + baseline.ErrorMessage);

            var keyMap = raw.KeyEntries.ToDictionary(x => (x.Namespace, x.Key));
            var refs = raw.KeyEntries.GroupBy(x => x.StringIndex).ToDictionary(g => g.Key, g => g.Count());
            var overrides = new Dictionary<int, string>();
            var plans = new List<MutationPlan>();

            foreach (var test in Cases)
            {
                var identity = (test.Namespace, test.Key);
                if (!keyMap.TryGetValue(identity, out var entry))
                    throw new InvalidOperationException($"Required Run 018B identity missing: {test.Namespace}::{test.Key}");
                if (!baseline.Entries.TryGetValue(identity, out var semantic))
                    throw new InvalidOperationException($"Required Run 018B semantic identity missing: {test.Namespace}::{test.Key}");

                var record = RecordAt(raw, entry.StringIndex);
                var actualRefs = refs.TryGetValue(entry.StringIndex, out var count) ? count : 0;
                if (!record.DecryptSucceeded)
                    throw new InvalidDataException($"Could not decrypt {test.Namespace}::{test.Key}: {record.DecryptError}");
                if (record.RefCount != 1 || actualRefs != 1)
                    throw new InvalidOperationException(
                        $"Run 018B target is not in-place safe: {test.Namespace}::{test.Key} strIdx={entry.StringIndex} refCount={record.RefCount} refs={actualRefs}");
                if (!string.Equals(record.PlainText, semantic.LocalizedString, StringComparison.Ordinal))
                    throw new InvalidDataException($"Raw/semantic source mismatch for {test.Namespace}::{test.Key}.");

                var sourceTokens = StructuralTokens(record.PlainText);
                var candidateTokens = StructuralTokens(test.CandidateText);
                if (!sourceTokens.SequenceEqual(candidateTokens, StringComparer.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Structural token sequence mismatch for {test.Namespace}::{test.Key}. " +
                        $"source=[{string.Join(", ", sourceTokens)}] candidate=[{string.Join(", ", candidateTokens)}]");
                }
                if (!overrides.TryAdd(entry.StringIndex, test.CandidateText))
                    throw new InvalidOperationException($"Two Run 018B identities unexpectedly share strIdx {entry.StringIndex}.");

                plans.Add(new MutationPlan
                {
                    Category = test.Category,
                    Namespace = test.Namespace,
                    Key = test.Key,
                    Identity = Identity(test.Namespace, test.Key),
                    StringIndex = entry.StringIndex,
                    OriginalText = record.PlainText,
                    CandidateText = test.CandidateText,
                    StructuralTokens = sourceTokens,
                    RuntimeHint = test.RuntimeHint
                });
            }

            var candidateBytes = BuildCandidate(raw, overrides);
            var candidatePath = Path.Combine(artifactsDir, "runtime-structure-es.locres");
            File.WriteAllBytes(candidatePath, candidateBytes);

            var candidateSemantic = ParseSemantic(candidateBytes, source.VirtualPath);
            if (!candidateSemantic.Success)
                throw new InvalidOperationException("Candidate semantic parse failed: " + candidateSemantic.ErrorMessage);
            var candidateRaw = ParseRaw(candidateBytes);
            var refValidation = ValidateRefCounts(candidateRaw);

            var expected = Cases.ToDictionary(x => (x.Namespace, x.Key), x => x.CandidateText);
            var missing = baseline.Entries.Keys.Except(candidateSemantic.Entries.Keys).ToList();
            var extra = candidateSemantic.Entries.Keys.Except(baseline.Entries.Keys).ToList();
            var shared = baseline.Entries.Keys.Intersect(candidateSemantic.Entries.Keys).ToList();
            var mismatches = shared.Where(k => baseline.Entries[k].LocalizedString != candidateSemantic.Entries[k].LocalizedString).ToList();
            var unexpected = mismatches.Where(k => !expected.ContainsKey(k)).ToList();
            var selectedWrong = expected.Count(x =>
                !candidateSemantic.Entries.TryGetValue(x.Key, out var value) ||
                !string.Equals(value.LocalizedString, x.Value, StringComparison.Ordinal));
            var hashMismatches = shared.Count(k =>
                baseline.Entries[k].NamespaceHash != candidateSemantic.Entries[k].NamespaceHash ||
                baseline.Entries[k].KeyHash != candidateSemantic.Entries[k].KeyHash);

            foreach (var plan in plans)
            {
                var candidateText = candidateSemantic.Entries[(plan.Namespace, plan.Key)].LocalizedString;
                plan.CandidateSemanticText = candidateText;
                plan.StructurePreserved = StructuralTokens(plan.OriginalText)
                    .SequenceEqual(StructuralTokens(candidateText), StringComparer.Ordinal);
            }

            WriteJsonl(Path.Combine(outputDir, "mutation-plan.jsonl"), plans);

            var success = missing.Count == 0 &&
                          extra.Count == 0 &&
                          unexpected.Count == 0 &&
                          selectedWrong == 0 &&
                          hashMismatches == 0 &&
                          refValidation.RefCountMismatchCount == 0 &&
                          refValidation.InvalidStringIndexCount == 0 &&
                          raw.StringRecords.Count == candidateRaw.StringRecords.Count &&
                          mismatches.Count == Cases.Length &&
                          plans.All(x => x.StructurePreserved);

            var summary = new RunSummary
            {
                Success = success,
                VirtualPath = source.VirtualPath,
                ContainerName = source.ContainerName,
                ReadOrder = source.ReadOrder,
                OriginalSha256 = Sha256(source.Bytes),
                CandidateSha256 = Sha256(candidateBytes),
                BaselineEntryCount = baseline.Entries.Count,
                CandidateEntryCount = candidateSemantic.Entries.Count,
                OriginalStringRecordCount = raw.StringRecords.Count,
                CandidateStringRecordCount = candidateRaw.StringRecords.Count,
                ExpectedChangedIdentityCount = Cases.Length,
                ActualChangedIdentityCount = mismatches.Count,
                MissingIdentityCount = missing.Count,
                ExtraIdentityCount = extra.Count,
                UnexpectedChangedIdentityCount = unexpected.Count,
                SelectedIdentityWrongValueCount = selectedWrong,
                NamespaceOrKeyHashMismatchCount = hashMismatches,
                RefCountMismatchCount = refValidation.RefCountMismatchCount,
                InvalidStringIndexCount = refValidation.InvalidStringIndexCount,
                StructurePreservedCount = plans.Count(x => x.StructurePreserved),
                CandidatePath = candidatePath
            };
            WriteJson(Path.Combine(outputDir, "summary.json"), summary);

            Console.WriteLine($"Effective ES: {source.ContainerName} (readOrder {source.ReadOrder})");
            Console.WriteLine($"Selected identities: {plans.Count}");
            foreach (var plan in plans)
            {
                Console.WriteLine($"- {plan.Category}: {plan.Identity}");
                Console.WriteLine($"  ES : {Compact(plan.OriginalText)}");
                Console.WriteLine($"  TEST: {Compact(plan.CandidateText)}");
                Console.WriteLine($"  HINT: {plan.RuntimeHint}");
            }
            Console.WriteLine();
            Console.WriteLine($"Actual changed identities: {mismatches.Count}/{Cases.Length}");
            Console.WriteLine($"Unexpected changes: {unexpected.Count}");
            Console.WriteLine($"Hash mismatches: {hashMismatches}");
            Console.WriteLine($"RefCount mismatches: {refValidation.RefCountMismatchCount}");
            Console.WriteLine($"Structure preserved: {summary.StructurePreservedCount}/{Cases.Length}");
            Console.WriteLine($"Builder success: {success}");
            return success ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex);
            return 1;
        }
    }

    private static SlotSource LoadEffectiveEs(DefaultFileProvider provider)
    {
        const string suffix = "/Content/Localization/Game/es/Game.locres";
        var virtualPath = provider.Files.Keys
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(x => NormalizePath(x).EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException("Effective ES Game.locres virtual path was not found.");
        if (!provider.Files.TryGetValue(virtualPath, out var file) || file is null)
            throw new InvalidOperationException($"Provider could not resolve effective ES LOCRES: {virtualPath}");
        var source = DescribeSource(file);
        return new SlotSource(virtualPath, source.ContainerName, source.ReadOrder, file.Read());
    }

    private static string[] StructuralTokens(string text) => StructuralTokenRegex.Matches(text)
        .Select(x => x.Value)
        .ToArray();

    private static StringRecord RecordAt(RawLocresDocument doc, int index)
    {
        if (index < 0 || index >= doc.StringRecords.Count)
            throw new InvalidDataException($"Invalid strIdx {index}.");
        return doc.StringRecords[index];
    }

    private static byte[] BuildCandidate(RawLocresDocument source, Dictionary<int, string> overrides)
    {
        var capacity = checked((int)(source.FileSize + 64L * 1024L));
        using var ms = new MemoryStream(capacity);
        ms.Write(source.Prefix);
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write((uint)source.StringRecords.Count);
        foreach (var record in source.StringRecords)
        {
            var encoded = overrides.TryGetValue(record.Index, out var replacement)
                ? EncryptNteStringPkcs7(replacement)
                : record.EncodedText;
            WriteFString(w, encoded, record.Storage);
            w.Write(record.RefCount);
        }
        w.Flush();
        return ms.ToArray();
    }

    private static RawLocresDocument ParseRaw(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
        ReadExact(r, 16);
        var ueVersion = r.ReadByte();
        var nteVersion = r.ReadInt32();
        var encrypted = r.ReadInt32() != 0;
        var stringTableOffset = r.ReadInt64();
        if (ueVersion != 3 || nteVersion < 10100 || !encrypted)
            throw new InvalidDataException($"Unexpected NTE LOCRES header: ue={ueVersion} nte={nteVersion} encrypted={encrypted}");

        var totalEntries = r.ReadUInt32();
        var namespaceCount = r.ReadUInt32();
        var keys = new List<KeyEntry>(checked((int)totalEntries));
        for (var i = 0u; i < namespaceCount; i++)
        {
            var nsHash = r.ReadUInt32();
            var ns = ReadFString(r).Value;
            var keyCount = r.ReadUInt32();
            for (var j = 0u; j < keyCount; j++)
            {
                var keyHash = r.ReadUInt32();
                var key = ReadFString(r).Value;
                var sourceHash = r.ReadInt32();
                var stringIndex = r.ReadInt32();
                keys.Add(new KeyEntry(ns, nsHash, key, keyHash, sourceHash, stringIndex));
            }
        }
        if (keys.Count != totalEntries)
            throw new InvalidDataException($"Key count mismatch: header={totalEntries} parsed={keys.Count}.");
        if (ms.Position != stringTableOffset)
            throw new InvalidDataException($"Key section ended at {ms.Position}; expected {stringTableOffset}.");

        var prefix = bytes.AsSpan(0, checked((int)stringTableOffset)).ToArray();
        var stringCount = r.ReadUInt32();
        var records = new List<StringRecord>(checked((int)stringCount));
        for (var i = 0; i < stringCount; i++)
        {
            var value = ReadFString(r);
            var refCount = r.ReadInt32();
            var decrypted = TryDecrypt(value.Value);
            records.Add(new StringRecord(i, value.Storage, value.Value, refCount, decrypted.PlainText, decrypted.Success, decrypted.Error));
        }
        if (ms.Position != ms.Length)
            throw new InvalidDataException($"String table ended at {ms.Position}; file length is {ms.Length}.");
        return new RawLocresDocument(prefix, keys, records, bytes.LongLength);
    }

    private static RefValidation ValidateRefCounts(RawLocresDocument doc)
    {
        var counts = new int[doc.StringRecords.Count];
        var invalid = 0;
        foreach (var key in doc.KeyEntries)
        {
            if (key.StringIndex < 0 || key.StringIndex >= counts.Length)
            {
                invalid++;
                continue;
            }
            counts[key.StringIndex]++;
        }
        var mismatches = 0;
        for (var i = 0; i < doc.StringRecords.Count; i++)
            if (doc.StringRecords[i].RefCount != counts[i]) mismatches++;
        return new RefValidation(mismatches, invalid);
    }

    private static SemanticSnapshot ParseSemantic(byte[] bytes, string virtualPath)
    {
        try
        {
            var versions = new VersionContainer(EGame.GAME_NevernessToEverness);
            using var archive = new FByteArchive(virtualPath, bytes, versions);
            var resource = new FTextLocalizationResource(archive);
            var entries = new Dictionary<(string Ns, string Key), SemanticEntry>();
            foreach (var (nsKey, values) in resource.Entries)
            foreach (var (textKey, entry) in values)
                entries[(nsKey.Str, textKey.Str)] = new SemanticEntry(nsKey.StrHash, textKey.StrHash, entry.LocalizedString ?? string.Empty);
            return new SemanticSnapshot(true, null, entries);
        }
        catch (Exception ex)
        {
            return new SemanticSnapshot(false, ex.Message, []);
        }
    }

    private static string EncryptNteStringPkcs7(string plain)
    {
        var input = Encoding.UTF8.GetBytes(plain + SplitMarker);
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        return Convert.ToBase64String(aes.CreateEncryptor(NteLocresKey, null).TransformFinalBlock(input, 0, input.Length))
            .Replace('+', '-').Replace('/', '_');
    }

    private static DecryptResult TryDecrypt(string encoded)
    {
        try
        {
            var cipher = Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/'));
            using var aes = Aes.Create();
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            var plainBytes = aes.CreateDecryptor(NteLocresKey, null).TransformFinalBlock(cipher, 0, cipher.Length);
            var decoded = Encoding.UTF8.GetString(plainBytes);
            var marker = decoded.IndexOf(SplitMarker, StringComparison.Ordinal);
            return marker < 0
                ? new DecryptResult(false, string.Empty, "HottaLocresSplit marker not found")
                : new DecryptResult(true, decoded[..marker], null);
        }
        catch (Exception ex)
        {
            return new DecryptResult(false, string.Empty, ex.Message);
        }
    }

    private static FStringRead ReadFString(BinaryReader r)
    {
        var length = r.ReadInt32();
        if (length == 0) return new FStringRead(string.Empty, FStringStorage.Empty);
        if (length > 0)
        {
            var bytes = ReadExact(r, length);
            var count = bytes.Length > 0 && bytes[^1] == 0 ? bytes.Length - 1 : bytes.Length;
            return new FStringRead(Encoding.UTF8.GetString(bytes, 0, count), FStringStorage.Ansi);
        }
        var chars = checked(-length);
        var wide = ReadExact(r, checked(chars * 2));
        var wideCount = wide.Length >= 2 && wide[^1] == 0 && wide[^2] == 0 ? wide.Length - 2 : wide.Length;
        return new FStringRead(Encoding.Unicode.GetString(wide, 0, wideCount), FStringStorage.Wide);
    }

    private static void WriteFString(BinaryWriter w, string value, FStringStorage storage)
    {
        if (string.IsNullOrEmpty(value))
        {
            w.Write(0);
            return;
        }
        if (storage == FStringStorage.Wide)
        {
            w.Write(-(value.Length + 1));
            w.Write(Encoding.Unicode.GetBytes(value));
            w.Write((short)0);
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(value);
        w.Write(bytes.Length + 1);
        w.Write(bytes);
        w.Write((byte)0);
    }

    private static byte[] ReadExact(BinaryReader r, int count)
    {
        var bytes = r.ReadBytes(count);
        if (bytes.Length != count)
            throw new EndOfStreamException($"Expected {count} bytes, got {bytes.Length}.");
        return bytes;
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

    private static void ValidateInputs(string gameRoot, string? aesConfig, string? aesFile)
    {
        if (!Directory.Exists(gameRoot)) throw new DirectoryNotFoundException(gameRoot);
        if (aesConfig is not null && !File.Exists(aesConfig)) throw new FileNotFoundException("AES config not found.", aesConfig);
        if (aesFile is not null && !File.Exists(aesFile)) throw new FileNotFoundException("AES file not found.", aesFile);
        if (aesConfig is not null && aesFile is not null) throw new ArgumentException("Use either --aes-config or --aes-file, not both.");
    }

    private static string NormalizePath(string value) => value.Replace('\\', '/');
    private static string Identity(string ns, string key) => ns + "::" + key;
    private static string Compact(string value) => value.Length <= 140 ? value : value[..137] + "...";
    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static void WriteJson(string path, object value) =>
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, Encoding.UTF8);

    private static void WriteJsonl<T>(string path, IEnumerable<T> rows)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        foreach (var row in rows) writer.WriteLine(JsonSerializer.Serialize(row, JsonOptions));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static void PrintUsage() =>
        Console.WriteLine("Usage: NTE.RuntimeStructureMicrotestBuilder <gameRoot> <outputDir> [--aes-config=<json> | --aes-file=<txt>]");
}

internal sealed record TestCase(string Category, string Namespace, string Key, string CandidateText, string RuntimeHint);
internal enum FStringStorage { Empty, Ansi, Wide }
internal sealed record FStringRead(string Value, FStringStorage Storage);
internal sealed record DecryptResult(bool Success, string PlainText, string? Error);
internal sealed record SourceDescription(string ContainerName, long ReadOrder);
internal sealed record KeyEntry(string Namespace, uint NamespaceHash, string Key, uint KeyHash, int SourceHash, int StringIndex);
internal sealed record StringRecord(int Index, FStringStorage Storage, string EncodedText, int RefCount, string PlainText, bool DecryptSucceeded, string? DecryptError);
internal sealed record RawLocresDocument(byte[] Prefix, List<KeyEntry> KeyEntries, List<StringRecord> StringRecords, long FileSize);
internal sealed record SemanticEntry(uint NamespaceHash, uint KeyHash, string LocalizedString);
internal sealed record SemanticSnapshot(bool Success, string? ErrorMessage, Dictionary<(string Ns, string Key), SemanticEntry> Entries);
internal sealed record RefValidation(int RefCountMismatchCount, int InvalidStringIndexCount);
internal sealed record SlotSource(string VirtualPath, string ContainerName, long ReadOrder, byte[] Bytes);

internal sealed class MutationPlan
{
    public required string Category { get; init; }
    public required string Namespace { get; init; }
    public required string Key { get; init; }
    public required string Identity { get; init; }
    public int StringIndex { get; init; }
    public required string OriginalText { get; init; }
    public required string CandidateText { get; init; }
    public required string[] StructuralTokens { get; init; }
    public required string RuntimeHint { get; init; }
    public string? CandidateSemanticText { get; set; }
    public bool StructurePreserved { get; set; }
}

internal sealed class RunSummary
{
    public bool Success { get; init; }
    public required string VirtualPath { get; init; }
    public required string ContainerName { get; init; }
    public long ReadOrder { get; init; }
    public required string OriginalSha256 { get; init; }
    public required string CandidateSha256 { get; init; }
    public int BaselineEntryCount { get; init; }
    public int CandidateEntryCount { get; init; }
    public int OriginalStringRecordCount { get; init; }
    public int CandidateStringRecordCount { get; init; }
    public int ExpectedChangedIdentityCount { get; init; }
    public int ActualChangedIdentityCount { get; init; }
    public int MissingIdentityCount { get; init; }
    public int ExtraIdentityCount { get; init; }
    public int UnexpectedChangedIdentityCount { get; init; }
    public int SelectedIdentityWrongValueCount { get; init; }
    public int NamespaceOrKeyHashMismatchCount { get; init; }
    public int RefCountMismatchCount { get; init; }
    public int InvalidStringIndexCount { get; init; }
    public int StructurePreservedCount { get; init; }
    public required string CandidatePath { get; init; }
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
