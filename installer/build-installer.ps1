<#
.SYNOPSIS
    Builds the Overlyric per-user MSI (WiX Toolset v6) from the published application payload.

.DESCRIPTION
    Single reproducible entry point for the installer. From the repository root:

        pwsh installer\build-installer.ps1
        pwsh installer\build-installer.ps1 -SkipPublish
        pwsh installer\build-installer.ps1 -Version 0.0.9 -OutputDir dist

    Steps:
      1. read the product version from src\Overlyric\Overlyric.csproj (or -Version),
      2. publish the app for win-x64 unless -SkipPublish,
      3. verify the payload (Overlyric.exe present, *.pdb never shipped),
      4. regenerate installer\Payload.wxs from the actual publish directory,
      5. build the MSI with the WiX SDK (NuGet, no global WiX install required),
      6. copy it to dist\Overlyric-<Version>-x64.msi, optionally code-sign it,
      7. print the SHA256 and the exact install/uninstall command lines.

    Fails loudly (non-zero exit + clear message) when a prerequisite is missing.

.PARAMETER Configuration
    Build configuration used for publish and MSI build. Default: Release.

.PARAMETER Version
    Override the product version (X.Y.Z). Default: <Version> from the app csproj.
    The MSI ProductVersion uses this value; the UpgradeCode never changes.

.PARAMETER SkipPublish
    Reuse the existing publish output instead of running dotnet publish.

.PARAMETER OutputDir
    Where the final MSI is written. Default: <repo>\dist.

.PARAMETER PublishDir
    Override the publish output directory (default: the csproj's win-x64 publish folder).

.PARAMETER CertificatePath
    PFX file used to code-sign the MSI with signtool. Signing is skipped when not supplied.

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
    [string]$CertificatePath,
    [string]$CertificatePassword,
    [switch]$NoSign
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot      = Split-Path -Parent $PSScriptRoot
$AppProject    = Join-Path $RepoRoot 'src\Overlyric\Overlyric.csproj'
$WixProject    = Join-Path $PSScriptRoot 'Overlyric.Installer.wixproj'
$WxsFile       = Join-Path $PSScriptRoot 'Overlyric.wxs'
$PayloadWxs    = Join-Path $PSScriptRoot 'Payload.wxs'
$WixVersion    = '6.0.2'

function Write-Step([string]$Text) { Write-Host "`n==> $Text" -ForegroundColor Cyan }
function Fail([string]$Text) {
    Write-Host "`nERROR: $Text" -ForegroundColor Red
    exit 1
}

Write-Host "Overlyric installer build" -ForegroundColor Green
Write-Host "  repo     : $RepoRoot"
Write-Host "  config   : $Configuration"
Write-Host "  WiX      : $WixVersion (WixToolset.Sdk via NuGet)"

# ---------------------------------------------------------------- prerequisites
Write-Step 'Checking prerequisites'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Fail 'dotnet was not found on PATH. Install the .NET SDK (10.x) and retry.'
}
if (-not (Test-Path -LiteralPath $AppProject)) {
    Fail "Application project not found: $AppProject"
}
if (-not (Test-Path -LiteralPath $WixProject)) {
    Fail "WiX project not found: $WixProject"
}
if (-not (Test-Path -LiteralPath $WxsFile)) {
    Fail "WiX authoring not found: $WxsFile"
}
Write-Host "  dotnet   : $(& dotnet --version)"

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
    Fail "Version '$Version' is not a valid MSI ProductVersion. Expected X.Y.Z (for example 0.1.0)."
}
Write-Host "  version  : $Version"

$tfm = ($csproj.Project.PropertyGroup.TargetFramework | Where-Object { $_ } | Select-Object -First 1)
if (-not $tfm) { Fail "No <TargetFramework> found in $AppProject." }

if (-not $PublishDir) {
    $PublishDir = Join-Path $RepoRoot "src\Overlyric\bin\$Configuration\$tfm\win-x64\publish"
}
$PublishDir = [System.IO.Path]::GetFullPath($PublishDir)

# ---------------------------------------------------------------- publish
Write-Step "Publishing application ($Configuration, win-x64)"

