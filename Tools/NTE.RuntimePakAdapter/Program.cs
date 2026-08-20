using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CUE4Parse.UE4.Pak;
using CUE4Parse.UE4.Versions;

namespace NTE.RuntimePakAdapter;

internal static class Program
{
    private const string ExpectedLocresSuffix = "Localization/Game/es/Game.locres";

    private static int Main(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("Usage: NTE.RuntimePakAdapter <standardPak> <ntePak> <reportJson>");
            return 2;
        }

        var standardPak = Path.GetFullPath(args[0]);
        var ntePak = Path.GetFullPath(args[1]);
        var reportJson = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(Path.GetDirectoryName(ntePak)!);
        Directory.CreateDirectory(Path.GetDirectoryName(reportJson)!);

        var report = new AdapterReport
        {
            StandardPak = standardPak,
            NtePak = ntePak,
            ReportJson = reportJson
        };

        try
        {
            if (!File.Exists(standardPak))
                throw new FileNotFoundException("Standard repak output not found.", standardPak);

            report.StandardSha256 = Sha256File(standardPak);
            report.StandardSize = new FileInfo(standardPak).Length;

            long standardIndexOffset;
            long standardIndexSize;
            string standardMountPoint;
            int standardFileCount;
            string[] standardFiles;

            using (var reader = new PakFileReader(standardPak, new VersionContainer(EGame.GAME_UE5_3)))
            {
                report.StandardPakVersion = (int)reader.Info.Version;
                report.StandardEncryptedIndex = reader.Info.EncryptedIndex;
                standardIndexOffset = reader.Info.IndexOffset;
                standardIndexSize = reader.Info.IndexSize;
                reader.Mount(StringComparer.OrdinalIgnoreCase);
                standardMountPoint = reader.MountPoint;
                standardFileCount = reader.FileCount;
                standardFiles = reader.Files.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            }

            report.StandardIndexOffset = standardIndexOffset;
            report.StandardIndexSize = standardIndexSize;
            report.StandardMountPoint = standardMountPoint;
            report.StandardFileCount = standardFileCount;
            report.StandardFiles = standardFiles;

            if (standardFileCount != 1 || !standardFiles.Any(IsExpectedLocres))
                throw new InvalidDataException("Standard pak does not contain exactly the expected ES Game.locres entry.");

            var bytes = File.ReadAllBytes(standardPak);
            Span<byte> needle = stackalloc byte[16];
            BinaryPrimitives.WriteInt64LittleEndian(needle[..8], standardIndexOffset);
            BinaryPrimitives.WriteInt64LittleEndian(needle[8..], standardIndexSize);

            var searchStart = Math.Max(0, bytes.Length - 2048);
            var matches = new List<int>();
            for (var i = searchStart; i <= bytes.Length - needle.Length; i++)
            {
                if (bytes.AsSpan(i, needle.Length).SequenceEqual(needle))
                    matches.Add(i);
            }

            report.FooterOffsetPairMatchCount = matches.Count;
            report.FooterOffsetPairMatches = matches.Select(x => (long)x).ToArray();
            if (matches.Count != 1)
                throw new InvalidDataException($"Expected exactly one footer IndexOffset+IndexSize pair in final 2048 bytes, found {matches.Count}.");

            var footerIndexOffsetPosition = matches[0];
            var storedNteIndexOffset = checked(standardIndexOffset + 1);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(footerIndexOffsetPosition, 8), storedNteIndexOffset);
            File.WriteAllBytes(ntePak, bytes);

            report.FooterIndexOffsetFieldPosition = footerIndexOffsetPosition;
            report.StandardStoredIndexOffset = standardIndexOffset;
            report.NteStoredIndexOffset = storedNteIndexOffset;
            report.NteSha256 = Sha256File(ntePak);
            report.NteSize = new FileInfo(ntePak).Length;
            report.DifferingByteCount = CountDifferences(File.ReadAllBytes(standardPak), bytes);

            using (var reader = new PakFileReader(ntePak, new VersionContainer(EGame.GAME_NevernessToEverness)))
            {
                report.NteReaderIndexOffsetAfterGameAdjustment = reader.Info.IndexOffset;
                report.NteReaderIndexSize = reader.Info.IndexSize;
                report.NtePakVersion = (int)reader.Info.Version;
                report.NteEncryptedIndex = reader.Info.EncryptedIndex;
                reader.Mount(StringComparer.OrdinalIgnoreCase);
                report.NteMountSucceeded = true;
                report.NteMountPoint = reader.MountPoint;
                report.NteFileCount = reader.FileCount;
                report.NteFiles = reader.Files.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();

                var locresEntry = reader.Files.Values.FirstOrDefault(x => IsExpectedLocres(x.Path));
                if (locresEntry is null)
                    throw new InvalidDataException("NTE-adapted pak mounted but expected ES Game.locres entry was not found.");

                var locresBytes = reader.Extract(locresEntry);
                report.ExtractedLocresSha256 = Convert.ToHexString(SHA256.HashData(locresBytes));
                report.ExtractedLocresSize = locresBytes.LongLength;
            }

