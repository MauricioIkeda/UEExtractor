using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Localization;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.VirtualFileSystem;
using LocresWriter;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NTE.LocresStringTableProbe;

internal static class Program
{
    private const int SampleLimit = 24;
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

            Console.WriteLine("NTE LOCRES String-Table Probe 010");
            Console.WriteLine("Purpose: explain Run 009 byte differences and prove semantic reparsing offline");
            Console.WriteLine("This probe does not modify game archives or install any file into NTE.");
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
            var originalPath = Path.Combine(artifactsDir, "effective-es-original.locres");
            var currentWriterPath = Path.Combine(artifactsDir, "current-writer-noop.locres");
            var preserveRefPath = Path.Combine(artifactsDir, "reencrypted-preserve-refcount.locres");
            var preserveStorageRefPath = Path.Combine(artifactsDir, "reencrypted-preserve-storage-refcount.locres");
            File.WriteAllBytes(originalPath, originalBytes);

            Console.WriteLine($"ES source: {source.ContainerName} (readOrder {source.ReadOrder})");
            Console.WriteLine($"Virtual path: {virtualPath}");
            Console.WriteLine($"Original SHA-256: {Sha256(originalBytes)}");
            Console.WriteLine();

            // Reproduce the current writer behavior. Enable the NTE flags only so its legacy
            // PrintVerification routine parses the NTE-specific header at the correct offsets.
            var previousNteFormat = LocresCompactWriter.NTEFormat;
            var previousNteEncrypted = LocresCompactWriter.NTEEncrypted;
            try
            {
                LocresCompactWriter.NTEFormat = true;
                LocresCompactWriter.NTEEncrypted = true;
                LocresCompactWriter.Patch(originalPath, null, currentWriterPath);
            }
            finally
            {
                LocresCompactWriter.NTEFormat = previousNteFormat;
                LocresCompactWriter.NTEEncrypted = previousNteEncrypted;
            }

            var currentWriterBytes = File.ReadAllBytes(currentWriterPath);
            var original = ParseRaw(originalBytes);
            var current = ParseRaw(currentWriterBytes);

            if (original.StringRecords.Count != current.StringRecords.Count)
                throw new InvalidDataException("Original/current writer string-table counts differ unexpectedly.");

            var preserveRefBytes = BuildReencryptedVariant(original, preserveStorage: false, preserveRefCount: true);
            var preserveStorageRefBytes = BuildReencryptedVariant(original, preserveStorage: true, preserveRefCount: true);
            File.WriteAllBytes(preserveRefPath, preserveRefBytes);
            File.WriteAllBytes(preserveStorageRefPath, preserveStorageRefBytes);

            var preserveRef = ParseRaw(preserveRefBytes);
            var preserveStorageRef = ParseRaw(preserveStorageRefBytes);

            var indexReferences = original.KeyEntries
                .GroupBy(x => x.StringIndex)
                .ToDictionary(g => g.Key, g => g.ToList());

            var invalidStringIndices = original.KeyEntries.Count(x => x.StringIndex < 0 || x.StringIndex >= original.StringRecords.Count);
            var refMismatches = new List<RefCountMismatch>();
            var topShared = new List<SharedIndexSample>();
            var sharedIndexCount = 0;
            var maxReferences = 0;

            for (var i = 0; i < original.StringRecords.Count; i++)
            {
                var calculated = indexReferences.TryGetValue(i, out var refs) ? refs.Count : 0;
                var stored = original.StringRecords[i].RefCount;
                maxReferences = Math.Max(maxReferences, calculated);
                if (calculated > 1) sharedIndexCount++;
                if (stored != calculated && refMismatches.Count < SampleLimit)
                {
                    refMismatches.Add(new RefCountMismatch
                    {
                        StringIndex = i,
                        StoredRefCount = stored,
                        CalculatedReferenceCount = calculated,
                        SampleIdentities = refs?.Take(8).Select(IdentityOf).ToArray() ?? []
                    });
                }
            }

