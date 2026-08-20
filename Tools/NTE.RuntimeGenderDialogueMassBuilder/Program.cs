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
using System.Text.RegularExpressions;

namespace NTE.RuntimeGenderDialogueMassBuilder;

internal static class Program
{
    private const string SplitMarker = "HottaLocresSplit";
    private static readonly byte[] NteLocresKey = Convert.FromHexString(
        "396d4330686f704b4e6a5377694364684e56375974435765754476484c513238");

    private static readonly Regex StructuralTokenRegex = new(
        @"\{[^{}\r\n]+\}|<[^>\r\n]+>|\[[^\]\r\n]+\]|%(?:\d+\$)?[A-Za-z]",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex GenderMarkerRegex = new(
        @"<male=>|<male>|<female=>|<female>",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly string[] ExactGenderMarkers = ["<male=>", "<male>", "<female=>", "<female>"];

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
            var candidatesPath = Path.GetFullPath(args[1]);
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

            ValidateInputs(gameRoot, candidatesPath, aesConfig, aesFile);
            Directory.CreateDirectory(outputDir);
            var artifactsDir = Path.Combine(outputDir, "artifacts-local-only");
            Directory.CreateDirectory(artifactsDir);

            Console.WriteLine("NTE Runtime Gender Dialogue Mass Builder 019B");
            Console.WriteLine("Purpose: instrument every Run 019A dialogue-referenced ES full gender branch with unique visible branch markers.");
            Console.WriteLine("Identity-aware writer: shared physical string records are split when required.");
            Console.WriteLine("Builder does not install anything into the game.");
            Console.WriteLine();

            var sourceCandidates = LoadDialogueCandidates(candidatesPath);
            if (sourceCandidates.Count == 0)
                throw new InvalidOperationException("Run 019A candidate file contained no dialogue gender identities.");

            var duplicateIdentities = sourceCandidates
                .GroupBy(x => (x.Namespace, x.Key))
                .Where(g => g.Count() > 1)
                .Select(g => Identity(g.Key.Namespace, g.Key.Key))
                .ToList();
            if (duplicateIdentities.Count != 0)
                throw new InvalidDataException("Run 019A candidate file contains duplicate identities: " + string.Join(", ", duplicateIdentities.Take(8)));

            var orderedCandidates = sourceCandidates
                .OrderBy(x => x.Score)
                .ThenByDescending(x => x.SpeechReferenceCount)
                .ThenByDescending(x => x.ChoiceReferenceCount)
                .ThenBy(x => x.Identity, StringComparer.Ordinal)
                .ToList();

            var aesKey = LoadAesKey(aesConfig, aesFile);
            using var aesScope = TemporaryAesCompatibility.Install(gameRoot, aesKey);
            using var reader = CreateReaderWithoutLeakingAes(gameRoot);
            var provider = GetProvider(reader);

            var source = LoadEffectiveEs(provider);
            var raw = ParseRaw(source.Bytes);
            var semantic = ParseSemantic(source.Bytes, source.VirtualPath);
            if (!semantic.Success)
                throw new InvalidOperationException("ES baseline semantic parse failed: " + semantic.ErrorMessage);

            var keyMap = raw.KeyEntries.ToDictionary(x => (x.Namespace, x.Key));
            var refsByIndex = raw.KeyEntries.GroupBy(x => x.StringIndex).ToDictionary(g => g.Key, g => g.ToList());
            var plans = new List<MutationPlan>(orderedCandidates.Count);

            for (var i = 0; i < orderedCandidates.Count; i++)
            {
                var candidate = orderedCandidates[i];
                var id = (candidate.Namespace, candidate.Key);
                if (!keyMap.TryGetValue(id, out var keyEntry))
                    throw new InvalidOperationException($"Run 019A dialogue identity missing from current ES: {candidate.Identity}");
                if (!semantic.Entries.TryGetValue(id, out var semanticEntry))
                    throw new InvalidOperationException($"Run 019A dialogue identity missing from semantic ES: {candidate.Identity}");

                var record = RecordAt(raw, keyEntry.StringIndex);
                if (!record.DecryptSucceeded)
                    throw new InvalidDataException($"Could not decrypt {candidate.Identity}: {record.DecryptError}");
                if (!string.Equals(record.PlainText, semanticEntry.LocalizedString, StringComparison.Ordinal))
                    throw new InvalidDataException($"Raw/semantic mismatch for {candidate.Identity}.");
                if (!string.Equals(record.PlainText, candidate.EsText, StringComparison.Ordinal))
                    throw new InvalidDataException($"Run 019A source snapshot drift for {candidate.Identity}. Re-run 019A before 019B.");
                if (!HasExactGenderMarkerSequence(record.PlainText))
                    throw new InvalidDataException($"Current ES is no longer the exact four-marker gender shape for {candidate.Identity}.");

                var number = i + 1;
                var maleMarker = $"PTBR019-M-{number:0000} | ";
                var femaleMarker = $"PTBR019-F-{number:0000} | ";
                var newText = InjectMarkers(record.PlainText, maleMarker, femaleMarker);

                var originalTokens = StructuralTokens(record.PlainText);
                var candidateTokens = StructuralTokens(newText);
                if (!originalTokens.SequenceEqual(candidateTokens, StringComparer.Ordinal))
                    throw new InvalidDataException($"Structural-token drift while instrumenting {candidate.Identity}.");

                plans.Add(new MutationPlan
                {
                    Order = number,
                    Identity = candidate.Identity,
                    Namespace = candidate.Namespace,
                    Key = candidate.Key,
                    OriginalStringIndex = keyEntry.StringIndex,
                    OriginalReferenceCount = refsByIndex[keyEntry.StringIndex].Count,
                    OriginalText = record.PlainText,
                    CandidateText = newText,
                    MaleMarker = maleMarker.TrimEnd(),
                    FemaleMarker = femaleMarker.TrimEnd(),
                    SpeechReferenceCount = candidate.SpeechReferenceCount,
                    ChoiceReferenceCount = candidate.ChoiceReferenceCount,
                    Packages = candidate.Packages ?? [],
                    StructuralTokens = originalTokens
                });
            }

            var candidateBytes = BuildCandidate(raw, plans, refsByIndex);
            var candidatePath = Path.Combine(artifactsDir, "runtime-gender-dialogue-es.locres");
            File.WriteAllBytes(candidatePath, candidateBytes);

            var candidateSemantic = ParseSemantic(candidateBytes, source.VirtualPath);
            if (!candidateSemantic.Success)
                throw new InvalidOperationException("Candidate semantic parse failed: " + candidateSemantic.ErrorMessage);
            var candidateRaw = ParseRaw(candidateBytes);
            var refValidation = ValidateRefCounts(candidateRaw);

            var expected = plans.ToDictionary(x => (x.Namespace, x.Key), x => x.CandidateText);
            var missing = semantic.Entries.Keys.Except(candidateSemantic.Entries.Keys).ToList();
            var extra = candidateSemantic.Entries.Keys.Except(semantic.Entries.Keys).ToList();
            var shared = semantic.Entries.Keys.Intersect(candidateSemantic.Entries.Keys).ToList();
            var valueMismatches = shared.Where(k => semantic.Entries[k].LocalizedString != candidateSemantic.Entries[k].LocalizedString).ToList();
            var unexpected = valueMismatches.Where(k => !expected.ContainsKey(k)).ToList();
            var selectedWrong = expected.Count(x =>
                !candidateSemantic.Entries.TryGetValue(x.Key, out var value) ||
                !string.Equals(value.LocalizedString, x.Value, StringComparison.Ordinal));
            var hashMismatches = shared.Count(k =>
                semantic.Entries[k].NamespaceHash != candidateSemantic.Entries[k].NamespaceHash ||
                semantic.Entries[k].KeyHash != candidateSemantic.Entries[k].KeyHash);

            var candidateKeyMap = candidateRaw.KeyEntries.ToDictionary(x => (x.Namespace, x.Key));
            var keyMetadataMismatches = raw.KeyEntries.Count(x =>
            {
                if (!candidateKeyMap.TryGetValue((x.Namespace, x.Key), out var other)) return true;
                return x.NamespaceHash != other.NamespaceHash || x.KeyHash != other.KeyHash || x.SourceHash != other.SourceHash;
            });

            var structurePreserved = 0;
            var markerPreserved = 0;
            foreach (var plan in plans)
            {
                if (!candidateSemantic.Entries.TryGetValue((plan.Namespace, plan.Key), out var value))
                    continue;
                plan.CandidateSemanticText = value.LocalizedString;
                plan.StructurePreserved = StructuralTokens(plan.OriginalText)
                    .SequenceEqual(StructuralTokens(value.LocalizedString), StringComparer.Ordinal);
                plan.MarkersPresent = value.LocalizedString.Contains(plan.MaleMarker, StringComparison.Ordinal) &&
                                      value.LocalizedString.Contains(plan.FemaleMarker, StringComparison.Ordinal);
                if (candidateKeyMap.TryGetValue((plan.Namespace, plan.Key), out var candidateKey))
                {
                    plan.NewStringIndex = candidateKey.StringIndex;
                    plan.RequiresSplit = candidateKey.StringIndex != plan.OriginalStringIndex;
                    plan.NewReferenceCount = candidateRaw.StringRecords[candidateKey.StringIndex].RefCount;
                }
                if (plan.StructurePreserved) structurePreserved++;
                if (plan.MarkersPresent) markerPreserved++;
            }

            WriteJsonl(Path.Combine(outputDir, "mutation-plan.jsonl"), plans);

            var appendedCount = candidateRaw.StringRecords.Count - raw.StringRecords.Count;
            var splitCount = plans.Count(x => x.RequiresSplit);
            var success = missing.Count == 0 &&
                          extra.Count == 0 &&
                          unexpected.Count == 0 &&
                          selectedWrong == 0 &&
                          hashMismatches == 0 &&
                          keyMetadataMismatches == 0 &&
                          refValidation.RefCountMismatchCount == 0 &&
                          refValidation.InvalidStringIndexCount == 0 &&
                          valueMismatches.Count == plans.Count &&
                          structurePreserved == plans.Count &&
                          markerPreserved == plans.Count &&
                          appendedCount >= 0;

            var summary = new RunSummary
            {
                Success = success,
                VirtualPath = source.VirtualPath,
                ContainerName = source.ContainerName,
                ReadOrder = source.ReadOrder,
                OriginalSha256 = Sha256(source.Bytes),
                CandidateSha256 = Sha256(candidateBytes),
                BaselineEntryCount = semantic.Entries.Count,
                CandidateEntryCount = candidateSemantic.Entries.Count,
                OriginalStringRecordCount = raw.StringRecords.Count,
                CandidateStringRecordCount = candidateRaw.StringRecords.Count,
                SelectedIdentityCount = plans.Count,
                ExpectedChangedIdentityCount = plans.Count,
                ActualChangedIdentityCount = valueMismatches.Count,
                MissingIdentityCount = missing.Count,
                ExtraIdentityCount = extra.Count,
                UnexpectedChangedIdentityCount = unexpected.Count,
                SelectedIdentityWrongValueCount = selectedWrong,
                NamespaceOrKeyHashMismatchCount = hashMismatches,
                KeyMetadataMismatchCount = keyMetadataMismatches,
                RefCountMismatchCount = refValidation.RefCountMismatchCount,
                InvalidStringIndexCount = refValidation.InvalidStringIndexCount,
                StructurePreservedCount = structurePreserved,
                MarkersPresentCount = markerPreserved,
                SplitIdentityCount = splitCount,
                AppendedStringRecordCount = appendedCount,
                CandidatePath = candidatePath
            };
            WriteJson(Path.Combine(outputDir, "summary.json"), summary);

            Console.WriteLine($"Effective ES: {source.ContainerName} (readOrder {source.ReadOrder})");
            Console.WriteLine($"Dialogue gender identities instrumented: {plans.Count}");
            Console.WriteLine($"Actual changed identities: {valueMismatches.Count}/{plans.Count}");
            Console.WriteLine($"Split identities: {splitCount}");
            Console.WriteLine($"Appended string records: {appendedCount}");
            Console.WriteLine($"Unexpected changes: {unexpected.Count}");
            Console.WriteLine($"Hash/key metadata mismatches: {hashMismatches}/{keyMetadataMismatches}");
            Console.WriteLine($"RefCount mismatches: {refValidation.RefCountMismatchCount}");
            Console.WriteLine($"Structure preserved: {structurePreserved}/{plans.Count}");
            Console.WriteLine($"Markers present: {markerPreserved}/{plans.Count}");
            Console.WriteLine($"Builder success: {success}");
            Console.WriteLine();
            Console.WriteLine("First high-priority markers:");
            foreach (var plan in plans.Take(12))
                Console.WriteLine($"- {plan.MaleMarker} / {plan.FemaleMarker} => {plan.Identity} | {string.Join(", ", plan.Packages.Take(2))}");

            return success ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex);
            return 1;
        }
    }

    private static List<DialogueCandidate> LoadDialogueCandidates(string path)
    {
        var rows = new List<DialogueCandidate>();
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var row = JsonSerializer.Deserialize<DialogueCandidate>(line, JsonRead)
                ?? throw new InvalidDataException("Could not deserialize a Run 019A candidate row.");
            if (string.IsNullOrWhiteSpace(row.Namespace) || string.IsNullOrWhiteSpace(row.Key))
                throw new InvalidDataException("Run 019A candidate row is missing namespace/key.");
            row.Identity = string.IsNullOrWhiteSpace(row.Identity) ? Identity(row.Namespace, row.Key) : row.Identity;
            rows.Add(row);
        }
        return rows;
    }

    private static string InjectMarkers(string source, string maleMarker, string femaleMarker)
    {
        if (!HasExactGenderMarkerSequence(source))
            throw new InvalidDataException("Gender marker sequence is not the exact canonical four-marker form.");

        var maleStart = source.IndexOf("<male=>", StringComparison.Ordinal);
        var afterMaleStart = maleStart + "<male=>".Length;
        var withMale = source.Insert(afterMaleStart, maleMarker);
        var femaleStart = withMale.IndexOf("<female=>", StringComparison.Ordinal);
        var afterFemaleStart = femaleStart + "<female=>".Length;
        return withMale.Insert(afterFemaleStart, femaleMarker);
    }

    private static bool HasExactGenderMarkerSequence(string text)
    {
        var markers = GenderMarkerRegex.Matches(text).Select(x => x.Value).ToArray();
        return markers.SequenceEqual(ExactGenderMarkers, StringComparer.Ordinal);
    }

    private static string[] StructuralTokens(string text) => StructuralTokenRegex.Matches(text)
        .Select(x => x.Value)
        .ToArray();

    private static byte[] BuildCandidate(
        RawLocresDocument source,
        List<MutationPlan> plans,
        Dictionary<int, List<KeyEntry>> refsByIndex)
    {
        var prefix = source.Prefix.ToArray();
        var adjustedRefCounts = source.StringRecords.Select(x => x.RefCount).ToArray();
        var plainOverrides = new Dictionary<int, string>();
        var appended = new List<AppendedRecord>();
        var keyMap = source.KeyEntries.ToDictionary(x => (x.Namespace, x.Key));

        foreach (var group in plans.GroupBy(x => x.OriginalStringIndex).OrderBy(x => x.Key))
        {
            var originalIndex = group.Key;
            var selected = group.OrderBy(x => x.Order).ToList();
            if (!refsByIndex.TryGetValue(originalIndex, out var allRefs))
                throw new InvalidOperationException($"Reference group missing for strIdx {originalIndex}.");
            if (source.StringRecords[originalIndex].RefCount != allRefs.Count)
                throw new InvalidDataException($"Baseline RefCount mismatch at strIdx {originalIndex}.");

            var unselectedRefCount = allRefs.Count - selected.Count;
            if (unselectedRefCount < 0)
                throw new InvalidDataException($"Selected identity count exceeds references at strIdx {originalIndex}.");

            var startAt = 0;
            if (unselectedRefCount == 0)
            {
                var first = selected[0];
                plainOverrides[originalIndex] = first.CandidateText;
                adjustedRefCounts[originalIndex] = 1;
                first.NewStringIndex = originalIndex;
                first.RequiresSplit = false;
                first.NewReferenceCount = 1;
                startAt = 1;
            }
            else
            {
                adjustedRefCounts[originalIndex] = unselectedRefCount;
            }

            for (var i = startAt; i < selected.Count; i++)
            {
                var plan = selected[i];
                var key = keyMap[(plan.Namespace, plan.Key)];
                var newIndex = source.StringRecords.Count + appended.Count;
                appended.Add(new AppendedRecord(plan.CandidateText, source.StringRecords[originalIndex].Storage, 1));
                BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(checked((int)key.StringIndexOffset), 4), newIndex);
                plan.NewStringIndex = newIndex;
                plan.RequiresSplit = true;
                plan.NewReferenceCount = 1;
            }
        }

        var capacity = checked((int)(source.FileSize + Math.Max(1_048_576L, appended.Count * 512L)));
        using var ms = new MemoryStream(capacity);
        ms.Write(prefix);
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        writer.Write((uint)(source.StringRecords.Count + appended.Count));

        foreach (var record in source.StringRecords)
        {
            var encoded = plainOverrides.TryGetValue(record.Index, out var replacement)
                ? EncryptNteStringPkcs7(replacement)
                : record.EncodedText;
            WriteFString(writer, encoded, record.Storage);
            writer.Write(adjustedRefCounts[record.Index]);
        }

        foreach (var extra in appended)
        {
            WriteFString(writer, EncryptNteStringPkcs7(extra.PlainText), extra.Storage);
            writer.Write(extra.RefCount);
        }

        writer.Flush();
        return ms.ToArray();
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

    private static RawLocresDocument ParseRaw(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        ReadExact(reader, 16);
        var ueVersion = reader.ReadByte();
        var nteVersion = reader.ReadInt32();
        var encrypted = reader.ReadInt32() != 0;
        var stringTableOffset = reader.ReadInt64();
        if (ueVersion != 3 || nteVersion < 10100 || !encrypted)
            throw new InvalidDataException($"Unexpected NTE LOCRES header: ue={ueVersion} nte={nteVersion} encrypted={encrypted}");

        var totalEntries = reader.ReadUInt32();
        var namespaceCount = reader.ReadUInt32();
        var keys = new List<KeyEntry>(checked((int)totalEntries));

        for (var i = 0u; i < namespaceCount; i++)
        {
            var nsHash = reader.ReadUInt32();
            var ns = ReadFString(reader).Value;
            var keyCount = reader.ReadUInt32();
            for (var j = 0u; j < keyCount; j++)
            {
                var keyHash = reader.ReadUInt32();
                var key = ReadFString(reader).Value;
                var sourceHash = reader.ReadInt32();
                var stringIndexOffset = ms.Position;
                var stringIndex = reader.ReadInt32();
                keys.Add(new KeyEntry(ns, nsHash, key, keyHash, sourceHash, stringIndex, stringIndexOffset));
            }
        }

        if (keys.Count != totalEntries)
            throw new InvalidDataException($"Key count mismatch: header={totalEntries} parsed={keys.Count}.");
        if (ms.Position != stringTableOffset)
            throw new InvalidDataException($"Key section ended at {ms.Position}; expected {stringTableOffset}.");

        var prefix = bytes.AsSpan(0, checked((int)stringTableOffset)).ToArray();
        var stringCount = reader.ReadUInt32();
        var records = new List<StringRecord>(checked((int)stringCount));
        for (var i = 0; i < stringCount; i++)
        {
            var value = ReadFString(reader);
            var refCount = reader.ReadInt32();
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

    private static StringRecord RecordAt(RawLocresDocument doc, int index)
    {
        if (index < 0 || index >= doc.StringRecords.Count)
            throw new InvalidDataException($"Invalid strIdx {index}.");
        return doc.StringRecords[index];
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

    private static FStringRead ReadFString(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length == 0) return new FStringRead(string.Empty, FStringStorage.Empty);
        if (length > 0)
        {
            var bytes = ReadExact(reader, length);
            var count = bytes.Length > 0 && bytes[^1] == 0 ? bytes.Length - 1 : bytes.Length;
            return new FStringRead(Encoding.UTF8.GetString(bytes, 0, count), FStringStorage.Ansi);
        }

        var chars = checked(-length);
        var wide = ReadExact(reader, checked(chars * 2));
        var wideCount = wide.Length >= 2 && wide[^1] == 0 && wide[^2] == 0 ? wide.Length - 2 : wide.Length;
        return new FStringRead(Encoding.Unicode.GetString(wide, 0, wideCount), FStringStorage.Wide);
    }

    private static void WriteFString(BinaryWriter writer, string value, FStringStorage storage)
    {
        if (string.IsNullOrEmpty(value))
        {
            writer.Write(0);
            return;
        }
        if (storage == FStringStorage.Wide)
        {
            writer.Write(-(value.Length + 1));
            writer.Write(Encoding.Unicode.GetBytes(value));
            writer.Write((short)0);
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length + 1);
        writer.Write(bytes);
        writer.Write((byte)0);
    }

    private static byte[] ReadExact(BinaryReader reader, int count)
    {
        var bytes = reader.ReadBytes(count);
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

    private static void ValidateInputs(string gameRoot, string candidatesPath, string? aesConfig, string? aesFile)
    {
        if (!Directory.Exists(gameRoot)) throw new DirectoryNotFoundException(gameRoot);
        if (!File.Exists(candidatesPath)) throw new FileNotFoundException("Run 019A dialogue candidate JSONL not found.", candidatesPath);
        if (aesConfig is not null && !File.Exists(aesConfig)) throw new FileNotFoundException("AES config not found.", aesConfig);
        if (aesFile is not null && !File.Exists(aesFile)) throw new FileNotFoundException("AES file not found.", aesFile);
        if (aesConfig is not null && aesFile is not null) throw new ArgumentException("Use either --aes-config or --aes-file, not both.");
    }

    private static string NormalizePath(string value) => value.Replace('\\', '/');
    private static string Identity(string ns, string key) => ns + "::" + key;
    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static void WriteJson(string path, object value) =>
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonIndented) + Environment.NewLine, new UTF8Encoding(false));

    private static void WriteJsonl<T>(string path, IEnumerable<T> rows)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        foreach (var row in rows) writer.WriteLine(JsonSerializer.Serialize(row, JsonLine));
    }

    private static readonly JsonSerializerOptions JsonRead = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions JsonIndented = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private static readonly JsonSerializerOptions JsonLine = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };

    private static void PrintUsage() =>
        Console.WriteLine("Usage: NTE.RuntimeGenderDialogueMassBuilder <gameRoot> <gender-dialogue-candidates.jsonl> <outputDir> [--aes-config=<json> | --aes-file=<txt>]");
}

