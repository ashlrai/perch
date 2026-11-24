# Builds notchbar-tap.dll (x64, static CRT) with MSVC. Usage: build.ps1 -out <dir>
param([Parameter(Mandatory)][string]$out)
$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw "MSVC not found (install Visual Studio's 'Desktop development with C++')" }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
$env:PATH += ";" + (Split-Path $vswhere)   # vcvars64.bat runs vswhere too
New-Item -ItemType Directory -Force $out | Out-Null
$obj = Join-Path $env:TEMP 'notchbar-tap-obj'
New-Item -ItemType Directory -Force $obj | Out-Null
$src = Join-Path $PSScriptRoot 'tap.cpp'; $def = Join-Path $PSScriptRoot 'tap.def'
$dll = Join-Path $out 'notchbar-tap.dll'
$cmd = "call `"$vcvars`" >nul && cl /nologo /O1 /EHsc /std:c++17 /MT /W3 /LD /Fo`"$obj\\`" `"$src`" /link /DEF:`"$def`" /OUT:`"$dll`" /NOIMPLIB /NOEXP runtimeobject.lib ole32.lib"
$ErrorActionPreference = 'Continue'   # cl writes progress to stderr
cmd /c $cmd 2>&1 | Where-Object { $_ -notmatch '^tap\.cpp$' }
if ($LASTEXITCODE) { throw "notchbar-tap.dll build failed" }
Remove-Item (Join-Path $out 'notchbar-tap.lib'), (Join-Path $out 'notchbar-tap.exp') -ErrorAction SilentlyContinue
