<#
    Whole-corpus cross-engine comparison: tuned lol-html against the managed streaming console.

    This is the harness behind the readme's per-document extraction table. It differs from
    bench-sweep.ps1 in three ways, each forced by running the full corpus rather than the
    standing fifteen documents:

    - By default it walks the canonical 47 captured pages in the corpus directory. Synthetic
      ladder and raw-text fixtures remain available through -Corpora but do not change the
      published comparison set.
    - A cross-lane value mismatch is recorded, not thrown. Three documents (ebay, pinterest,
      codeproject) each contain one <a href> inside a <noscript> element, which this engine
      extracts and lol-html does not: <noscript> is raw text only with scripting enabled, and
      lol-html hardcodes that while this engine follows the scripting-disabled default. Over
      a full-corpus run a throwing guard just aborts the run, so those rows are marked instead and
      left out of every aggregate.
    - It runs several independent passes and reports run-to-run drift, because a single pass
      cannot distinguish a structural delta from machine state.

    Reported rates are the median over all samples from all passes, per document and lane -
    not the mean of per-pass medians, which would give a drifting pass equal weight with a
    stable one. Measurement rules follow bench-sweep.ps1: workstation GC, unpinned processes,
    ABBA lane interleaving, warmup scaled to document size.

    Build the tuned Rust lane first (see Cargo.toml for the profile and RUSTFLAGS):

        RUSTFLAGS="-C target-cpu=native" cargo build --profile release-tuned `
            --manifest-path benchmarks/ProductComparison/lol-html-server/Cargo.toml `
            --bin lol-html-console

    An untuned `cargo build --release` binary is not an honest bar - it leaves roughly 45% on
    the table - so this script refuses to guess and takes the path explicitly by default.
#>
param(
    [double] $Seconds = 3,
    # Interleaved rounds within one pass.
    [int] $Rounds = 5,
    # Independent passes. Two is the minimum that says anything about stability.
    [int] $Passes = 2,
    [int] $ChunkSize = 4096,
    [ValidateSet("passthrough", "match", "extract")]
    [string] $Workload = "match",
    # "generic" (a[href]) is the only selector that matches across the whole corpus.
    [ValidateSet("qq", "generic")]
    [string] $Query = "generic",
    [ValidateSet("stream", "stream-trusted", "push", "buffer-arbitrary", "buffer-trusted")]
    [string] $Mode = "push",
    # Tuned lol-html console. Defaults to the release-tuned profile output.
    [string] $LolConsole,
    # Overrides the working-tree console; a .exe (e.g. a NativeAOT publish) runs directly.
    [string] $CandidateDll,
    # Subset of corpus file names, for a quick check of the harness itself.
    [string[]] $Corpora,
    [switch] $SkipBuild
)

$ErrorActionPreference = "Stop"
$culture = [Globalization.CultureInfo]::InvariantCulture
$root = Split-Path -Parent $PSScriptRoot
$angleProject = Join-Path $root "benchmarks/ProductComparison/AngleSharp.NativeConsole/AngleSharp.NativeConsole.csproj"
$corpusRoot = Join-Path $root "tests/AngleSharp.ReadOnlyDom.Tests/TestData/corpus"
$extension = if ($IsWindows) { ".exe" } else { "" }
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$outputDirectory = Join-Path $root "artifacts/benchmarks/$timestamp-cross-engine-corpus"

if (-not $LolConsole) {
    $LolConsole = Join-Path $root "benchmarks/ProductComparison/lol-html-server/target/release-tuned/lol-html-console$extension"
}
if (-not (Test-Path $LolConsole)) { throw "Missing tuned lol-html console: $LolConsole" }

