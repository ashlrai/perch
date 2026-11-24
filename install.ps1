# Builds notchbar + nb with Native AOT, installs to %LOCALAPPDATA%\Programs\notchbar,
# adds that folder to your user PATH (so `nb` works everywhere) and starts the notch.
$ErrorActionPreference = 'Stop'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\notchbar'

# The AOT linker locates MSVC via vswhere, which isn't on PATH by default.
$vsInstaller = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if (Test-Path $vsInstaller) { $env:PATH += ";$vsInstaller" }

Get-Process notchbar -ErrorAction SilentlyContinue | Stop-Process
foreach ($p in 'src\NotchBar', 'src\Nb') {
    dotnet publish (Join-Path $PSScriptRoot $p) -c Release -o $dest --nologo -v quiet
    if ($LASTEXITCODE) { throw "build failed: $p (needs .NET 9 SDK + VS 'Desktop development with C++')" }
}

# notchbar-tap.dll clears the taskbar's own background (see src\TaskbarTap). Explorer keeps the old copy
# loaded, so it can't be overwritten: move it aside (allowed while loaded) and clean up old ones later.
$tmp = Join-Path $env:TEMP 'notchbar-tap-build'
& (Join-Path $PSScriptRoot 'src\TaskbarTap\build.ps1') -out $tmp
Get-ChildItem $dest -Filter 'notchbar-tap.old-*.dll' -ErrorAction SilentlyContinue | Remove-Item -ErrorAction SilentlyContinue
$tap = Join-Path $dest 'notchbar-tap.dll'
if (Test-Path $tap) {
    try { Remove-Item $tap -ErrorAction Stop }
    catch { Rename-Item $tap ("notchbar-tap.old-{0}.dll" -f [DateTime]::Now.Ticks) }
}
Copy-Item (Join-Path $tmp 'notchbar-tap.dll') $tap

$userPath = [Environment]::GetEnvironmentVariable('PATH', 'User')
if (($userPath -split ';') -notcontains $dest) {
    [Environment]::SetEnvironmentVariable('PATH', "$userPath;$dest", 'User')
    Write-Host "Added $dest to your PATH (open a new terminal to use nb)."
}

Start-Process (Join-Path $dest 'notchbar.exe')
Write-Host "notchbar is running. Right-click the notch for 'Start with Windows'."
Write-Host "Connect Claude Code:  nb hooks"