if ($SkipPublish) {
    Write-Host '  skipped (-SkipPublish); using existing publish output'
} else {
    # The product version is passed into the build as well as the installer. Without this the
    # binary keeps the <Version> from the csproj while the MSI carries -Version, and the two drift:
    # the executable's own version string - which the lrclib User-Agent is built from - would name
    # a release that was never shipped.
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

$allFiles   = @(Get-ChildItem -LiteralPath $PublishDir -File)
$nested     = @(Get-ChildItem -LiteralPath $PublishDir -Directory)
$payload    = @($allFiles | Where-Object { $_.Extension -ne '.pdb' })
$debugFiles = @($allFiles | Where-Object { $_.Extension -eq '.pdb' })

if ($nested.Count -gt 0) {
    Fail ("The publish directory contains subdirectories, which installer\Overlyric.wxs does not " +
          "model yet: $($nested.Name -join ', '). Extend the authoring (or flatten the publish output).")
}
if ($payload.Count -eq 0) {
    Fail "No payload files found in $PublishDir."
}

Write-Host "  publish  : $PublishDir"
foreach ($f in $payload) { Write-Host ("  payload  : {0,-24} {1,10:N0} bytes" -f $f.Name, $f.Length) }
foreach ($f in $debugFiles) { Write-Host ("  excluded : {0,-24} {1,10:N0} bytes (*.pdb)" -f $f.Name, $f.Length) }

$payloadBytes  = ($payload | Measure-Object -Property Length -Sum).Sum
$estimatedSizeKb = [int][Math]::Ceiling($payloadBytes / 1024.0)
Write-Host "  installed: $([Math]::Round($payloadBytes / 1MB, 2)) MB (EstimatedSize ${estimatedSizeKb} KB)"

# ---------------------------------------------------------------- generated payload fragment
Write-Step 'Generating installer\Payload.wxs from the publish directory'

$extras = @($payload | Where-Object { $_.Name -ne 'Overlyric.exe' })

$sha1 = [System.Security.Cryptography.SHA1]::Create()
function New-StableId([string]$Kind, [string]$RelativePath) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes("Overlyric.Payload/$RelativePath")
    $hash  = $sha1.ComputeHash($bytes)
    $hex   = ([System.BitConverter]::ToString($hash)).Replace('-', '')
    return "$Kind`_$($hex.Substring(0, 12))"
}
function New-StableGuid([string]$RelativePath) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes("Overlyric.Payload/$RelativePath")
    $hash  = $sha1.ComputeHash($bytes)
    $hex   = ([System.BitConverter]::ToString($hash)).Replace('-', '')
    return '{' + $hex.Substring(0, 8) + '-' + $hex.Substring(8, 4) + '-' + $hex.Substring(12, 4) + '-' +
           $hex.Substring(16, 4) + '-' + $hex.Substring(20, 12) + '}'
}

$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
[void]$sb.AppendLine('<!--')
[void]$sb.AppendLine('  GENERATED FILE - do not edit, do not commit.')
[void]$sb.AppendLine('  Written by installer\build-installer.ps1 from the publish output.')
[void]$sb.AppendLine('  Contains every published file except *.pdb and except Overlyric.exe (authored in')
[void]$sb.AppendLine('  Overlyric.wxs so that util:CloseApplication can be attached to it).')
[void]$sb.AppendLine('-->')
[void]$sb.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
[void]$sb.AppendLine('  <Fragment>')
[void]$sb.AppendLine('    <ComponentGroup Id="PayloadExtras" Directory="INSTALLFOLDER">')
foreach ($f in $extras) {
    $rel = $f.Name
    [void]$sb.AppendLine("      <Component Id=""$(New-StableId 'Payload' $rel)"" Directory=""INSTALLFOLDER"" Guid=""$(New-StableGuid $rel)"">")
    [void]$sb.AppendLine("        <File Id=""$(New-StableId 'File' $rel)"" Source=""$([System.Security.SecurityElement]::Escape($f.FullName))"" KeyPath=""yes"" />")
    [void]$sb.AppendLine('      </Component>')
}
[void]$sb.AppendLine('    </ComponentGroup>')
[void]$sb.AppendLine('  </Fragment>')
[void]$sb.AppendLine('</Wix>')
Set-Content -LiteralPath $PayloadWxs -Value $sb.ToString() -Encoding UTF8
Write-Host "  wrote    : $PayloadWxs ($($extras.Count) extra file(s))"