if (-not $SkipBuild -and -not $CandidateDll) {
    dotnet build $angleProject -c Release --no-restore -m:1 --disable-build-servers `
        -p:UseSharedCompilation=false -p:PublishAot=false -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw "AngleSharp console build failed." }
}
$candidate = if ($CandidateDll) { $CandidateDll }
             else { Join-Path (Split-Path $angleProject) "bin/Release/net10.0/AngleSharp.NativeConsole.dll" }
if (-not (Test-Path $candidate)) { throw "Missing managed console: $candidate" }

$lanes = [ordered]@{
    candidate  = if ([IO.Path]::GetExtension($candidate) -eq ".exe") { @{ Executable = $candidate; Prefix = @() } }
                 else { @{ Executable = "dotnet"; Prefix = @($candidate) } }
    "lol-html" = @{ Executable = $LolConsole; Prefix = @() }
}

function Invoke-Lane([Hashtable] $Lane, [String] $CorpusPath, [Int32] $Warmup) {
    $info = [Diagnostics.ProcessStartInfo]::new($Lane.Executable)
    $arguments = @($Lane.Prefix) + @(
        "--input", $CorpusPath,
        "--seconds", $Seconds.ToString($culture),
        "--warmup", $Warmup.ToString($culture),
        "--copies", "1",
        "--chunk-size", $ChunkSize.ToString($culture),
        "--workload", $Workload,
        "--query", $Query
    )
    # Only the managed console understands --mode; the Rust lane always streams chunks.
    if ($Lane.Executable -eq "dotnet") { $arguments += @("--mode", $Mode, "--unlimited", "false") }
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
    [pscustomobject]@{
        Rate     = [Int64]::Parse($values.requests, $culture) / ([Double]::Parse($values.elapsed_ms, $culture) / 1000.0)
        Checksum = $values.value_checksum
        Urls     = [Int64]::Parse($values.urls, $culture)
    }
}

function Get-Median([Double[]] $Values) {
    $ordered = @($Values | Sort-Object)
    $middle = [Int32][Math]::Floor($ordered.Count / 2)
    if ($ordered.Count % 2 -eq 1) { return $ordered[$middle] }
    return ($ordered[$middle - 1] + $ordered[$middle]) / 2.0
}

$files = Get-ChildItem $corpusRoot -Filter *.html | Sort-Object Length -Descending
if ($Corpora) {
    $wanted = @($Corpora | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_) })
    $files = @($files | Where-Object { $wanted -contains $_.BaseName })
    if ($files.Count -ne $wanted.Count) { throw "Requested corpora not found under $corpusRoot." }
}
else {
    $files = @($files | Where-Object { $_.BaseName -notlike "ladder-*" -and $_.BaseName -notlike "synth-*" })
    if ($files.Count -ne 47) {
        throw "Expected the canonical 47-page comparison corpus, found $($files.Count) under $corpusRoot."
    }
}

New-Item -ItemType Directory -Force $outputDirectory | Out-Null
$samplePath = Join-Path $outputDirectory "samples.jsonl"
Write-Host "corpora=$($files.Count) workload=$Workload query=$Query mode=$Mode chunk=$ChunkSize passes=$Passes x $Rounds rounds x ${Seconds}s"

$rows = [Collections.Generic.List[object]]::new()
foreach ($file in $files) {
    # Floor of 30 iterations: below that the managed lane is still running tier-0 code on the
    # largest documents, which reads as a 90% loss that is purely JIT tiering.
    $warmup = [Int32][Math]::Max(30, [Math]::Min(400, 40MB / $file.Length))
    $samples = @{}
    $perPass = @{}
    $facts = @{}
    foreach ($lane in $lanes.Keys) {
        $samples[$lane] = [Collections.Generic.List[Double]]::new()
        foreach ($pass in 1..$Passes) { $perPass["$lane-$pass"] = [Collections.Generic.List[Double]]::new() }
    }

    foreach ($pass in 1..$Passes) {
        for ($round = 1; $round -le $Rounds; $round++) {
            $order = if ($round % 2 -eq 1) { @($lanes.Keys) } else { @($lanes.Keys)[($lanes.Count - 1)..0] }
            foreach ($lane in $order) {
                $result = Invoke-Lane $lanes[$lane] $file.FullName $warmup
                $samples[$lane].Add($result.Rate)
                $perPass["$lane-$pass"].Add($result.Rate)
                $facts[$lane] = $result
                ([pscustomobject]@{
                    corpus = $file.BaseName; lane = $lane; pass = $pass; round = $round
                    rate = $result.Rate; checksum = $result.Checksum; urls = $result.Urls
                }) | ConvertTo-Json -Compress | Add-Content $samplePath
            }
        }
    }

    $candidateRate = Get-Median $samples["candidate"].ToArray()
    $lolRate = Get-Median $samples["lol-html"].ToArray()
    # Per-pass deltas expose a document whose result depends on machine state rather than shape.
    $passDeltas = @(foreach ($pass in 1..$Passes) {
        100.0 * ((Get-Median $perPass["candidate-$pass"].ToArray()) / (Get-Median $perPass["lol-html-$pass"].ToArray()) - 1.0)
    })

    $row = [pscustomobject]@{
        Corpus  = $file.BaseName
        KB      = [Math]::Round($file.Length / 1KB)
        Matches = $facts["candidate"].Urls
        LolUrls = $facts["lol-html"].Urls
        Lol     = $lolRate
        Candidate = $candidateRate
        Delta   = 100.0 * ($candidateRate / $lolRate - 1.0)
        PassDeltas = ($passDeltas | ForEach-Object { $_.ToString('+0.0;-0.0', $culture) }) -join " / "
        DriftPoints = ($passDeltas | Measure-Object -Maximum).Maximum - ($passDeltas | Measure-Object -Minimum).Minimum
        Agree   = ($facts["candidate"].Checksum -eq $facts["lol-html"].Checksum) -and
                  ($facts["candidate"].Urls -eq $facts["lol-html"].Urls)
    }
    $rows.Add($row)
    Write-Host (
        "{0,-24} {1,6} KB  matches={2,-6} lol={3,10:N0}  candidate={4,10:N0}  delta={5,7}  passes={6}  drift={7,4:N1}pp  agree={8}" -f
        $row.Corpus, $row.KB, $row.Matches, $row.Lol, $row.Candidate,
        $row.Delta.ToString('+0.0;-0.0', $culture), $row.PassDeltas, $row.DriftPoints, $row.Agree
    )
}

$rows | Export-Csv -NoTypeInformation -Encoding utf8 (Join-Path $outputDirectory "rows.csv")

$comparable = @($rows | Where-Object Agree)
$deltas = @($comparable | Select-Object -ExpandProperty Delta)
$large = @($comparable | Where-Object { $_.KB -ge 100 } | Select-Object -ExpandProperty Delta)
$small = @($comparable | Where-Object { $_.KB -lt 100 } | Select-Object -ExpandProperty Delta)
$drift = @($comparable | Select-Object -ExpandProperty DriftPoints)

$lines = [Collections.Generic.List[String]]::new()
$lines.Add("# Cross-engine whole-corpus comparison")
$lines.Add("")
$lines.Add("- Workload: $Workload (query: $Query), input mode $Mode in $ChunkSize-byte chunks")
$lines.Add("- Measurement: $Passes passes x $Rounds ABBA rounds x ${Seconds}s per lane; median of all samples")
$lines.Add("- Rust lane: $LolConsole")
$lines.Add("- GC: workstation (measurement only; the console ships Server GC); processes unpinned")
$lines.Add("")
$lines.Add("| Corpus | KB | a[href] | lol-html docs/s | candidate docs/s | Delta % | per-pass | drift pp |")
$lines.Add("| --- | ---: | ---: | ---: | ---: | ---: | :--- | ---: |")
foreach ($row in $rows) {
    $name = if ($row.Agree) { $row.Corpus } else { "$($row.Corpus) (values differ: $($row.Matches) vs $($row.LolUrls))" }
    $lines.Add(
        "| $name | $($row.KB) | $($row.Matches) | $($row.Lol.ToString('N0', $culture)) | " +
        "$($row.Candidate.ToString('N0', $culture)) | $($row.Delta.ToString('+0.0;-0.0', $culture)) | " +
        "$($row.PassDeltas) | $($row.DriftPoints.ToString('N1', $culture)) |"
    )
}
$lines.Add("")
foreach ($group in @(
    @{ Name = "all comparable"; Values = $deltas },
    @{ Name = "documents >= 100 KB"; Values = $large },
    @{ Name = "documents < 100 KB"; Values = $small }
)) {
    if ($group.Values.Count -eq 0) { continue }
    $lines.Add(
        ("- {0}: {1} documents, {2} wins, {3} losses, median {4}%, range {5}% to {6}%" -f
            $group.Name, $group.Values.Count,
            @($group.Values | Where-Object { $_ -gt 0 }).Count,
            @($group.Values | Where-Object { $_ -le 0 }).Count,
            (Get-Median $group.Values).ToString('+0.0;-0.0', $culture),
            (($group.Values | Measure-Object -Minimum).Minimum).ToString('+0.0;-0.0', $culture),
            (($group.Values | Measure-Object -Maximum).Maximum).ToString('+0.0;-0.0', $culture))
    )
}
$lines.Add(("- Excluded on value divergence: {0}" -f
    ((@($rows | Where-Object { -not $_.Agree } | ForEach-Object { "$($_.Corpus) ($($_.Matches) vs $($_.LolUrls))" }) -join ", ") -replace '^$', 'none')))
if ($Passes -gt 1) {
    $lines.Add(("- Run-to-run delta drift: median {0:N1}pp, max {1:N1}pp ({2})" -f
        (Get-Median $drift), ($drift | Measure-Object -Maximum).Maximum,
        ($comparable | Sort-Object DriftPoints -Descending | Select-Object -First 1).Corpus))
}

$reportPath = Join-Path $outputDirectory "report.md"
$lines | Set-Content -Encoding utf8 $reportPath
Write-Host ""
$lines | Select-Object -Last 8 | ForEach-Object { Write-Host $_ }
Write-Host "Report: $reportPath"
