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

namespace NTE.LocresPaddingProbe;

internal static class Program
{
    private const string SplitMarker = "HottaLocresSplit";
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

            Console.WriteLine("NTE LOCRES Padding Probe 011");
            Console.WriteLine("Purpose: prove the encrypted string padding rule and attempt a byte-identical no-op rebuild");
            Console.WriteLine("Offline only; game archives are not modified.");
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
            var original = ParseRaw(originalBytes);

            Console.WriteLine($"ES source: {source.ContainerName} (readOrder {source.ReadOrder})");
            Console.WriteLine($"Virtual path: {virtualPath}");
            Console.WriteLine($"Original SHA-256: {originalSha}");
            Console.WriteLine($"Keys: {original.KeyEntries.Count}; string records: {original.StringRecords.Count}");
            Console.WriteLine();

            var referenceCounts = original.KeyEntries
                .GroupBy(x => x.StringIndex)
                .ToDictionary(g => g.Key, g => g.Count());

            var refCountExact = 0;
            var pkcs7CipherExact = 0;
            var zeroCipherExact = 0;
            var pkcs7LengthExact = 0;
            var zeroLengthExact = 0;
            var exactBlockInputCount = 0;
            var decryptFailures = 0;
            var lengthDeltaHistogram = new Dictionary<int, int>();
            var mismatchSamples = new List<MismatchSample>();

            foreach (var record in original.StringRecords)
            {
                if (referenceCounts.TryGetValue(record.Index, out var refs) && refs == record.RefCount)
                    refCountExact++;

                if (!record.DecryptSucceeded)
                {
                    decryptFailures++;
                    continue;
                }

                var inputByteLength = Encoding.UTF8.GetByteCount(record.PlainText + SplitMarker);
                if (inputByteLength % 16 == 0) exactBlockInputCount++;

                var pkcs7 = EncryptNteStringPkcs7(record.PlainText);
                var zero = EncryptNteStringZeroPad(record.PlainText);

                if (string.Equals(pkcs7, record.EncodedText, StringComparison.Ordinal)) pkcs7CipherExact++;
                if (string.Equals(zero, record.EncodedText, StringComparison.Ordinal)) zeroCipherExact++;
                if (pkcs7.Length == record.EncodedText.Length) pkcs7LengthExact++;
                if (zero.Length == record.EncodedText.Length) zeroLengthExact++;

                var delta = record.EncodedText.Length - zero.Length;
                lengthDeltaHistogram[delta] = lengthDeltaHistogram.GetValueOrDefault(delta) + 1;

                if (!string.Equals(pkcs7, record.EncodedText, StringComparison.Ordinal) && mismatchSamples.Count < 20)
                {
                    mismatchSamples.Add(new MismatchSample
                    {
                        StringIndex = record.Index,
                        PlainTextPreview = Preview(record.PlainText),
                        InputByteLength = inputByteLength,
                        OriginalEncodedLength = record.EncodedText.Length,
                        Pkcs7EncodedLength = pkcs7.Length,
                        ZeroPadEncodedLength = zero.Length,
                        OriginalRefCount = record.RefCount,
                        CalculatedRefCount = referenceCounts.GetValueOrDefault(record.Index)
                    });
                }
            }

            var candidateBytes = BuildPkcs7NoOp(original);
            var candidatePath = Path.Combine(artifactsDir, "pkcs7-preserve-refcount-noop.locres");
            File.WriteAllBytes(candidatePath, candidateBytes);

            var byteComparison = CompareBytes(originalBytes, candidateBytes);
            var originalSemantic = ParseSemantic(originalBytes, virtualPath);
            var candidateSemantic = ParseSemantic(candidateBytes, virtualPath);
            var semantic = CompareSemantic(originalSemantic, candidateSemantic);

