<#
    Fast A/B loop for streaming tokenizer work.

    Built for iteration speed: a full bench-native-console.ps1 run takes ~20 minutes, which is
    too slow to steer an optimisation. This compares the working tree against a saved console
    build on one corpus in well under a minute, at roughly 2% spread.

    Snapshot a baseline before starting work:

        Copy-Item benchmarks/ProductComparison/AngleSharp.NativeConsole/bin/Release/net10.0 `
            artifacts/baseline-console -Recurse

    then iterate:

        ./scripts/bench-ab.ps1 -BaselineDll artifacts/baseline-console/AngleSharp.NativeConsole.dll

    Confirm anything promising with bench-sweep.ps1 before believing it: a single corpus is easy
    to overfit, and changes to this loop interact - two edits worth ~0% alone can move together.

    See bench-sweep.ps1 for why measurement forces workstation GC and leaves affinity alone.
#>
param(
    # Either lane accepts a managed console dll (run through `dotnet`) or a NativeAOT
    # console exe (run directly).
    [Parameter(Mandatory = $true)]
    [String] $BaselineDll,
    # Overrides the working-tree console as the candidate lane; use to A/B two saved
    # builds (e.g. two NativeAOT publishes). Implies -SkipBuild.
    [String] $CandidateDll,
    [double] $Seconds = 3,
    [int] $Rounds = 5,
    [int] $Warmup = 400,
    [Int32[]] $Copies = @(1),
    [ValidateSet("passthrough", "match", "extract", "rewrite", "rewrite-sink", "rewrite-stream", "rewrite-text")]
    [string] $Workload = "extract",
    [ValidateSet("qq", "generic")]
    [string] $Query = "qq",
    [ValidateSet("stream", "stream-trusted", "push", "buffer-arbitrary", "buffer-trusted")]
    [string] $Mode = "stream",
    [switch] $Unlimited,
    [switch] $BaselineUnlimited,
    [Int32] $ChunkSize = 4096,
    [String] $Corpus = "qq.html",
    [switch] $SkipBuild
)

$ErrorActionPreference = "Stop"
$culture = [Globalization.CultureInfo]::InvariantCulture
$root = Split-Path -Parent $PSScriptRoot
$angleProject = Join-Path $root "benchmarks/ProductComparison/AngleSharp.NativeConsole/AngleSharp.NativeConsole.csproj"
$corpusPath = Join-Path $root "tests/AngleSharp.ReadOnlyDom.Tests/temp/$Corpus"
if (-not (Test-Path $corpusPath)) { throw "Missing corpus: $corpusPath" }
if (-not (Test-Path $BaselineDll)) { throw "Missing baseline console: $BaselineDll" }

if ($CandidateDll -and -not (Test-Path $CandidateDll)) { throw "Missing candidate console: $CandidateDll" }
if (-not $SkipBuild -and -not $CandidateDll) {
    dotnet build $angleProject -c Release --no-restore -m:1 --disable-build-servers `
        -p:UseSharedCompilation=false -p:PublishAot=false -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw "AngleSharp console build failed." }
}

$lanes = [ordered]@{
    candidate = @{
        Dll = if ($CandidateDll) { $CandidateDll }
              else { Join-Path (Split-Path $angleProject) "bin/Release/net10.0/AngleSharp.NativeConsole.dll" }
        Unlimited = $Unlimited.IsPresent
    }
    baseline = @{ Dll = $BaselineDll; Unlimited = $BaselineUnlimited.IsPresent }
}

