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

namespace NTE.RuntimeSlotABBuilder;

internal static class Program
{
    private const string SplitMarker = "HottaLocresSplit";
    private const string TargetNamespace = "ST_Ui";
    private const string RequiredKey = "Setting_name";
    private const int MaxTargets = 24;

    private static readonly byte[] NteLocresKey = Convert.FromHexString(
        "396d4330686f704b4e6a5377694364684e56375974435765754476484c513238");

    private static readonly string[] PriorityKeyFragments =
    [
        "setting", "language", "graphic", "display", "resolution", "quality",
        "audio", "sound", "volume", "control", "keyboard", "mouse",
        "confirm", "cancel", "save", "apply", "close", "back", "return",
        "exit", "menu", "account"
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

            Console.WriteLine("NTE Runtime Slot A/B Builder 017");
            Console.WriteLine("Purpose: build FR and ES candidate LOCRES files from the same real identities and marker values.");
            Console.WriteLine("Builder is offline/read-only with respect to game archives; it does not install a PAK.");
            Console.WriteLine();

            var aesKey = LoadAesKey(aesConfig, aesFile);
            using var aesScope = TemporaryAesCompatibility.Install(gameRoot, aesKey);
            using var reader = CreateReaderWithoutLeakingAes(gameRoot);
            var provider = GetProvider(reader);

            var frSource = LoadEffectiveCulture(provider, "fr");
            var esSource = LoadEffectiveCulture(provider, "es");

            var frRaw = ParseRaw(frSource.Bytes);
            var esRaw = ParseRaw(esSource.Bytes);
            var frSemantic = ParseSemantic(frSource.Bytes, frSource.VirtualPath);
            var esSemantic = ParseSemantic(esSource.Bytes, esSource.VirtualPath);

            if (!frSemantic.Success)
                throw new InvalidOperationException("FR baseline semantic parse failed: " + frSemantic.ErrorMessage);
            if (!esSemantic.Success)
                throw new InvalidOperationException("ES baseline semantic parse failed: " + esSemantic.ErrorMessage);

            var frOnly = frSemantic.Entries.Keys.Except(esSemantic.Entries.Keys).ToList();
            var esOnly = esSemantic.Entries.Keys.Except(frSemantic.Entries.Keys).ToList();
            if (frOnly.Count != 0 || esOnly.Count != 0)
                throw new InvalidOperationException($"FR/ES identity sets diverged: frOnly={frOnly.Count}, esOnly={esOnly.Count}.");

            var selected = SelectTargets(frRaw, esRaw);
            if (selected.Count < 8)
                throw new InvalidOperationException($"Expected at least 8 safe shared UI identities; selected {selected.Count}.");
            if (!selected.Any(x => x.Namespace == TargetNamespace && x.Key == RequiredKey))
                throw new InvalidOperationException($"Required identity {TargetNamespace}::{RequiredKey} was not selected.");

            for (var i = 0; i < selected.Count; i++)
                selected[i].Marker = $"PTBR017-{i + 1:00}";

            var frBuild = BuildAndValidateSlot("fr", frSource, frRaw, frSemantic, selected, artifactsDir);
            var esBuild = BuildAndValidateSlot("es", esSource, esRaw, esSemantic, selected, artifactsDir);

            var targetRows = selected.Select(x => new TargetRow
            {
                Order = x.Order,
                Namespace = x.Namespace,
                Key = x.Key,
                Identity = Identity(x.Namespace, x.Key),
                Marker = x.Marker,
                FrOriginal = x.FrOriginal,
                EsOriginal = x.EsOriginal
            }).ToList();
            WriteJsonl(Path.Combine(outputDir, "selected-identities.jsonl"), targetRows);

            var success = frBuild.Success && esBuild.Success && selected.Count == frBuild.ActualChangedIdentityCount &&
                          selected.Count == esBuild.ActualChangedIdentityCount;

            var summary = new RunSummary
            {
                Success = success,
                SelectedIdentityCount = selected.Count,
                TargetNamespace = TargetNamespace,
                RequiredIdentity = Identity(TargetNamespace, RequiredKey),
                SameIdentitySetAcrossFrEs = frOnly.Count == 0 && esOnly.Count == 0,
                Fr = frBuild,
                Es = esBuild
            };
            WriteJson(Path.Combine(outputDir, "summary.json"), summary);

            Console.WriteLine($"Selected shared UI identities: {selected.Count}");
            foreach (var target in selected)
                Console.WriteLine($"- {target.Marker}: {target.Namespace}::{target.Key} | FR='{Compact(target.FrOriginal)}' | ES='{Compact(target.EsOriginal)}'");
            Console.WriteLine();
            Console.WriteLine($"FR candidate validation: {(frBuild.Success ? "SUCCESS" : "FAILED")} | changes={frBuild.ActualChangedIdentityCount} unexpected={frBuild.UnexpectedChangedIdentityCount} refCountErrors={frBuild.RefCountMismatchCount}");
            Console.WriteLine($"ES candidate validation: {(esBuild.Success ? "SUCCESS" : "FAILED")} | changes={esBuild.ActualChangedIdentityCount} unexpected={esBuild.UnexpectedChangedIdentityCount} refCountErrors={esBuild.RefCountMismatchCount}");
            Console.WriteLine($"Overall builder success: {success}");
            return success ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex);
            return 1;
        }
    }

    private static SlotSource LoadEffectiveCulture(DefaultFileProvider provider, string culture)
    {
        var suffix = $"/Content/Localization/Game/{culture}/Game.locres";
        var virtualPath = provider.Files.Keys
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(x => NormalizePath(x).EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"Effective {culture.ToUpperInvariant()} Game.locres virtual path was not found.");

        if (!provider.Files.TryGetValue(virtualPath, out var file) || file is null)
            throw new InvalidOperationException($"Provider could not resolve effective {culture.ToUpperInvariant()} LOCRES: {virtualPath}");

        var source = DescribeSource(file);
        return new SlotSource
        {
            Culture = culture,
            VirtualPath = virtualPath,
            ContainerName = source.ContainerName,
            ReadOrder = source.ReadOrder,
            Bytes = file.Read()
        };
    }

    private static List<TargetSelection> SelectTargets(RawLocresDocument fr, RawLocresDocument es)
    {
        var frKeys = fr.KeyEntries.ToDictionary(x => (x.Namespace, x.Key));
        var esKeys = es.KeyEntries.ToDictionary(x => (x.Namespace, x.Key));
        var shared = frKeys.Keys.Intersect(esKeys.Keys)
            .Where(x => string.Equals(x.Namespace, TargetNamespace, StringComparison.Ordinal))
            .Select(identity =>
            {
                var frEntry = frKeys[identity];
                var esEntry = esKeys[identity];
                var frText = PlainAt(fr, frEntry.StringIndex);
                var esText = PlainAt(es, esEntry.StringIndex);
                return new
                {
                    identity.Namespace,
                    identity.Key,
                    Fr = frText,
                    Es = esText,
                    Priority = KeyPriority(identity.Key)
                };
            })
            .Where(x => IsSimpleHumanText(x.Fr) && IsSimpleHumanText(x.Es))
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.Key, StringComparer.Ordinal)
            .ToList();

        var required = shared.FirstOrDefault(x => string.Equals(x.Key, RequiredKey, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Required safe identity {TargetNamespace}::{RequiredKey} was not found in both FR and ES.");

        var ordered = new List<(string Namespace, string Key, string Fr, string Es, int Priority)>
        {
            (required.Namespace, required.Key, required.Fr, required.Es, required.Priority)
        };

        foreach (var item in shared)
        {
            if (item.Key == RequiredKey) continue;
            ordered.Add((item.Namespace, item.Key, item.Fr, item.Es, item.Priority));
            if (ordered.Count >= MaxTargets) break;
        }

        return ordered.Select((x, i) => new TargetSelection
        {
            Order = i + 1,
            Namespace = x.Namespace,
            Key = x.Key,
            FrOriginal = x.Fr,
            EsOriginal = x.Es,
            Marker = string.Empty
        }).ToList();
    }

    private static int KeyPriority(string key)
    {
        if (string.Equals(key, RequiredKey, StringComparison.Ordinal)) return -1000;
        for (var i = 0; i < PriorityKeyFragments.Length; i++)
        {
            if (key.Contains(PriorityKeyFragments[i], StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return 1000;
    }

    private static bool IsSimpleHumanText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 60) return false;
        if (!text.Any(char.IsLetter)) return false;
        return text.IndexOfAny(['<', '>', '{', '}', '[', ']', '%', '\r', '\n']) < 0;
    }

    private static string PlainAt(RawLocresDocument doc, int index)
    {
        if (index < 0 || index >= doc.StringRecords.Count)
            throw new InvalidDataException($"Invalid strIdx {index}.");
        var record = doc.StringRecords[index];
        if (!record.DecryptSucceeded)
            throw new InvalidDataException($"Could not decrypt strIdx {index}: {record.DecryptError}");
        return record.PlainText;
    }

    private static SlotBuildResult BuildAndValidateSlot(
        string culture,
        SlotSource source,
        RawLocresDocument raw,
        SemanticSnapshot baseline,
        List<TargetSelection> selected,
        string artifactsDir)
    {
        var slotTargets = selected.Select(x => new SlotMutation
        {
            Namespace = x.Namespace,
            Key = x.Key,
            NewText = x.Marker
        }).ToList();

        var candidateBytes = BuildCandidate(raw, slotTargets);
        var candidatePath = Path.Combine(artifactsDir, $"runtime-slot-{culture}.locres");
        File.WriteAllBytes(candidatePath, candidateBytes);

        var candidateSemantic = ParseSemantic(candidateBytes, source.VirtualPath);
        var candidateRaw = ParseRaw(candidateBytes);
        var refValidation = ValidateRefCounts(candidateRaw);

        if (!candidateSemantic.Success)
        {
            return new SlotBuildResult
            {
                Culture = culture,
                VirtualPath = source.VirtualPath,
                ContainerName = source.ContainerName,
                ReadOrder = source.ReadOrder,
                OriginalSha256 = Sha256(source.Bytes),
                CandidateSha256 = Sha256(candidateBytes),
                CandidatePath = candidatePath,
                BaselineEntryCount = baseline.Entries.Count,
                CandidateEntryCount = 0,
                CandidateSemanticParseSucceeded = false,
                CandidateSemanticError = candidateSemantic.ErrorMessage,
                RefCountMismatchCount = refValidation.RefCountMismatchCount,
                InvalidStringIndexCount = refValidation.InvalidStringIndexCount,
                Success = false
            };
        }

        var expected = slotTargets.ToDictionary(x => (x.Namespace, x.Key), x => x.NewText);
        var missing = baseline.Entries.Keys.Except(candidateSemantic.Entries.Keys).ToList();
        var extra = candidateSemantic.Entries.Keys.Except(baseline.Entries.Keys).ToList();
        var shared = baseline.Entries.Keys.Intersect(candidateSemantic.Entries.Keys).ToList();
        var mismatches = shared.Where(k => baseline.Entries[k].LocalizedString != candidateSemantic.Entries[k].LocalizedString).ToList();
        var unexpected = mismatches.Where(k => !expected.ContainsKey(k)).ToList();
        var selectedWrong = expected.Count(x => !candidateSemantic.Entries.TryGetValue(x.Key, out var value) || value.LocalizedString != x.Value);
        var hashMismatches = shared.Count(k =>
            baseline.Entries[k].NamespaceHash != candidateSemantic.Entries[k].NamespaceHash ||
            baseline.Entries[k].KeyHash != candidateSemantic.Entries[k].KeyHash);

        var result = new SlotBuildResult
        {
            Culture = culture,
            VirtualPath = source.VirtualPath,
            ContainerName = source.ContainerName,
            ReadOrder = source.ReadOrder,
            OriginalSha256 = Sha256(source.Bytes),
            CandidateSha256 = Sha256(candidateBytes),
            OriginalSize = source.Bytes.LongLength,
            CandidateSize = candidateBytes.LongLength,
            BaselineEntryCount = baseline.Entries.Count,
            CandidateEntryCount = candidateSemantic.Entries.Count,
            OriginalStringRecordCount = raw.StringRecords.Count,
            CandidateStringRecordCount = candidateRaw.StringRecords.Count,
            ExpectedChangedIdentityCount = expected.Count,
            ActualChangedIdentityCount = mismatches.Count,
            MissingIdentityCount = missing.Count,
            ExtraIdentityCount = extra.Count,
            UnexpectedChangedIdentityCount = unexpected.Count,
            SelectedIdentityWrongValueCount = selectedWrong,
            NamespaceOrKeyHashMismatchCount = hashMismatches,
            RefCountMismatchCount = refValidation.RefCountMismatchCount,
            InvalidStringIndexCount = refValidation.InvalidStringIndexCount,
            CandidateSemanticParseSucceeded = true,
            CandidatePath = candidatePath
        };
        result.Success = result.CandidateSemanticParseSucceeded &&
                         result.MissingIdentityCount == 0 &&
                         result.ExtraIdentityCount == 0 &&
                         result.UnexpectedChangedIdentityCount == 0 &&
                         result.SelectedIdentityWrongValueCount == 0 &&
                         result.NamespaceOrKeyHashMismatchCount == 0 &&
                         result.RefCountMismatchCount == 0 &&
                         result.InvalidStringIndexCount == 0 &&
                         result.ActualChangedIdentityCount == result.ExpectedChangedIdentityCount;
        return result;
    }

    private static byte[] BuildCandidate(RawLocresDocument source, List<SlotMutation> mutations)
    {
        var prefix = source.Prefix.ToArray();
        var keyMap = source.KeyEntries.ToDictionary(x => (x.Namespace, x.Key));
        var refsByIndex = source.KeyEntries.GroupBy(x => x.StringIndex).ToDictionary(g => g.Key, g => g.ToList());
        var adjustedRefCounts = source.StringRecords.Select(x => x.RefCount).ToArray();
        var plainOverrides = new Dictionary<int, string>();
        var appended = new List<(string Plain, int RefCount)>();

        var resolved = mutations.Select(m =>
        {
            if (!keyMap.TryGetValue((m.Namespace, m.Key), out var key))
                throw new InvalidOperationException($"Target identity missing from slot: {m.Namespace}::{m.Key}");
            return (Mutation: m, Key: key);
        }).ToList();

        foreach (var group in resolved.GroupBy(x => x.Key.StringIndex))
        {
            var originalIndex = group.Key;
            if (!refsByIndex.TryGetValue(originalIndex, out var allRefs))
                throw new InvalidDataException($"No references found for strIdx {originalIndex}.");

            var selected = group.ToList();
            if (selected.Count > allRefs.Count)
                throw new InvalidDataException($"Selected references exceed total references for strIdx {originalIndex}.");

            if (selected.Count == allRefs.Count)
            {
                var keep = selected[0];
                plainOverrides[originalIndex] = keep.Mutation.NewText;
                adjustedRefCounts[originalIndex] = 1;
                keep.Mutation.NewStringIndex = originalIndex;

                foreach (var item in selected.Skip(1))
                    SplitToNewRecord(prefix, source, item.Key, item.Mutation, appended);
            }
            else
            {
                adjustedRefCounts[originalIndex] -= selected.Count;
                if (adjustedRefCounts[originalIndex] <= 0)
                    throw new InvalidDataException($"Unexpected RefCount after split for strIdx {originalIndex}.");

                foreach (var item in selected)
                    SplitToNewRecord(prefix, source, item.Key, item.Mutation, appended);
            }
        }

        using var ms = new MemoryStream(source.FileSize + 64 * 1024);
        ms.Write(prefix);
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write((uint)(source.StringRecords.Count + appended.Count));

        foreach (var record in source.StringRecords)
        {
            var encoded = plainOverrides.TryGetValue(record.Index, out var replacement)
                ? EncryptNteStringPkcs7(replacement)
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

    private static void SplitToNewRecord(
        byte[] prefix,
        RawLocresDocument source,
        KeyEntry key,
        SlotMutation mutation,
        List<(string Plain, int RefCount)> appended)
    {
        var newIndex = source.StringRecords.Count + appended.Count;
        appended.Add((mutation.NewText, 1));
        BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(checked((int)key.StringIndexOffset), 4), newIndex);
        mutation.NewStringIndex = newIndex;
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
        for (var i = 0; i < counts.Length; i++)
        {
            if (counts[i] != doc.StringRecords[i].RefCount)
                mismatches++;
        }
        return new RefValidation(mismatches, invalid);
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
            throw new InvalidDataException($"Unexpected NTE LOCRES header: ue={ueVersion} nte={nteVersion} encrypted={encrypted}.");

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

        if (keys.Count != totalEntries)
            throw new InvalidDataException($"Key count mismatch: header={totalEntries}, parsed={keys.Count}.");
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
            records.Add(new StringRecord(
                checked((int)i), value.Storage, value.Value, refCount,
                decrypted.PlainText, decrypted.Success, decrypted.Error));
        }

        if (ms.Position != ms.Length)
            throw new InvalidDataException($"String table ended at {ms.Position}; file length={ms.Length}.");

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
            {
                foreach (var (textKey, entry) in values)
                {
                    entries[(nsKey.Str, textKey.Str)] = new SemanticEntry(
                        nsKey.StrHash, textKey.StrHash, entry.LocalizedString ?? string.Empty);
                }
            }
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
        aes.KeySize = 256;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        var cipher = aes.CreateEncryptor(NteLocresKey, null).TransformFinalBlock(input, 0, input.Length);
        return Convert.ToBase64String(cipher).Replace('+', '-').Replace('/', '_');
    }

    private static DecryptResult TryDecrypt(string encoded)
    {
        try
        {
            var cipher = Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/'));
            using var aes = Aes.Create();
            aes.KeySize = 256;
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
        var countWide = wide.Length >= 2 && wide[^1] == 0 && wide[^2] == 0 ? wide.Length - 2 : wide.Length;
        return new FStringRead(Encoding.Unicode.GetString(wide, 0, countWide), FStringStorage.Wide);
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

    private static SourceDescription DescribeSource(GameFile file)
    {
        if (file is VfsEntry entry)
            return new SourceDescription(entry.Vfs.Name, entry.Vfs.ReadOrder);
        return new SourceDescription("<non-vfs>", 0);
    }

    private static string LoadAesKey(string? aesConfig, string? aesFile)
    {
        if (aesConfig is null && aesFile is null) return string.Empty;
        string raw;
        if (aesFile is not null)
        {
            raw = File.ReadAllText(aesFile).Trim();
        }
        else
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(aesConfig!));
            if (!doc.RootElement.TryGetProperty("aes_key", out var property))
                throw new InvalidDataException("AES config does not contain aes_key.");
            raw = property.GetString()?.Trim() ?? string.Empty;
        }

        if (raw.Length != 66 || !raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            raw[2..].Any(ch => !Uri.IsHexDigit(ch)))
            throw new InvalidDataException("AES key must be 0x followed by 64 hex digits.");
        return raw;
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
            throw new ArgumentException("Use only one of --aes-config or --aes-file.");
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');
    private static string Identity(string ns, string key) => ns + "::" + key;
    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string Compact(string value) => value.Length <= 42 ? value : value[..39] + "...";

    private static void WriteJson<T>(string path, T value)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }), new UTF8Encoding(false));
    }

    private static void WriteJsonl<T>(string path, IEnumerable<T> rows)
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        foreach (var row in rows)
            writer.WriteLine(JsonSerializer.Serialize(row, options));
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: NTE.RuntimeSlotABBuilder <gameRoot> <outputDir> [--aes-config=<json> | --aes-file=<txt>]");
    }
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

