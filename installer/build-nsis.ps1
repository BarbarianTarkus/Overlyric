<#
.SYNOPSIS
    Builds the Overlyric per-user NSIS installer from the published application payload.

.DESCRIPTION
    The NSIS sibling of installer\build-installer.ps1 (which builds the MSI). From the repository root:

        pwsh installer\build-nsis.ps1
        pwsh installer\build-nsis.ps1 -SkipPublish
        pwsh installer\build-nsis.ps1 -Version 0.0.9 -OutputDir dist

    Steps:
      1. read the product version from src\Overlyric.csproj (or -Version),
      2. publish the app for win-x64 unless -SkipPublish,
      3. verify the payload (Overlyric.exe present, *.pdb never shipped, no subdirectories),
      4. compile installer\Overlyric.nsi with makensis,
      5. copy the setup to dist\Overlyric-<Version>-x64-setup.exe, optionally code-sign it,
      6. print the SHA256 and the exact install/uninstall command lines.

    Fails loudly (non-zero exit + clear message) when a prerequisite is missing.

.PARAMETER Configuration
    Build configuration used for publish. Default: Release.

.PARAMETER Version
    Override the product version (X.Y.Z). Default: <Version> from the app csproj.

.PARAMETER SkipPublish
    Reuse the existing publish output instead of running dotnet publish.

.PARAMETER OutputDir
    Where the final setup is written. Default: <repo>\dist.

.PARAMETER PublishDir
    Override the publish output directory (default: the csproj's win-x64 publish folder).

.PARAMETER MakensisPath
    makensis.exe to use. Default: PATH, then the usual NSIS install folders.

.PARAMETER CertificatePath
    PFX file used to code-sign the setup with signtool. Signing is skipped when not supplied.

.PARAMETER CertificatePassword
    Password for the PFX supplied with -CertificatePath.

.PARAMETER NoSign
    Never sign, even if -CertificatePath is supplied.
#>
#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$Version,
    [switch]$SkipPublish,
    [string]$OutputDir,
    [string]$PublishDir,
    [string]$MakensisPath,
    [string]$CertificatePath,
    [string]$CertificatePassword,
    [switch]$NoSign
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot   = Split-Path -Parent $PSScriptRoot
$AppProject = Join-Path $RepoRoot 'src\Overlyric.csproj'
$NsiFile    = Join-Path $PSScriptRoot 'Overlyric.nsi'
$IconPath   = Join-Path $RepoRoot 'src\Assets\Overlyric.ico'

function Write-Step([string]$Text) { Write-Host "`n==> $Text" -ForegroundColor Cyan }
function Fail([string]$Text) {
    Write-Host "`nERROR: $Text" -ForegroundColor Red
    exit 1
}

Write-Host 'Overlyric NSIS installer build' -ForegroundColor Green
Write-Host "  repo     : $RepoRoot"
Write-Host "  config   : $Configuration"

# ---------------------------------------------------------------- prerequisites
Write-Step 'Checking prerequisites'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Fail 'dotnet was not found on PATH. Install the .NET SDK (10.x) and retry.'
}
if (-not (Test-Path -LiteralPath $AppProject)) {
    Fail "Application project not found: $AppProject"
}
if (-not (Test-Path -LiteralPath $NsiFile)) {
    Fail "NSIS script not found: $NsiFile"
}
if (-not (Test-Path -LiteralPath $IconPath)) {
    Fail "Product icon not found: $IconPath"
}

[xml]$csproj = Get-Content -LiteralPath $AppProject

# ---------------------------------------------------------------- version
Write-Step 'Resolving product version'

if (-not $Version) {
    $Version = ($csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
}
if (-not $Version) {
    Fail "No <Version> found in $AppProject and no -Version supplied."
}
$Version = ([string]$Version).Trim()
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    Fail "Version '$Version' is not a valid X.Y.Z version (for example 0.1.0)."
}
Write-Host "  version  : $Version"

