param(
    [int] $Seconds = 10,
    [int] $Rounds = 3,
    [string] $Concurrency = "1,6"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$rustManifest = Join-Path $root "benchmarks/ProductComparison/lol-html-server/Cargo.toml"
$angleProject = Join-Path $root "benchmarks/ProductComparison/AngleSharp.NativeServer/AngleSharp.NativeServer.csproj"
$runnerProject = Join-Path $root "benchmarks/ProductComparison/LoadRunner/LoadRunner.csproj"
$corpus = Join-Path $root "tests/AngleSharp.ReadOnlyDom.Tests/temp/qq.html"
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$output = Join-Path $root "artifacts/benchmarks/$timestamp-product-comparison/report.md"

$architecture = (uname -m).Trim()
if ($IsMacOS) {
    $rid = if ($architecture -eq "arm64") { "osx-arm64" } else { "osx-x64" }
    $angleExecutable = Join-Path (Split-Path $angleProject) "bin/Release/net10.0/$rid/publish/AngleSharp.NativeServer"
    $lolExecutable = Join-Path (Split-Path $rustManifest) "target/release/lol-html-server"
}
elseif ($IsLinux) {
    $rid = if ($architecture -eq "aarch64") { "linux-arm64" } else { "linux-x64" }
    $angleExecutable = Join-Path (Split-Path $angleProject) "bin/Release/net10.0/$rid/publish/AngleSharp.NativeServer"
    $lolExecutable = Join-Path (Split-Path $rustManifest) "target/release/lol-html-server"
}
else {
    $rid = "win-x64"
    $angleExecutable = Join-Path (Split-Path $angleProject) "bin/Release/net10.0/$rid/publish/AngleSharp.NativeServer.exe"
    $lolExecutable = Join-Path (Split-Path $rustManifest) "target/release/lol-html-server.exe"
}

cargo build --release --locked --manifest-path $rustManifest
if ($LASTEXITCODE -ne 0) { throw "lol-html server build failed." }

dotnet publish $angleProject -c Release -r $rid --self-contained true -p:PublishAot=true
if ($LASTEXITCODE -ne 0) { throw "AngleSharp NativeAOT server publish failed." }

dotnet build $runnerProject -c Release
if ($LASTEXITCODE -ne 0) { throw "Product load runner build failed." }

dotnet run --project $runnerProject -c Release --no-build -- `
    --angle $angleExecutable `
    --lol $lolExecutable `
    --corpus $corpus `
    --output $output `
    --seconds $Seconds `
    --rounds $Rounds `
    --concurrency $Concurrency
if ($LASTEXITCODE -ne 0) { throw "Product comparison failed." }

Write-Host "Product comparison report: $output"