internal sealed class SlotSource
{
    public required string Culture { get; init; }
    public required string VirtualPath { get; init; }
    public required string ContainerName { get; init; }
    public long ReadOrder { get; init; }
    public required byte[] Bytes { get; init; }
}

internal sealed class TargetSelection
{
    public int Order { get; init; }
    public required string Namespace { get; init; }
    public required string Key { get; init; }
    public required string FrOriginal { get; init; }
    public required string EsOriginal { get; init; }
    public required string Marker { get; set; }
}

internal sealed class SlotMutation
{
    public required string Namespace { get; init; }
    public required string Key { get; init; }
    public required string NewText { get; init; }
    public int NewStringIndex { get; set; } = -1;
}

internal sealed class TargetRow
{
    public int Order { get; init; }
    public required string Namespace { get; init; }
    public required string Key { get; init; }
    public required string Identity { get; init; }
    public required string Marker { get; init; }
    public required string FrOriginal { get; init; }
    public required string EsOriginal { get; init; }
}

internal sealed class SlotBuildResult
{
    public required string Culture { get; init; }
    public required string VirtualPath { get; init; }
    public required string ContainerName { get; init; }
    public long ReadOrder { get; init; }
    public string? OriginalSha256 { get; init; }
    public string? CandidateSha256 { get; init; }
    public long OriginalSize { get; init; }
    public long CandidateSize { get; init; }
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
    public bool CandidateSemanticParseSucceeded { get; init; }
    public string? CandidateSemanticError { get; init; }
    public required string CandidatePath { get; init; }
    public bool Success { get; set; }
}

internal sealed class RunSummary
{
    public bool Success { get; init; }
    public int SelectedIdentityCount { get; init; }
    public required string TargetNamespace { get; init; }
    public required string RequiredIdentity { get; init; }
    public bool SameIdentitySetAcrossFrEs { get; init; }
    public required SlotBuildResult Fr { get; init; }
    public required SlotBuildResult Es { get; init; }
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
