<#
    Hold the C++ disk reader to the C# one, on a real disk.

        .\crosscheck.ps1 E:\akai_DSKA0000_akai.hfe
        .\crosscheck.ps1 E:\                        # every image in a folder

    WHY IT WORKS THIS WAY

    Every other part of this port is checked against numbers generated from the C# and
    committed - Plugin/Tests/Reference.h. That cannot be done here: a reference for the disk
    reader would be a transcript of somebody else's disk, with their sample names and a
    hash of their audio in it, and those stay out of a public repository.

    So the reference is made at the moment of checking and thrown away. Both readers dump
    the same image to the same format, and the two are diffed. Nothing is kept.

    Both readers take the original file, .hfe or .img alike. For an .hfe that means the two
    MFM decoders are compared as well - and the dump carries the bad-sector and
    missing-sector counts, so they have to agree about what could NOT be read too, which is
    the half of a thirty-year-old floppy that is easy to get quietly wrong.
#>
param(
    [Parameter(Mandatory = $true)][string]$Image,
    [switch]$KeepFiles
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Split-Path -Parent $root

$work = Join-Path $env:TEMP "s950crosscheck"
New-Item -ItemType Directory -Force -Path $work | Out-Null

# ---------------------------------------------------------------- the C# dumper

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { Write-Error "No .NET Framework 4.0 compiler found." }

$csDump = Join-Path $work "DiskDump.exe"
$csSrc  = @(Join-Path $repo "AkaiS950Tests\DiskDump.cs")
$csSrc += Get-ChildItem (Join-Path $repo "AkaiS950List\*.cs") |
          Where-Object { $_.Name -ne "Program.cs" } | ForEach-Object { $_.FullName }

& $csc /nologo /target:exe /main:DiskDump /out:$csDump /r:System.dll /r:System.Core.dll $csSrc
if ($LASTEXITCODE -ne 0) { Write-Error "the C# dumper did not build" }

# --------------------------------------------------------------- the C++ dumper

$cppDump = Join-Path $work "DiskDumpCpp.exe"

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
                     -property installationPath
if (-not $vsPath) { Write-Error "No Visual Studio C++ tools found." }

$vcvars = Join-Path $vsPath "VC\Auxiliary\Build\vcvars64.bat"
$include = Join-Path $root "Source\S950"

cmd /c "call `"$vcvars`" >nul 2>&1 && cd /d `"$work`" && cl /nologo /std:c++17 /EHsc /W4 /I `"$include`" `"$root\Tests\DiskDump.cpp`" `"$include\Disk.cpp`" `"$include\Hfe.cpp`" /Fe:`"$cppDump`"" | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Error "the C++ dumper did not build" }

# ------------------------------------------------------------------- the images

$images = @()
if (Test-Path $Image -PathType Container) {
    $images += Get-ChildItem $Image -Filter *.hfe -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName }
    $images += Get-ChildItem $Image -Filter *.img -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName }
} else {
    $images = @($Image)
}

if ($images.Count -eq 0) { Write-Error "no images under $Image" }

Write-Host ""
Write-Host "  $($images.Count) image(s), two readers, one diff each"
Write-Host ""

$same = 0
$differ = @()

foreach ($img in $images) {
    $name = [System.IO.Path]::GetFileNameWithoutExtension($img)
    $a    = Join-Path $work "$name.cs.txt"
    $b    = Join-Path $work "$name.cpp.txt"

    & $csDump $img > $a 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Host ("  {0,-34} the C# reader would not open it" -f $name) -ForegroundColor DarkYellow
        continue
    }

    & $cppDump $img > $b 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Host ("  {0,-34} the C++ reader would not open it" -f $name) -ForegroundColor Red
        $differ += $name
        continue
    }

    $diff = Compare-Object (Get-Content $a) (Get-Content $b)

    if ($null -eq $diff) {
        $same++
        $lines = (Get-Content $a | Measure-Object -Line).Lines
        Write-Host ("  {0,-34} identical, {1} lines" -f $name, $lines) -ForegroundColor Green
    }
    else {
        $differ += $name
        Write-Host ("  {0,-34} {1} line(s) differ" -f $name, $diff.Count) -ForegroundColor Red
        $diff | Select-Object -First 6 | ForEach-Object {
            $side = if ($_.SideIndicator -eq "<=") { "C#  " } else { "C++ " }
            Write-Host ("      {0}{1}" -f $side, $_.InputObject)
        }
    }
}

Write-Host ""
if ($differ.Count -eq 0) {
    Write-Host "  the two readers agree on all $same" -ForegroundColor Green
} else {
    Write-Host ("  {0} agree, {1} differ: {2}" -f $same, $differ.Count, ($differ -join ", ")) -ForegroundColor Red
}
Write-Host ""

if (-not $KeepFiles) {
    # The converted images and the dumps are somebody else's audio and somebody else's
    # sample names. They were made to be compared and there is no reason to keep them.
    Remove-Item (Join-Path $work "*.img") -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $work "*.txt") -Force -ErrorAction SilentlyContinue
}

exit $(if ($differ.Count -eq 0) { 0 } else { 1 })
