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
using System.Text.Json.Serialization;

namespace NTE.RuntimeMicrotestBuilder;

internal static class Program
{
    private const string SplitMarker = "HottaLocresSplit";
    private const string SourceMarker = "Ajustes";
    private const string TestMarker = "PTBR TESTE 016";
    private static readonly byte[] NteLocresKey = Convert.FromHexString(
        "396d4330686f704b4e6a5377694364684e56375974435765754476484c513238");

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

            Console.WriteLine("NTE Runtime Microtest Builder 016");
            Console.WriteLine($"Purpose: change effective ES value '{SourceMarker}' to '{TestMarker}' only, then validate before packaging.");
            Console.WriteLine("This builder does NOT install anything into the game.");
            Console.WriteLine();

            var aesKey = LoadAesKey(aesConfig, aesFile);
            using var aesScope = TemporaryAesCompatibility.Install(gameRoot, aesKey);
            using var reader = CreateReaderWithoutLeakingAes(gameRoot);
            var provider = GetProvider(reader);

            var virtualPath = provider.Files.Keys
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(x => NormalizePath(x).EndsWith(
                    "/Content/Localization/Game/es/Game.locres",
                    StringComparison.OrdinalIgnoreCase))
                ?? throw new FileNotFoundException("Effective ES Game.locres virtual path was not found.");

            if (!provider.Files.TryGetValue(virtualPath, out var effectiveEs) || effectiveEs is null)
                throw new InvalidOperationException($"Provider could not resolve effective ES LOCRES: {virtualPath}");

            var source = DescribeSource(effectiveEs);
            var originalBytes = effectiveEs.Read();
            var originalSha = Sha256(originalBytes);
            var raw = ParseRaw(originalBytes);

            var matchingStringRecords = raw.StringRecords
                .Where(x => x.DecryptSucceeded && string.Equals(x.PlainText, SourceMarker, StringComparison.Ordinal))
                .Select(x => x.Index)
                .ToHashSet();
            if (matchingStringRecords.Count == 0)
                throw new InvalidOperationException($"No physical string record equals '{SourceMarker}'.");

            var baseline = ParseSemantic(originalBytes, virtualPath);
            if (!baseline.Success)
                throw new InvalidOperationException("Baseline semantic parse failed: " + baseline.ErrorMessage);

            var expectedIdentities = baseline.Entries
                .Where(x => string.Equals(x.Value.LocalizedString, SourceMarker, StringComparison.Ordinal))
                .Select(x => x.Key)
                .OrderBy(x => x.Ns, StringComparer.Ordinal)
                .ThenBy(x => x.Key, StringComparer.Ordinal)
                .ToList();

            if (expectedIdentities.Count != 5)
                throw new InvalidOperationException($"Expected exactly 5 semantic identities with value '{SourceMarker}', found {expectedIdentities.Count}.");

            var candidateBytes = BuildCandidate(raw, matchingStringRecords);
            var candidatePath = Path.Combine(artifactsDir, "runtime-microtest-es.locres");
            File.WriteAllBytes(candidatePath, candidateBytes);

            var candidate = ParseSemantic(candidateBytes, virtualPath);
            if (!candidate.Success)
                throw new InvalidOperationException("Candidate semantic parse failed: " + candidate.ErrorMessage);

            var missing = baseline.Entries.Keys.Except(candidate.Entries.Keys).ToList();
            var extra = candidate.Entries.Keys.Except(baseline.Entries.Keys).ToList();
            var shared = baseline.Entries.Keys.Intersect(candidate.Entries.Keys).ToList();
            var valueMismatches = shared
                .Where(k => !string.Equals(baseline.Entries[k].LocalizedString, candidate.Entries[k].LocalizedString, StringComparison.Ordinal))
                .ToList();
            var expectedSet = expectedIdentities.ToHashSet();
            var unexpected = valueMismatches.Where(x => !expectedSet.Contains(x)).ToList();
            var selectedWrong = expectedIdentities.Where(k =>
                !candidate.Entries.TryGetValue(k, out var value) ||
                !string.Equals(value.LocalizedString, TestMarker, StringComparison.Ordinal)).ToList();
            var hashMismatches = shared.Count(k =>
                baseline.Entries[k].NamespaceHash != candidate.Entries[k].NamespaceHash ||
                baseline.Entries[k].KeyHash != candidate.Entries[k].KeyHash);

            var changedRows = expectedIdentities.Select(k => new ChangedIdentity
            {
                Namespace = k.Ns,
                Key = k.Key,
                Identity = Identity(k.Ns, k.Key),
                OriginalText = baseline.Entries[k].LocalizedString,
                TestText = candidate.Entries[k].LocalizedString
            }).ToList();
            WriteJsonl(Path.Combine(outputDir, "changed-identities.jsonl"), changedRows);