internal enum FStringStorage { Empty, Ansi, Wide }
internal sealed record FStringRead(string Value, FStringStorage Storage);
internal sealed record DecryptResult(bool Success, string PlainText, string? Error);
internal sealed record SourceDescription(string ContainerName, long ReadOrder);
internal sealed record KeyEntry(string Namespace, uint NamespaceHash, string Key, uint KeyHash, int SourceHash, int StringIndex, long StringIndexOffset);
internal sealed record StringRecord(int Index, FStringStorage Storage, string EncodedText, int RefCount, string PlainText, bool DecryptSucceeded, string? DecryptError);
internal sealed record RawLocresDocument(byte[] Prefix, List<KeyEntry> KeyEntries, List<StringRecord> StringRecords, long FileSize);
internal sealed record SemanticEntry(uint NamespaceHash, uint KeyHash, string LocalizedString);
internal sealed record SemanticSnapshot(bool Success, string? ErrorMessage, Dictionary<(string Ns, string Key), SemanticEntry> Entries);
internal sealed record RefValidation(int RefCountMismatchCount, int InvalidStringIndexCount);
internal sealed record SlotSource(string VirtualPath, string ContainerName, long ReadOrder, byte[] Bytes);
internal sealed record AppendedRecord(string PlainText, FStringStorage Storage, int RefCount);

