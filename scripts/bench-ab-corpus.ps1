param(
    [Parameter(Mandatory = $true)]
    [String] $BaselineDll,
    [String] $CandidateDll,
    [String] $ResultsFile,
    [Double] $Seconds = 10,
    [Int32] $Warmup = 200,
    [Int32] $Rounds = 3
)

$ErrorActionPreference = "Stop"
$env:DOTNET_gcServer = "0"

$root = Split-Path -Parent $PSScriptRoot
if (-not $CandidateDll) {
    $CandidateDll = Join-Path $root "benchmarks/ProductComparison/AngleSharp.NativeConsole/bin/Release/net10.0/AngleSharp.NativeConsole.dll"
}
$corpDir = Join-Path $root "dom/tests/AngleSharp.ReadOnlyDom.Tests/TestData/corpus"
if (-not $ResultsFile) {
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $ResultsFile = Join-Path $root "artifacts/benchmarks/$timestamp-ab-corpus/results.jsonl"
}

if (-not (Test-Path $BaselineDll)) { throw "Missing baseline console: $BaselineDll" }
if (-not (Test-Path $CandidateDll)) { throw "Missing candidate console: $CandidateDll" }
New-Item -ItemType Directory -Force (Split-Path $ResultsFile) | Out-Null

$corpora = @(
    "yahoo.html","google.html","spiegel.html","aliexpress.html","baidu.html",
    "linkedin.html","ebay.html","en.wikipedia.html","imdb.html","qq.html",
    "reddit.html","nytimes.html","nbc.html","mail.ru.html","tumblr.html"
)

function Run-One {
    param($dll, $input_, $chunk)
    $out = dotnet $dll --input $input_ --workload match --query generic --mode push --chunk-size $chunk --warmup $Warmup --seconds $Seconds
    return $out
}

function Parse-Result {
    param($line)
    $result = @{}
    if ($line -match "requests=(\d+)") { $result.requests = [long]$Matches[1] }
    if ($line -match "elapsed_ms=([\d\.]+)") { $result.elapsed_ms = [double]$Matches[1] }
    if ($line -match "value_checksum=(-?\d+)") { $result.value_checksum = $Matches[1] }
    $result.rps = $result.requests / ($result.elapsed_ms / 1000.0)
    return $result
}

Remove-Item $ResultsFile -ErrorAction SilentlyContinue

foreach ($corpus in $corpora) {
    $inputPath = Join-Path $corpDir $corpus
    Write-Host "=== $corpus ==="
    for ($round = 1; $round -le $Rounds; $round++) {
        $lineA = Run-One -dll $BaselineDll -input_ $inputPath -chunk 4096
        $pA = Parse-Result $lineA
        $recA = [PSCustomObject]@{ corpus=$corpus; lane="A"; round=$round; chunk=4096; rps=$pA.rps; value_checksum=$pA.value_checksum; raw=$lineA }
        $recA | ConvertTo-Json -Compress | Add-Content $ResultsFile
        Write-Host "  A round $round : rps=$($pA.rps) checksum=$($pA.value_checksum)"

        $lineB = Run-One -dll $CandidateDll -input_ $inputPath -chunk 4096
        $pB = Parse-Result $lineB
        $recB = [PSCustomObject]@{ corpus=$corpus; lane="B"; round=$round; chunk=4096; rps=$pB.rps; value_checksum=$pB.value_checksum; raw=$lineB }
        $recB | ConvertTo-Json -Compress | Add-Content $ResultsFile
        Write-Host "  B round $round : rps=$($pB.rps) checksum=$($pB.value_checksum)"
    }
}

# entity-dense focus at chunk-size 65536, one A/B round
foreach ($corpus in @("baidu.html","aliexpress.html")) {
    $inputPath = Join-Path $corpDir $corpus
    Write-Host "=== $corpus (chunk 65536) ==="
    $lineA = Run-One -dll $BaselineDll -input_ $inputPath -chunk 65536
    $pA = Parse-Result $lineA
    $recA = [PSCustomObject]@{ corpus=$corpus; lane="A"; round=1; chunk=65536; rps=$pA.rps; value_checksum=$pA.value_checksum; raw=$lineA }
    $recA | ConvertTo-Json -Compress | Add-Content $ResultsFile
    Write-Host "  A chunk65536 : rps=$($pA.rps) checksum=$($pA.value_checksum)"

    $lineB = Run-One -dll $CandidateDll -input_ $inputPath -chunk 65536
    $pB = Parse-Result $lineB
    $recB = [PSCustomObject]@{ corpus=$corpus; lane="B"; round=1; chunk=65536; rps=$pB.rps; value_checksum=$pB.value_checksum; raw=$lineB }
    $recB | ConvertTo-Json -Compress | Add-Content $ResultsFile
    Write-Host "  B chunk65536 : rps=$($pB.rps) checksum=$($pB.value_checksum)"
}

Write-Host "DONE: $ResultsFile"
