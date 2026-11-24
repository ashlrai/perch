# Builds perch with Native AOT: perchd.exe (the notch) and perch.exe (the command line). Installs them to
# %LOCALAPPDATA%\Programs\perch, adds that folder to your user PATH (so `perch` works everywhere) and starts the notch.
$ErrorActionPreference = 'Stop'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\perch'

# The AOT linker locates MSVC via vswhere, which isn't on PATH by default.
$vsInstaller = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if (Test-Path $vsInstaller) { $env:PATH += ";$vsInstaller" }

Get-Process perchd, notchbar -ErrorAction SilentlyContinue | Stop-Process
foreach ($p in 'src\Perchd', 'src\Perch') {
    dotnet publish (Join-Path $PSScriptRoot $p) -c Release -o $dest --nologo -v quiet
    if ($LASTEXITCODE) { throw "build failed: $p (needs .NET 9 SDK + VS 'Desktop development with C++')" }
}

# perch-tap.dll clears the taskbar's own background (see src\TaskbarTap). Explorer keeps the old copy
# loaded, so it can't be overwritten: move it aside (allowed while loaded) and clean up old ones later.
$tmp = Join-Path $env:TEMP 'perch-tap-build'
& (Join-Path $PSScriptRoot 'src\TaskbarTap\build.ps1') -out $tmp
Get-ChildItem $dest -Filter 'perch-tap.old-*.dll' -ErrorAction SilentlyContinue | Remove-Item -ErrorAction SilentlyContinue
$tap = Join-Path $dest 'perch-tap.dll'
if (Test-Path $tap) {
    try { Remove-Item $tap -ErrorAction Stop }
    catch { Rename-Item $tap ("perch-tap.old-{0}.dll" -f [DateTime]::Now.Ticks) }
}
Copy-Item (Join-Path $tmp 'perch-tap.dll') $tap

$userPath = [Environment]::GetEnvironmentVariable('PATH', 'User')
$parts = @($userPath -split ';' | Where-Object { $_ })

# perch used to be called notchbar: take the old install off PATH, out of autostart, and off disk
$old = Join-Path $env:LOCALAPPDATA 'Programs\notchbar'
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$wasAuto = $null -ne (Get-ItemProperty $run -Name notchbar -ErrorAction SilentlyContinue)
if ($wasAuto) { Remove-ItemProperty $run -Name notchbar; Set-ItemProperty $run -Name perch -Value "`"$(Join-Path $dest 'perchd.exe')`"" }
$parts = @($parts | Where-Object { $_ -ne $old })
if (Test-Path $old) { Remove-Item $old -Recurse -Force -ErrorAction SilentlyContinue }   # a tap DLL still loaded in Explorer stays until it restarts

if ($parts -notcontains $dest) { $parts += $dest; Write-Host "Added $dest to your PATH (open a new terminal to use perch)." }
$newPath = $parts -join ';'
if ($newPath -ne $userPath) { [Environment]::SetEnvironmentVariable('PATH', $newPath, 'User') }

Start-Process (Join-Path $dest 'perchd.exe')
Write-Host "perch is running. Right-click the notch for 'Start with Windows'."
Write-Host "Connect Claude Code:  perch hooks"
