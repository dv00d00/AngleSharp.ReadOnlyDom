param(
    [int] $Seconds = 10,
    [int] $Rounds = 5,
    [int] $Warmup = 500,
    [int] $ChunkSize = 4096,
    [ValidateSet("passthrough", "match", "extract", "rewrite", "rewrite-text")]
    [string] $Workload = "extract",
    [switch] $NativeAot,
    # NativeAOT ISA baseline. The ILC default is x86-64-v2, which compiles out the AVX2
    # SearchValues/IndexOf paths the tokenizer lives on (worth ~+12% on Zen 3); "native"
    # targets the build machine, "x86-64-v3" is the shippable equivalent on AVX2 hardware.
    [string] $IlcInstructionSet = "native",
    # Directory of .mibc files for NativeAOT static PGO (see scripts/collect-pgo.ps1).
    # Worth ~+30-43% on top of the ISA baseline: guarded devirtualization and block
    # layout are what the JIT's dynamic PGO otherwise holds over AOT.
    [string] $MibcPath
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$rustManifest = Join-Path $root "benchmarks/ProductComparison/lol-html-server/Cargo.toml"
$angleProject = Join-Path $root "benchmarks/ProductComparison/AngleSharp.NativeConsole/AngleSharp.NativeConsole.csproj"
$corpus = Join-Path $root "dom/tests/AngleSharp.ReadOnlyDom.Tests/TestData/corpus/qq.html"
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$reportPath = Join-Path $root "artifacts/benchmarks/$timestamp-native-console/report.md"

$architecture = (uname -m).Trim()
if ($IsMacOS) {
    $rid = if ($architecture -eq "arm64") { "osx-arm64" } else { "osx-x64" }
}
elseif ($IsLinux) {
    $rid = if ($architecture -eq "aarch64") { "linux-arm64" } else { "linux-x64" }
}
else {
    $rid = "win-x64"
}

$extension = if ($IsWindows) { ".exe" } else { "" }
$lolExecutable = Join-Path (Split-Path $rustManifest) "target/release/lol-html-console$extension"

cargo build --release --locked --manifest-path $rustManifest --bin lol-html-console
if ($LASTEXITCODE -ne 0) { throw "lol-html console build failed." }

if ($NativeAot) {
    $publishArgs = @(
        "-p:PublishAot=true", "-p:OptimizationPreference=Speed", "-p:NuGetAudit=false",
        "-p:IlcInstructionSet=$IlcInstructionSet"
    )
    if ($MibcPath) {
        if (-not (Test-Path $MibcPath)) { throw "Missing mibc directory: $MibcPath" }
        $publishArgs += @("-p:IlcPgoOptimize=true", "-p:IlcMibcPath=$MibcPath/")
    }
    dotnet publish $angleProject -c Release -r $rid --self-contained true @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "AngleSharp NativeAOT console publish failed." }
    $angleExecutable = Join-Path (Split-Path $angleProject) "bin/Release/net10.0/$rid/publish/AngleSharp.NativeConsole$extension"
    $anglePrefix = @()
    $angleService = "AngleSharp NativeAOT-speed"
}
else {
    dotnet build $angleProject -c Release --no-restore -m:1 --disable-build-servers `
        -p:UseSharedCompilation=false -p:PublishAot=false -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw "AngleSharp Release console build failed." }
    $angleExecutable = "dotnet"
    $anglePrefix = @(Join-Path (Split-Path $angleProject) "bin/Release/net10.0/AngleSharp.NativeConsole.dll")
    $angleService = "AngleSharp .NET JIT"
}

$results = [System.Collections.Generic.List[object]]::new()

function Invoke-Lane([string] $Executable, [string[]] $Prefix, [string] $Service, [int] $Copies, [int] $Round) {
    $arguments = @(
        "--input", $corpus,
        "--seconds", $Seconds,
        "--warmup", $Warmup,
        "--copies", $Copies,
        "--chunk-size", $ChunkSize,
        "--workload", $Workload
    )
    $line = (& $Executable @Prefix @arguments | Where-Object { $_ -like "RESULT *" } | Select-Object -Last 1)
    if ($LASTEXITCODE -ne 0 -or -not $line) { throw "$Service console benchmark failed." }
    Write-Host "round=$Round $line"

    $values = @{}
    foreach ($token in $line.Split(' ')) {
        $pair = $token.Split('=', 2)
        if ($pair.Length -eq 2) { $values[$pair[0]] = $pair[1] }
    }
    $elapsed = [double]::Parse($values.elapsed_ms, [Globalization.CultureInfo]::InvariantCulture)
    $requests = [long]::Parse($values.requests, [Globalization.CultureInfo]::InvariantCulture)
    $results.Add([pscustomobject]@{
        Corpus = if ($Copies -eq 1) { "qq" } else { "qq-x4" }
        Service = $Service
        Round = $Round
        Rate = $requests / ($elapsed / 1000.0)
        Checksum = $values.value_checksum
        Urls = [int]$values.urls
    })
}

foreach ($copies in 1, 4) {
    foreach ($round in 1..$Rounds) {
        if ($round % 2 -eq 1) {
            Invoke-Lane $angleExecutable $anglePrefix $angleService $copies $round
            Invoke-Lane $lolExecutable @() "lol-html Rust" $copies $round
        }
        else {
            Invoke-Lane $lolExecutable @() "lol-html Rust" $copies $round
            Invoke-Lane $angleExecutable $anglePrefix $angleService $copies $round
        }
    }
}

foreach ($corpusName in "qq", "qq-x4") {
    $checksums = @($results | Where-Object Corpus -eq $corpusName | Select-Object -ExpandProperty Checksum -Unique)
    $urlCounts = @($results | Where-Object Corpus -eq $corpusName | Select-Object -ExpandProperty Urls -Unique)
    if ($checksums.Count -ne 1 -or $urlCounts.Count -ne 1) {
        throw "Correctness mismatch for $corpusName."
    }
}

function Get-Median([double[]] $Values) {
    $ordered = @($Values | Sort-Object)
    $middle = [int][Math]::Floor($ordered.Count / 2)
    if ($ordered.Count % 2 -eq 1) { return $ordered[$middle] }
    return ($ordered[$middle - 1] + $ordered[$middle]) / 2.0
}

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add("# Native console comparison")
$lines.Add("")
$lines.Add("- Input: in-memory UTF-8 supplied in $ChunkSize-byte streaming chunks")
$lines.Add("- Workload: $Workload")
$lines.Add("- Measurement: $Rounds alternating rounds x $Seconds seconds")
$lines.Add("- Binaries: Rust release and $(if ($NativeAot) { '.NET NativeAOT with OptimizationPreference=Speed' } else { '.NET Release JIT' })")
$lines.Add("")
$lines.Add("| Corpus | Service | Median docs/s | Min docs/s | Max docs/s |")
$lines.Add("| --- | --- | ---: | ---: | ---: |")
foreach ($group in $results | Group-Object Corpus, Service) {
    $rates = @($group.Group | Select-Object -ExpandProperty Rate)
    $median = Get-Median $rates
    $minimum = ($rates | Measure-Object -Minimum).Minimum
    $maximum = ($rates | Measure-Object -Maximum).Maximum
    $culture = [Globalization.CultureInfo]::InvariantCulture
    $lines.Add("| $($group.Group[0].Corpus) | $($group.Group[0].Service) | $($median.ToString('N1', $culture)) | $($minimum.ToString('N1', $culture)) | $($maximum.ToString('N1', $culture)) |")
}

New-Item -ItemType Directory -Force (Split-Path $reportPath) | Out-Null
$lines | Set-Content -Encoding utf8 $reportPath
$lines | ForEach-Object { Write-Host $_ }
Write-Host "Report: $reportPath"
