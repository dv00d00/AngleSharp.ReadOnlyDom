<#
    Cross-corpus throughput sweep for the streaming console.

    Where bench-native-console.ps1 answers "how do we compare on qq.html", this answers
    "does a change hold across the corpus set" - a single-corpus delta is easy to overfit.
    Throughput is reported in MB/s so documents from 9 KB to 2 MB stay comparable.

    Lanes are interleaved per repetition so clock/thermal drift lands on every lane instead
    of masquerading as a delta. Measurement notes:

    - Workstation GC is forced for measurement. The console enables Server GC, which starts
      one GC thread per core; that is right for production but adds variance here.
    - The processes are deliberately NOT pinned. Narrowing affinity starves the background
      tier-1 JIT thread behind the hot loop, which leaves tier-0 code running and reports
      throughput roughly an order of magnitude low.
#>
param(
    [double] $Seconds = 2,
    [int] $Rounds = 3,
    [ValidateSet("passthrough", "match", "extract", "rewrite", "rewrite-sink")]
    [string] $Workload = "passthrough",
    [ValidateSet("stream", "stream-trusted", "push", "buffer-arbitrary", "buffer-trusted")]
    [string] $Mode = "stream",
    [switch] $Unlimited,
    [Int32] $ChunkSize = 4096,
    # Optional second .NET lane: a console DLL built from another commit.
    [string] $BaselineDll,
    # Optional third .NET lane: the same candidate DLL driven through the push session,
    # the structural equivalent of lol-html's write() shape.
    [switch] $IncludePush,
    # Optional Rust lane; build with bench-native-console.ps1 or cargo first.
    [switch] $IncludeLolHtml,
    [String[]] $Corpora = @(
        "spiegel.html", "yahoo.html", "imdb.html", "nytimes.html", "en.wikipedia.html",
        "reddit.html", "ebay.html", "baidu.html", "stackoverflow.html", "google.html",
        "aliexpress.html", "linkedin.html", "qq.html", "html5test.html", "weibo.html"
    )
)

$ErrorActionPreference = "Stop"
$culture = [Globalization.CultureInfo]::InvariantCulture
$root = Split-Path -Parent $PSScriptRoot
$angleProject = Join-Path $root "benchmarks/ProductComparison/AngleSharp.NativeConsole/AngleSharp.NativeConsole.csproj"
$corpusRoot = Join-Path $root "tests/AngleSharp.ReadOnlyDom.Tests/temp"
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$reportPath = Join-Path $root "artifacts/benchmarks/$timestamp-sweep/report.md"

dotnet build $angleProject -c Release --no-restore -m:1 --disable-build-servers `
    -p:UseSharedCompilation=false -p:PublishAot=false -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw "AngleSharp console build failed." }

$candidateDll = Join-Path (Split-Path $angleProject) "bin/Release/net10.0/AngleSharp.NativeConsole.dll"
$extension = if ($IsWindows) { ".exe" } else { "" }
$lolExecutable = Join-Path $root "benchmarks/ProductComparison/lol-html-server/target/release/lol-html-console$extension"
if ($IncludeLolHtml -and -not (Test-Path $lolExecutable)) {
    throw "lol-html console not built. Run scripts/bench-native-console.ps1 first."
}

$lanes = [ordered]@{ candidate = @{ Executable = "dotnet"; Prefix = @($candidateDll) } }
if ($BaselineDll) { $lanes["baseline"] = @{ Executable = "dotnet"; Prefix = @($BaselineDll) } }
if ($IncludePush) { $lanes["push"] = @{ Executable = "dotnet"; Prefix = @($candidateDll); Mode = "push" } }
if ($IncludeLolHtml) { $lanes["lol-html"] = @{ Executable = $lolExecutable; Prefix = @() } }

function Invoke-Lane([Hashtable] $Lane, [String] $CorpusPath, [Int32] $Warmup, [Int64] $Bytes) {
    $info = [Diagnostics.ProcessStartInfo]::new($Lane.Executable)
    # The Rust lane implements one rewrite workload; the sink shape is a managed-side choice.
    $laneWorkload = if ($Lane.Executable -ne "dotnet" -and $Workload -eq "rewrite-sink") { "rewrite" } else { $Workload }
    $arguments = @($Lane.Prefix) + @(
        "--input", $CorpusPath,
        "--seconds", $Seconds.ToString($culture),
        "--warmup", $Warmup.ToString($culture),
        "--copies", "1",
        "--chunk-size", $ChunkSize.ToString($culture),
        "--workload", $laneWorkload
    )
    # Only the managed console understands --mode; the Rust lane always streams.
    if ($Lane.Executable -eq "dotnet") {
        $laneMode = if ($Lane.Contains("Mode")) { $Lane.Mode } else { $Mode }
        if ($laneMode -ne "stream") { $arguments += @("--mode", $laneMode) }
        $arguments += @("--unlimited", $Unlimited.IsPresent.ToString().ToLowerInvariant())
    }
    foreach ($argument in $arguments) { $info.ArgumentList.Add([String]$argument) }
    $info.RedirectStandardOutput = $true
    $info.UseShellExecute = $false
    $info.Environment["DOTNET_gcServer"] = "0"
    $process = [Diagnostics.Process]::Start($info)
    if ($IsWindows) { $process.PriorityClass = "High" }
    $output = $process.StandardOutput.ReadToEnd()
    $process.WaitForExit()

    $line = ($output -split "`n" | Where-Object { $_ -like "RESULT *" } | Select-Object -Last 1)
    if ($process.ExitCode -ne 0 -or -not $line) { throw "$($Lane.Executable) failed on $CorpusPath." }
    $values = @{}
    foreach ($token in $line.Trim().Split(' ')) {
        $pair = $token.Split('=', 2)
        if ($pair.Length -eq 2) { $values[$pair[0]] = $pair[1] }
    }
    $requests = [Int64]::Parse($values.requests, $culture)
    $elapsed = [Double]::Parse($values.elapsed_ms, $culture)
    [pscustomobject]@{
        MbPerSecond = ($requests * $Bytes) / ($elapsed / 1000.0) / 1MB
        Checksum    = $values.value_checksum
        Urls        = $values.urls
    }
}