            topShared = indexReferences
                .Where(x => x.Value.Count > 1)
                .OrderByDescending(x => x.Value.Count)
                .ThenBy(x => x.Key)
                .Take(SampleLimit)
                .Select(x => new SharedIndexSample
                {
                    StringIndex = x.Key,
                    ReferenceCount = x.Value.Count,
                    StoredRefCount = original.StringRecords[x.Key].RefCount,
                    SampleIdentities = x.Value.Take(12).Select(IdentityOf).ToArray(),
                    PlainTextPreview = Preview(original.StringRecords[x.Key].PlainText)
                })
                .ToList();

            var refCountExactMatches = Enumerable.Range(0, original.StringRecords.Count)
                .Count(i => original.StringRecords[i].RefCount == (indexReferences.TryGetValue(i, out var refs) ? refs.Count : 0));

            var sharedIndexSummary = new SharedIndexSummary
            {
                KeyEntryCount = original.KeyEntries.Count,
                StringRecordCount = original.StringRecords.Count,
                ReferencedStringIndexCount = indexReferences.Count,
                SharedStringIndexCount = sharedIndexCount,
                MaxReferencesToSingleString = maxReferences,
                InvalidStringIndexCount = invalidStringIndices,
                StoredRefCountSum = original.StringRecords.Sum(x => (long)x.RefCount),
                CalculatedReferenceCountSum = original.KeyEntries.Count,
                RefCountExactMatchCount = refCountExactMatches,
                RefCountMismatchCount = original.StringRecords.Count - refCountExactMatches,
                MismatchSamples = refMismatches,
                TopSharedIndices = topShared
            };
            WriteJson(Path.Combine(outputDir, "shared-string-indices.json"), sharedIndexSummary);

            var mismatchSamples = new List<RecordMismatchSample>();
            var storageTransitions = new Dictionary<string, int>(StringComparer.Ordinal);
            var sizeDeltaHistogram = new Dictionary<int, int>();
            var originalStorageHistogram = new Dictionary<string, int>(StringComparer.Ordinal);
            var currentStorageHistogram = new Dictionary<string, int>(StringComparer.Ordinal);

            var plaintextEqual = 0;
            var ciphertextEqual = 0;
            var canonicalCiphertextMatchesOriginal = 0;
            var refCountEqual = 0;
            var storageEqual = 0;
            var signedLengthEqual = 0;
            var recordSizeEqual = 0;
            long summedRecordSizeDelta = 0;

            for (var i = 0; i < original.StringRecords.Count; i++)
            {
                var a = original.StringRecords[i];
                var b = current.StringRecords[i];
                Increment(originalStorageHistogram, a.Storage.ToString());
                Increment(currentStorageHistogram, b.Storage.ToString());
                Increment(storageTransitions, $"{a.Storage}->{b.Storage}");
                var delta = checked((int)(b.RecordSize - a.RecordSize));
                Increment(sizeDeltaHistogram, delta);
                summedRecordSizeDelta += delta;

                var plainEq = string.Equals(a.PlainText, b.PlainText, StringComparison.Ordinal);
                var cipherEq = string.Equals(a.EncodedText, b.EncodedText, StringComparison.Ordinal);
                var canonicalEq = string.Equals(a.EncodedText, EncryptNteString(a.PlainText), StringComparison.Ordinal);
                if (plainEq) plaintextEqual++;
                if (cipherEq) ciphertextEqual++;
                if (canonicalEq) canonicalCiphertextMatchesOriginal++;
                if (a.RefCount == b.RefCount) refCountEqual++;
                if (a.Storage == b.Storage) storageEqual++;
                if (a.SignedLength == b.SignedLength) signedLengthEqual++;
                if (a.RecordSize == b.RecordSize) recordSizeEqual++;

                if ((!plainEq || !cipherEq || a.RefCount != b.RefCount || a.Storage != b.Storage || a.RecordSize != b.RecordSize) &&
                    mismatchSamples.Count < SampleLimit)
                {
                    indexReferences.TryGetValue(i, out var refs);
                    mismatchSamples.Add(new RecordMismatchSample
                    {
                        StringIndex = i,
                        SampleIdentities = refs?.Take(8).Select(IdentityOf).ToArray() ?? [],
                        OriginalStorage = a.Storage,
                        CurrentStorage = b.Storage,
                        OriginalSignedLength = a.SignedLength,
                        CurrentSignedLength = b.SignedLength,
                        OriginalRefCount = a.RefCount,
                        CurrentRefCount = b.RefCount,
                        OriginalRecordSize = a.RecordSize,
                        CurrentRecordSize = b.RecordSize,
                        RecordSizeDelta = delta,
                        PlaintextEqual = plainEq,
                        CiphertextEqual = cipherEq,
                        CanonicalReEncryptionMatchesOriginalCiphertext = canonicalEq,
                        PlainTextPreview = Preview(a.PlainText)
                    });
                }
            }

