<#
    Harvests static PGO data (.mibc) for NativeAOT publishes.

    Runs the JIT console under dotnet-trace with JIT instrumentation events and converts
    the traces with dotnet-pgo. DOTNET_ReadyToRun=0 forces the framework out of its R2R
    images so BCL hot paths (SearchValues, IndexOfAny, UTF-8 transcode) are instrumented
    too - that is where most of the win lives.

    Feed the output to a publish via bench-native-console.ps1 -NativeAot -MibcPath <dir>,
    or directly: -p:IlcPgoOptimize=true -p:IlcMibcPath=<dir>/ (the trailing slash matters;
    ILC globs $(IlcMibcPath)*.mibc into --mibc: arguments).

    Measured on yahoo (Zen 3, interleaved A/B): ISA native alone +12%, +MIBC +37.6%
    rewrite-stream / +40.2% match on top; generalizes to unseen corpora (google +30.8%,
    spiegel +43.4%). Together they close the AOT-vs-JIT gap from about -34% to -9%.

    Tools:
      dotnet tool install -g dotnet-trace
      dotnet-pgo is not on nuget.org; it lives on the dnceng transport feed. Source
      mapping blocks --add-source, so install via an isolated nuget.config:
        <packageSources><clear/><add key="t" value="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet10-transport/nuget/v3/index.json"/></packageSources>
        dotnet tool install --tool-path <dir> dotnet-pgo --version 10.0.10-servicing.26361.102
      (pick the version matching the runtime; list them at
       https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet10-transport/nuget/v3/flat2/dotnet-pgo/index.json)
#>
param(
    [String[]] $Corpora = @("yahoo.html", "google.html"),
    [String[]] $Workloads = @("rewrite-stream", "match"),
    [ValidateSet("qq", "generic")]
    [string] $Query = "generic",
    [double] $Seconds = 8,
    [int] $Warmup = 200,
    [string] $OutputDir,
    # Path to dotnet-pgo.exe if it is not on PATH.
    [string] $DotnetPgo = "dotnet-pgo",
    [switch] $SkipBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$angleProject = Join-Path $root "benchmarks/ProductComparison/AngleSharp.NativeConsole/AngleSharp.NativeConsole.csproj"
$consoleDll = Join-Path (Split-Path $angleProject) "bin/Release/net10.0/AngleSharp.NativeConsole.dll"
if (-not $OutputDir) { $OutputDir = Join-Path $root "artifacts/pgo/mibc" }
$traceDir = Join-Path (Split-Path $OutputDir) "traces"
New-Item -ItemType Directory -Force $OutputDir | Out-Null
New-Item -ItemType Directory -Force $traceDir | Out-Null

if (-not $SkipBuild) {
    dotnet build $angleProject -c Release --no-restore -m:1 --disable-build-servers `
        -p:UseSharedCompilation=false -p:PublishAot=false -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw "AngleSharp console build failed." }
}

$env:DOTNET_TieredPGO = "1"
$env:DOTNET_ReadyToRun = "0"
$env:DOTNET_TC_QuickJitForLoops = "1"
$env:DOTNET_JitCollect64BitCounts = "1"
try {
    foreach ($corpus in $Corpora) {
        $corpusPath = Join-Path $root "dom/tests/AngleSharp.ReadOnlyDom.Tests/temp/$corpus"
        if (-not (Test-Path $corpusPath)) { throw "Missing corpus: $corpusPath" }
        foreach ($workload in $Workloads) {
            $stem = "$($corpus -replace '\.html$', '')-$workload"
            $trace = Join-Path $traceDir "$stem.nettrace"
            $mode = if ($workload -eq "match") { "push" } else { "stream" }
            Write-Host "collecting $stem ..."
            dotnet-trace collect --providers Microsoft-Windows-DotNETRuntime:0x1F000080018:5 `
                --output $trace -- `
                dotnet $consoleDll --input $corpusPath --seconds $Seconds --warmup $Warmup `
                --workload $workload --mode $mode --query $Query
            if ($LASTEXITCODE -ne 0) { throw "dotnet-trace failed for $stem" }
            & $DotnetPgo create-mibc --trace $trace --output (Join-Path $OutputDir "$stem.mibc")
            if ($LASTEXITCODE -ne 0) { throw "dotnet-pgo failed for $stem" }
        }
    }
}
finally {
    Remove-Item Env:DOTNET_TieredPGO, Env:DOTNET_ReadyToRun, Env:DOTNET_TC_QuickJitForLoops, Env:DOTNET_JitCollect64BitCounts -ErrorAction SilentlyContinue
}

Write-Host "mibc files in $OutputDir"
