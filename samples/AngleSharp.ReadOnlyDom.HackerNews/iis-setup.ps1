#Requires -RunAsAdministrator
<#
    Hosts the published app in IIS under a local hostname.

        dotnet publish samples/AngleSharp.ReadOnlyDom.HackerNews -c Release -o artifacts/iis/local-hackernews
        ./samples/AngleSharp.ReadOnlyDom.HackerNews/iis-setup.ps1        # elevated

    Creates an app pool with no managed runtime (the ASP.NET Core Module hosts .NET itself), a site bound to
    http/*:80:<Hostname>, a hosts entry, and read access for the pool identity. Re-runnable: an existing site
    is repointed rather than duplicated. Nothing else in IIS is touched.
#>
param(
    [string]$Hostname = "local-hackernews",
    [string]$SiteName = "local-hackernews",
    [string]$PhysicalPath = "$PSScriptRoot\..\..\artifacts\iis\local-hackernews",
    [int]$Port = 80,
    [string]$LogPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
if ($LogPath) { Start-Transcript -Path $LogPath -Force | Out-Null }

try {
    $appcmd = "$env:WINDIR\system32\inetsrv\appcmd.exe"
    if (-not (Test-Path $appcmd)) { throw "IIS is not installed: $appcmd is missing." }

    $root = (Resolve-Path $PhysicalPath).Path
    if (-not (Test-Path (Join-Path $root "web.config"))) {
        throw "No web.config in $root. Publish the project there first."
    }

    if (-not (& $appcmd list apppool /name:$SiteName)) {
        & $appcmd add apppool /name:$SiteName /managedRuntimeVersion:"" | Out-Null
        "app pool created: $SiteName"
    }

    if (& $appcmd list site /name:$SiteName) {
        & $appcmd set vdir "$SiteName/" /physicalPath:$root | Out-Null
        "site repointed: $SiteName -> $root"
    }
    else {
        & $appcmd add site /name:$SiteName /bindings:"http/*:${Port}:${Hostname}" /physicalPath:$root | Out-Null
        & $appcmd set app "$SiteName/" /applicationPool:$SiteName | Out-Null
        "site created: $SiteName on http/*:${Port}:${Hostname}"
    }

    icacls $root /grant "IIS AppPool\${SiteName}:(OI)(CI)(RX)" /T /Q | Out-Null

    $hosts = "$env:WINDIR\System32\drivers\etc\hosts"
    if (-not (Select-String -Path $hosts -Pattern "\s$([regex]::Escape($Hostname))\s*$" -Quiet)) {
        Add-Content -Path $hosts -Value "127.0.0.1 $Hostname"
        "hosts entry added: 127.0.0.1 $Hostname"
    }

    & $appcmd start site /site.name:$SiteName 2>&1 | Out-Null
    "ready: http://$Hostname/"
}
finally {
    if ($LogPath) { Stop-Transcript | Out-Null }
}
