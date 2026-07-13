param(
    [ValidateSet("micro", "small", "full", "retained", "compact", "query", "plan", "streaming", "all")]
    [string] $Tier = "all"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$commit = (git -C $root rev-parse --short HEAD).Trim()
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$output = Join-Path $root "artifacts/benchmarks/$timestamp-$commit-$Tier"
$project = Join-Path $root "AngleSharp.ReadOnlyDom.Benchmarks/AngleSharp.ReadOnlyDom.Benchmarks.csproj"
New-Item -ItemType Directory -Force -Path $output | Out-Null

$metadata = @(
    "# Benchmark run"
    ""
    "- Commit: ``$commit``"
    "- Timestamp: ``$(Get-Date -Format o)``"
    "- Tier: ``$Tier``"
    "- Runtime: ``$(dotnet --version)``"
    "- GC: Server GC (enforced by the benchmark executable and BenchmarkDotNet job)"
    "- Job: BenchmarkDotNet ShortRun, in-process emit"
    "- Corpus: checked-in snapshots under AngleSharp.ReadOnlyDom.Tests/temp"
    "- Note: ShortRun time results have wide confidence intervals; allocation results are the primary micro gate."
)
$metadata | Set-Content (Join-Path $output "run.md")

dotnet build $project -c Release -f net10.0
if ($LASTEXITCODE -ne 0) { throw "Benchmark build failed." }

function Invoke-Benchmark([string] $filter, [string] $name, [string] $corpusTier = "") {
    if ($corpusTier) { $env:AS_BENCH_CORPUS_TIER = $corpusTier }
    try {
        $artifacts = Join-Path $output $name
        dotnet run --project $project -c Release -f net10.0 --no-build -- `
            --filter $filter --join --artifacts $artifacts
        if ($LASTEXITCODE -ne 0) { throw "$name benchmark failed." }
    }
    finally {
        Remove-Item Env:AS_BENCH_CORPUS_TIER -ErrorAction SilentlyContinue
    }
}

if ($Tier -in @("micro", "all")) {
    Invoke-Benchmark "*OverheadBenchmark*" "micro"
}
if ($Tier -in @("compact", "all")) {
    Invoke-Benchmark "*CompactBuildBenchmark*" "compact"
}
if ($Tier -in @("plan", "all")) {
    Invoke-Benchmark "*CompactExtractionPlanBenchmark*" "plan"
}
if ($Tier -in @("streaming", "all")) {
    Invoke-Benchmark "*CompactStreamingExtractionBenchmark*" "streaming"
}
if ($Tier -in @("small", "all")) {
    Invoke-Benchmark "*CorpusBenchmark*" "corpus-small" "small"
}
if ($Tier -eq "full" -or $Tier -eq "all") {
    Invoke-Benchmark "*CorpusBenchmark*" "corpus-full" "full"
}
if ($Tier -in @("retained", "all")) {
    dotnet run --project $project -c Release -f net10.0 --no-build -- `
        --retained --tier small --repetitions 3 --output (Join-Path $output "retained-small.md")
    if ($LASTEXITCODE -ne 0) { throw "Retained-memory benchmark failed." }
}
if ($Tier -in @("query", "all")) {
    dotnet run --project $project -c Release -f net10.0 --no-build -- `
        --query-workloads --iterations 30 --output (Join-Path $output "query-workloads.md")
    if ($LASTEXITCODE -ne 0) { throw "Query workload measurement failed." }
}
if ($Tier -eq "full" -or $Tier -eq "all") {
    dotnet run --project $project -c Release -f net10.0 --no-build -- `
        --retained --tier full --repetitions 3 --output (Join-Path $output "retained-full.md")
    if ($LASTEXITCODE -ne 0) { throw "Full retained-memory benchmark failed." }
}

Write-Host "Benchmark artifacts: $output"
