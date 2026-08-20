using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Localization;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.VirtualFileSystem;
using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NTE.LocresMutationProbe;

internal static class Program
{
    private const string SplitMarker = "HottaLocresSplit";
    private static readonly byte[] NteLocresKey = Convert.FromHexString(
        "396d4330686f704b4e6a5377694364684e56375974435765754476484c513238");

    private static readonly Regex GenderRegex = new(
        @"^(?<pre>.*?)<male=>(?<male>.*?)<male>(?<mid>.*?)<female=>(?<female>.*?)<female>(?<post>.*)$",
        RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex BraceRegex = new(@"\{(?:0|PlayerName)\}", RegexOptions.Compiled);
    private static readonly Regex AngleRegex = new(@"<(?:NumGreen|Blue|Orange|red|Yellow|Green)(?:\s[^>]*)?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ProtectedTokenRegex = new(
        @"<[^>\r\n]+>|\{[^{}\r\n]+\}|\[[^\]\r\n]+\]|%(?:\d+\$)?[A-Za-z]",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

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
                if (arg.StartsWith("--aes-config=", StringComparison.OrdinalIgnoreCase))
                    aesConfig = Path.GetFullPath(arg["--aes-config=".Length..].Trim('"'));
                else if (arg.StartsWith("--aes-file=", StringComparison.OrdinalIgnoreCase))
                    aesFile = Path.GetFullPath(arg["--aes-file=".Length..].Trim('"'));
                else throw new ArgumentException($"Unknown argument: {arg}");
            }

            ValidateInputs(gameRoot, aesConfig, aesFile);
            Directory.CreateDirectory(outputDir);
            var artifactsDir = Path.Combine(outputDir, "artifacts-local-only");
            Directory.CreateDirectory(artifactsDir);

            Console.WriteLine("NTE LOCRES Controlled Mutation Probe 012");
            Console.WriteLine("Purpose: prove identity-aware edits, shared-strIdx splitting, protected-token preservation, and RefCount repair");
            Console.WriteLine("Offline only; this probe does not install or modify game archives.");
            Console.WriteLine();

            var aesKey = LoadAesKey(aesConfig, aesFile);
            using var aesScope = TemporaryAesCompatibility.Install(gameRoot, aesKey);
            using var reader = CreateReaderWithoutLeakingAes(gameRoot);
            var provider = GetProvider(reader);

            var virtualPath = provider.Files.Keys
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(x => NormalizePath(x).EndsWith("/Content/Localization/Game/es/Game.locres", StringComparison.OrdinalIgnoreCase))
                ?? throw new FileNotFoundException("Effective ES Game.locres virtual path was not found.");
            if (!provider.Files.TryGetValue(virtualPath, out var effectiveEs) || effectiveEs is null)
                throw new InvalidOperationException($"Provider could not resolve effective ES LOCRES: {virtualPath}");

            var sourceDesc = DescribeSource(effectiveEs);
            var originalBytes = effectiveEs.Read();
            var original = ParseRaw(originalBytes);
            var refsByIndex = original.KeyEntries.GroupBy(x => x.StringIndex).ToDictionary(g => g.Key, g => g.ToList());

            var mutations = SelectMutations(original, refsByIndex);
            if (mutations.Count < 4)
                throw new InvalidOperationException($"Expected at least four controlled mutation categories; selected {mutations.Count}.");

            var candidateBytes = BuildCandidate(original, mutations);
            var candidatePath = Path.Combine(artifactsDir, "controlled-mutation-candidate.locres");
            File.WriteAllBytes(candidatePath, candidateBytes);
            var candidate = ParseRaw(candidateBytes);

            var originalSemantic = ParseSemantic(originalBytes, virtualPath);
            var candidateSemantic = ParseSemantic(candidateBytes, virtualPath);
            if (!originalSemantic.Success)
                throw new InvalidOperationException($"Original semantic parse failed: {originalSemantic.ErrorMessage}");

            var semanticValidation = ValidateSemantics(originalSemantic, candidateSemantic, mutations);
            var refValidation = ValidateRefCounts(candidate);
            var mutationReport = BuildMutationReport(mutations, original, candidate);

            WriteJson(Path.Combine(outputDir, "mutation-plan.json"), mutationReport);
            WriteJson(Path.Combine(outputDir, "semantic-validation.json"), semanticValidation);
            WriteJson(Path.Combine(outputDir, "refcount-validation.json"), refValidation);

            var success = candidateSemantic.Success &&
                          semanticValidation.MissingIdentityCount == 0 &&
                          semanticValidation.ExtraIdentityCount == 0 &&
                          semanticValidation.UnexpectedLocalizedValueMismatchCount == 0 &&
                          semanticValidation.SelectedValueMatchCount == mutations.Count &&
                          semanticValidation.NamespaceOrKeyHashMismatchCount == 0 &&
                          semanticValidation.ProtectedTokenMismatchCount == 0 &&
                          refValidation.RefCountMismatchCount == 0 &&
                          refValidation.InvalidStringIndexCount == 0;

            var summary = new ProbeSummary
            {
                VirtualPath = virtualPath,
                ContainerName = sourceDesc.ContainerName,
                ReadOrder = sourceDesc.ReadOrder,
                OriginalSha256 = Sha256(originalBytes),
                CandidateSha256 = Sha256(candidateBytes),
                OriginalSize = originalBytes.LongLength,
                CandidateSize = candidateBytes.LongLength,
                OriginalKeyEntryCount = original.KeyEntries.Count,
                CandidateKeyEntryCount = candidate.KeyEntries.Count,
                OriginalStringRecordCount = original.StringRecords.Count,
                CandidateStringRecordCount = candidate.StringRecords.Count,
                MutationCount = mutations.Count,
                InPlaceMutationCount = mutations.Count(x => !x.RequiresSplit),
                SplitMutationCount = mutations.Count(x => x.RequiresSplit),
                SemanticReparseSucceeded = candidateSemantic.Success,
                SelectedValueMatchCount = semanticValidation.SelectedValueMatchCount,
                UnexpectedLocalizedValueMismatchCount = semanticValidation.UnexpectedLocalizedValueMismatchCount,
                NamespaceOrKeyHashMismatchCount = semanticValidation.NamespaceOrKeyHashMismatchCount,
                ProtectedTokenMismatchCount = semanticValidation.ProtectedTokenMismatchCount,
                RefCountMismatchCount = refValidation.RefCountMismatchCount,
                InvalidStringIndexCount = refValidation.InvalidStringIndexCount,
                Success = success,
                LocalCandidatePath = candidatePath
            };
            WriteJson(Path.Combine(outputDir, "summary.json"), summary);

            Console.WriteLine($"Selected mutations: {mutations.Count} ({summary.InPlaceMutationCount} in-place, {summary.SplitMutationCount} split)");
            foreach (var m in mutations)
                Console.WriteLine($"- {m.Category}: {m.Identity} | old strIdx={m.OriginalStringIndex} | split={m.RequiresSplit}");
            Console.WriteLine($"Candidate semantic reparse: {(candidateSemantic.Success ? "SUCCESS" : "FAILED")}");
            Console.WriteLine($"Selected values exact: {semanticValidation.SelectedValueMatchCount}/{mutations.Count}");
            Console.WriteLine($"Unexpected value mismatches: {semanticValidation.UnexpectedLocalizedValueMismatchCount}");
            Console.WriteLine($"Protected-token mismatches: {semanticValidation.ProtectedTokenMismatchCount}");
            Console.WriteLine($"RefCount mismatches: {refValidation.RefCountMismatchCount}");
            Console.WriteLine($"Overall controlled mutation validation: {(success ? "SUCCESS" : "FAILED")}");
            Console.WriteLine($"Reports: {outputDir}");
            Console.WriteLine($"Raw candidate remains local only: {artifactsDir}");
            return success ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex}");
            return 1;
        }
    }

    private static List<MutationCase> SelectMutations(RawLocresDocument source, Dictionary<int, List<KeyEntry>> refsByIndex)
    {
        var result = new List<MutationCase>();
        var usedIndices = new HashSet<int>();

        KeyEntry? Find(Func<string, int, bool> predicate, bool requireUnique)
        {
            foreach (var entry in source.KeyEntries)
            {
                if (usedIndices.Contains(entry.StringIndex)) continue;
                if (!refsByIndex.TryGetValue(entry.StringIndex, out var refs)) continue;
                if (requireUnique && refs.Count != 1) continue;
                if (!requireUnique && refs.Count < 2) continue;
                var text = source.StringRecords[entry.StringIndex].PlainText;
                if (predicate(text, refs.Count)) return entry;
            }
            return null;
        }

        void Add(string category, KeyEntry entry, string newText)
        {
            var refs = refsByIndex[entry.StringIndex];
            var originalText = source.StringRecords[entry.StringIndex].PlainText;
            result.Add(new MutationCase
            {
                Category = category,
                Namespace = entry.Namespace,
                Key = entry.Key,
                Identity = IdentityOf(entry),
                OriginalStringIndex = entry.StringIndex,
                OriginalReferenceCount = refs.Count,
                RequiresSplit = refs.Count > 1,
                OriginalText = originalText,
                NewText = newText,
                OriginalProtectedTokens = ProtectedTokens(originalText),
                NewProtectedTokens = ProtectedTokens(newText)
            });
            usedIndices.Add(entry.StringIndex);
        }

        var plain = Find((text, _) => IsSimpleHumanText(text), requireUnique: true)
            ?? throw new InvalidOperationException("Could not find a unique plain-text identity for mutation.");
        Add("unique_plain", plain, "PTBR TESTE: " + source.StringRecords[plain.StringIndex].PlainText);

        var shared = Find((text, _) => IsSimpleHumanText(text), requireUnique: false)
            ?? throw new InvalidOperationException("Could not find a shared plain-text strIdx for split mutation.");
        Add("shared_stridx_split", shared, "PTBR SPLIT: " + source.StringRecords[shared.StringIndex].PlainText);

        var gender = Find((text, _) => CanSafelyMutateGender(text), requireUnique: true)
            ?? Find((text, _) => CanSafelyMutateGender(text), requireUnique: false)
            ?? throw new InvalidOperationException("Could not find a safe <male>/<female> sample.");
        Add("male_female_branch", gender, MutateGender(source.StringRecords[gender.StringIndex].PlainText));

        var brace = Find((text, _) => BraceRegex.IsMatch(text) && !text.Contains("<male=>", StringComparison.OrdinalIgnoreCase), requireUnique: true)
            ?? Find((text, _) => BraceRegex.IsMatch(text) && !text.Contains("<male=>", StringComparison.OrdinalIgnoreCase), requireUnique: false)
            ?? throw new InvalidOperationException("Could not find a placeholder sample.");
        Add("brace_placeholder", brace, "PTBR PLACEHOLDER: " + source.StringRecords[brace.StringIndex].PlainText);

        var angle = Find((text, _) => AngleRegex.IsMatch(text) && !text.Contains("<male=>", StringComparison.OrdinalIgnoreCase), requireUnique: true)
            ?? Find((text, _) => AngleRegex.IsMatch(text) && !text.Contains("<male=>", StringComparison.OrdinalIgnoreCase), requireUnique: false);
        if (angle is not null)
            Add("angle_markup", angle, "PTBR MARKUP: " + source.StringRecords[angle.StringIndex].PlainText);

        return result;
    }

    private static bool IsSimpleHumanText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 3 || text.Length > 120) return false;
        if (text.IndexOfAny(['<', '>', '{', '}', '[', ']', '%', '\r', '\n']) >= 0) return false;
        return text.Any(char.IsLetter);
    }

    private static bool CanSafelyMutateGender(string text)
    {
        var m = GenderRegex.Match(text);
        if (!m.Success) return false;
        static bool SimpleBranch(string value) => value.Length > 0 && value.Length < 120 &&
            value.IndexOfAny(['<', '>', '{', '}', '[', ']']) < 0;
        return SimpleBranch(m.Groups["male"].Value) && SimpleBranch(m.Groups["female"].Value);
    }

    private static string MutateGender(string text)
    {
        var m = GenderRegex.Match(text);
        if (!m.Success) throw new InvalidOperationException("Gender mutation called for a non-matching value.");
        return m.Groups["pre"].Value +
               "<male=>PTBR TESTE MASCULINO<male>" +
               m.Groups["mid"].Value +
               "<female=>PTBR TESTE FEMININO<female>" +
               m.Groups["post"].Value;
    }

    private static string[] ProtectedTokens(string text) => ProtectedTokenRegex.Matches(text)
        .Select(x => x.Value)
        .OrderBy(x => x, StringComparer.Ordinal)
        .ToArray();

    private static byte[] BuildCandidate(RawLocresDocument source, List<MutationCase> mutations)
    {
        var prefix = source.Prefix.ToArray();
        var plainOverrides = new Dictionary<int, string>();
        var adjustedRefCounts = source.StringRecords.Select(x => x.RefCount).ToArray();
        var appended = new List<(string Plain, int RefCount)>();

        foreach (var mutation in mutations)
        {
            var key = source.KeyEntries.Single(x => x.Namespace == mutation.Namespace && x.Key == mutation.Key);
            if (!mutation.RequiresSplit)
            {
                plainOverrides[key.StringIndex] = mutation.NewText;
                mutation.NewStringIndex = key.StringIndex;
                mutation.NewReferenceCount = adjustedRefCounts[key.StringIndex];
                continue;
            }

            adjustedRefCounts[key.StringIndex]--;
            if (adjustedRefCounts[key.StringIndex] <= 0)
                throw new InvalidOperationException($"Split mutation unexpectedly orphaned original strIdx {key.StringIndex}.");

            var newIndex = source.StringRecords.Count + appended.Count;
            appended.Add((mutation.NewText, 1));
            BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(checked((int)key.StringIndexOffset), 4), newIndex);
            mutation.NewStringIndex = newIndex;
            mutation.NewReferenceCount = 1;
        }

        using var ms = new MemoryStream(source.FileSize + 8192);
        ms.Write(prefix);
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write((uint)(source.StringRecords.Count + appended.Count));

        foreach (var record in source.StringRecords)
        {
            var encoded = plainOverrides.TryGetValue(record.Index, out var newPlain)
                ? EncryptNteStringPkcs7(newPlain)
                : record.EncodedText;
            WriteFString(w, encoded, record.Storage);
            w.Write(adjustedRefCounts[record.Index]);
        }
        foreach (var extra in appended)
        {
            WriteFString(w, EncryptNteStringPkcs7(extra.Plain), FStringStorage.Ansi);
            w.Write(extra.RefCount);
        }
        w.Flush();
        return ms.ToArray();
    }

    private static MutationPlanReport BuildMutationReport(List<MutationCase> mutations, RawLocresDocument original, RawLocresDocument candidate)
    {
        foreach (var m in mutations)
        {
            m.CandidateText = candidate.StringRecords[m.NewStringIndex].PlainText;
            m.OriginalRefCountAfterMutation = candidate.StringRecords[m.OriginalStringIndex].RefCount;
            m.ProtectedTokensPreserved = m.OriginalProtectedTokens.SequenceEqual(m.NewProtectedTokens, StringComparer.Ordinal);
        }
        return new MutationPlanReport { Mutations = mutations };
    }

    private static SemanticValidation ValidateSemantics(SemanticSnapshot baseline, SemanticSnapshot candidate, List<MutationCase> mutations)
    {
        if (!candidate.Success)
            return new SemanticValidation { CandidateParseSucceeded = false, CandidateError = candidate.ErrorMessage };

        var expected = mutations.ToDictionary(x => (x.Namespace, x.Key), x => x.NewText);
        var missing = baseline.Entries.Keys.Except(candidate.Entries.Keys).ToList();
        var extra = candidate.Entries.Keys.Except(baseline.Entries.Keys).ToList();
        var shared = baseline.Entries.Keys.Intersect(candidate.Entries.Keys).ToList();
        var selectedMatches = expected.Count(x => candidate.Entries.TryGetValue(x.Key, out var e) && e.LocalizedString == x.Value);
        var allValueMismatches = shared.Where(k => baseline.Entries[k].LocalizedString != candidate.Entries[k].LocalizedString).ToList();
        var unexpected = allValueMismatches.Count(k => !expected.ContainsKey(k));
        var hashMismatches = shared.Count(k => baseline.Entries[k].NamespaceHash != candidate.Entries[k].NamespaceHash || baseline.Entries[k].KeyHash != candidate.Entries[k].KeyHash);
        var tokenMismatches = mutations.Count(x => !x.OriginalProtectedTokens.SequenceEqual(x.NewProtectedTokens, StringComparer.Ordinal));

        return new SemanticValidation
        {
            CandidateParseSucceeded = true,
            BaselineEntryCount = baseline.Entries.Count,
            CandidateEntryCount = candidate.Entries.Count,
            MissingIdentityCount = missing.Count,
            ExtraIdentityCount = extra.Count,
            TotalLocalizedValueMismatchCount = allValueMismatches.Count,
            ExpectedLocalizedValueMismatchCount = mutations.Count,
            UnexpectedLocalizedValueMismatchCount = unexpected,
            SelectedValueMatchCount = selectedMatches,
            NamespaceOrKeyHashMismatchCount = hashMismatches,
            ProtectedTokenMismatchCount = tokenMismatches,
            MissingIdentitySamples = missing.Take(10).Select(IdentityOf).ToArray(),
            ExtraIdentitySamples = extra.Take(10).Select(IdentityOf).ToArray()
        };
    }

    private static RefCountValidation ValidateRefCounts(RawLocresDocument doc)
    {
        var invalidIndices = doc.KeyEntries.Count(x => x.StringIndex < 0 || x.StringIndex >= doc.StringRecords.Count);
        var refs = doc.KeyEntries.GroupBy(x => x.StringIndex).ToDictionary(g => g.Key, g => g.Count());
        var mismatches = new List<RefCountMismatch>();
        for (var i = 0; i < doc.StringRecords.Count; i++)
        {
            var calculated = refs.GetValueOrDefault(i);
            var stored = doc.StringRecords[i].RefCount;
            if (stored != calculated && mismatches.Count < 20)
                mismatches.Add(new RefCountMismatch(i, stored, calculated));
        }
        var exact = Enumerable.Range(0, doc.StringRecords.Count).Count(i => doc.StringRecords[i].RefCount == refs.GetValueOrDefault(i));
        return new RefCountValidation
        {
            KeyEntryCount = doc.KeyEntries.Count,
            StringRecordCount = doc.StringRecords.Count,
            InvalidStringIndexCount = invalidIndices,
            RefCountExactMatchCount = exact,
            RefCountMismatchCount = doc.StringRecords.Count - exact,
            MismatchSamples = mismatches
        };
    }

    private static RawLocresDocument ParseRaw(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
        ReadExact(r, 16);
        r.ReadByte();
        r.ReadInt32();
        r.ReadInt32();
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
                var indexOffset = ms.Position;
                var stringIndex = r.ReadInt32();
                keys.Add(new KeyEntry(ns, nsHash, key, keyHash, sourceHash, stringIndex, indexOffset));
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
            if (!decrypt.Success) throw new InvalidDataException($"String {i} failed decryption: {decrypt.Error}");
            records.Add(new StringRecord(checked((int)i), encoded.Storage, encoded.Value, refCount, decrypt.PlainText));
        }
        if (ms.Position != ms.Length)
            throw new InvalidDataException($"String table ended at {ms.Position}; file length is {ms.Length}.");
        return new RawLocresDocument(prefix, keys, records, bytes.LongLength);
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
        catch (Exception ex) { return new SemanticSnapshot(false, ex.Message, []); }
    }

    private static string EncryptNteStringPkcs7(string plain)
    {
        var input = Encoding.UTF8.GetBytes(plain + SplitMarker);
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        var encrypted = aes.CreateEncryptor(NteLocresKey, null).TransformFinalBlock(input, 0, input.Length);
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
            var decrypted = aes.CreateDecryptor(NteLocresKey, null).TransformFinalBlock(encrypted, 0, encrypted.Length);
            var decoded = Encoding.UTF8.GetString(decrypted);
            var marker = decoded.IndexOf(SplitMarker, StringComparison.Ordinal);
            return marker < 0 ? new(false, string.Empty, "HottaLocresSplit marker not found") : new(true, decoded[..marker], null);
        }
        catch (Exception ex) { return new(false, string.Empty, ex.Message); }
    }

    private static FStringRead ReadFString(BinaryReader r)
    {
        var signedLength = r.ReadInt32();
        if (signedLength == 0) return new(string.Empty, FStringStorage.Empty);
        if (signedLength > 0)
        {
            var payload = ReadExact(r, signedLength);
            return new(Encoding.UTF8.GetString(payload, 0, Math.Max(0, payload.Length - 1)), FStringStorage.Ansi);
        }
        var chars = checked(-signedLength);
        var payloadWide = ReadExact(r, checked(chars * 2));
        return new(Encoding.Unicode.GetString(payloadWide, 0, Math.Max(0, payloadWide.Length - 2)), FStringStorage.Wide);
    }

    private static void WriteFString(BinaryWriter w, string value, FStringStorage storage)
    {
        if (string.IsNullOrEmpty(value)) { w.Write(0); return; }
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
        if (data.Length != count) throw new EndOfStreamException($"Expected {count} bytes, got {data.Length}.");
        return data;
    }

    private static string IdentityOf(KeyEntry e) => string.IsNullOrEmpty(e.Namespace) ? e.Key : $"{e.Namespace}::{e.Key}";
    private static string IdentityOf((string Ns, string Key) e) => string.IsNullOrEmpty(e.Ns) ? e.Key : $"{e.Ns}::{e.Key}";
    private static string NormalizePath(string path) => path.Replace('\\', '/');
    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private static SourceDescription DescribeSource(GameFile file) => file is VfsEntry e
        ? new(e.Vfs.Name, e.Vfs.Path, e.Vfs.ReadOrder)
        : new("<non-vfs>", string.Empty, 0);

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
        try { Console.SetOut(new AesRedactingTextWriter(originalOut)); return new UnrealArchiveReader(gameRoot); }
        finally { Console.SetOut(originalOut); }
    }

    private static string LoadAesKey(string? aesConfig, string? aesFile)
    {
        if (aesConfig is null && aesFile is null) return string.Empty;
        string raw;
        if (aesFile is not null) raw = File.ReadAllText(aesFile).Trim();
        else
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(aesConfig!));
            if (!doc.RootElement.TryGetProperty("aes_key", out var p)) throw new InvalidDataException("AES config lacks aes_key.");
            raw = p.GetString()?.Trim() ?? string.Empty;
        }
        if (!raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) raw = "0x" + raw;
        if (raw.Length != 66 || raw.Skip(2).Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("AES key must have 64 hex digits.");
        return raw;
    }

    private static void ValidateInputs(string gameRoot, string? aesConfig, string? aesFile)
    {
        if (!Directory.Exists(gameRoot)) throw new DirectoryNotFoundException(gameRoot);
        if (aesConfig is not null && aesFile is not null) throw new ArgumentException("Use only one AES source.");
        if (aesConfig is not null && !File.Exists(aesConfig)) throw new FileNotFoundException("AES config not found.", aesConfig);
        if (aesFile is not null && !File.Exists(aesFile)) throw new FileNotFoundException("AES file not found.", aesFile);
    }

    private static void WriteJson<T>(string path, T value) => File.WriteAllText(path, JsonSerializer.Serialize(value, JsonIndented), new UTF8Encoding(false));
    private static void PrintUsage() => Console.WriteLine("NTE.LocresMutationProbe <gameRoot> <outputDir> [--aes-config=<path> | --aes-file=<path>]");
    private static readonly JsonSerializerOptions JsonIndented = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = true };
}