            var summary = new ProbeSummary
            {
                VirtualPath = virtualPath,
                ContainerName = source.ContainerName,
                ReadOrder = source.ReadOrder,
                OriginalSha256 = originalSha,
                CandidateSha256 = Sha256(candidateBytes),
                OriginalSize = originalBytes.LongLength,
                CandidateSize = candidateBytes.LongLength,
                BaselineEntryCount = baseline.Entries.Count,
                CandidateEntryCount = candidate.Entries.Count,
                MatchingPhysicalStringRecordCount = matchingStringRecords.Count,
                ExpectedChangedIdentityCount = expectedIdentities.Count,
                ActualChangedIdentityCount = valueMismatches.Count,
                MissingIdentityCount = missing.Count,
                ExtraIdentityCount = extra.Count,
                UnexpectedChangedIdentityCount = unexpected.Count,
                SelectedIdentityWrongValueCount = selectedWrong.Count,
                NamespaceOrKeyHashMismatchCount = hashMismatches,
                SourceMarker = SourceMarker,
                TestMarker = TestMarker,
                CandidatePath = candidatePath,
                Success = missing.Count == 0 && extra.Count == 0 && unexpected.Count == 0 &&
                          selectedWrong.Count == 0 && hashMismatches == 0 &&
                          valueMismatches.Count == expectedIdentities.Count
            };
            WriteJson(Path.Combine(outputDir, "summary.json"), summary);