            var byteComparison = CompareBytes(originalBytes, currentWriterBytes);
            var recordForensics = new RecordForensics
            {
                StringRecordCount = original.StringRecords.Count,
                PlaintextEqualCount = plaintextEqual,
                CiphertextEqualCount = ciphertextEqual,
                CanonicalReEncryptionMatchesOriginalCiphertextCount = canonicalCiphertextMatchesOriginal,
                RefCountEqualCount = refCountEqual,
                StorageEqualCount = storageEqual,
                SignedLengthEqualCount = signedLengthEqual,
                RecordSizeEqualCount = recordSizeEqual,
                OriginalStorageHistogram = originalStorageHistogram,
                CurrentStorageHistogram = currentStorageHistogram,
                StorageTransitions = storageTransitions,
                RecordSizeDeltaHistogram = sizeDeltaHistogram.OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Value),
                SummedRecordSizeDelta = summedRecordSizeDelta,
                WholeFileSizeDelta = currentWriterBytes.LongLength - originalBytes.LongLength,
                FirstDifferentOffset = byteComparison.FirstDifferentOffset,
                WholeFileDifferingByteCount = byteComparison.DifferingByteCount,
                MismatchSamples = mismatchSamples
            };
            WriteJson(Path.Combine(outputDir, "string-record-forensics.json"), recordForensics);

            var originalSemantic = ParseSemantic(originalBytes, virtualPath);
            var currentSemantic = ParseSemantic(currentWriterBytes, virtualPath);
            var preserveRefSemantic = ParseSemantic(preserveRefBytes, virtualPath);
            var preserveStorageRefSemantic = ParseSemantic(preserveStorageRefBytes, virtualPath);

            var semanticReport = new SemanticReparseReport
            {
                Original = originalSemantic.ToSummary("original"),
                CurrentWriterNoOp = CompareSemantic(originalSemantic, currentSemantic, "current-writer-noop"),
                ReencryptedPreserveRefCount = CompareSemantic(originalSemantic, preserveRefSemantic, "reencrypted-preserve-refcount"),
                ReencryptedPreserveStorageAndRefCount = CompareSemantic(originalSemantic, preserveStorageRefSemantic, "reencrypted-preserve-storage-refcount")
            };
            WriteJson(Path.Combine(outputDir, "semantic-reparse.json"), semanticReport);

            var variants = new List<VariantReport>
            {
                BuildVariantReport("current-writer-noop", originalBytes, currentWriterBytes, original, current),
                BuildVariantReport("reencrypted-preserve-refcount", originalBytes, preserveRefBytes, original, preserveRef),
                BuildVariantReport("reencrypted-preserve-storage-refcount", originalBytes, preserveStorageRefBytes, original, preserveStorageRef)
            };
            WriteJson(Path.Combine(outputDir, "variant-comparison.json"), variants);

            var summary = new ProbeSummary
            {
                VirtualPath = virtualPath,
                ContainerName = source.ContainerName,
                ReadOrder = source.ReadOrder,
                OriginalSha256 = Sha256(originalBytes),
                OriginalSize = originalBytes.LongLength,
                KeyEntryCount = original.KeyEntries.Count,
                StringRecordCount = original.StringRecords.Count,
                SharedStringIndexCount = sharedIndexCount,
                RefCountExactMatchCount = refCountExactMatches,
                RefCountMismatchCount = original.StringRecords.Count - refCountExactMatches,
                CurrentWriterSemanticReparseSucceeded = currentSemantic.Success,
                CurrentWriterSemanticValueMismatchCount = currentSemantic.Success ? CountValueMismatches(originalSemantic, currentSemantic) : null,
                CurrentWriterByteIdentical = byteComparison.ByteIdentical,
                PreserveStorageAndRefCountByteIdentical = CompareBytes(originalBytes, preserveStorageRefBytes).ByteIdentical,
                LocalArtifactsDirectory = artifactsDir
            };
            WriteJson(Path.Combine(outputDir, "summary.json"), summary);

            Console.WriteLine();
            Console.WriteLine($"Keys: {original.KeyEntries.Count}; string records: {original.StringRecords.Count}; shared indices: {sharedIndexCount}");
            Console.WriteLine($"RefCount exact matches calculated key references: {refCountExactMatches}/{original.StringRecords.Count}");
            Console.WriteLine($"Current writer semantic reparse: {(currentSemantic.Success ? "SUCCESS" : "FAILED")}");
            if (currentSemantic.Success)
                Console.WriteLine($"Current writer semantic value mismatches: {CountValueMismatches(originalSemantic, currentSemantic)}");
            Console.WriteLine($"Current writer byte-identical: {byteComparison.ByteIdentical}");
            Console.WriteLine($"Preserve storage + RefCount byte-identical: {CompareBytes(originalBytes, preserveStorageRefBytes).ByteIdentical}");
            Console.WriteLine($"Reports: {outputDir}");
            Console.WriteLine($"Raw LOCRES variants remain local only: {artifactsDir}");
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

        var magic = Convert.ToHexString(ReadExact(r, 16));
        var ueVersion = r.ReadByte();
        var nteVersion = r.ReadInt32();
        var encrypted = r.ReadInt32() != 0;
        var stringTableOffset = r.ReadInt64();
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

        if (ms.Position != stringTableOffset)
            throw new InvalidDataException($"Key section ended at {ms.Position}, expected string table offset {stringTableOffset}.");

        var prefix = bytes.AsSpan(0, checked((int)stringTableOffset)).ToArray();
        var stringCount = r.ReadUInt32();
        var records = new List<StringRecord>(checked((int)stringCount));
        for (var i = 0; i < stringCount; i++)
        {
            var start = ms.Position;
            var encoded = ReadFString(r);
            var refCount = r.ReadInt32();
            var end = ms.Position;
            var decrypt = TryDecryptNteString(encoded.Value);
            records.Add(new StringRecord
            {
                Index = checked((int)i),
                RecordStart = start,
                RecordEnd = end,
                RecordSize = end - start,
                SignedLength = encoded.SignedLength,
                Storage = encoded.Storage,
                EncodedText = encoded.Value,
                EncodedPayloadByteCount = encoded.PayloadByteCount,
                RefCount = refCount,
                PlainText = decrypt.PlainText,
                DecryptSucceeded = decrypt.Success,
                DecryptError = decrypt.Error
            });
        }

        if (ms.Position != ms.Length)
            throw new InvalidDataException($"String table ended at {ms.Position} but file length is {ms.Length}.");

        return new RawLocresDocument
        {
            MagicHex = magic,
            UeVersion = ueVersion,
            NteVersion = nteVersion,
            IsEncrypted = encrypted,
            StringTableOffset = stringTableOffset,
            TotalEntries = totalEntries,
            NamespaceCount = namespaceCount,
            Prefix = prefix,
            KeyEntries = keys,
            StringRecords = records,
            FileSize = bytes.LongLength
        };
    }

    private static byte[] BuildReencryptedVariant(RawLocresDocument source, bool preserveStorage, bool preserveRefCount)
    {
        using var ms = new MemoryStream(checked((int)Math.Min(int.MaxValue, source.FileSize + 4096)));
        ms.Write(source.Prefix);
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write((uint)source.StringRecords.Count);

        foreach (var record in source.StringRecords)
        {
            if (!record.DecryptSucceeded)
                throw new InvalidDataException($"Cannot build diagnostic variant: string index {record.Index} failed decryption: {record.DecryptError}");

            var encoded = EncryptNteString(record.PlainText);
            var storage = preserveStorage ? record.Storage : FStringStorage.Ansi;
            WriteFString(w, encoded, storage);
            w.Write(preserveRefCount ? record.RefCount : 1);
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
                    entries[(nsKey.Str, textKey.Str)] = new SemanticEntry
                    {
                        NamespaceHash = nsKey.StrHash,
                        KeyHash = textKey.StrHash,
                        LocalizedString = entry.LocalizedString ?? string.Empty
                    };
                }
            }

            return new SemanticSnapshot { Success = true, Entries = entries };
        }
        catch (Exception ex)
        {
            return new SemanticSnapshot
            {
                Success = false,
                ErrorType = ex.GetType().FullName ?? ex.GetType().Name,
                ErrorMessage = ex.Message,
                Entries = []
            };
        }
    }

    private static SemanticComparison CompareSemantic(SemanticSnapshot baseline, SemanticSnapshot candidate, string name)
    {
        if (!baseline.Success)
            throw new InvalidOperationException("Baseline semantic parse failed unexpectedly.");

        if (!candidate.Success)
        {
            return new SemanticComparison
            {
                Name = name,
                ParseSucceeded = false,
                ErrorType = candidate.ErrorType,
                ErrorMessage = candidate.ErrorMessage
            };
        }

        var missing = baseline.Entries.Keys.Except(candidate.Entries.Keys).ToList();
        var extra = candidate.Entries.Keys.Except(baseline.Entries.Keys).ToList();
        var shared = baseline.Entries.Keys.Intersect(candidate.Entries.Keys).ToList();
        var valueMismatches = shared.Count(k => !string.Equals(
            baseline.Entries[k].LocalizedString,
            candidate.Entries[k].LocalizedString,
            StringComparison.Ordinal));
        var hashMismatches = shared.Count(k =>
            baseline.Entries[k].NamespaceHash != candidate.Entries[k].NamespaceHash ||
            baseline.Entries[k].KeyHash != candidate.Entries[k].KeyHash);

        return new SemanticComparison
        {
            Name = name,
            ParseSucceeded = true,
            BaselineEntryCount = baseline.Entries.Count,
            CandidateEntryCount = candidate.Entries.Count,
            MissingIdentityCount = missing.Count,
            ExtraIdentityCount = extra.Count,
            LocalizedValueMismatchCount = valueMismatches,
            NamespaceOrKeyHashMismatchCount = hashMismatches,
            MissingIdentitySamples = missing.Take(12).Select(IdentityOf).ToArray(),
            ExtraIdentitySamples = extra.Take(12).Select(IdentityOf).ToArray()
        };
    }

    private static int? CountValueMismatches(SemanticSnapshot baseline, SemanticSnapshot candidate)
    {
        if (!baseline.Success || !candidate.Success) return null;
        return baseline.Entries.Keys.Intersect(candidate.Entries.Keys).Count(k => !string.Equals(
            baseline.Entries[k].LocalizedString,
            candidate.Entries[k].LocalizedString,
            StringComparison.Ordinal));
    }

    private static VariantReport BuildVariantReport(
        string name,
        byte[] baselineBytes,
        byte[] candidateBytes,
        RawLocresDocument baseline,
        RawLocresDocument candidate)
    {
        var comparison = CompareBytes(baselineBytes, candidateBytes);
        return new VariantReport
        {
            Name = name,
            Sha256 = Sha256(candidateBytes),
            Size = candidateBytes.LongLength,
            SizeDelta = candidateBytes.LongLength - baselineBytes.LongLength,
            ByteIdentical = comparison.ByteIdentical,
            FirstDifferentOffset = comparison.FirstDifferentOffset,
            DifferingByteCount = comparison.DifferingByteCount,
            PrefixThroughKeySectionIdentical = baseline.StringTableOffset == candidate.StringTableOffset &&
                baseline.Prefix.AsSpan().SequenceEqual(candidate.Prefix),
            StringRecordCount = candidate.StringRecords.Count,
            RefCountSum = candidate.StringRecords.Sum(x => (long)x.RefCount)
        };
    }

    private static FStringRead ReadFString(BinaryReader r)
    {
        var signedLength = r.ReadInt32();
        if (signedLength == 0)
            return new FStringRead(string.Empty, 0, FStringStorage.Empty, 0);

        if (signedLength > 0)
        {
            var byteCount = signedLength;
            var payload = ReadExact(r, byteCount);
            var contentLength = Math.Max(0, byteCount - 1);
            var value = Encoding.UTF8.GetString(payload, 0, contentLength);
            return new FStringRead(value, signedLength, FStringStorage.Ansi, byteCount);
        }

        var charCount = checked(-signedLength);
        var payloadByteCount = checked(charCount * 2);
        var widePayload = ReadExact(r, payloadByteCount);
        var contentByteCount = Math.Max(0, payloadByteCount - 2);
        var wideValue = Encoding.Unicode.GetString(widePayload, 0, contentByteCount);
        return new FStringRead(wideValue, signedLength, FStringStorage.Wide, payloadByteCount);
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
        var data = r.ReadBytes(count);
        if (data.Length != count)
            throw new EndOfStreamException($"Expected {count} bytes, received {data.Length}.");
        return data;
    }

    private static DecryptResult TryDecryptNteString(string encoded)
    {
        if (string.IsNullOrEmpty(encoded))
            return new DecryptResult(true, string.Empty, null);

        try
        {
            var encrypted = Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/'));
            using var aes = Aes.Create();
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            var decryptedBytes = aes.CreateDecryptor(NteLocresKey, null)
                .TransformFinalBlock(encrypted, 0, encrypted.Length);
            var decoded = Encoding.UTF8.GetString(decryptedBytes);
            var markerIndex = decoded.IndexOf(SplitMarker, StringComparison.Ordinal);
            if (markerIndex < 0)
                return new DecryptResult(false, string.Empty, "HottaLocresSplit marker not found after AES decryption.");
            return new DecryptResult(true, decoded[..markerIndex], null);
        }
        catch (Exception ex)
        {
            return new DecryptResult(false, string.Empty, ex.Message);
        }
    }

    private static string EncryptNteString(string plain)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plain + SplitMarker);
        var paddedLength = ((plainBytes.Length + 15) / 16) * 16;
        var padded = new byte[paddedLength];
        plainBytes.CopyTo(padded, 0);
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        var encrypted = aes.CreateEncryptor(NteLocresKey, null)
            .TransformFinalBlock(padded, 0, padded.Length);
        return Convert.ToBase64String(encrypted).Replace('+', '-').Replace('/', '_');
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

    private static string IdentityOf(KeyEntry entry) => string.IsNullOrEmpty(entry.Namespace)
        ? entry.Key
        : $"{entry.Namespace}::{entry.Key}";

    private static string IdentityOf((string Ns, string Key) value) => string.IsNullOrEmpty(value.Ns)
        ? value.Key
        : $"{value.Ns}::{value.Key}";

    private static string Preview(string text)
    {
        const int max = 300;
        var normalized = text.Replace("\r", "\\r").Replace("\n", "\\n");
        return normalized.Length <= max ? normalized : normalized[..max] + "…";
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');
    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static SourceDescription DescribeSource(GameFile file)
    {
        if (file is VfsEntry entry)
            return new SourceDescription(entry.Vfs.Name, entry.Vfs.Path, entry.Vfs.ReadOrder);
        return new SourceDescription("<non-vfs>", string.Empty, 0);
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

    private static void WriteJson<T>(string path, T value) => File.WriteAllText(
        path,
        JsonSerializer.Serialize(value, JsonIndented),
        new UTF8Encoding(false));

    private static void Increment<TKey>(Dictionary<TKey, int> dictionary, TKey key) where TKey : notnull
        => dictionary[key] = dictionary.GetValueOrDefault(key) + 1;

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  NTE.LocresStringTableProbe <gameRoot> <outputDir> [--aes-config=<path> | --aes-file=<path>]");
    }

    private static readonly JsonSerializerOptions JsonIndented = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };
}