            var forensics = new PaddingForensics
            {
                StringRecordCount = original.StringRecords.Count,
                DecryptFailureCount = decryptFailures,
                RefCountExactMatchCount = refCountExact,
                Pkcs7CiphertextExactMatchCount = pkcs7CipherExact,
                ZeroPadCiphertextExactMatchCount = zeroCipherExact,
                Pkcs7EncodedLengthExactMatchCount = pkcs7LengthExact,
                ZeroPadEncodedLengthExactMatchCount = zeroLengthExact,
                ExactAesBlockInputCount = exactBlockInputCount,
                OriginalMinusZeroPadEncodedLengthHistogram = lengthDeltaHistogram
                    .OrderBy(x => x.Key)
                    .ToDictionary(x => x.Key, x => x.Value),
                MismatchSamples = mismatchSamples
            };
            WriteJson(Path.Combine(outputDir, "padding-forensics.json"), forensics);

            var semanticReport = new SemanticReport
            {
                OriginalParseSucceeded = originalSemantic.Success,
                CandidateParseSucceeded = candidateSemantic.Success,
                OriginalEntryCount = originalSemantic.Entries.Count,
                CandidateEntryCount = candidateSemantic.Entries.Count,
                MissingIdentityCount = semantic.MissingIdentityCount,
                ExtraIdentityCount = semantic.ExtraIdentityCount,
                LocalizedValueMismatchCount = semantic.LocalizedValueMismatchCount,
                NamespaceOrKeyHashMismatchCount = semantic.NamespaceOrKeyHashMismatchCount,
                CandidateError = candidateSemantic.ErrorMessage
            };
            WriteJson(Path.Combine(outputDir, "semantic-reparse.json"), semanticReport);

            var summary = new ProbeSummary
            {
                VirtualPath = virtualPath,
                ContainerName = source.ContainerName,
                ReadOrder = source.ReadOrder,
                OriginalSha256 = originalSha,
                CandidateSha256 = Sha256(candidateBytes),
                OriginalSize = originalBytes.LongLength,
                CandidateSize = candidateBytes.LongLength,
                KeyEntryCount = original.KeyEntries.Count,
                StringRecordCount = original.StringRecords.Count,
                RefCountExactMatchCount = refCountExact,
                Pkcs7CiphertextExactMatchCount = pkcs7CipherExact,
                ZeroPadCiphertextExactMatchCount = zeroCipherExact,
                ExactAesBlockInputCount = exactBlockInputCount,
                ByteIdentical = byteComparison.ByteIdentical,
                FirstDifferentOffset = byteComparison.FirstDifferentOffset,
                DifferingByteCount = byteComparison.DifferingByteCount,
                SemanticReparseSucceeded = candidateSemantic.Success,
                SemanticLocalizedValueMismatchCount = semantic.LocalizedValueMismatchCount,
                LocalArtifactsDirectory = artifactsDir
            };
            WriteJson(Path.Combine(outputDir, "summary.json"), summary);