            Console.WriteLine($"Effective ES: {source.ContainerName} (readOrder {source.ReadOrder})");
            Console.WriteLine($"Semantic identities: {baseline.Entries.Count}");
            Console.WriteLine($"Physical records containing '{SourceMarker}': {matchingStringRecords.Count}");
            Console.WriteLine($"Expected identities changed: {expectedIdentities.Count}");
            Console.WriteLine($"Actual identities changed: {valueMismatches.Count}");
            Console.WriteLine($"Unexpected changes: {unexpected.Count}");
            Console.WriteLine($"Hash mismatches: {hashMismatches}");
            Console.WriteLine($"Candidate: {candidatePath}");
            Console.WriteLine($"Builder success: {summary.Success}");
            return summary.Success ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex);
            return 1;
        }
    }

    private static RawLocres ParseRaw(byte[] bytes)
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
        for (var i = 0u; i < namespaceCount; i++)
        {
            r.ReadUInt32();
            ReadFString(r);
            var keyCount = r.ReadUInt32();
            for (var j = 0u; j < keyCount; j++)
            {
                r.ReadUInt32();
                ReadFString(r);
                r.ReadInt32();
                r.ReadInt32();
            }
        }
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
            records.Add(new StringRecord
            {
                Index = checked((int)i),
                Storage = value.Storage,
                EncodedText = value.Value,
                RefCount = refCount,
                PlainText = decrypted.PlainText,
                DecryptSucceeded = decrypted.Success,
                DecryptError = decrypted.Error
            });
        }
        if (ms.Position != ms.Length)
            throw new InvalidDataException($"String table ended at {ms.Position}; file length is {ms.Length}.");
        return new RawLocres(prefix, records);
    }

    private static byte[] BuildCandidate(RawLocres source, HashSet<int> matchingRecords)
    {
        using var ms = new MemoryStream(source.Prefix.Length + 13_000_000);
        ms.Write(source.Prefix);
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write((uint)source.StringRecords.Count);
        foreach (var record in source.StringRecords)
        {
            var encoded = matchingRecords.Contains(record.Index)
                ? EncryptPkcs7(TestMarker)
                : record.EncodedText;
            WriteFString(w, encoded, record.Storage);
            w.Write(record.RefCount);
        }
        w.Flush();
        return ms.ToArray();
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
            {
                foreach (var (textKey, entry) in values)
                {
                    entries[(nsKey.Str, textKey.Str)] = new SemanticEntry(
                        nsKey.StrHash,
                        textKey.StrHash,
                        entry.LocalizedString ?? string.Empty);
                }
            }
            return new SemanticSnapshot(true, null, entries);
        }
        catch (Exception ex)
        {
            return new SemanticSnapshot(false, ex.Message, []);
        }
    }

    private static string EncryptPkcs7(string plain)
    {
        var input = Encoding.UTF8.GetBytes(plain + SplitMarker);
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        return Convert.ToBase64String(aes.CreateEncryptor(NteLocresKey, null)
            .TransformFinalBlock(input, 0, input.Length))
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
            var plainBytes = aes.CreateDecryptor(NteLocresKey, null)
                .TransformFinalBlock(cipher, 0, cipher.Length);
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

    private static FStringValue ReadFString(BinaryReader r)
    {
        var length = r.ReadInt32();
        if (length == 0) return new FStringValue(string.Empty, FStringStorage.Ansi);
        if (length > 0)
        {
            var bytes = ReadExact(r, length);
            var count = bytes.Length > 0 && bytes[^1] == 0 ? bytes.Length - 1 : bytes.Length;
            return new FStringValue(Encoding.UTF8.GetString(bytes, 0, count), FStringStorage.Ansi);
        }
        var chars = checked(-length);
        var bytesWide = ReadExact(r, checked(chars * 2));
        var countWide = bytesWide.Length >= 2 && bytesWide[^1] == 0 && bytesWide[^2] == 0 ? bytesWide.Length - 2 : bytesWide.Length;
        return new FStringValue(Encoding.Unicode.GetString(bytesWide, 0, countWide), FStringStorage.Wide);
    }

    private static void WriteFString(BinaryWriter w, string value, FStringStorage storage)
    {
        if (value.Length == 0)
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
        if (bytes.Length != count) throw new EndOfStreamException($"Expected {count} bytes, got {bytes.Length}.");
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
            Console.SetOut(new AesRedactingTextWriter(originalOut));
            return new UnrealArchiveReader(gameRoot);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    private static SourceDescription DescribeSource(GameFile file)
    {
        if (file is VfsEntry entry) return new SourceDescription(entry.Vfs.Name, entry.Vfs.ReadOrder);
        return new SourceDescription("<non-vfs>", 0);
    }

    private static string LoadAesKey(string? aesConfig, string? aesFile)
    {
        if (aesConfig is null && aesFile is null) return string.Empty;
        string raw;
        if (aesFile is not null) raw = File.ReadAllText(aesFile).Trim();
        else
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(aesConfig!));
            if (!doc.RootElement.TryGetProperty("aes_key", out var property))
                throw new InvalidDataException("AES config does not contain aes_key.");
            raw = property.GetString()?.Trim() ?? string.Empty;
        }
        if (!raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) raw = "0x" + raw;
        if (raw.Length != 66 || raw.Skip(2).Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("AES key must contain exactly 64 hexadecimal digits.");
        return raw;
    }

    private static void ValidateInputs(string gameRoot, string? aesConfig, string? aesFile)
    {
        if (!Directory.Exists(gameRoot)) throw new DirectoryNotFoundException($"Game root not found: {gameRoot}");
        if (aesConfig is not null && aesFile is not null) throw new ArgumentException("Use only one AES source.");
        if (aesConfig is not null && !File.Exists(aesConfig)) throw new FileNotFoundException("AES config not found.", aesConfig);
        if (aesFile is not null && !File.Exists(aesFile)) throw new FileNotFoundException("AES file not found.", aesFile);
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');
    private static string Identity(string ns, string key) => string.IsNullOrEmpty(ns) ? key : $"{ns}::{key}";
    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private static void WriteJson<T>(string path, T value) => File.WriteAllText(
        path, JsonSerializer.Serialize(value, JsonIndented), new UTF8Encoding(false));
    private static void WriteJsonl<T>(string path, IEnumerable<T> values)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        foreach (var value in values) writer.WriteLine(JsonSerializer.Serialize(value, JsonCompact));
    }

    private static void PrintUsage() => Console.WriteLine(
        "NTE.RuntimeMicrotestBuilder <gameRoot> <outputDir> [--aes-config=<path> | --aes-file=<path>]");

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

internal enum FStringStorage { Ansi, Wide }
internal sealed record FStringValue(string Value, FStringStorage Storage);
internal sealed record RawLocres(byte[] Prefix, List<StringRecord> StringRecords);
internal sealed class StringRecord
{
    public int Index { get; init; }
    public FStringStorage Storage { get; init; }
    public required string EncodedText { get; init; }
    public int RefCount { get; init; }
    public required string PlainText { get; init; }
    public bool DecryptSucceeded { get; init; }
    public string? DecryptError { get; init; }
}
internal sealed record DecryptResult(bool Success, string PlainText, string? Error);
internal sealed record SourceDescription(string ContainerName, long ReadOrder);
internal sealed record SemanticEntry(uint NamespaceHash, uint KeyHash, string LocalizedString);
internal sealed record SemanticSnapshot(bool Success, string? ErrorMessage, Dictionary<(string Ns, string Key), SemanticEntry> Entries);
internal sealed class ChangedIdentity
{
    public required string Namespace { get; init; }
    public required string Key { get; init; }
    public required string Identity { get; init; }
    public required string OriginalText { get; init; }
    public required string TestText { get; init; }
}
internal sealed class ProbeSummary
{
    public required string VirtualPath { get; init; }
    public required string ContainerName { get; init; }
    public long ReadOrder { get; init; }
    public required string OriginalSha256 { get; init; }
    public required string CandidateSha256 { get; init; }
    public long OriginalSize { get; init; }
    public long CandidateSize { get; init; }
    public int BaselineEntryCount { get; init; }
    public int CandidateEntryCount { get; init; }
    public int MatchingPhysicalStringRecordCount { get; init; }
    public int ExpectedChangedIdentityCount { get; init; }
    public int ActualChangedIdentityCount { get; init; }
    public int MissingIdentityCount { get; init; }
    public int ExtraIdentityCount { get; init; }
    public int UnexpectedChangedIdentityCount { get; init; }
    public int SelectedIdentityWrongValueCount { get; init; }
    public int NamespaceOrKeyHashMismatchCount { get; init; }
    public required string SourceMarker { get; init; }
    public required string TestMarker { get; init; }
    public required string CandidatePath { get; init; }
    public bool Success { get; init; }
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
        else _inner.WriteLine(value);
    }
}