internal enum FStringStorage { Empty, Ansi, Wide }
internal sealed record FStringRead(string Value, int SignedLength, FStringStorage Storage, int PayloadByteCount);
internal sealed record DecryptResult(bool Success, string PlainText, string? Error);
internal sealed record SourceDescription(string ContainerName, string ContainerPath, long ReadOrder);
internal sealed record BinaryComparison(bool ByteIdentical, long? FirstDifferentOffset, long DifferingByteCount);
internal sealed record KeyEntry(string Namespace, uint NamespaceHash, string Key, uint KeyHash, int SourceHash, int StringIndex);

internal sealed class RawLocresDocument
{
    public required string MagicHex { get; init; }
    public byte UeVersion { get; init; }
    public int NteVersion { get; init; }
    public bool IsEncrypted { get; init; }
    public long StringTableOffset { get; init; }
    public uint TotalEntries { get; init; }
    public uint NamespaceCount { get; init; }
    public required byte[] Prefix { get; init; }
    public required List<KeyEntry> KeyEntries { get; init; }
    public required List<StringRecord> StringRecords { get; init; }
    public long FileSize { get; init; }
}

internal sealed class StringRecord
{
    public int Index { get; init; }
    public long RecordStart { get; init; }
    public long RecordEnd { get; init; }
    public long RecordSize { get; init; }
    public int SignedLength { get; init; }
    public FStringStorage Storage { get; init; }
    public required string EncodedText { get; init; }
    public int EncodedPayloadByteCount { get; init; }
    public int RefCount { get; init; }
    public required string PlainText { get; init; }
    public bool DecryptSucceeded { get; init; }
    public string? DecryptError { get; init; }
}

