using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.VirtualFileSystem;
using Solicen.Localization.UE4;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NTE.SourcePrecedenceProbe;

internal static class Program
{
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

            var aesKey = LoadAesKey(aesConfig, aesFile);

            Console.WriteLine("NTE Source Precedence Probe");
            Console.WriteLine("Purpose: prove which physical container copy wins for duplicate localization virtual paths");
            Console.WriteLine();

            using var aesScope = TemporaryAesCompatibility.Install(gameRoot, aesKey);
            using var reader = CreateReaderWithoutLeakingAes(gameRoot);
            var provider = GetProvider(reader);

            var mountedRows = provider.MountedVfs
                .OrderByDescending(x => x.ReadOrder)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => new MountedContainerRow
                {
                    Name = x.Name,
                    Path = x.Path,
                    ReadOrder = x.ReadOrder,
                    FileCount = x.FileCount,
                    MountPoint = x.MountPoint,
                    IsEncrypted = x.IsEncrypted
                })
                .ToList();

            WriteJsonl(Path.Combine(outputDir, "mounted-containers.jsonl"), mountedRows);

            var locresPaths = provider.Files.Keys
                .Where(x => x.EndsWith("/game.locres", StringComparison.OrdinalIgnoreCase) ||
                            x.EndsWith("\\game.locres", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Console.WriteLine($"Mounted containers: {mountedRows.Count}");
            Console.WriteLine($"Distinct game.locres virtual paths: {locresPaths.Count}");

            var candidateRows = new List<LocresCandidateRow>();
            var effectiveRows = new List<EffectiveLocresRow>();
            var errors = new List<ProbeError>();

            foreach (var virtualPath in locresPaths)
            {
                var culture = CultureFromVirtualPath(virtualPath);

                if (!provider.Files.TryGetValues(virtualPath, out var candidates) || candidates.Count == 0)
                    continue;

                provider.Files.TryGetValue(virtualPath, out var effectiveFile);

                for (var rank = 0; rank < candidates.Count; rank++)
                {
                    var file = candidates[rank];
                    try
                    {
                        var source = DescribeSource(file);
                        var bytes = file.Read();
                        var hash = Convert.ToHexString(SHA256.HashData(bytes));

                        candidateRows.Add(new LocresCandidateRow
                        {
                            VirtualPath = virtualPath,
                            Culture = culture,
                            CandidateRank = rank,
                            IsEffective = ReferenceEquals(file, effectiveFile),
                            ContainerName = source.ContainerName,
                            ContainerPath = source.ContainerPath,
                            ReadOrder = source.ReadOrder,
                            FileSize = bytes.LongLength,
                            Sha256 = hash,
                            GameFileType = file.GetType().FullName ?? file.GetType().Name
                        });
                    }
                    catch (Exception ex)
                    {
                        errors.Add(ProbeError.From(virtualPath, $"ReadCandidate[{rank}]", ex));
                    }
                }

                if (effectiveFile is not null)
                {
                    try
                    {
                        var source = DescribeSource(effectiveFile);
                        var bytes = effectiveFile.Read();
                        var hash = Convert.ToHexString(SHA256.HashData(bytes));
                        effectiveRows.Add(new EffectiveLocresRow
                        {
                            VirtualPath = virtualPath,
                            Culture = culture,
                            CandidateCount = candidates.Count,
                            ContainerName = source.ContainerName,
                            ContainerPath = source.ContainerPath,
                            ReadOrder = source.ReadOrder,
                            FileSize = bytes.LongLength,
                            Sha256 = hash
                        });
                    }
                    catch (Exception ex)
                    {
                        errors.Add(ProbeError.From(virtualPath, "ReadEffective", ex));
                    }
                }
            }

            WriteJsonl(Path.Combine(outputDir, "locres-candidates.jsonl"), candidateRows);
            WriteJsonl(Path.Combine(outputDir, "effective-locres.jsonl"), effectiveRows);
            WriteJsonl(Path.Combine(outputDir, "errors.jsonl"), errors);

            var duplicatePaths = effectiveRows.Where(x => x.CandidateCount > 1).ToList();
            var en = effectiveRows.FirstOrDefault(x => x.Culture.Equals("en", StringComparison.OrdinalIgnoreCase));
            var es = effectiveRows.FirstOrDefault(x => x.Culture.Equals("es", StringComparison.OrdinalIgnoreCase));

            var summary = new ProbeSummary
            {
                MountedContainerCount = mountedRows.Count,
                DistinctGameLocresPathCount = locresPaths.Count,
                DuplicateGameLocresPathCount = duplicatePaths.Count,
                CandidateRecordCount = candidateRows.Count,
                EffectiveRecordCount = effectiveRows.Count,
                ErrorCount = errors.Count,
                EffectiveEnglish = en,
                EffectiveSpanish = es
            };

            File.WriteAllText(
                Path.Combine(outputDir, "summary.json"),
                JsonSerializer.Serialize(summary, JsonWriteIndented),
                new UTF8Encoding(false));

            Console.WriteLine();
            Console.WriteLine($"Candidate records: {candidateRows.Count}");
            Console.WriteLine($"Duplicate game.locres paths: {duplicatePaths.Count}");
            if (en is not null)
                Console.WriteLine($"EN effective: {en.ContainerName} (readOrder {en.ReadOrder}) SHA256 {en.Sha256}");
            if (es is not null)
                Console.WriteLine($"ES effective: {es.ContainerName} (readOrder {es.ReadOrder}) SHA256 {es.Sha256}");
            Console.WriteLine($"Errors: {errors.Count}");
            Console.WriteLine($"Output: {outputDir}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex}");
            return 1;
        }
    }

    private static SourceDescription DescribeSource(GameFile file)
    {
        if (file is VfsEntry entry)
        {
            return new SourceDescription(
                entry.Vfs.Name,
                entry.Vfs.Path,
                entry.Vfs.ReadOrder);
        }

        return new SourceDescription("<non-vfs>", string.Empty, 0);
    }

    private static string CultureFromVirtualPath(string virtualPath)
    {
        var normalized = virtualPath.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 ? segments[^2] : string.Empty;
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
            writer.WriteLine(JsonSerializer.Serialize(value, JsonWriteOptions));
    }

    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private static readonly JsonSerializerOptions JsonWriteIndented = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  NTE.SourcePrecedenceProbe <gameRoot> <outputDir> [--aes-config=<path> | --aes-file=<path>]");
    }
}

