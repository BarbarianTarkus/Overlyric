<#
.SYNOPSIS
    Collects line coverage from the probe programs into one Cobertura file for SonarQube.

.DESCRIPTION
    probe\ holds console programs with hand-written checks, not a test framework, so
    "dotnet test --collect" has nothing to run. This script wraps the ones that need no desktop
    session, media player or AOT publish in "dotnet-coverage collect", merges the per-probe reports
    into coverage\probes.xml, and prints how much of src\Overlyric that covers.

    SonarQube reads the merged file through sonar.cs.cobertura.reportsPaths in
    SonarQube.Analysis.xml, so run this before the scanner begins:

        pwsh tools\collect-coverage.ps1
        dotnet-sonarscanner begin /k:overlyric /v:<version> "/s:<repo>\SonarQube.Analysis.xml" ...
        dotnet build src\Overlyric\Overlyric.csproj
        dotnet-sonarscanner end ...

    The probes not listed below need a desktop session, hardware or an AOT publish and are
    deliberately left out: ApiBudget, FullscreenProbe, NowPlaying, SmtcAot, SmtcProbe, ToastProbe,
    VorticeAot.

.PARAMETER OutputDir
    Where the reports are written. Default: <repo>\coverage.

.PARAMETER Probe
    Collect only these probes instead of the default set.

.EXAMPLE
    pwsh tools\collect-coverage.ps1 -Probe LyricsTests, ClockTests
#>
[CmdletBinding()]
param(
    [string]$OutputDir,
    [string[]]$Probe
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDir) { $OutputDir = Join-Path $RepoRoot 'coverage' }

$defaultProbes = @(
    'ClockTests',
    'TimingTests',
    'InfoTests',
    'KaraokeTests',
    'LocalLyricsTests',
    'LyricEditTests',
    'PresenceTests',
    'LyricWindowsTests',
    'LyricsTests',
    'ResilienceTests',
    'ShortcutFeedbackTests',
    'ShortcutsTests',
    'SourcePreferenceTests',
    'ThemeTests'
)
$probes = if ($Probe) { $Probe } else { $defaultProbes }

if (-not (Get-Command dotnet-coverage -ErrorAction SilentlyContinue)) {
    throw 'dotnet-coverage was not found. Install it once with: dotnet tool install --global dotnet-coverage'
}

$rawDir = Join-Path $OutputDir 'raw'
if (Test-Path -LiteralPath $rawDir) { Remove-Item -LiteralPath $rawDir -Recurse -Force }
New-Item -ItemType Directory -Path $rawDir -Force | Out-Null

$reports = [System.Collections.Generic.List[string]]::new()
$failed = [System.Collections.Generic.List[string]]::new()

foreach ($name in $probes) {
    $project = Join-Path $RepoRoot "probe\$name\$name.csproj"
    if (-not (Test-Path -LiteralPath $project)) {
        Write-Warning "no probe project for '$name' at $project"
        continue
    }

    Write-Host "==> $name" -ForegroundColor Cyan
    $report = Join-Path $rawDir "$name.xml"
    $command = 'dotnet run --project "' + $project + '" -c Release --nologo'
    & dotnet-coverage collect -f cobertura -o $report $command

    if ($LASTEXITCODE -ne 0) {
        $failed.Add($name)
        Write-Warning "$name exited with code $LASTEXITCODE; its coverage is still merged."
    }

    if (Test-Path -LiteralPath $report) { $reports.Add($report) }
}

if ($reports.Count -eq 0) { throw 'No coverage report was produced.' }

$merged = Join-Path $OutputDir 'probes.xml'
& dotnet-coverage merge $reports -f cobertura -o $merged
if ($LASTEXITCODE -ne 0) { throw "dotnet-coverage merge failed with code $LASTEXITCODE." }

# Sonar counts the whole project; this counts what the probes compile, so the two numbers differ.
[xml]$doc = Get-Content -LiteralPath $merged
$seen = @{}
foreach ($class in $doc.SelectNodes('//class[contains(@filename, "\src\Overlyric\")]')) {
    $file = $class.filename
    foreach ($line in $class.SelectNodes('.//line')) {
        $key = "$file|$($line.number)"
        $hits = [int]$line.hits
        if (-not $seen.ContainsKey($key) -or $seen[$key] -lt $hits) { $seen[$key] = $hits }
    }
}

$covered = @($seen.Values | Where-Object { $_ -gt 0 }).Count
$total = $seen.Count
Write-Host ''
Write-Host ("  report    : {0}" -f $merged)
Write-Host ("  source    : {0:N0}/{1:N0} lines covered of the files the probes compile ({2:P1})" -f $covered, $total, $(if ($total) { $covered / $total } else { 0 }))
Write-Host ("  probes    : {0} collected, {1} failed ({2})" -f $reports.Count, $failed.Count, $(if ($failed.Count) { $failed -join ', ' } else { 'none' }))

if ($failed.Count -gt 0) { exit 1 }
exit 0