# ---------------------------------------------------------------- build the MSI
Write-Step "Building the MSI with WiX $WixVersion"

& dotnet build $WixProject -c $Configuration --nologo `
    "-p:OverlyricVersion=$Version" `
    "-p:PublishDir=$PublishDir"
if ($LASTEXITCODE -ne 0) {
    Fail "MSI build failed with exit code $LASTEXITCODE."
}

$builtMsi = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'bin') -Recurse -Filter '*.msi' -File -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $builtMsi) {
    Fail "The WiX build reported success but no .msi was found under $(Join-Path $PSScriptRoot 'bin')."
}
Write-Host "  built    : $($builtMsi.FullName)"

# ---------------------------------------------------------------- publish artifact
Write-Step 'Writing the distributable artifact'

if (-not $OutputDir) { $OutputDir = Join-Path $RepoRoot 'dist' }
if (-not (Test-Path -LiteralPath $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}
$OutputDir = (Resolve-Path -LiteralPath $OutputDir).Path
$msiPath = Join-Path $OutputDir "Overlyric-$Version-x64.msi"
Copy-Item -LiteralPath $builtMsi.FullName -Destination $msiPath -Force
Write-Host "  artifact : $msiPath"

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
    $signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if (-not $signtool) {
        $kits = @(
            "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
            "$env:ProgramFiles\Windows Kits\10\bin"
        ) | Where-Object { Test-Path $_ }
        $candidate = foreach ($k in $kits) {
            Get-ChildItem -LiteralPath $k -Recurse -Filter signtool.exe -File -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match '\\x64\\' } | Select-Object -Last 1
        }
        $signtool = $candidate | Select-Object -First 1
    }
    if (-not $signtool) {
        Fail 'signtool.exe not found. Install the Windows SDK or omit -CertificatePath.'
    }
    $signtoolPath = if ($signtool -is [System.Management.Automation.CommandInfo]) { $signtool.Source } else { $signtool.FullName }
    $signArgs = @('sign', '/fd', 'SHA256', '/f', (Resolve-Path -LiteralPath $CertificatePath).Path)
    if ($CertificatePassword) { $signArgs += @('/p', $CertificatePassword) }
    $signArgs += @('/tr', 'http://timestamp.digicert.com', '/td', 'SHA256', $msiPath)
    & $signtoolPath @signArgs
    if ($LASTEXITCODE -ne 0) {
        Fail "signtool sign failed with exit code $LASTEXITCODE."
    }
    Write-Host '  signed   : yes'
}

# ---------------------------------------------------------------- summary
Write-Step 'Done'

$finalMsi = Get-Item -LiteralPath $msiPath
$hash = (Get-FileHash -LiteralPath $msiPath -Algorithm SHA256).Hash

Write-Host ''
Write-Host '  MSI        : ' -NoNewline; Write-Host $finalMsi.FullName -ForegroundColor Green
Write-Host ("  Size       : {0:N0} bytes ({1:N2} MB)" -f $finalMsi.Length, ($finalMsi.Length / 1MB))
Write-Host "  SHA256     : $hash"
Write-Host "  Version    : $Version"
Write-Host ''
Write-Host '  Install   : msiexec /i "' -NoNewline; Write-Host $finalMsi.FullName -NoNewline -ForegroundColor Yellow; Write-Host '" /qn /norestart'
Write-Host '  Uninstall : msiexec /x "' -NoNewline; Write-Host $finalMsi.FullName -NoNewline -ForegroundColor Yellow; Write-Host '" /qn /norestart'
Write-Host '  Uninstall + delete %LOCALAPPDATA%\Overlyric data:' -ForegroundColor DarkGray
Write-Host '              msiexec /x "' -NoNewline; Write-Host $finalMsi.FullName -NoNewline; Write-Host '" /qn /norestart REMOVEDATA=1'
Write-Host ''
Write-Host '  Install dir: %LOCALAPPDATA%\Programs\Overlyric (per-user, no elevation)'

exit 0