internal sealed class DialogueCandidate
{
    public string Identity { get; set; } = string.Empty;
    public string Namespace { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string EsText { get; set; } = string.Empty;
    public int SpeechReferenceCount { get; set; }
    public int ChoiceReferenceCount { get; set; }
    public string[]? Packages { get; set; }
    public int Score { get; set; }
}

internal sealed class MutationPlan
{
    public int Order { get; set; }
    public required string Identity { get; init; }
    public required string Namespace { get; init; }
    public required string Key { get; init; }
    public int OriginalStringIndex { get; init; }
    public int NewStringIndex { get; set; }
    public int OriginalReferenceCount { get; init; }
    public int NewReferenceCount { get; set; }
    public bool RequiresSplit { get; set; }
    public required string OriginalText { get; init; }
    public required string CandidateText { get; init; }
    public required string MaleMarker { get; init; }
    public required string FemaleMarker { get; init; }
    public int SpeechReferenceCount { get; init; }
    public int ChoiceReferenceCount { get; init; }
    public required string[] Packages { get; init; }
    public required string[] StructuralTokens { get; init; }
    public string? CandidateSemanticText { get; set; }
    public bool StructurePreserved { get; set; }
    public bool MarkersPresent { get; set; }
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
    public int SelectedIdentityCount { get; init; }
    public int ExpectedChangedIdentityCount { get; init; }
    public int ActualChangedIdentityCount { get; init; }
    public int MissingIdentityCount { get; init; }
    public int ExtraIdentityCount { get; init; }
    public int UnexpectedChangedIdentityCount { get; init; }
    public int SelectedIdentityWrongValueCount { get; init; }
    public int NamespaceOrKeyHashMismatchCount { get; init; }
    public int KeyMetadataMismatchCount { get; init; }
    public int RefCountMismatchCount { get; init; }
    public int InvalidStringIndexCount { get; init; }
    public int StructurePreservedCount { get; init; }
    public int MarkersPresentCount { get; init; }
    public int SplitIdentityCount { get; init; }
    public int AppendedStringRecordCount { get; init; }
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