internal sealed class SemanticEntry
{
    public uint NamespaceHash { get; init; }
    public uint KeyHash { get; init; }
    public required string LocalizedString { get; init; }
}

internal sealed class SemanticSnapshot
{
    public bool Success { get; init; }
    public string? ErrorType { get; init; }
    public string? ErrorMessage { get; init; }
    public required Dictionary<(string Ns, string Key), SemanticEntry> Entries { get; init; }

    public SemanticComparison ToSummary(string name) => new()
    {
        Name = name,
        ParseSucceeded = Success,
        CandidateEntryCount = Entries.Count,
        ErrorType = ErrorType,
        ErrorMessage = ErrorMessage
    };
}

internal sealed class SemanticComparison
{
    public required string Name { get; init; }
    public bool ParseSucceeded { get; init; }
    public int? BaselineEntryCount { get; init; }
    public int? CandidateEntryCount { get; init; }
    public int? MissingIdentityCount { get; init; }
    public int? ExtraIdentityCount { get; init; }
    public int? LocalizedValueMismatchCount { get; init; }
    public int? NamespaceOrKeyHashMismatchCount { get; init; }
    public string[]? MissingIdentitySamples { get; init; }
    public string[]? ExtraIdentitySamples { get; init; }
    public string? ErrorType { get; init; }
    public string? ErrorMessage { get; init; }
}

