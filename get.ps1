# Installs (or updates) perch from the latest GitHub release:
#   irm https://raw.githubusercontent.com/ashlrai/perch/main/get.ps1 | iex
# Puts perchd.exe and perch.exe in %LOCALAPPDATA%\Programs\perch, adds that folder to your user PATH and starts the
# notch. Nothing needs admin rights. To remove perch: quit it (right-click the notch), delete that folder and
# %APPDATA%\perch, and take the folder off your PATH.
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Version.Build -lt 19041) { throw 'perch needs Windows 10 (2004) or later; Windows 11 is recommended.' }

$dest = Join-Path $env:LOCALAPPDATA 'Programs\perch'
$rel = Invoke-RestMethod 'https://api.github.com/repos/ashlrai/perch/releases/latest' -Headers @{ 'User-Agent' = 'perch-installer' }
$asset = $rel.assets | Where-Object name -eq 'perch-win-x64.zip' | Select-Object -First 1
if (-not $asset) { throw "The latest release ($($rel.tag_name)) has no perch-win-x64.zip." }
$zip = Join-Path $env:TEMP "perch-$($rel.tag_name).zip"
Write-Host "Downloading perch $($rel.tag_name)..."
Invoke-WebRequest $asset.browser_download_url -OutFile $zip -UseBasicParsing

Get-Process perchd -ErrorAction SilentlyContinue | Stop-Process
Start-Sleep -Milliseconds 500
New-Item -ItemType Directory -Force $dest | Out-Null
# Explorer may still have the taskbar helper loaded: it can be moved aside, not overwritten
Get-ChildItem $dest -Filter 'perch-tap.old-*.dll' -ErrorAction SilentlyContinue | Remove-Item -ErrorAction SilentlyContinue
$tap = Join-Path $dest 'perch-tap.dll'
if (Test-Path $tap) {
    try { Remove-Item $tap -ErrorAction Stop }
    catch { Rename-Item $tap ("perch-tap.old-{0}.dll" -f [DateTime]::Now.Ticks) }
}
Expand-Archive $zip $dest -Force
Remove-Item $zip

$userPath = [Environment]::GetEnvironmentVariable('PATH', 'User')
if (($userPath -split ';') -notcontains $dest) {
    [Environment]::SetEnvironmentVariable('PATH', ($userPath.TrimEnd(';') + ";$dest").TrimStart(';'), 'User')
    Write-Host "Added $dest to your PATH (open a new terminal to use perch)."
}

Start-Process (Join-Path $dest 'perchd.exe')
Write-Host "perch $($rel.tag_name) is running. Right-click the notch for settings; run 'perch hooks' to connect Claude Code."