function Invoke-Lane([Hashtable] $Lane, [Int32] $CopyCount) {
    $native = [IO.Path]::GetExtension($Lane.Dll) -eq ".exe"
    $info = [Diagnostics.ProcessStartInfo]::new($(if ($native) { $Lane.Dll } else { "dotnet" }))
    if (-not $native) { $info.ArgumentList.Add([String]$Lane.Dll) }
    $arguments = @(
        "--input", $corpusPath,
        "--seconds", $Seconds.ToString($culture),
        "--warmup", $Warmup.ToString($culture),
        "--copies", $CopyCount.ToString($culture),
        "--chunk-size", $ChunkSize.ToString($culture),
        "--workload", $Workload,
        "--query", $Query,
        "--mode", $Mode,
        "--unlimited", $Lane.Unlimited.ToString().ToLowerInvariant()
    )
    foreach ($argument in $arguments) { $info.ArgumentList.Add([String]$argument) }
    $info.RedirectStandardOutput = $true
    $info.UseShellExecute = $false
    $info.Environment["DOTNET_gcServer"] = "0"
    $process = [Diagnostics.Process]::Start($info)
    if ($IsWindows) { $process.PriorityClass = "High" }
    $output = $process.StandardOutput.ReadToEnd()
    $process.WaitForExit()

    $line = ($output -split "`n" | Where-Object { $_ -like "RESULT *" } | Select-Object -Last 1)
    if ($process.ExitCode -ne 0 -or -not $line) { throw "Console run failed: $($Lane.Dll)" }
    $values = @{}
    foreach ($token in $line.Trim().Split(' ')) {
        $pair = $token.Split('=', 2)
        if ($pair.Length -eq 2) { $values[$pair[0]] = $pair[1] }
    }
    [pscustomobject]@{
        Rate      = [Int64]::Parse($values.requests, $culture) / ([Double]::Parse($values.elapsed_ms, $culture) / 1000.0)
        # Rust lanes do not report allocations.
        Allocated = if ($values.ContainsKey("allocated_bytes_per_request")) { [Double]::Parse($values.allocated_bytes_per_request, $culture) } else { 0.0 }
        Checksum  = $values.value_checksum
        Urls      = $values.urls
    }
}

function Get-Median([Double[]] $Values) {
    $ordered = @($Values | Sort-Object)
    $middle = [Int32][Math]::Floor($ordered.Count / 2)
    if ($ordered.Count % 2 -eq 1) { return $ordered[$middle] }
    return ($ordered[$middle - 1] + $ordered[$middle]) / 2.0
}

Write-Host "corpus=$Corpus workload=$Workload mode=$Mode candidate-unlimited=$($Unlimited.IsPresent) baseline-unlimited=$($BaselineUnlimited.IsPresent) rounds=$Rounds x ${Seconds}s"
foreach ($copyCount in $Copies) {
    $samples = @{}
    $facts = @{}
    foreach ($lane in $lanes.Keys) { $samples[$lane] = [Collections.Generic.List[Double]]::new() }
    for ($round = 1; $round -le $Rounds; $round++) {
        # Alternate lane order so drift is shared rather than attributed to one lane.
        $order = if ($round % 2 -eq 1) { @($lanes.Keys) } else { @($lanes.Keys)[($lanes.Count - 1)..0] }
        foreach ($lane in $order) {
            $result = Invoke-Lane $lanes[$lane] $copyCount
            $samples[$lane].Add($result.Rate)
            $facts[$lane] = $result
        }
    }

    if ($facts["candidate"].Checksum -ne $facts["baseline"].Checksum -or $facts["candidate"].Urls -ne $facts["baseline"].Urls) {
        throw "Correctness mismatch: candidate and baseline disagree on the extracted values."
    }

    $label = if ($copyCount -eq 1) { $Corpus -replace '\.html$', '' } else { "$($Corpus -replace '\.html$', '')-x$copyCount" }
    foreach ($lane in $lanes.Keys) {
        $rates = $samples[$lane].ToArray()
        $median = Get-Median $rates
        $minimum = ($rates | Measure-Object -Minimum).Minimum
        $maximum = ($rates | Measure-Object -Maximum).Maximum
        Write-Host (
            "{0,-14} {1,-10} median={2,9:N1} docs/s  spread={3,5:N1}%  alloc/req={4,7:N0} B" -f
            $label, $lane, $median, (100.0 * ($maximum - $minimum) / $median), $facts[$lane].Allocated
        )
    }
    $delta = 100.0 * ((Get-Median $samples["candidate"].ToArray()) / (Get-Median $samples["baseline"].ToArray()) - 1.0)
    Write-Host ("{0,-14} {1}% candidate vs baseline" -f $label, $delta.ToString('+0.00;-0.00', $culture))
}