internal enum FStringStorage { Empty, Ansi, Wide }
internal sealed record FStringRead(string Value, FStringStorage Storage);
internal sealed record DecryptResult(bool Success, string PlainText, string? Error);
internal sealed record SourceDescription(string ContainerName, string ContainerPath, long ReadOrder);
internal sealed record KeyEntry(string Namespace, uint NamespaceHash, string Key, uint KeyHash, int SourceHash, int StringIndex, long StringIndexOffset);
internal sealed record StringRecord(int Index, FStringStorage Storage, string EncodedText, int RefCount, string PlainText);
internal sealed record RawLocresDocument(byte[] Prefix, List<KeyEntry> KeyEntries, List<StringRecord> StringRecords, long FileSize);
internal sealed record SemanticEntry(uint NamespaceHash, uint KeyHash, string LocalizedString);
internal sealed record SemanticSnapshot(bool Success, string? ErrorMessage, Dictionary<(string Ns, string Key), SemanticEntry> Entries);

internal sealed class MutationCase
{
    public required string Category { get; init; }
    public required string Namespace { get; init; }
    public required string Key { get; init; }
    public required string Identity { get; init; }
    public int OriginalStringIndex { get; init; }
    public int OriginalReferenceCount { get; init; }
    public bool RequiresSplit { get; init; }
    public required string OriginalText { get; init; }
    public required string NewText { get; init; }
    public required string[] OriginalProtectedTokens { get; init; }
    public required string[] NewProtectedTokens { get; init; }
    public int NewStringIndex { get; set; }
    public int NewReferenceCount { get; set; }
    public string? CandidateText { get; set; }
    public int OriginalRefCountAfterMutation { get; set; }
    public bool ProtectedTokensPreserved { get; set; }
}
internal sealed class MutationPlanReport { public required List<MutationCase> Mutations { get; init; } }
internal sealed class RefCountMismatch(int stringIndex, int stored, int calculated) { public int StringIndex { get; init; } = stringIndex; public int Stored { get; init; } = stored; public int Calculated { get; init; } = calculated; }
internal sealed class RefCountValidation
{
    public int KeyEntryCount { get; init; }
    public int StringRecordCount { get; init; }
    public int InvalidStringIndexCount { get; init; }
    public int RefCountExactMatchCount { get; init; }
    public int RefCountMismatchCount { get; init; }
    public required List<RefCountMismatch> MismatchSamples { get; init; }
}
internal sealed class SemanticValidation
{
    public bool CandidateParseSucceeded { get; init; }
    public string? CandidateError { get; init; }
    public int BaselineEntryCount { get; init; }
    public int CandidateEntryCount { get; init; }
    public int MissingIdentityCount { get; init; }
    public int ExtraIdentityCount { get; init; }
    public int TotalLocalizedValueMismatchCount { get; init; }
    public int ExpectedLocalizedValueMismatchCount { get; init; }
    public int UnexpectedLocalizedValueMismatchCount { get; init; }
    public int SelectedValueMatchCount { get; init; }
    public int NamespaceOrKeyHashMismatchCount { get; init; }
    public int ProtectedTokenMismatchCount { get; init; }
    public string[]? MissingIdentitySamples { get; init; }
    public string[]? ExtraIdentitySamples { get; init; }
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
    public int OriginalKeyEntryCount { get; init; }
    public int CandidateKeyEntryCount { get; init; }
    public int OriginalStringRecordCount { get; init; }
    public int CandidateStringRecordCount { get; init; }
    public int MutationCount { get; init; }
    public int InPlaceMutationCount { get; init; }
    public int SplitMutationCount { get; init; }
    public bool SemanticReparseSucceeded { get; init; }
    public int SelectedValueMatchCount { get; init; }
    public int UnexpectedLocalizedValueMismatchCount { get; init; }
    public int NamespaceOrKeyHashMismatchCount { get; init; }
    public int ProtectedTokenMismatchCount { get; init; }
    public int RefCountMismatchCount { get; init; }
    public int InvalidStringIndexCount { get; init; }
    public bool Success { get; init; }
    public required string LocalCandidatePath { get; init; }
}

