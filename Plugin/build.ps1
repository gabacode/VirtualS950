<#
    Build and run the conformance check.

        .\build.ps1             builds and runs it
        .\build.ps1 -NoRun      builds only

    WHY THIS EXISTS AND NOT JUST "cl"

    cl.exe is never on the PATH. The installer leaves it off on purpose, because the
    compiler needs INCLUDE, LIB and PATH set for one particular target architecture, and
    vcvars64.bat is what sets them. Start menu -> "Developer PowerShell for VS 2026" does
    the same thing; this finds the batch file itself so the build works from any shell,
    including one driven by a script.

    vswhere reports where Visual Studio is rather than a path being written down here,
    because that path carries the edition and the major version in it - C:\Program Files\
    Microsoft Visual Studio\18\Community today - and hard-coding it would break on the
    next upgrade with an error that says nothing useful.
#>
param(
    [switch]$NoRun,
    [string]$Out = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

if ($Out -eq "") { $Out = Join-Path $env:TEMP "s950build" }
New-Item -ItemType Directory -Force -Path $Out | Out-Null

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) {
    Write-Error "No Visual Studio found. Install VS Community with 'Desktop development with C++'."
}

$vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
                     -property installationPath
if (-not $vsPath) {
    Write-Error "Visual Studio is installed but the C++ tools are not. Add the 'Desktop development with C++' workload."
}

$vcvars = Join-Path $vsPath "VC\Auxiliary\Build\vcvars64.bat"
if (-not (Test-Path $vcvars)) { Write-Error "No vcvars64.bat under $vsPath." }

$sources = @(
    (Join-Path $root "Tests\ConformanceCheck.cpp"),
    (Join-Path $root "Source\S950\Voice.cpp"),
    (Join-Path $root "Source\S950\Engine.cpp")
)

$include = Join-Path $root "Source\S950"
$exe = Join-Path $Out "ConformanceCheck.exe"

Write-Host "building $($sources.Count) files -> $exe"

# One cmd session: vcvars sets the environment, and it only lasts for that session.
$quoted = ($sources | ForEach-Object { "`"$_`"" }) -join " "
cmd /c "call `"$vcvars`" >nul 2>&1 && cd /d `"$Out`" && cl /nologo /std:c++17 /EHsc /W4 /I `"$include`" $quoted /Fe:`"$exe`""

if ($LASTEXITCODE -ne 0) { Write-Error "build failed" }

Write-Host "ok  -  $([math]::Round((Get-Item $exe).Length / 1KB)) KB" -ForegroundColor Green

if ($NoRun) { exit 0 }

& $exe
exit $LASTEXITCODE