$tfm = ($csproj.Project.PropertyGroup.TargetFramework | Where-Object { $_ } | Select-Object -First 1)
if (-not $tfm) { Fail "No <TargetFramework> found in $AppProject." }

if (-not $PublishDir) {
    $PublishDir = Join-Path $RepoRoot "src\bin\$Configuration\$tfm\win-x64\publish"
}
$PublishDir = [System.IO.Path]::GetFullPath($PublishDir)

# ---------------------------------------------------------------- publish
Write-Step "Publishing application ($Configuration, win-x64)"

if ($SkipPublish) {
    Write-Host '  skipped (-SkipPublish); using existing publish output'
} else {
    & dotnet publish $AppProject -c $Configuration --nologo "-p:Version=$Version"
    if ($LASTEXITCODE -ne 0) {
        Fail "dotnet publish failed with exit code $LASTEXITCODE."
    }
}

# ---------------------------------------------------------------- payload check
Write-Step 'Verifying payload'

if (-not (Test-Path -LiteralPath $PublishDir)) {
    Fail "Publish directory not found: $PublishDir. Run without -SkipPublish or pass -PublishDir."
}

$exePath = Join-Path $PublishDir 'Overlyric.exe'
if (-not (Test-Path -LiteralPath $exePath)) {
    Fail "Publish output does not contain Overlyric.exe: $PublishDir"
}
if ((Get-Item -LiteralPath $exePath).Length -lt 1MB) {
    Fail "Overlyric.exe looks truncated ($((Get-Item -LiteralPath $exePath).Length) bytes)."
}

$allFiles = @(Get-ChildItem -LiteralPath $PublishDir -File)
$nested   = @(Get-ChildItem -LiteralPath $PublishDir -Directory)
$payload  = @($allFiles | Where-Object { $_.Extension -ne '.pdb' })
$debug    = @($allFiles | Where-Object { $_.Extension -eq '.pdb' })

if ($nested.Count -gt 0) {
    Fail ('The publish directory contains subdirectories, which installer\Overlyric.nsi does not copy yet: ' +
          ($nested.Name -join ', ') + '. Extend the script (File /r) or flatten the publish output.')
}
if ($payload.Count -eq 0) {
    Fail "No payload files found in $PublishDir."
}
if (-not ($payload | Where-Object { $_.Name -eq 'Overlyric.exe' })) {
    Fail 'The payload filter dropped Overlyric.exe; refusing to build an installer without it.'
}

foreach ($f in $payload) { Write-Host ('  payload  : {0,-24} {1,10:N0} bytes' -f $f.Name, $f.Length) }
foreach ($f in $debug)   { Write-Host ('  excluded : {0,-24} {1,10:N0} bytes (*.pdb)' -f $f.Name, $f.Length) }

# ---------------------------------------------------------------- makensis
Write-Step 'Locating makensis'

$candidates = @()
if ($MakensisPath) {
    $candidates += $MakensisPath
} else {
    $onPath = Get-Command makensis -ErrorAction SilentlyContinue
    if ($onPath) { $candidates += $onPath.Source }
    $candidates += 'C:\Program Files (x86)\NSIS\makensis.exe'
    $candidates += 'C:\Program Files\NSIS\makensis.exe'
}

$makensis = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
if (-not $makensis) {
    Fail 'makensis.exe was not found. Install NSIS 3.x (winget install --id NSIS.NSIS -e) or pass -MakensisPath.'
}
$nsisVersion = ((& $makensis /VERSION).Trim() -replace '^[vV]', '')
Write-Host "  makensis: $makensis (v$nsisVersion)"

# ---------------------------------------------------------------- compile
Write-Step 'Compiling the setup executable'

$stagingDir = Join-Path $PSScriptRoot 'bin'
if (-not (Test-Path -LiteralPath $stagingDir)) {
    New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null
}