            if (report.NteReaderIndexOffsetAfterGameAdjustment != standardIndexOffset)
                throw new InvalidDataException("NTE reader did not resolve the adapted footer back to the original real index offset.");
            if (report.NteFileCount != 1 || report.NteFiles is null || !report.NteFiles.Any(IsExpectedLocres))
                throw new InvalidDataException("NTE-adapted pak did not expose exactly the expected ES Game.locres entry.");
            if (report.DifferingByteCount <= 0 || report.DifferingByteCount > 8)
                throw new InvalidDataException($"Unexpected number of changed bytes while adapting footer: {report.DifferingByteCount}.");

            report.Success = true;
        }
        catch (Exception ex)
        {
            report.Success = false;
            report.ErrorType = ex.GetType().FullName ?? ex.GetType().Name;
            report.ErrorMessage = ex.Message;
            report.Error = ex.ToString();
        }

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        File.WriteAllText(reportJson, JsonSerializer.Serialize(report, jsonOptions), new UTF8Encoding(false));

        Console.WriteLine("NTE Runtime Pak Adapter");
        Console.WriteLine($"Standard PAK: {standardPak}");
        Console.WriteLine($"NTE PAK: {ntePak}");
        Console.WriteLine($"Standard IndexOffset: {report.StandardIndexOffset?.ToString() ?? "?"}");
        Console.WriteLine($"Stored NTE IndexOffset: {report.NteStoredIndexOffset?.ToString() ?? "?"}");
        Console.WriteLine($"NTE reader resolved IndexOffset: {report.NteReaderIndexOffsetAfterGameAdjustment?.ToString() ?? "?"}");
        Console.WriteLine($"Footer pair matches: {report.FooterOffsetPairMatchCount}");
        Console.WriteLine($"Differing bytes: {report.DifferingByteCount?.ToString() ?? "?"}");
        Console.WriteLine($"NTE mount succeeded: {report.NteMountSucceeded}");
        Console.WriteLine($"NTE files: {report.NteFileCount?.ToString() ?? "?"}");
        Console.WriteLine($"Extracted LOCRES SHA256: {report.ExtractedLocresSha256 ?? "<none>"}");
        Console.WriteLine($"Success: {report.Success}");
        if (!report.Success) Console.WriteLine($"Error: {report.ErrorType}: {report.ErrorMessage}");

        return report.Success ? 0 : 1;
    }

    private static bool IsExpectedLocres(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.EndsWith(ExpectedLocresSuffix, StringComparison.OrdinalIgnoreCase);
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static int CountDifferences(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return int.MaxValue;
        var count = 0;
        for (var i = 0; i < a.Length; i++)
            if (a[i] != b[i]) count++;
        return count;
    }
}

internal sealed class AdapterReport
{
    public required string StandardPak { get; init; }
    public required string NtePak { get; init; }
    public required string ReportJson { get; init; }
    public string? StandardSha256 { get; set; }
    public long? StandardSize { get; set; }
    public int? StandardPakVersion { get; set; }
    public bool? StandardEncryptedIndex { get; set; }
    public long? StandardIndexOffset { get; set; }
    public long? StandardIndexSize { get; set; }
    public string? StandardMountPoint { get; set; }
    public int? StandardFileCount { get; set; }
    public string[]? StandardFiles { get; set; }
    public int FooterOffsetPairMatchCount { get; set; }
    public long[]? FooterOffsetPairMatches { get; set; }
    public long? FooterIndexOffsetFieldPosition { get; set; }
    public long? StandardStoredIndexOffset { get; set; }
    public long? NteStoredIndexOffset { get; set; }
    public string? NteSha256 { get; set; }
    public long? NteSize { get; set; }
    public int? DifferingByteCount { get; set; }
    public long? NteReaderIndexOffsetAfterGameAdjustment { get; set; }
    public long? NteReaderIndexSize { get; set; }
    public int? NtePakVersion { get; set; }
    public bool? NteEncryptedIndex { get; set; }
    public bool NteMountSucceeded { get; set; }
    public string? NteMountPoint { get; set; }
    public int? NteFileCount { get; set; }
    public string[]? NteFiles { get; set; }
    public string? ExtractedLocresSha256 { get; set; }
    public long? ExtractedLocresSize { get; set; }
    public bool Success { get; set; }
    public string? ErrorType { get; set; }
    public string? ErrorMessage { get; set; }
    public string? Error { get; set; }
}
