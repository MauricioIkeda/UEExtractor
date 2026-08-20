using CUE4Parse.UE4.Pak;
using CUE4Parse.UE4.Versions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NTE.RuntimeContainerProbe;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: NTE.RuntimeContainerProbe <pakPath> <outputDir>");
            return 2;
        }

        var pakPath = Path.GetFullPath(args[0]);
        var outputDir = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(outputDir);

        var result = new ProbeResult
        {
            PakPath = pakPath,
            Exists = File.Exists(pakPath)
        };

        try
        {
            if (!result.Exists)
                throw new FileNotFoundException("Runtime override pak not found.", pakPath);

            var bytes = File.ReadAllBytes(pakPath);
            result.Size = bytes.LongLength;
            result.Sha256 = Convert.ToHexString(SHA256.HashData(bytes));

            using var reader = new PakFileReader(pakPath, new VersionContainer(EGame.GAME_NevernessToEverness));
            result.ReaderCreated = true;
            result.PakVersion = (int)reader.Info.Version;
            result.PakVersionName = reader.Info.Version.ToString();
            result.EncryptedIndex = reader.Info.EncryptedIndex;
            result.HasDirectoryIndex = reader.HasDirectoryIndex;

            try
            {
                reader.Mount(StringComparer.OrdinalIgnoreCase);
                result.MountSucceeded = true;
                result.MountPoint = reader.MountPoint;
                result.FileCount = reader.FileCount;
                result.Files = reader.Files.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(50).ToArray();
            }
            catch (Exception ex)
            {
                result.MountSucceeded = false;
                result.MountErrorType = ex.GetType().FullName ?? ex.GetType().Name;
                result.MountErrorMessage = ex.Message;
                result.MountError = ex.ToString();
            }
        }
        catch (Exception ex)
        {
            result.FatalErrorType = ex.GetType().FullName ?? ex.GetType().Name;
            result.FatalErrorMessage = ex.Message;
            result.FatalError = ex.ToString();
        }

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        File.WriteAllText(Path.Combine(outputDir, "container-probe.json"), JsonSerializer.Serialize(result, options), new UTF8Encoding(false));

        Console.WriteLine("NTE Runtime Container Probe");
        Console.WriteLine($"PAK: {pakPath}");
        Console.WriteLine($"Exists: {result.Exists}");
        Console.WriteLine($"SHA256: {result.Sha256 ?? "<none>"}");
        Console.WriteLine($"Reader created: {result.ReaderCreated}");
        Console.WriteLine($"Pak version: {result.PakVersionName ?? "<unknown>"} ({result.PakVersion?.ToString() ?? "?"})");
        Console.WriteLine($"Encrypted index: {result.EncryptedIndex?.ToString() ?? "?"}");
        Console.WriteLine($"Has directory index: {result.HasDirectoryIndex?.ToString() ?? "?"}");
        Console.WriteLine($"Mount succeeded: {result.MountSucceeded}");
        if (result.MountSucceeded)
        {
            Console.WriteLine($"Mount point: {result.MountPoint}");
            Console.WriteLine($"Files: {result.FileCount}");
            foreach (var file in result.Files ?? Array.Empty<string>()) Console.WriteLine($"- {file}");
        }
        else
        {
            Console.WriteLine($"Mount error: {result.MountErrorType}: {result.MountErrorMessage}");
        }
        if (result.FatalError is not null)
            Console.WriteLine($"Fatal error: {result.FatalErrorType}: {result.FatalErrorMessage}");

        return result.ReaderCreated ? 0 : 1;
    }
}

internal sealed class ProbeResult
{
    public required string PakPath { get; init; }
    public bool Exists { get; set; }
    public long? Size { get; set; }
    public string? Sha256 { get; set; }
    public bool ReaderCreated { get; set; }
    public int? PakVersion { get; set; }
    public string? PakVersionName { get; set; }
    public bool? EncryptedIndex { get; set; }
    public bool? HasDirectoryIndex { get; set; }
    public bool MountSucceeded { get; set; }
    public string? MountPoint { get; set; }
    public int? FileCount { get; set; }
    public string[]? Files { get; set; }
    public string? MountErrorType { get; set; }
    public string? MountErrorMessage { get; set; }
    public string? MountError { get; set; }
    public string? FatalErrorType { get; set; }
    public string? FatalErrorMessage { get; set; }
    public string? FatalError { get; set; }
}