$stagedSetup = Join-Path $stagingDir "Overlyric-$Version-x64-setup.exe"
if (Test-Path -LiteralPath $stagedSetup) { Remove-Item -LiteralPath $stagedSetup -Force }

& $makensis /V2 "/DAPP_VERSION=$Version" "/DPAYLOAD_DIR=$PublishDir" "/DOUT_FILE=$stagedSetup" "/DAPP_ICON=$IconPath" $NsiFile
if ($LASTEXITCODE -ne 0) {
    Fail "makensis failed with exit code $LASTEXITCODE."
}
if (-not (Test-Path -LiteralPath $stagedSetup)) {
    Fail "makensis reported success but $stagedSetup does not exist."
}

# ---------------------------------------------------------------- publish artifact
Write-Step 'Writing the distributable artifact'

if (-not $OutputDir) { $OutputDir = Join-Path $RepoRoot 'dist' }
if (-not (Test-Path -LiteralPath $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}
$OutputDir = (Resolve-Path -LiteralPath $OutputDir).Path
$setupPath = Join-Path $OutputDir "Overlyric-$Version-x64-setup.exe"
Copy-Item -LiteralPath $stagedSetup -Destination $setupPath -Force
Write-Host "  artifact : $setupPath"

# ---------------------------------------------------------------- optional code signing
Write-Step 'Code signing'

if ($NoSign) {
    Write-Host '  skipped (-NoSign)'
} elseif (-not $CertificatePath) {
    Write-Host '  skipped (no -CertificatePath supplied)'
} else {
    if (-not (Test-Path -LiteralPath $CertificatePath)) {
        Fail "Certificate not found: $CertificatePath"
    }

    $signtool = (Get-Command signtool.exe -ErrorAction SilentlyContinue).Source
    if (-not $signtool) {
        $kits = @(Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending)
        foreach ($kit in $kits) {
            $candidate = Join-Path $kit.FullName 'x64\signtool.exe'
            if (Test-Path -LiteralPath $candidate) { $signtool = $candidate; break }
        }
    }
    if (-not $signtool) {
        Fail 'signtool.exe was not found. Install the Windows SDK or sign the artifact yourself.'
    }

    $signArgs = @('sign', '/fd', 'sha256', '/f', $CertificatePath)
    if ($CertificatePassword) { $signArgs += @('/p', $CertificatePassword) }
    $signArgs += @('/tr', 'http://timestamp.digicert.com', '/td', 'sha256', $setupPath)

    & $signtool @signArgs
    if ($LASTEXITCODE -ne 0) {
        Fail "signtool failed with exit code $LASTEXITCODE."
    }

    $sig = Get-AuthenticodeSignature -LiteralPath $setupPath
    Write-Host "  signed   : $($sig.Status) - $($sig.SignerCertificate.Subject)"
}

# ---------------------------------------------------------------- summary
Write-Step 'Done'

$hash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
$sizeMb = [Math]::Round((Get-Item -LiteralPath $setupPath).Length / 1MB, 2)

Write-Host ''
Write-Host "  setup    : $setupPath"
Write-Host "  size     : $sizeMb MB"
Write-Host "  sha256   : $hash"
Write-Host ''
Write-Host '  install  :' -ForegroundColor Gray
Write-Host "    & '$setupPath'" -ForegroundColor Gray
Write-Host "    & '$setupPath' /S" -ForegroundColor Gray
Write-Host "    & '$setupPath' /S /D=C:\Tools\Overlyric" -ForegroundColor Gray
Write-Host '  uninstall:' -ForegroundColor Gray
Write-Host "    & `"$env:LOCALAPPDATA\Programs\Overlyric\Uninstall.exe`" /S" -ForegroundColor Gray
Write-Host "    & `"$env:LOCALAPPDATA\Programs\Overlyric\Uninstall.exe`" /S /REMOVEDATA" -ForegroundColor Gray