internal sealed class SemanticReparseReport
{
    public required SemanticComparison Original { get; init; }
    public required SemanticComparison CurrentWriterNoOp { get; init; }
    public required SemanticComparison ReencryptedPreserveRefCount { get; init; }
    public required SemanticComparison ReencryptedPreserveStorageAndRefCount { get; init; }
}

internal sealed class RefCountMismatch
{
    public int StringIndex { get; init; }
    public int StoredRefCount { get; init; }
    public int CalculatedReferenceCount { get; init; }
    public required string[] SampleIdentities { get; init; }
}

internal sealed class SharedIndexSample
{
    public int StringIndex { get; init; }
    public int ReferenceCount { get; init; }
    public int StoredRefCount { get; init; }
    public required string[] SampleIdentities { get; init; }
    public required string PlainTextPreview { get; init; }
}

internal sealed class SharedIndexSummary
{
    public int KeyEntryCount { get; init; }
    public int StringRecordCount { get; init; }
    public int ReferencedStringIndexCount { get; init; }
    public int SharedStringIndexCount { get; init; }
    public int MaxReferencesToSingleString { get; init; }
    public int InvalidStringIndexCount { get; init; }
    public long StoredRefCountSum { get; init; }
    public long CalculatedReferenceCountSum { get; init; }
    public int RefCountExactMatchCount { get; init; }
    public int RefCountMismatchCount { get; init; }
    public required List<RefCountMismatch> MismatchSamples { get; init; }
    public required List<SharedIndexSample> TopSharedIndices { get; init; }
}

