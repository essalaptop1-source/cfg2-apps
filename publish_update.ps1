# ============================================================
#  publish_update.ps1 - build a new version of the app
#
#  Usage:
#    powershell -ExecutionPolicy Bypass -File publish_update.ps1 -Version 1.2.0 [-Repo owner/repo] [-Url https://host/app.exe]
#
#  -Version  required - bumps the csproj <Version> and builds the single-file exe
#  -Repo     optional - your GitHub repo (owner/repo). Then publish a GitHub
#            release with tag v<Version> and the exe as its only asset.
#            The app auto-checks this repo's latest release.
#  -Url      optional - a direct URL where the exe will be hosted. The script
#            writes a matching version.json for you to upload next to it.
#            The app auto-checks this URL when set in Settings.
#
#  Output: Cfg2 apps\CFG2 Embed sender.exe (in this project's parent folder)
# ============================================================
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Repo = "",
    [string]$Url = ""
)
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csproj = Join-Path $root "Configuration2App\Configuration2App.csproj"
$outDir = Join-Path $root "..\Cfg2 apps"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# 1) Bump the version
$content = Get-Content $csproj -Raw
if ($content -notmatch "<Version>$([regex]::Escape($Version))</Version>") {
    $content = $content -replace '<Version>[^<]*</Version>', "<Version>$Version</Version>"
    Set-Content -Path $csproj -Value $content -NoNewline -Encoding UTF8
    Write-Host "Bumped <Version> to $Version"
}

# 2) Publish the single-file exe (native-lib extraction fix is baked into csproj)
Push-Location (Join-Path $root "Configuration2App")
try {
    dotnet publish -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:AssemblyName="CFG2 Embed sender" -o $outDir | Out-Null
} finally {
    Pop-Location
}

$exe = Join-Path $outDir "CFG2 Embed sender.exe"
if (-not (Test-Path $exe)) { throw "Publish failed: exe not found at $exe" }
Write-Host "Built: $exe"

# 3) Tell the user how to ship it
if ($Repo) {
    Write-Host ""
    Write-Host "Now create the GitHub release with the exe as its asset (gh stores it as"
    Write-Host "'CFG2.Embed.sender.exe' - the app matches either form):"
    Write-Host "  gh release create v$Version `"$exe`" --repo $Repo --title `"v$Version`" --notes `"What changed?`""
    Write-Host "The app finds this repo's latest release automatically once users set the repo in Settings."
}
elseif ($Url) {
    $vj = Join-Path $outDir "version.json"
    @{ version = $Version; url = $Url; notes = "" } | ConvertTo-Json | Set-Content -Path $vj -Encoding UTF8
    Write-Host ""
    Write-Host "Upload BOTH files to your host (the exe must be reachable at the URL below):"
    Write-Host "  $exe"
    Write-Host "  $vj"
    Write-Host "version.json contents:"; Get-Content $vj
}
else {
    Write-Host ""
    Write-Host "No update source given. Copy the exe to your host and set one of these in app Settings:"
    Write-Host "  - GitHub repo  ->  create a release tagged v$Version with the exe as its asset"
    Write-Host "  - Update URL   ->  host a version.json next to it: { `"version`": `"$Version`", `"url`": `"<public exe url>`" }"
}
