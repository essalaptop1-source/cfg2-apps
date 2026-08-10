# ============================================================
#  setup_github.ps1 — one-time GitHub setup for CFG2 apps
#
#  Requirements (run once):
#    1. Install the GitHub CLI:
#         winget install --id GitHub.cli
#    2. Log in:
#         gh auth login
#
#  Then run this script:
#     powershell -ExecutionPolicy Bypass -File setup_github.ps1
#
#  What it does:
#    - creates a PUBLIC repo named cfg2-apps (auto-updates need public access)
#    - pushes this project to it
#    - uploads the current exe as a v<version> GitHub release
#    - sets the repo in the app's settings so it auto-updates from here
# ============================================================
param(
    [string]$RepoName = "cfg2-apps",
    [string]$Repo = ""          # override: "owner/repo" to skip creating a new repo
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# ---- 1. Make sure gh is installed and logged in ----
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Write-Host "GitHub CLI is not installed." -ForegroundColor Red
    Write-Host "Install it with:  winget install --id GitHub.cli"
    Write-Host "Then log in with: gh auth login"
    exit 1
}
gh auth status *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Host "You are not logged in. Run:  gh auth login" -ForegroundColor Red
    exit 1
}
$owner = gh api user --jq .login
Write-Host "Logged in as: $owner" -ForegroundColor Green

if (-not $Repo) { $Repo = "$owner/$RepoName" }

# ---- 2. Create the repo if needed ----
$repoExists = gh repo view $Repo --json name 2>$null
if (-not $repoExists) {
    Write-Host "Creating public repo $Repo ..."
    gh repo create $RepoName --public --source $root --push
    if ($LASTEXITCODE -ne 0) { Write-Host "Repo create/push failed." -ForegroundColor Red; exit 1 }
} else {
    Write-Host "Repo $Repo already exists — pushing."
    Push-Location $root
    try { git push -u origin main } finally { Pop-Location }
}

# ---- 3. Build + publish the current version ----
& (Join-Path $root "publish_update.ps1") -Repo $Repo

$versionLine = (Select-String -Path (Join-Path $root "Configuration2App\Configuration2App.csproj") -Pattern '<Version>([^<]*)</Version>').Matches.Groups[1].Value
$exe = Join-Path $root "..\Cfg2 apps\CFG2 Embed sender.exe"
if (-not (Test-Path $exe)) { Write-Host "Build output missing: $exe" -ForegroundColor Red; exit 1 }

# ---- 4. Upload the exe as a release asset ----
Write-Host "Creating GitHub release v$versionLine ..."
gh release create "v$versionLine" $exe --repo $Repo --title "v$versionLine" --notes "Latest build of CFG2 Embed sender."
if ($LASTEXITCODE -ne 0) { Write-Host "Release create failed (maybe it exists?) — run publish_update.ps1 manually." -ForegroundColor Yellow }

# ---- 5. Point the app at this repo so it auto-updates ----
$settingsPath = Join-Path $env:APPDATA "Kicia\settings.json"
if (Test-Path $settingsPath) {
    $j = Get-Content $settingsPath -Raw | ConvertFrom-Json
    $j | Add-Member -NotePropertyName GitHubRepo -NotePropertyValue $Repo -Force
    $j | ConvertTo-Json -Depth 6 | Set-Content $settingsPath -Encoding UTF8
    Write-Host "App update source set to: $Repo (in Settings → UPDATES)" -ForegroundColor Green
} else {
    Write-Host "No settings.json found yet — set GitHub repo to '$Repo' in the app's Settings → UPDATES." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Done! Your app will now check $Repo for updates."
Write-Host "To ship a new version later:"
Write-Host "  powershell -ExecutionPolicy Bypass -File publish_update.ps1 -Version 1.2.0 -Repo $Repo"
Write-Host "  gh release create v1.2.0 `"$exe`" --repo $Repo"