internal sealed class RecordMismatchSample
{
    public int StringIndex { get; init; }
    public required string[] SampleIdentities { get; init; }
    public FStringStorage OriginalStorage { get; init; }
    public FStringStorage CurrentStorage { get; init; }
    public int OriginalSignedLength { get; init; }
    public int CurrentSignedLength { get; init; }
    public int OriginalRefCount { get; init; }
    public int CurrentRefCount { get; init; }
    public long OriginalRecordSize { get; init; }
    public long CurrentRecordSize { get; init; }
    public int RecordSizeDelta { get; init; }
    public bool PlaintextEqual { get; init; }
    public bool CiphertextEqual { get; init; }
    public bool CanonicalReEncryptionMatchesOriginalCiphertext { get; init; }
    public required string PlainTextPreview { get; init; }
}

internal sealed class RecordForensics
{
    public int StringRecordCount { get; init; }
    public int PlaintextEqualCount { get; init; }
    public int CiphertextEqualCount { get; init; }
    public int CanonicalReEncryptionMatchesOriginalCiphertextCount { get; init; }
    public int RefCountEqualCount { get; init; }
    public int StorageEqualCount { get; init; }
    public int SignedLengthEqualCount { get; init; }
    public int RecordSizeEqualCount { get; init; }
    public required Dictionary<string, int> OriginalStorageHistogram { get; init; }
    public required Dictionary<string, int> CurrentStorageHistogram { get; init; }
    public required Dictionary<string, int> StorageTransitions { get; init; }
    public required Dictionary<int, int> RecordSizeDeltaHistogram { get; init; }
    public long SummedRecordSizeDelta { get; init; }
    public long WholeFileSizeDelta { get; init; }
    public long? FirstDifferentOffset { get; init; }
    public long WholeFileDifferingByteCount { get; init; }
    public required List<RecordMismatchSample> MismatchSamples { get; init; }
}

internal sealed class VariantReport
{
    public required string Name { get; init; }
    public required string Sha256 { get; init; }
    public long Size { get; init; }
    public long SizeDelta { get; init; }
    public bool ByteIdentical { get; init; }
    public long? FirstDifferentOffset { get; init; }
    public long DifferingByteCount { get; init; }
    public bool PrefixThroughKeySectionIdentical { get; init; }
    public int StringRecordCount { get; init; }
    public long RefCountSum { get; init; }
}

internal sealed class ProbeSummary
{
    public required string VirtualPath { get; init; }
    public required string ContainerName { get; init; }
    public long ReadOrder { get; init; }
    public required string OriginalSha256 { get; init; }
    public long OriginalSize { get; init; }
    public int KeyEntryCount { get; init; }
    public int StringRecordCount { get; init; }
    public int SharedStringIndexCount { get; init; }
    public int RefCountExactMatchCount { get; init; }
    public int RefCountMismatchCount { get; init; }
    public bool CurrentWriterSemanticReparseSucceeded { get; init; }
    public int? CurrentWriterSemanticValueMismatchCount { get; init; }
    public bool CurrentWriterByteIdentical { get; init; }
    public bool PreserveStorageAndRefCountByteIdentical { get; init; }
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