internal sealed class TemporaryAesCompatibility : IDisposable
{
    private readonly string? _path; private readonly bool _hadExisting; private readonly byte[]? _previous;
    private TemporaryAesCompatibility(string? path, bool hadExisting, byte[]? previous) { _path = path; _hadExisting = hadExisting; _previous = previous; }
    public static TemporaryAesCompatibility Install(string gameRoot, string aesKey)
    {
        if (string.IsNullOrEmpty(aesKey)) return new(null, false, null);
        var path = Path.Combine(gameRoot, "aes.txt"); var had = File.Exists(path); var prev = had ? File.ReadAllBytes(path) : null;
        File.WriteAllText(path, aesKey, Encoding.ASCII); return new(path, had, prev);
    }
    public void Dispose() { if (_path is null) return; if (_hadExisting && _previous is not null) File.WriteAllBytes(_path, _previous); else if (File.Exists(_path)) File.Delete(_path); }
}
internal sealed class AesRedactingTextWriter(TextWriter inner) : TextWriter
{
    public override Encoding Encoding => inner.Encoding;
    public override void Write(char value) => inner.Write(value);
    public override void Write(string? value) => inner.Write(value);
    public override void WriteLine(string? value) => inner.WriteLine(value is not null && value.StartsWith("AES key loaded:", StringComparison.OrdinalIgnoreCase) ? "AES key loaded [REDACTED]" : value);
}
