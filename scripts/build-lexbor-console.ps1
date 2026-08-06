<#
    Builds the lexbor comparison console (benchmarks/ProductComparison/lexbor-console).

    Clones lexbor, generates the single-file html-module amalgamation with its single.pl
    (perl ships with Git for Windows), and compiles with MSVC at maximum optimization.
    Requires vswhere.exe on PATH (VS Installer directory) to locate the toolchain.

    The console mirrors the lol-html console's RESULT protocol and chunked push shape;
    see the header of lexbor-console.c for semantics caveats (standalone tokenizer, no
    tree context, so noscript/rawtext edges can differ by a count or two).
#>
param(
    # Commit tested Aug 2026; pass a branch/tag to move forward deliberately.
    [string] $LexborRef = "de1d07a7765aad37090cc36f7fac3bb59e21467d",
    [string] $Arch = "AVX2"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$consoleDir = Join-Path $root "benchmarks/ProductComparison/lexbor-console"
$workDir = Join-Path $root "artifacts/lexbor"
$sourceDir = Join-Path $workDir "lexbor-src"
New-Item -ItemType Directory -Force $workDir | Out-Null

if (-not (Test-Path $sourceDir)) {
    git clone https://github.com/lexbor/lexbor.git $sourceDir
    if ($LASTEXITCODE -ne 0) { throw "lexbor clone failed." }
}
git -C $sourceDir fetch --depth 1 origin $LexborRef
git -C $sourceDir checkout --detach $LexborRef
if ($LASTEXITCODE -ne 0) { throw "lexbor checkout failed." }

Push-Location $sourceDir
try {
    perl single.pl --port=windows_nt html > (Join-Path $workDir "lexbor_single.h")
    if ($LASTEXITCODE -ne 0) { throw "amalgamation failed." }
}
finally {
    Pop-Location
}

$vsRoot = & vswhere.exe -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsRoot) { throw "No MSVC toolchain found via vswhere." }
$vcvars = Join-Path $vsRoot "VC/Auxiliary/Build/vcvars64.bat"

Push-Location $workDir
try {
    cmd /c "`"$vcvars`" >nul 2>&1 && cl /nologo /O2 /GL /arch:$Arch /I`"$workDir`" `"$consoleDir/lexbor-console.c`" /Fe:lexbor-console.exe /link /LTCG"
    if ($LASTEXITCODE -ne 0) { throw "lexbor console compilation failed." }
}
finally {
    Pop-Location
}

Write-Host "built $(Join-Path $workDir 'lexbor-console.exe')"