            Console.WriteLine($"RefCount exact: {refCountExact}/{original.StringRecords.Count}");
            Console.WriteLine($"PKCS#7 ciphertext exact: {pkcs7CipherExact}/{original.StringRecords.Count}");
            Console.WriteLine($"Zero-pad ciphertext exact: {zeroCipherExact}/{original.StringRecords.Count}");
            Console.WriteLine($"Inputs exactly on AES block boundary: {exactBlockInputCount}");
            Console.WriteLine($"PKCS#7 no-op byte-identical: {byteComparison.ByteIdentical}");
            Console.WriteLine($"Candidate semantic reparse: {(candidateSemantic.Success ? "SUCCESS" : "FAILED")}");
            if (candidateSemantic.Success)
                Console.WriteLine($"Semantic localized-value mismatches: {semantic.LocalizedValueMismatchCount}");
            Console.WriteLine($"Reports: {outputDir}");
            Console.WriteLine($"Raw candidate remains local only: {artifactsDir}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex}");
            return 1;
        }
    }

    private static RawLocresDocument ParseRaw(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        ReadExact(r, 16); // magic
        r.ReadByte();     // UE version
        r.ReadInt32();    // NTE version
        r.ReadInt32();    // encrypted bool
        var stringTableOffset = r.ReadInt64();
        var totalEntries = r.ReadUInt32();
        var namespaceCount = r.ReadUInt32();

        var keys = new List<KeyEntry>(checked((int)totalEntries));
        for (var i = 0u; i < namespaceCount; i++)
        {
            r.ReadUInt32();
            var ns = ReadFString(r).Value;
            var keyCount = r.ReadUInt32();
            for (var j = 0u; j < keyCount; j++)
            {
                r.ReadUInt32();
                var key = ReadFString(r).Value;
                r.ReadInt32();
                var stringIndex = r.ReadInt32();
                keys.Add(new KeyEntry(ns, key, stringIndex));
            }
        }

        if (ms.Position != stringTableOffset)
            throw new InvalidDataException($"Key section ended at {ms.Position}, expected {stringTableOffset}.");

        var prefix = bytes.AsSpan(0, checked((int)stringTableOffset)).ToArray();
        var stringCount = r.ReadUInt32();
        var records = new List<StringRecord>(checked((int)stringCount));
        for (var i = 0; i < stringCount; i++)
        {
            var encoded = ReadFString(r);
            var refCount = r.ReadInt32();
            var decrypt = TryDecryptNteString(encoded.Value);
            records.Add(new StringRecord
            {
                Index = checked((int)i),
                Storage = encoded.Storage,
                EncodedText = encoded.Value,
                RefCount = refCount,
                PlainText = decrypt.PlainText,
                DecryptSucceeded = decrypt.Success,
                DecryptError = decrypt.Error
            });
        }

        if (ms.Position != ms.Length)
            throw new InvalidDataException($"String table ended at {ms.Position}; file length is {ms.Length}.");

        return new RawLocresDocument(prefix, keys, records);
    }

    private static byte[] BuildPkcs7NoOp(RawLocresDocument source)
    {
        using var ms = new MemoryStream(source.Prefix.Length + 12_000_000);
        ms.Write(source.Prefix);
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write((uint)source.StringRecords.Count);
        foreach (var record in source.StringRecords)
        {
            if (!record.DecryptSucceeded)
                throw new InvalidDataException($"String {record.Index} failed decryption: {record.DecryptError}");
            var encoded = EncryptNteStringPkcs7(record.PlainText);
            WriteFString(w, encoded, record.Storage);
            w.Write(record.RefCount);
        }
        w.Flush();
        return ms.ToArray();
    }

    private static string EncryptNteStringPkcs7(string plain)
    {
        var input = Encoding.UTF8.GetBytes(plain + SplitMarker);
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        var encrypted = aes.CreateEncryptor(NteLocresKey, null)
            .TransformFinalBlock(input, 0, input.Length);
        return Convert.ToBase64String(encrypted).Replace('+', '-').Replace('/', '_');
    }

    private static string EncryptNteStringZeroPad(string plain)
    {
        var input = Encoding.UTF8.GetBytes(plain + SplitMarker);
        var paddedLength = ((input.Length + 15) / 16) * 16;
        var padded = new byte[paddedLength];
        input.CopyTo(padded, 0);
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        var encrypted = aes.CreateEncryptor(NteLocresKey, null)
            .TransformFinalBlock(padded, 0, padded.Length);
        return Convert.ToBase64String(encrypted).Replace('+', '-').Replace('/', '_');
    }

    private static DecryptResult TryDecryptNteString(string encoded)
    {
        try
        {
            var encrypted = Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/'));
            using var aes = Aes.Create();
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            var decrypted = aes.CreateDecryptor(NteLocresKey, null)
                .TransformFinalBlock(encrypted, 0, encrypted.Length);
            var decoded = Encoding.UTF8.GetString(decrypted);
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

    private static SemanticComparison CompareSemantic(SemanticSnapshot baseline, SemanticSnapshot candidate)
    {
        if (!baseline.Success) throw new InvalidOperationException("Baseline semantic parse failed.");
        if (!candidate.Success) return new SemanticComparison(0, 0, 0, 0);

        var missing = baseline.Entries.Keys.Except(candidate.Entries.Keys).Count();
        var extra = candidate.Entries.Keys.Except(baseline.Entries.Keys).Count();
        var shared = baseline.Entries.Keys.Intersect(candidate.Entries.Keys).ToList();
        var values = shared.Count(k => !string.Equals(
            baseline.Entries[k].LocalizedString,
            candidate.Entries[k].LocalizedString,
            StringComparison.Ordinal));
        var hashes = shared.Count(k =>
            baseline.Entries[k].NamespaceHash != candidate.Entries[k].NamespaceHash ||
            baseline.Entries[k].KeyHash != candidate.Entries[k].KeyHash);
        return new SemanticComparison(missing, extra, values, hashes);
    }

    private static FStringRead ReadFString(BinaryReader r)
    {
        var signedLength = r.ReadInt32();
        if (signedLength == 0) return new FStringRead(string.Empty, FStringStorage.Empty);
        if (signedLength > 0)
        {
            var payload = ReadExact(r, signedLength);
            return new FStringRead(Encoding.UTF8.GetString(payload, 0, Math.Max(0, payload.Length - 1)), FStringStorage.Ansi);
        }
        var charCount = checked(-signedLength);
        var payloadWide = ReadExact(r, checked(charCount * 2));
        return new FStringRead(Encoding.Unicode.GetString(payloadWide, 0, Math.Max(0, payloadWide.Length - 2)), FStringStorage.Wide);
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
        var bytes = Encoding.ASCII.GetBytes(value);
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

    private static BinaryComparison CompareBytes(byte[] left, byte[] right)
    {
        var common = Math.Min(left.Length, right.Length);
        long differing = 0;
        long? first = null;
        for (var i = 0; i < common; i++)
        {
            if (left[i] == right[i]) continue;
            differing++;
            first ??= i;
        }
        if (left.Length != right.Length)
        {
            first ??= common;
            differing += Math.Abs((long)left.Length - right.Length);
        }
        return new BinaryComparison(differing == 0, first, differing);
    }

    private static string Preview(string value)
    {
        var normalized = value.Replace("\r", "\\r").Replace("\n", "\\n");
        return normalized.Length <= 240 ? normalized : normalized[..240] + "…";
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');
    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static SourceDescription DescribeSource(GameFile file)
    {
        if (file is VfsEntry entry) return new SourceDescription(entry.Vfs.Name, entry.Vfs.ReadOrder);
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
        if (aesFile is not null) raw = File.ReadAllText(aesFile).Trim();
        else
        {
            using var document = JsonDocument.Parse(File.ReadAllText(aesConfig!));
            if (!document.RootElement.TryGetProperty("aes_key", out var prop))
                throw new InvalidDataException("AES config does not contain aes_key.");
            raw = prop.GetString()?.Trim() ?? string.Empty;
        }
        if (!raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) raw = "0x" + raw;
        if (raw.Length != 66 || raw.Skip(2).Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("AES key must contain exactly 64 hexadecimal digits.");
        return raw;
    }

    private static void ValidateInputs(string gameRoot, string? aesConfig, string? aesFile)
    {
        if (!Directory.Exists(gameRoot)) throw new DirectoryNotFoundException(gameRoot);
        if (aesConfig is not null && aesFile is not null) throw new ArgumentException("Use only one AES source.");
        if (aesConfig is not null && !File.Exists(aesConfig)) throw new FileNotFoundException("AES config not found.", aesConfig);
        if (aesFile is not null && !File.Exists(aesFile)) throw new FileNotFoundException("AES file not found.", aesFile);
    }

    private static void WriteJson<T>(string path, T value) => File.WriteAllText(
        path,
        JsonSerializer.Serialize(value, JsonIndented),
        new UTF8Encoding(false));

    private static void PrintUsage() => Console.WriteLine(
        "Usage: NTE.LocresPaddingProbe <gameRoot> <outputDir> [--aes-config=<path> | --aes-file=<path>]");

    private static readonly JsonSerializerOptions JsonIndented = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };
}

internal enum FStringStorage { Empty, Ansi, Wide }
internal sealed record FStringRead(string Value, FStringStorage Storage);
internal sealed record DecryptResult(bool Success, string PlainText, string? Error);
internal sealed record KeyEntry(string Namespace, string Key, int StringIndex);
internal sealed record StringRecord
{
    public int Index { get; init; }
    public FStringStorage Storage { get; init; }
    public required string EncodedText { get; init; }
    public int RefCount { get; init; }
    public required string PlainText { get; init; }
    public bool DecryptSucceeded { get; init; }
    public string? DecryptError { get; init; }
}
internal sealed record RawLocresDocument(byte[] Prefix, List<KeyEntry> KeyEntries, List<StringRecord> StringRecords);
internal sealed record SourceDescription(string ContainerName, long ReadOrder);
internal sealed record BinaryComparison(bool ByteIdentical, long? FirstDifferentOffset, long DifferingByteCount);
internal sealed record SemanticEntry(uint NamespaceHash, uint KeyHash, string LocalizedString);
internal sealed record SemanticSnapshot(bool Success, string? ErrorMessage, Dictionary<(string Ns, string Key), SemanticEntry> Entries);
internal sealed record SemanticComparison(int MissingIdentityCount, int ExtraIdentityCount, int LocalizedValueMismatchCount, int NamespaceOrKeyHashMismatchCount);

internal sealed class MismatchSample
{
    public int StringIndex { get; init; }
    public required string PlainTextPreview { get; init; }
    public int InputByteLength { get; init; }
    public int OriginalEncodedLength { get; init; }
    public int Pkcs7EncodedLength { get; init; }
    public int ZeroPadEncodedLength { get; init; }
    public int OriginalRefCount { get; init; }
    public int CalculatedRefCount { get; init; }
}

internal sealed class PaddingForensics
{
    public int StringRecordCount { get; init; }
    public int DecryptFailureCount { get; init; }
    public int RefCountExactMatchCount { get; init; }
    public int Pkcs7CiphertextExactMatchCount { get; init; }
    public int ZeroPadCiphertextExactMatchCount { get; init; }
    public int Pkcs7EncodedLengthExactMatchCount { get; init; }
    public int ZeroPadEncodedLengthExactMatchCount { get; init; }
    public int ExactAesBlockInputCount { get; init; }
    public required Dictionary<int, int> OriginalMinusZeroPadEncodedLengthHistogram { get; init; }
    public required List<MismatchSample> MismatchSamples { get; init; }
}

internal sealed class SemanticReport
{
    public bool OriginalParseSucceeded { get; init; }
    public bool CandidateParseSucceeded { get; init; }
    public int OriginalEntryCount { get; init; }
    public int CandidateEntryCount { get; init; }
    public int MissingIdentityCount { get; init; }
    public int ExtraIdentityCount { get; init; }
    public int LocalizedValueMismatchCount { get; init; }
    public int NamespaceOrKeyHashMismatchCount { get; init; }
    public string? CandidateError { get; init; }
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
    public int KeyEntryCount { get; init; }
    public int StringRecordCount { get; init; }
    public int RefCountExactMatchCount { get; init; }
    public int Pkcs7CiphertextExactMatchCount { get; init; }
    public int ZeroPadCiphertextExactMatchCount { get; init; }
    public int ExactAesBlockInputCount { get; init; }
    public bool ByteIdentical { get; init; }
    public long? FirstDifferentOffset { get; init; }
    public long DifferingByteCount { get; init; }
    public bool SemanticReparseSucceeded { get; init; }
    public int SemanticLocalizedValueMismatchCount { get; init; }
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
