param(
    [ValidateSet("small", "full", "retained", "compact", "compact-stages", "query", "extraction", "scraping", "utf8", "utf8-baseline", "rows", "long-streaming", "utf8-tokenizer", "utf8-rodom", "utf8-dom", "all")]
    [string] $Tier = "all",
    [switch] $HardwareCounters
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$commit = (git -C $root rev-parse --short HEAD).Trim()
$workingTree = if (git -C $root status --porcelain) { "dirty (see git-status.txt)" } else { "clean" }
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$output = Join-Path $root "artifacts/benchmarks/$timestamp-$commit-$Tier"
$project = Join-Path $root "benchmarks/AngleSharp.ReadOnlyDom.Benchmarks/AngleSharp.ReadOnlyDom.Benchmarks.csproj"
$hardwareCounterNote = if ($HardwareCounters) { "TotalCycles requested; availability is host-dependent" } else { "disabled" }
New-Item -ItemType Directory -Force -Path $output | Out-Null

$metadata = @(
    "# Benchmark run"
    ""
    "- Commit: ``$commit``"
    "- Working tree: $workingTree"
    "- Timestamp: ``$(Get-Date -Format o)``"
    "- Tier: ``$Tier``"
    "- Runtime: ``$(dotnet --version)``"
    "- GC: Server GC (enforced by the benchmark executable and BenchmarkDotNet job)"
    "- Job: BenchmarkDotNet Default, LaunchCount=1, out-of-process (no in-process emit toolchain)"
    "- Corpus: checked-in snapshots under tests/AngleSharp.ReadOnlyDom.Tests/temp"
    "- Hardware counters: $hardwareCounterNote"
    "- Note: a single launch cannot separate per-process variance from a real effect; allocation results remain the primary micro gate."
)
$metadata | Set-Content (Join-Path $output "run.md")
git -C $root status --short | Set-Content (Join-Path $output "git-status.txt")

dotnet build $project -c Release -f net10.0
if ($LASTEXITCODE -ne 0) { throw "Benchmark build failed." }

function Invoke-Benchmark([string] $filter, [string] $name, [string] $corpusTier = "") {
    if ($corpusTier) { $env:AS_BENCH_CORPUS_TIER = $corpusTier }
    try {
        $artifacts = Join-Path $output $name
        dotnet run --project $project -c Release -f net10.0 --no-build -- `
            --filter $filter --join --artifacts $artifacts
        if ($LASTEXITCODE -ne 0) { throw "$name benchmark failed." }
        $reports = Get-ChildItem -Path $artifacts -Recurse -Filter "*-report-github.md"
        $missingResults = $reports -and (Select-String -Path $reports.FullName -SimpleMatch "There are not any results runs")
        if (-not $reports -or $missingResults) {
            throw "$name benchmark produced no results. Check its setup output above."
        }
    }
    finally {
        Remove-Item Env:AS_BENCH_CORPUS_TIER -ErrorAction SilentlyContinue
    }
}

if ($Tier -in @("compact", "all")) {
    Invoke-Benchmark "*CompactBuildBenchmark*" "compact"
}
if ($Tier -in @("compact-stages", "all")) {
    Invoke-Benchmark "*CompactBuildStageBenchmark*" "compact-stages"
}
if ($Tier -in @("rows", "extraction", "all")) {
    Invoke-Benchmark "*ExtractionRowScaleBenchmark*" "row-scale"
}
if ($Tier -in @("long-streaming", "extraction", "scraping", "all")) {
    Invoke-Benchmark "*LongSyntheticConstructionBenchmark*" "long-streaming"
}
if ($Tier -in @("scraping", "extraction", "all")) {
    Invoke-Benchmark "*QqArticleScraperBenchmark*" "qq-scraper"
}
if ($HardwareCounters) { $env:AS_BENCH_HARDWARE_COUNTERS = "1" }

if ($Tier -in @("utf8-tokenizer", "utf8", "all")) {
    Invoke-Benchmark "*Utf8TokenizerBenchmark*" "utf8-tokenizer"
}
if ($Tier -in @("utf8-baseline", "all")) {
    Invoke-Benchmark "*Utf8TokenizerBaselineBenchmark*" "utf8-baseline"
    dotnet run --project $project -c Release -f net10.0 --no-build -- `
        --utf8-tokenizer-baseline --output (Join-Path $output "utf8-baseline-diagnostics.md")
    if ($LASTEXITCODE -ne 0) { throw "UTF-8 baseline diagnostics failed." }
}
if ($Tier -in @("utf8-rodom", "utf8", "all")) {
    Invoke-Benchmark "*Utf8RodomBenchmark*" "utf8-rodom"
}
if ($Tier -in @("utf8-dom", "utf8", "all")) {
    Invoke-Benchmark "*Utf8DomProjectionBenchmark*" "utf8-dom"
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
    Invoke-Benchmark "*HttpClientStreamingQueryBenchmark*" "query-http"
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
Remove-Item Env:AS_BENCH_HARDWARE_COUNTERS -ErrorAction SilentlyContinue
