# ============================================================
#  generate_keys.ps1 - create premium license keys for FPS Booster
#
#  Usage:
#    powershell -ExecutionPolicy Bypass -File generate_keys.ps1 [-Count 5] [-KeysFile ..\Cfg2 apps\FPS booster\keys.txt]
#
#  Keys are appended to keys.txt next to the exe. A key activates when a user
#  enters it in the app; the app then writes the device HWID + IP next to the
#  key in this file, binding the key to that one device + IP.
#
#  To revoke a key, just delete its line (or the whole file).
# ============================================================
param(
    [int]$Count = 5,
    [string]$KeysFile = "..\Cfg2 apps\FPS booster\keys.txt"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$path = Join-Path $root $KeysFile
New-Item -ItemType Directory -Force -Path (Split-Path $path) | Out-Null

# No I/O/0/1 - avoids keys that look alike.
$chars = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789'
$rng = New-Object System.Security.Cryptography.RNGCryptoServiceProvider
function New-Key {
    $groups = @()
    for ($g = 0; $g -lt 4; $g++) {
        $bytes = New-Object byte[] 4
        $rng.GetBytes($bytes)
        $sb = New-Object System.Text.StringBuilder
        for ($i = 0; $i -lt 4; $i++) {
            [void]$sb.Append($chars[$bytes[$i] % $chars.Length])
        }
        $groups += $sb.ToString()
    }
    return ($groups -join '-')
}

$lines = if (Test-Path $path) { Get-Content $path } else { @() }
$added = @()
for ($i = 0; $i -lt $Count; $i++) {
    $key = New-Key
    $added += $key
}
# WriteAllLines guarantees one key per line, no matter what.
[System.IO.File]::WriteAllLines($path, (@($lines) + @($added)))
Write-Host "Wrote $Count key(s) to $path"
Write-Host ""
$added | ForEach-Object { Write-Host $_ }
Write-Host ""
Write-Host "Share these keys with buyers. The first device that activates a key owns it."