internal sealed record SourceDescription(string ContainerName, string ContainerPath, long ReadOrder);

internal sealed class MountedContainerRow
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public long ReadOrder { get; init; }
    public int FileCount { get; init; }
    public required string MountPoint { get; init; }
    public bool IsEncrypted { get; init; }
}

internal sealed class LocresCandidateRow
{
    public required string VirtualPath { get; init; }
    public required string Culture { get; init; }
    public int CandidateRank { get; init; }
    public bool IsEffective { get; init; }
    public required string ContainerName { get; init; }
    public required string ContainerPath { get; init; }
    public long ReadOrder { get; init; }
    public long FileSize { get; init; }
    public required string Sha256 { get; init; }
    public required string GameFileType { get; init; }
}

internal sealed class EffectiveLocresRow
{
    public required string VirtualPath { get; init; }
    public required string Culture { get; init; }
    public int CandidateCount { get; init; }
    public required string ContainerName { get; init; }
    public required string ContainerPath { get; init; }
    public long ReadOrder { get; init; }
    public long FileSize { get; init; }
    public required string Sha256 { get; init; }
}

internal sealed class ProbeError
{
    public required string VirtualPath { get; init; }
    public required string Stage { get; init; }
    public required string ErrorType { get; init; }
    public required string ErrorMessage { get; init; }

    public static ProbeError From(string virtualPath, string stage, Exception ex) => new()
    {
        VirtualPath = virtualPath,
        Stage = stage,
        ErrorType = ex.GetType().FullName ?? ex.GetType().Name,
        ErrorMessage = ex.Message
    };
}

internal sealed class ProbeSummary
{
    public int MountedContainerCount { get; init; }
    public int DistinctGameLocresPathCount { get; init; }
    public int DuplicateGameLocresPathCount { get; init; }
    public int CandidateRecordCount { get; init; }
    public int EffectiveRecordCount { get; init; }
    public int ErrorCount { get; init; }
    public EffectiveLocresRow? EffectiveEnglish { get; init; }
    public EffectiveLocresRow? EffectiveSpanish { get; init; }
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