function Get-Median([Double[]] $Values) {
    $ordered = @($Values | Sort-Object)
    $middle = [Int32][Math]::Floor($ordered.Count / 2)
    if ($ordered.Count % 2 -eq 1) { return $ordered[$middle] }
    return ($ordered[$middle - 1] + $ordered[$middle]) / 2.0
}

$rows = [Collections.Generic.List[object]]::new()
foreach ($corpus in $Corpora) {
    $corpusPath = Join-Path $corpusRoot $corpus
    if (-not (Test-Path $corpusPath)) { throw "Missing corpus: $corpusPath" }
    $bytes = (Get-Item $corpusPath).Length
    # Large documents drive the inner loops hard enough to reach tier-1 in few passes.
    $warmup = [Int32][Math]::Max(8, [Math]::Min(400, 40MB / $bytes))

    $samples = @{}
    $facts = @{}
    foreach ($lane in $lanes.Keys) { $samples[$lane] = [Collections.Generic.List[Double]]::new() }
    for ($round = 1; $round -le $Rounds; $round++) {
        $order = if ($round % 2 -eq 1) { @($lanes.Keys) } else { @($lanes.Keys)[($lanes.Count - 1)..0] }
        foreach ($lane in $order) {
            $result = Invoke-Lane $lanes[$lane] $corpusPath $warmup $bytes
            $samples[$lane].Add($result.MbPerSecond)
            $facts[$lane] = $result
        }
    }

    # Every comparable lane must agree on the extracted values before its throughput counts.
    # The checksum covers concatenated bytes without value boundaries, so the URL count is
    # compared as well. The Rust lane only implements the match/extract value semantics.
    foreach ($lane in @($lanes.Keys) | Where-Object { $_ -ne "candidate" }) {
        if ($lane -eq "lol-html" -and $Workload -notin @("match", "extract", "rewrite", "rewrite-sink")) { continue }
        if ($facts[$lane].Checksum -ne $facts["candidate"].Checksum -or $facts[$lane].Urls -ne $facts["candidate"].Urls) {
            throw (
                "Correctness mismatch on ${corpus}: $lane disagrees with candidate " +
                "(checksum $($facts[$lane].Checksum) vs $($facts['candidate'].Checksum), " +
                "urls $($facts[$lane].Urls) vs $($facts['candidate'].Urls))."
            )
        }
    }

    $row = [ordered]@{ Corpus = ($corpus -replace '\.html$', ''); KB = [Math]::Round($bytes / 1KB) }
    foreach ($lane in $lanes.Keys) { $row[$lane] = Get-Median $samples[$lane].ToArray() }
    if ($BaselineDll) {
        $row["Delta"] = 100.0 * ($row["candidate"] / $row["baseline"] - 1.0)
    }
    $entry = [pscustomobject]$row
    $rows.Add($entry)
    Write-Host ($entry | Format-Table -AutoSize | Out-String).Trim()
}

$lines = [Collections.Generic.List[String]]::new()
$lines.Add("# Cross-corpus streaming sweep")
$lines.Add("")
$lines.Add("- Workload: $Workload")
$lines.Add("- Input mode: $Mode, $ChunkSize-byte chunks")
$lines.Add("- Measurement: $Rounds interleaved rounds x $Seconds seconds per corpus, median reported")
$lines.Add("- GC: workstation (measurement only; the console ships Server GC)")
$lines.Add("- Affinity: unpinned, so background tier-1 JIT is never starved")
$lines.Add("")
$header = "| Corpus | KB |"
$divider = "| --- | ---: |"
foreach ($lane in $lanes.Keys) { $header += " $lane MB/s |"; $divider += " ---: |" }
if ($BaselineDll) { $header += " Delta |"; $divider += " ---: |" }
$lines.Add($header)
$lines.Add($divider)
foreach ($row in $rows) {
    $text = "| $($row.Corpus) | $($row.KB) |"
    foreach ($lane in $lanes.Keys) { $text += " $($row.$lane.ToString('N1', $culture)) |" }
    if ($BaselineDll) { $text += " $($row.Delta.ToString('+0.00;-0.00', $culture))% |" }
    $lines.Add($text)
}
if ($BaselineDll) {
    $deltas = @($rows | Select-Object -ExpandProperty Delta)
    $ordered = @($deltas | Sort-Object)
    $lines.Add("")
    # Parenthesised: inside a method call, commas would split the format arguments instead.
    $summary = (
        "Delta median {0}%, range {1}% to {2}% across {3} corpora." -f
            (Get-Median $deltas).ToString('+0.00;-0.00', $culture),
            $ordered[0].ToString('+0.00;-0.00', $culture),
            $ordered[-1].ToString('+0.00;-0.00', $culture),
            $deltas.Count
    )
    $lines.Add($summary)
}

New-Item -ItemType Directory -Force (Split-Path $reportPath) | Out-Null
$lines | Set-Content -Encoding utf8 $reportPath
$lines | ForEach-Object { Write-Host $_ }
Write-Host "Report: $reportPath"
