<#
    Run the checks.

        .\test.ps1              everything that needs nothing but the repo
        .\test.ps1 -Audio       also open the sound card and play a chord
        .\test.ps1 -Images D:\  also play every programme in a disk library

    The three of them answer different questions and none of them substitutes for another.
    EngineCheck is the maths, against the numbers the web version prints for the same
    inputs. LoopClickCheck is the loop join, measured as a step against the steps the
    waveform makes on its own. PatchCheck is real Akai programmes, which is where the
    surprises live. AudioCheck is the only one that can tell you whether the WASAPI vtable
    is in the right order, because that is not a question a buffer can answer.
#>
param(
    [switch]$Audio,
    [string]$Images = "",
    [switch]$KeepGoing
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not (Test-Path $csc)) { Write-Error "No .NET Framework 4.0 compiler found." }

$engine = Get-ChildItem (Join-Path $root "AkaiS950Engine\*.cs") | ForEach-Object { $_.FullName }
$list   = Get-ChildItem (Join-Path $root "AkaiS950List\*.cs") |
          Where-Object { $_.Name -ne "Program.cs" } | ForEach-Object { $_.FullName }
$studio = Join-Path $root "AkaiS950Studio\Instrument.cs"

$failed = @()

function Run-Check($name, $sources, $arguments) {
    Write-Host ""
    Write-Host ("--- $name " + ("-" * [math]::Max(0, 60 - $name.Length))) -ForegroundColor Cyan

    $exe = Join-Path $env:TEMP "$name.exe"
    & $csc /nologo /unsafe /target:exe /main:$name /out:$exe `
        /r:System.dll /r:System.Core.dll /r:System.Drawing.dll $sources
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  did not compile" -ForegroundColor Red
        $script:failed += $name
        return
    }

    if ($arguments) { & $exe $arguments } else { & $exe }
    if ($LASTEXITCODE -ne 0) { $script:failed += $name }
}

Run-Check "EngineCheck" (@(Join-Path $root "AkaiS950Tests\EngineCheck.cs") + $engine) $null
Run-Check "LoopClickCheck" (@(Join-Path $root "AkaiS950Tests\LoopClickCheck.cs") + $engine) $null

if ($Images -ne "") {
    Run-Check "PatchCheck" `
        (@(Join-Path $root "AkaiS950Tests\PatchCheck.cs") + $engine + $studio + $list) $Images
} else {
    Write-Host ""
    Write-Host "(PatchCheck skipped - pass -Images <folder of .hfe> to play the library)"
}

if ($Audio) {
    Run-Check "AudioCheck" (@(Join-Path $root "AkaiS950Tests\AudioCheck.cs") + $engine) $null
} else {
    Write-Host ""
    Write-Host "(AudioCheck skipped - pass -Audio to open the sound card)"
}

Write-Host ""
if ($failed.Count -eq 0) {
    Write-Host "all checks passed" -ForegroundColor Green
    exit 0
}

Write-Host ("failed: " + ($failed -join ", ")) -ForegroundColor Red
exit 1
