# ============================================================
#  publish_update.ps1 - build and ship a new version of a CFG2 app
#
#  Usage:
#    powershell -ExecutionPolicy Bypass -File publish_update.ps1 -Version 1.2.0 [-App embed|fps] [-Repo owner/repo] [-Url https://host/app.exe] [-Notes "what changed"]
#
#  -Version  required - bumps the csproj <Version> and builds the single-file exe
#  -App      optional - which app: embed (default) or fps
#  -Repo     optional - your GitHub repo (owner/repo). When set, the script
#            uploads the exe as a GitHub release (tag v<Version>) automatically.
#  -Url      optional - a direct URL where the exe will be hosted. The script
#            writes a matching version.json for you to upload next to it.
#  -Notes    optional - release notes for the GitHub release (used with -Repo).
#
#  Output: Cfg2 apps\<App folder>\<App name>.exe (in this project's parent folder)
# ============================================================
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [ValidateSet("embed", "fps")][string]$App = "embed",
    [string]$Repo = "",
    [string]$Url = "",
    [string]$Notes = ""
)
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path

switch ($App) {
    "embed" {
        $csproj = Join-Path $root "Configuration2App\Configuration2App.csproj"
        $assemblyName = "CFG2 Embed sender"
        $outDir = Join-Path $root "..\Cfg2 apps\CFG2 embed sender"
    }
    "fps" {
        $csproj = Join-Path $root "FPSBoosterApp\FPSBoosterApp.csproj"
        $assemblyName = "FPS Booster"
        $outDir = Join-Path $root "..\Cfg2 apps\FPS booster"
    }
}
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# 1) Bump the version
$content = Get-Content $csproj -Raw
if ($content -notmatch "<Version>$([regex]::Escape($Version))</Version>") {
    $content = $content -replace '<Version>[^<]*</Version>', "<Version>$Version</Version>"
    Set-Content -Path $csproj -Value $content -NoNewline -Encoding UTF8
    Write-Host "Bumped <Version> to $Version ($App)"
}

# 2) Publish the single-file exe (native-lib extraction fix is baked into csproj)
$projDir = Split-Path -Parent $csproj
Push-Location $projDir
try {
    dotnet publish -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:AssemblyName="$assemblyName" -o $outDir | Out-Null
} finally {
    Pop-Location
}

$exe = Join-Path $outDir "$assemblyName.exe"
if (-not (Test-Path $exe)) { throw "Publish failed: exe not found at $exe" }
Write-Host "Built: $exe"

# 3) Ship it
if ($Repo) {
    $notes = if ($Notes) { $Notes } else { "Version $Version of $assemblyName." }
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        Write-Host "GitHub CLI not found - publish the release manually:" -ForegroundColor Yellow
        Write-Host "  gh release create v$Version `"$exe`" --repo $Repo --title `"v$Version`" --notes `"$notes`""
        exit 0
    }
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = "SilentlyContinue"
    gh release view "v$Version" --repo $Repo --json tagName 2>$null | Out-Null
    $exists = ($LASTEXITCODE -eq 0)
    if ($exists) {
        Write-Host "Release v$Version already exists - updating its asset."
        gh release upload "v$Version" $exe --repo $Repo --clobber 2>$null
        $ok = ($LASTEXITCODE -eq 0)
    } else {
        Write-Host "Creating GitHub release v$Version ..."
        gh release create "v$Version" $exe --repo $Repo --title "v$Version" --notes $notes 2>$null
        $ok = ($LASTEXITCODE -eq 0)
    }
    $ErrorActionPreference = $prevEap
    if ($ok) {
        Write-Host "Published: https://github.com/$Repo/releases/tag/v$Version" -ForegroundColor Green
        Write-Host "Users' apps will detect v$Version on their next launch."
    } else {
        Write-Host "Release step failed (tag name collision?). Run it manually:" -ForegroundColor Yellow
        Write-Host "  gh release create v$Version `"$exe`" --repo $Repo --title `"v$Version`" --notes `"$notes`""
    }
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
