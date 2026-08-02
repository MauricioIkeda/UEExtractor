[CmdletBinding()]
param(
    [string] $Configuration = "Release",
    [string] $OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot "dist"
}
$project = Join-Path $repositoryRoot "UEExtractor\UEExtractor.csproj"
$sourceCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
$sourceBranch = (& git -C $repositoryRoot branch --show-current).Trim()
$cueCommit = (& git -C (Join-Path $repositoryRoot "CUE4Parse") rev-parse HEAD).Trim()
$shortSource = $sourceCommit.Substring(0, 7)
$shortCue = $cueCommit.Substring(0, 7)
$packageName = "UEExtractor-NTE-1.0.8.4-$shortSource-$shortCue-win-x64.zip"
$buildDirectory = Join-Path $repositoryRoot "UEExtractor\bin\$Configuration\net10.0"
$stageDirectory = Join-Path ([IO.Path]::GetTempPath()) ("nte-ueextractor-" + [guid]::NewGuid().ToString("N"))

try {
    & dotnet build $project --configuration $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "A compilacao do UEExtractor falhou."
    }

    New-Item -ItemType Directory -Path $stageDirectory -Force | Out-Null
    $requiredFiles = @(
        "CUE4Parse-Natives.dll",
        "UEExtractor.deps.json",
        "UEExtractor.dll",
        "UEExtractor.dll.config",
        "UEExtractor.exe",
        "UEExtractor.runtimeconfig.json"
    )
    foreach ($name in $requiredFiles) {
        $source = Join-Path $buildDirectory $name
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Arquivo obrigatorio ausente no build: $source"
        }
        Copy-Item -LiteralPath $source -Destination (Join-Path $stageDirectory $name)
    }

    $runtimeSource = Join-Path $buildDirectory "runtimes"
    if (-not (Test-Path -LiteralPath $runtimeSource -PathType Container)) {
        throw "Diretorio de runtimes ausente: $runtimeSource"
    }
    Copy-Item -LiteralPath $runtimeSource -Destination $stageDirectory -Recurse
    Copy-Item -LiteralPath (Join-Path $repositoryRoot "LICENSE") `
        -Destination (Join-Path $stageDirectory "LICENSE-UEExtractor.txt")

    $packagedFiles = Get-ChildItem -LiteralPath $stageDirectory -File -Recurse |
        Sort-Object FullName |
        ForEach-Object {
            [ordered]@{
                path = $_.FullName.Substring($stageDirectory.Length + 1).Replace("\", "/")
                size = $_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
    $provenance = [ordered]@{
        schemaVersion = 1
        tool = "UEExtractor NTE"
        reportedVersion = "1.0.8.4"
        sourceRepository = "MauricioIkeda/UEExtractor"
        sourceBranch = $sourceBranch
        sourceCommit = $sourceCommit
        cue4ParseRepository = "MauricioIkeda/CUE4Parse"
        cue4ParseCommit = $cueCommit
        ueExtractorSha256 = ($packagedFiles | Where-Object path -eq "UEExtractor.exe").sha256
        ueExtractorDllSha256 = ($packagedFiles | Where-Object path -eq "UEExtractor.dll").sha256
        packagedAt = [DateTimeOffset]::UtcNow.ToString("o")
        files = @($packagedFiles)
    }
    $provenance | ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath (Join-Path $stageDirectory "NTE-BUILD-PROVENANCE.json") -Encoding utf8

    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $outputZip = Join-Path (Resolve-Path $OutputDirectory).Path $packageName
    Remove-Item -LiteralPath $outputZip -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $stageDirectory "*") -DestinationPath $outputZip -CompressionLevel Optimal

    [ordered]@{
        package = $outputZip
        packageSha256 = (Get-FileHash -LiteralPath $outputZip -Algorithm SHA256).Hash.ToLowerInvariant()
        sourceCommit = $sourceCommit
        cue4ParseCommit = $cueCommit
        ueExtractorSha256 = $provenance.ueExtractorSha256
        ueExtractorDllSha256 = $provenance.ueExtractorDllSha256
    } | ConvertTo-Json
}
finally {
    Remove-Item -LiteralPath $stageDirectory -Recurse -Force -ErrorAction SilentlyContinue
}
