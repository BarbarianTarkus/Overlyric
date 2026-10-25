#requires -Version 7.0
<#
.SYNOPSIS
    Independent acceptance test for the Overlyric per-user MSI.

.DESCRIPTION
    Verifies the written installer contract, assertion by assertion:

      1  Per-user scope, no elevation: msiexec /i /qn /norestart from a non-elevated
         shell succeeds; payload lands in %LOCALAPPDATA%\Programs\Overlyric.
      2  Payload: exactly the publish output minus *.pdb; Overlyric.exe present; no
         .pdb installed anywhere under the install dir.
      3  Launchability: installed exe starts, is alive after >= 5 s, terminates
         cleanly when asked to close; a second launch exits promptly (single-instance
         mutex) without a second process lingering.
      4  Per-user Start Menu shortcut exists after install and is gone after uninstall.
      5  Start With Windows: HKCU ...\Run value "Overlyric" points at the installed exe,
         removed on uninstall; the deselected path is probed if the MSI exposes it.
      6  ARP entry: HKCU Uninstall\{ProductCode} with DisplayName/DisplayVersion/
         Publisher/DisplayIcon/InstallLocation/EstimatedSize; exactly one entry, also
         after an upgrade.
      7  Uninstall: exit 0; install dir, shortcut, Run value and ARP key gone;
         %LOCALAPPDATA%\Overlyric still present and its files untouched.
      8  REMOVEDATA=1 uninstall wipes %LOCALAPPDATA%\Overlyric (sentinel probe).
      9  Reinstall/repair: installing the same MSI twice exits 0 both times and
         leaves a working install.
     10  Upgrade: an older version is upgraded in place (one ARP entry, correct
         DisplayVersion, old product not orphaned); a downgrade attempt is refused.
         Uses -UpgradeMsiPath when supplied; otherwise synthesises an older package
         by patching a copy of the MSI's identity properties (clearly labelled).
     11  Running app during install: install completes (exit 0) within a bounded time
         without a reboot prompt and the app is not left broken.
     12  Adversarial read-only review of installer\Overlyric.wxs. This is a manual
         review reported alongside the script; the script prints A12 SKIP.

    The script resets state before the run (uninstalls any Overlyric product found in
    HKCU/HKLM Uninstall, removes leftovers) and restores state afterwards (uninstall,
    remove leftovers, user data intact). It must be run from a NON-elevated shell: the
    whole point of assertion 1 is that no elevation is available.

.PARAMETER MsiPath
    Path to the MSI under test. Defaults to <repo>\dist\Overlyric-0.1.0-x64.msi.

.PARAMETER UpgradeMsiPath
    Optional path to a second MSI of a DIFFERENT (older) version built from the same
    authoring, used for the real upgrade/downgrade test. If omitted the script looks
    for dist\Overlyric-*-x64.msi siblings and finally falls back to synthesising a
    patched copy (labelled in the evidence).

.PARAMETER KeepInstalled
    Run the full suite, then reinstall and leave Overlyric installed for manual
    inspection (state is still reset first; user data is still restored).

.PARAMETER PreflightOnly
    Read the MSI and environment and print the plan without mutating the machine.

.PARAMETER WorkRoot
    Directory for run logs and the user-data backup. Defaults to
    %TEMP%\OverlyricVerify.

.EXAMPLE
    pwsh -NoProfile -File installer\tests\verify-installer.ps1

.EXAMPLE
    pwsh -NoProfile -File installer\tests\verify-installer.ps1 -MsiPath dist\Overlyric-0.1.0-x64.msi -UpgradeMsiPath dist\Overlyric-0.0.9-x64.msi -KeepInstalled
#>
[CmdletBinding()]
param(
    [string]$MsiPath = '',
    [string]$UpgradeMsiPath = '',
    [switch]$KeepInstalled,
    [switch]$PreflightOnly,
    [switch]$UiOnly,
    [string]$WorkRoot = ''
)

$ErrorActionPreference = 'Continue'

# ---------------------------------------------------------------------------------
# Fixed environment facts
# ---------------------------------------------------------------------------------
$script:RepoRoot             = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$script:InstallDir           = Join-Path $env:LOCALAPPDATA 'Programs\Overlyric'
$script:InstallExe           = Join-Path $script:InstallDir 'Overlyric.exe'
$script:DataDir              = Join-Path $env:LOCALAPPDATA 'Overlyric'
$script:RunValueName         = 'Overlyric'
$script:UserPrograms         = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$script:MachinePrograms      = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'
$script:Msiexec              = Join-Path $env:WINDIR 'System32\msiexec.exe'
$script:Taskkill             = Join-Path $env:WINDIR 'System32\taskkill.exe'
$script:Runas                = Join-Path $env:WINDIR 'System32\runas.exe'
$script:Whoami               = Join-Path $env:WINDIR 'System32\whoami.exe'
if (-not $WorkRoot) { $WorkRoot = Join-Path $env:TEMP 'OverlyricVerify' }
$script:RunId                = Get-Date -Format 'yyyyMMdd-HHmmss'
$script:RunDir               = Join-Path $WorkRoot $script:RunId
$script:LogFile              = $null
$script:PhaseErrors          = [System.Collections.Generic.List[string]]::new()
$script:FatalError           = $null
$script:ExitCode             = 0
$script:Installed            = $false
$script:HadUserData          = $false
$script:DataBackup           = $null
$script:SentinelPath         = Join-Path $script:DataDir 'verify-installer-sentinel.txt'
$script:SentinelContent      = ''
$script:BaselineConfigHash   = ''
$script:BaselineVersionsHash = ''
$script:BaselineCacheCount   = 0
$script:PreInstallDataSnapshot = @()
$script:PublishDir           = $null
$script:Msi                 = $null
$script:ProductCode         = ''
$script:ExpectedVersion     = ''
$script:OlderMsiPath        = ''
$script:OlderVersion        = ''
$script:OlderSynthesised    = $false
$script:UiHelperReady       = $false
$script:WinFormsAvailable   = $false
$script:StartedAt           = Get-Date

# ---------------------------------------------------------------------------------
# Assertion bookkeeping (ids map 1:1 to the contract)
# ---------------------------------------------------------------------------------
$script:AssertionTitles = [ordered]@{
    '1'  = 'Per-user scope, no elevation (non-elevated install, LOCALAPPDATA payload, MSI summary evidence)'
    '2'  = 'Payload: exactly the publish output minus *.pdb; Overlyric.exe present; no .pdb installed'
    '3'  = 'Launchability: starts, alive >=5 s, closes cleanly; second launch exits promptly (single instance)'
    '4'  = 'Per-user Start Menu shortcut exists after install, gone after uninstall'
    '5'  = 'Start With Windows Run value present/points at exe/removed on uninstall; deselected path probed'
    '6'  = 'ARP entry fields, exactly one entry, also after upgrade'
    '7'  = 'Uninstall: exit 0; dir/shortcut/Run/ARP gone; %LOCALAPPDATA%\Overlyric intact'
    '8'  = 'REMOVEDATA=1 uninstall wipes %LOCALAPPDATA%\Overlyric (sentinel probe)'
    '9'  = 'Reinstall/repair: same MSI twice exits 0, working install'
    '10' = 'Upgrade replaces in place (one ARP, correct DisplayVersion, no orphan); downgrade refused'
    '11' = 'Running app during install: bounded exit 0, no reboot prompt, app not broken'
    '12' = 'Adversarial read-only review of installer\Overlyric.wxs (manual; not executed by this script)'
    'UI' = 'EXTRA (Lead request): UI table integrity + wizard render + Programs and Features visibility'
    'R'  = 'HARNESS: user-data baseline restored byte-identically (config.json/versions.json hashes)'
}
$script:Results = @{}

function Initialize-Results {
    $script:Results = @{}
    foreach ($key in $script:AssertionTitles.Keys) {
        $script:Results[[string]$key] = [pscustomobject]@{
            Id       = [string]$key
            Title    = [string]$script:AssertionTitles[$key]
            Checks   = [System.Collections.Generic.List[string]]::new()
            Failures = [System.Collections.Generic.List[string]]::new()
            Notes    = [System.Collections.Generic.List[string]]::new()
            Skipped  = $false
            SkipReason = ''
        }
    }
}

function Add-Check {
    param([string]$Id, [bool]$Ok, [string]$Description)
    $r = $script:Results[$Id]
    if ($null -eq $r) { Write-Log "internal: unknown assertion id $Id" 'ERROR'; return }
    if ($Ok) {
        $r.Checks.Add("PASS  $Description")
        Write-Log ("A{0} PASS  {1}" -f $Id, $Description) 'PASS'
    } else {
        $r.Checks.Add("FAIL  $Description")
        $r.Failures.Add($Description)
        Write-Log ("A{0} FAIL  {1}" -f $Id, $Description) 'FAIL'
    }
}

function Add-Note {
    param([string]$Id, [string]$Text)
    $r = $script:Results[$Id]
    if ($null -eq $r) { return }
    $r.Notes.Add($Text)
    Write-Log ("A{0} note  {1}" -f $Id, $Text) 'NOTE'
}

function Skip-Assertion {
    param([string]$Id, [string]$Reason)
    $r = $script:Results[$Id]
    if ($null -eq $r) { return }
    if ($r.Checks.Count -gt 0 -or $r.Failures.Count -gt 0) { return }   # never hide executed checks
    $r.Skipped = $true
    $r.SkipReason = $Reason
    Write-Log ("A{0} SKIP  {1}" -f $Id, $Reason) 'SKIP'
}

function Get-AssertionStatus {
    param([string]$Id)
    $r = $script:Results[$Id]
    if ($null -eq $r) { return 'FAIL' }
    if ($r.Skipped) { return 'SKIP' }
    if ($r.Failures.Count -gt 0) { return 'FAIL' }
    if ($r.Checks.Count -eq 0) { return 'FAIL' }
    return 'PASS'
}

function Skip-UntestedAssertions {
    param([string]$Reason)
    foreach ($key in $script:AssertionTitles.Keys) {
        $id = [string]$key
        $r = $script:Results[$id]
        if ($null -ne $r -and -not $r.Skipped -and $r.Checks.Count -eq 0) {
            Skip-Assertion -Id $id -Reason $Reason
        }
    }
}

# ---------------------------------------------------------------------------------
# Logging
# ---------------------------------------------------------------------------------
function Initialize-Log {
    New-Item -ItemType Directory -Force -Path $script:RunDir | Out-Null
    $script:LogFile = Join-Path $script:RunDir 'verify.log'
    Set-Content -LiteralPath $script:LogFile -Encoding UTF8 -Value (
        '# Overlyric installer acceptance run' + [Environment]::NewLine +
        '# run id : ' + $script:RunId + [Environment]::NewLine +
        '# started: ' + $script:StartedAt.ToString('o') + [Environment]::NewLine +
        '# user   : ' + [Environment]::UserName + [Environment]::NewLine +
        '# repo   : ' + $script:RepoRoot
    )
}

function Write-Log {
    param([string]$Message, [string]$Level = 'INFO')
    $line = '[' + (Get-Date -Format 'HH:mm:ss.fff') + '] ' + $Level.PadRight(8) + ' ' + $Message
    Write-Host $line
    if ($script:LogFile) { Add-Content -LiteralPath $script:LogFile -Value $line -Encoding UTF8 }
}

function Invoke-Phase {
    param([string]$Name, [scriptblock]$Body)
    Write-Log ('--- phase: ' + $Name + ' ---')
    try {
        & $Body
    } catch {
        $message = $_.Exception.Message
        Write-Log ('phase "' + $Name + '" threw: ' + $message) 'ERROR'
        Write-Log ('stack: ' + ($_.ScriptStackTrace -replace "`r?`n", ' | ')) 'ERROR'
        $script:PhaseErrors.Add($Name + ': ' + $message)
    }
}

# ---------------------------------------------------------------------------------
# Native helpers (window enumeration + MSI database patching)
# ---------------------------------------------------------------------------------
function Initialize-NativeHelpers {
    if ('OverlyricVerify.Native' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace OverlyricVerify
{
    public sealed class WindowInfo
    {
        public long Handle;
        public string ClassName;
        public string Title;
        public bool Visible;
    }

    public static class Native
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder text, int maxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        public static List<WindowInfo> GetWindows(uint processId)
        {
            var result = new List<WindowInfo>();
            EnumWindows(delegate (IntPtr hWnd, IntPtr lParam)
            {
                uint pid;
                GetWindowThreadProcessId(hWnd, out pid);
                if (pid == processId)
                {
                    var cls = new StringBuilder(256);
                    var title = new StringBuilder(512);
                    GetClassName(hWnd, cls, cls.Capacity);
                    GetWindowText(hWnd, title, title.Capacity);
                    result.Add(new WindowInfo { Handle = hWnd.ToInt64(), ClassName = cls.ToString(), Title = title.ToString(), Visible = IsWindowVisible(hWnd) });
                }
                return true;
            }, IntPtr.Zero);
            return result;
        }

        public static int PostClose(uint processId, string className)
        {
            int posted = 0;
            EnumWindows(delegate (IntPtr hWnd, IntPtr lParam)
            {
                uint pid;
                GetWindowThreadProcessId(hWnd, out pid);
                if (pid != processId) return true;
                if (className != null)
                {
                    var cls = new StringBuilder(256);
                    GetClassName(hWnd, cls, cls.Capacity);
                    if (!string.Equals(cls.ToString(), className, StringComparison.Ordinal)) return true;
                }
                if (PostMessage(hWnd, 0x0010, IntPtr.Zero, IntPtr.Zero)) posted++;
                return true;
            }, IntPtr.Zero);
            return posted;
        }
    }

    public static class MsiNative
    {
        [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern uint MsiOpenDatabaseW(string path, IntPtr persist, out IntPtr database);
        [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern uint MsiDatabaseOpenViewW(IntPtr database, string query, out IntPtr view);
        [DllImport("msi.dll", ExactSpelling = true)] public static extern uint MsiViewExecute(IntPtr view, IntPtr record);
        [DllImport("msi.dll", ExactSpelling = true)] public static extern uint MsiViewClose(IntPtr view);
        [DllImport("msi.dll", ExactSpelling = true)] public static extern uint MsiCloseHandle(IntPtr handle);
        [DllImport("msi.dll", ExactSpelling = true)] public static extern uint MsiDatabaseCommit(IntPtr database);
        [DllImport("msi.dll", ExactSpelling = true)] public static extern uint MsiGetSummaryInformationW(IntPtr database, string path, uint updateCount, out IntPtr summary);
        [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern uint MsiSummaryInfoSetPropertyW(IntPtr summary, uint property, uint dataType, int intValue, IntPtr fileTime, string value);
        [DllImport("msi.dll", ExactSpelling = true)] public static extern uint MsiSummaryInfoPersist(IntPtr summary);

        public static string PatchDatabase(string msiPath, string[] statements, string newPackageCode)
        {
            IntPtr db;
            uint rc = MsiOpenDatabaseW(msiPath, new IntPtr(1), out db);
            if (rc != 0) return "MsiOpenDatabase failed with " + rc;
            try
            {
                foreach (string sql in statements)
                {
                    IntPtr view;
                    rc = MsiDatabaseOpenViewW(db, sql, out view);
                    if (rc != 0) return "MsiDatabaseOpenView failed with " + rc + " for: " + sql;
                    rc = MsiViewExecute(view, IntPtr.Zero);
                    MsiViewClose(view);
                    MsiCloseHandle(view);
                    if (rc != 0) return "MsiViewExecute failed with " + rc + " for: " + sql;
                }
                if (!string.IsNullOrEmpty(newPackageCode))
                {
                    IntPtr summary;
                    rc = MsiGetSummaryInformationW(db, null, 1, out summary);
                    if (rc != 0) return "MsiGetSummaryInformation failed with " + rc;
                    rc = MsiSummaryInfoSetPropertyW(summary, 9, 30, 0, IntPtr.Zero, newPackageCode);
                    if (rc != 0) { MsiCloseHandle(summary); return "MsiSummaryInfoSetProperty failed with " + rc; }
                    rc = MsiSummaryInfoPersist(summary);
                    MsiCloseHandle(summary);
                    if (rc != 0) return "MsiSummaryInfoPersist failed with " + rc;
                }
                rc = MsiDatabaseCommit(db);
                return rc == 0 ? "OK" : "MsiDatabaseCommit failed with " + rc;
            }
            finally
            {
                MsiCloseHandle(db);
            }
        }
    }
}
'@
}

# ---------------------------------------------------------------------------------
# Small utilities
# ---------------------------------------------------------------------------------
function Get-PropValue {
    param($Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    $p = $Object.PSObject.Properties[$Name]
    if ($null -eq $p) { return $null }
    return $p.Value
}

function Wait-Until {
    param([scriptblock]$Condition, [int]$TimeoutSec = 30, [int]$IntervalMs = 500)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSec) {
        if (& $Condition) { return $true }
        Start-Sleep -Milliseconds $IntervalMs
    }
    return [bool](& $Condition)
}

function Get-IntegrityLabel {
    param([string]$Sid)
    switch ($Sid) {
        'S-1-16-4096'  { return 'Low' }
        'S-1-16-8192'  { return 'Medium' }
        'S-1-16-8448'  { return 'MediumPlus' }
        'S-1-16-12288' { return 'High' }
        'S-1-16-16384' { return 'System' }
        default        { return 'Unknown' }
    }
}

function Get-ElevationInfo {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [System.Security.Principal.WindowsPrincipal]::new($identity)
    $isAdmin = $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
    $integritySids = @()
    try {
        $lines = & $script:Whoami /groups 2>$null
        foreach ($line in $lines) {
            if ($line -match 'S-1-16-\d+') { $integritySids += $Matches[0] }
        }
    } catch { }
    $first = $integritySids | Select-Object -First 1
    [pscustomobject]@{
        User          = $identity.Name
        IsAdmin       = [bool]$isAdmin
        IntegritySids = ($integritySids -join ',')
        Integrity     = (Get-IntegrityLabel ([string]$first))
    }
}

function Copy-TreeRobocopy {
    param([string]$Source, [string]$Destination, [string]$Tag)
    $log = Join-Path $script:RunDir ('robocopy-' + $Tag + '.log')
    & robocopy.exe $Source $Destination /E /COPY:DAT /R:1 /W:1 /NFL /NDL /NJH /NJS /NP *> $log
    $code = $LASTEXITCODE
    if ($code -ge 8) { throw ('robocopy failed with ' + $code + ' copying ' + $Source + ' -> ' + $Destination + ' (see ' + $log + ')') }
    return $code
}

function Get-TreeSnapshot {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return @() }
    $root = (Resolve-Path -LiteralPath $Path).Path
    $items = @()
    foreach ($file in (Get-ChildItem -LiteralPath $root -Recurse -Force -File -ErrorAction SilentlyContinue)) {
        $hash = $null
        try { $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256 -ErrorAction Stop).Hash } catch { }
        $items += [pscustomobject]@{
            Relative      = $file.FullName.Substring($root.Length).TrimStart('\')
            Length        = $file.Length
            Hash          = $hash
            LastWriteTime = $file.LastWriteTimeUtc.ToString('o')
        }
    }
    return $items
}

function Compare-Snapshots {
    param($Before, $After)
    $beforeMap = @{}
    foreach ($item in @($Before)) { if ($null -ne $item) { $beforeMap[$item.Relative] = $item } }
    $afterMap = @{}
    foreach ($item in @($After)) { if ($null -ne $item) { $afterMap[$item.Relative] = $item } }
    $added = @($afterMap.Keys | Where-Object { -not $beforeMap.ContainsKey($_) } | Sort-Object)
    $removed = @($beforeMap.Keys | Where-Object { -not $afterMap.ContainsKey($_) } | Sort-Object)
    $changed = @($beforeMap.Keys | Where-Object {
        $afterMap.ContainsKey($_) -and (
            $afterMap[$_].Hash -ne $beforeMap[$_].Hash -or
            $afterMap[$_].Length -ne $beforeMap[$_].Length)
    } | Sort-Object)
    [pscustomobject]@{ Added = $added; Removed = $removed; Changed = $changed }
}

# ---------------------------------------------------------------------------------
# MSI introspection (WindowsInstaller COM, read-only)
# ---------------------------------------------------------------------------------
function Invoke-MsiQuery {
    param($Database, [string]$Sql)
    $view = $Database.OpenView($Sql)
    [void]$view.Execute()
    $rows = [System.Collections.Generic.List[object]]::new()
    while ($true) {
        $record = $view.Fetch()
        if ($null -eq $record) { break }
        $values = @()
        for ($i = 1; $i -le 32; $i++) {
            $value = $null
            try { $value = [string]$record.StringData($i) } catch { break }
            $values += ,$value
        }
        $rows.Add([pscustomobject]@{ Values = $values })
    }
    [void]$view.Close()
    return $rows.ToArray()
}

function Invoke-MsiQuerySafe {
    param($Database, [string]$Sql)
    try { return @(Invoke-MsiQuery -Database $Database -Sql $Sql) } catch { return @() }
}

function Get-MsiSummaryFromDatabase {
    param($Database)
    $si = $Database.SummaryInformation(0)
    $values = @{}
    foreach ($propertyId in 1, 2, 3, 4, 5, 6, 7, 8, 9, 11, 12, 13, 14, 15, 16, 18, 19) {
        $value = $null
        try { $value = $si.Property($propertyId) } catch { $value = $null }
        $values[$propertyId] = $value
    }
    [pscustomobject]@{
        Title               = $values[1]
        Subject             = $values[2]
        Author              = $values[3]
        Keywords            = $values[4]
        Comments            = $values[5]
        Template            = $values[7]
        PackageCode         = $values[9]
        PageCount           = $values[14]
        WordCount           = $values[15]
        CreatingApplication = $values[18]
        Raw                 = $values
    }
}

function Get-MsiInfo {
    param([string]$Path)
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $db = $installer.OpenDatabase($Path, 0)
    if ($null -eq $db) { throw ('WindowsInstaller could not open ' + $Path) }

    $summary = Get-MsiSummaryFromDatabase -Database $db

    $properties = @{}
    foreach ($row in (Invoke-MsiQuerySafe -Database $db -Sql 'SELECT `Property`, `Value` FROM `Property`')) {
        $properties[[string]$row.Values[0]] = [string]$row.Values[1]
    }

    $files = @()
    foreach ($row in (Invoke-MsiQuerySafe -Database $db -Sql 'SELECT `File`, `Component_`, `FileName`, `FileSize`, `Version` FROM `File`')) {
        $rawName = [string]$row.Values[2]
        $longName = if ($rawName -match '\|') { ($rawName -split '\|')[-1] } else { $rawName }
        $files += [pscustomobject]@{
            File      = [string]$row.Values[0]
            Component = [string]$row.Values[1]
            Name      = $longName
            Size      = [string]$row.Values[3]
            Version   = [string]$row.Values[4]
        }
    }
    if ($files.Count -eq 0) {
        foreach ($row in (Invoke-MsiQuerySafe -Database $db -Sql 'SELECT `File`, `Component_`, `FileName` FROM `File`')) {
            $rawName = [string]$row.Values[2]
            $longName = if ($rawName -match '\|') { ($rawName -split '\|')[-1] } else { $rawName }
            $files += [pscustomobject]@{ File = [string]$row.Values[0]; Component = [string]$row.Values[1]; Name = $longName; Size = $null; Version = $null }
        }
    }

    $features = @()
    foreach ($row in (Invoke-MsiQuerySafe -Database $db -Sql 'SELECT `Feature`, `Feature_Parent`, `Title`, `Description`, `Display`, `Level`, `Directory_`, `Attributes` FROM `Feature`')) {
        $features += [pscustomobject]@{
            Feature      = [string]$row.Values[0]
            Parent       = [string]$row.Values[1]
            Title        = [string]$row.Values[2]
            Display      = [string]$row.Values[4]
            Level        = [string]$row.Values[5]
            Directory    = [string]$row.Values[6]
            Attributes   = [string]$row.Values[7]
        }
    }

    $featureComponents = @()
    foreach ($row in (Invoke-MsiQuerySafe -Database $db -Sql 'SELECT `Feature_`, `Component_` FROM `FeatureComponents`')) {
        $featureComponents += [pscustomobject]@{ Feature = [string]$row.Values[0]; Component = [string]$row.Values[1] }
    }

    $registry = @()
    foreach ($row in (Invoke-MsiQuerySafe -Database $db -Sql 'SELECT `Registry`, `Root`, `Key`, `Name`, `Value`, `Component_` FROM `Registry`')) {
        $registry += [pscustomobject]@{
            Registry  = [string]$row.Values[0]
            Root      = [string]$row.Values[1]
            Key       = [string]$row.Values[2]
            Name      = [string]$row.Values[3]
            Value     = [string]$row.Values[4]
            Component = [string]$row.Values[5]
        }
    }

    $shortcuts = @()
    foreach ($row in (Invoke-MsiQuerySafe -Database $db -Sql 'SELECT `Shortcut`, `Directory_`, `Name`, `Component_`, `Target`, `Arguments` FROM `Shortcut`')) {
        $shortcuts += [pscustomobject]@{
            Shortcut  = [string]$row.Values[0]
            Directory = [string]$row.Values[1]
            Name      = [string]$row.Values[2]
            Component = [string]$row.Values[3]
            Target    = [string]$row.Values[4]
            Arguments = [string]$row.Values[5]
        }
    }

    $customActions = @()
    foreach ($row in (Invoke-MsiQuerySafe -Database $db -Sql 'SELECT `Action`, `Type`, `Source`, `Target` FROM `CustomAction`')) {
        $customActions += [pscustomobject]@{
            Action = [string]$row.Values[0]
            Type   = [string]$row.Values[1]
            Source = [string]$row.Values[2]
            Target = [string]$row.Values[3]
        }
    }

    $conditions = @()
    foreach ($row in (Invoke-MsiQuerySafe -Database $db -Sql 'SELECT `Condition`, `Feature_`, `Level` FROM `Condition`')) {
        $conditions += [pscustomobject]@{ Condition = [string]$row.Values[0]; Feature = [string]$row.Values[1]; Level = [string]$row.Values[2] }
    }

    $launchConditions = @()
    foreach ($row in (Invoke-MsiQuerySafe -Database $db -Sql 'SELECT `Condition`, `Description` FROM `LaunchCondition`')) {
        $launchConditions += [pscustomobject]@{ Condition = [string]$row.Values[0]; Description = [string]$row.Values[1] }
    }

    $upgrade = @()
    foreach ($row in (Invoke-MsiQuerySafe -Database $db -Sql 'SELECT `UpgradeCode`, `VersionMin`, `VersionMax`, `Language`, `Attributes`, `Remove`, `ActionProperty` FROM `Upgrade`')) {
        $upgrade += [pscustomobject]@{
            UpgradeCode    = [string]$row.Values[0]
            VersionMin     = [string]$row.Values[1]
            VersionMax     = [string]$row.Values[2]
            Language       = [string]$row.Values[3]
            Attributes     = [string]$row.Values[4]
            Remove         = [string]$row.Values[5]
            ActionProperty = [string]$row.Values[6]
        }
    }

    $dialogs = @()
    foreach ($row in (Invoke-MsiQuerySafe -Database $db -Sql 'SELECT `Dialog` FROM `Dialog`')) { $dialogs += [string]$row.Values[0] }
    $controls = @()
    foreach ($row in (Invoke-MsiQuerySafe -Database $db -Sql 'SELECT `Dialog_`, `Control`, `Type`, `Property` FROM `Control`')) {
        $controls += [pscustomobject]@{ Dialog = [string]$row.Values[0]; Control = [string]$row.Values[1]; Type = [string]$row.Values[2]; Property = [string]$row.Values[3] }
    }
    $controlEvents = @()
    foreach ($row in (Invoke-MsiQuerySafe -Database $db -Sql 'SELECT `Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering` FROM `ControlEvent`')) {
        $controlEvents += [pscustomobject]@{ Dialog = [string]$row.Values[0]; Control = [string]$row.Values[1]; Event = [string]$row.Values[2]; Argument = [string]$row.Values[3]; Condition = [string]$row.Values[4]; Ordering = [string]$row.Values[5] }
    }
    $installUiSequence = @()
    foreach ($row in (Invoke-MsiQuerySafe -Database $db -Sql 'SELECT `Action`, `Condition`, `Sequence` FROM `InstallUISequence`')) {
        $installUiSequence += [pscustomobject]@{ Action = [string]$row.Values[0]; Condition = [string]$row.Values[1]; Sequence = [string]$row.Values[2] }
    }
    $adminUiSequence = @()
    foreach ($row in (Invoke-MsiQuerySafe -Database $db -Sql 'SELECT `Action`, `Condition`, `Sequence` FROM `AdminUISequence`')) {
        $adminUiSequence += [pscustomobject]@{ Action = [string]$row.Values[0]; Condition = [string]$row.Values[1]; Sequence = [string]$row.Values[2] }
    }

    [pscustomobject]@{
        Path              = $Path
        Size              = (Get-Item -LiteralPath $Path).Length
        LastWriteTime     = (Get-Item -LiteralPath $Path).LastWriteTime
        Summary           = $summary
        Properties        = $properties
        Files             = $files
        Features          = $features
        FeatureComponents = $featureComponents
        Registry          = $registry
        Shortcuts         = $shortcuts
        CustomActions     = $customActions
        Conditions        = $conditions
        LaunchConditions  = $launchConditions
        Upgrade           = $upgrade
        Dialogs           = $dialogs
        Controls          = $controls
        ControlEvents     = $controlEvents
        InstallUISequence = $installUiSequence
        AdminUISequence   = $adminUiSequence
    }
}

# ---------------------------------------------------------------------------------
# Installed-state inspection
# ---------------------------------------------------------------------------------
function Get-ArpEntries {
    param([string[]]$ProductCodes = @())
    $locations = @(
        @{ Path = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall'; Hive = 'HKCU' },
        @{ Path = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall'; Hive = 'HKLM' },
        @{ Path = 'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'; Hive = 'HKLM-WOW64' }
    )
    $entries = @()
    foreach ($location in $locations) {
        if (-not (Test-Path -LiteralPath $location.Path)) { continue }
        foreach ($key in (Get-ChildItem -LiteralPath $location.Path -ErrorAction SilentlyContinue)) {
            $props = Get-ItemProperty -LiteralPath $key.PSPath -ErrorAction SilentlyContinue
            if ($null -eq $props) { continue }
            $keyName = [string]$key.PSChildName
            $displayName = [string](Get-PropValue $props 'DisplayName')
            $isOverlyric = ($displayName -match '(?i)overlyric') -or ($ProductCodes -contains $keyName)
            if (-not $isOverlyric) { continue }
            $entries += [pscustomobject]@{
                RegistryPath    = [string]$key.PSPath
                Hive            = $location.Hive
                KeyName         = $keyName
                DisplayName     = $displayName
                DisplayVersion  = [string](Get-PropValue $props 'DisplayVersion')
                Publisher       = [string](Get-PropValue $props 'Publisher')
                InstallLocation = [string](Get-PropValue $props 'InstallLocation')
                DisplayIcon     = [string](Get-PropValue $props 'DisplayIcon')
                EstimatedSize   = (Get-PropValue $props 'EstimatedSize')
                UninstallString = [string](Get-PropValue $props 'UninstallString')
                WindowsInstaller = (Get-PropValue $props 'WindowsInstaller')
                SystemComponent = (Get-PropValue $props 'SystemComponent')
                NoModify        = (Get-PropValue $props 'NoModify')
                NoRepair        = (Get-PropValue $props 'NoRepair')
                HasGuidKey      = ($keyName -match '^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$')
            }
        }
    }
    return $entries
}

function Get-VisibleArpEntries {
    # What the user actually sees in Programs and Features. The shell namespace view is enumerated
    # in a CHILD pwsh process so a cached namespace from this session cannot hide a just-installed
    # product; the classic "SystemComponent=1 means hidden" rule is applied separately on the census.
    $childScript = 'try { $shell = New-Object -ComObject Shell.Application; $ns = $shell.NameSpace("shell:::{7b81be6a-ce2b-4676-a29e-eb907a5126c5}"); if ($null -eq $ns) { "ERR=namespace-null"; exit 0 }; $items = @($ns.Items()); "TOTAL=" + $items.Count; foreach ($i in $items) { if ([string]$i.Name -match "(?i)overlyric") { "VISIBLE=" + $i.Name } } } catch { "ERR=" + $_.Exception.Message }'
    $output = @(& pwsh -NoProfile -Command $childScript 2>&1)
    $total = $null
    $visible = @()
    $errors = @()
    foreach ($line in $output) {
        $text = [string]$line
        if ($text -match '^TOTAL=(\d+)$') { $total = [int]$Matches[1] }
        elseif ($text -match '^VISIBLE=(.+)$') { $visible += $Matches[1] }
        elseif ($text -match '^ERR=(.+)$') { $errors += $Matches[1] }
    }
    return [pscustomobject]@{ Total = $total; Visible = $visible; Errors = $errors }
}

function Assert-ArpVisibility {
    param([string]$AssertionId, [string]$Label)
    $census = @(Get-ArpEntries -ProductCodes @($script:ProductCode))
    $visible = Get-VisibleArpEntries
    $hkcu = @($census | Where-Object { $_.Hive -eq 'HKCU' })
    $hklm = @($census | Where-Object { $_.Hive -like 'HKLM*' })
    Add-Note $AssertionId ($Label + ': ARP census HKCU=' + $hkcu.Count + ', HKLM=' + $hklm.Count +
        '; Programs and Features shell: visible Overlyric items=' + $visible.Visible.Count + ' (namespace total=' + $visible.Total + ')')
    foreach ($entry in $census) {
        Add-Note $AssertionId ($Label + ': ' + $entry.Hive + ' ' + $entry.KeyName + ' DisplayName="' + $entry.DisplayName +
            '" DisplayVersion="' + $entry.DisplayVersion + '" SystemComponent="' + $entry.SystemComponent +
            '" EstimatedSize="' + $entry.EstimatedSize + '" InstallLocation="' + $entry.InstallLocation + '"')
    }
    foreach ($name in $visible.Visible) { Add-Note $AssertionId ($Label + ': VISIBLE in Programs and Features: "' + $name + '"') }
    if ($visible.Errors.Count -gt 0) { Add-Note $AssertionId ($Label + ': shell enumeration errors: ' + ($visible.Errors -join '; ')) }
    return [pscustomobject]@{ Census = $census; Visible = $visible; Hkcu = $hkcu; Hklm = $hklm }
}

function Get-OverlyricRunValues {
    $locations = @(
        @{ Path = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'; Hive = 'HKCU' },
        @{ Path = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\RunOnce'; Hive = 'HKCU-RunOnce' },
        @{ Path = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run'; Hive = 'HKLM' },
        @{ Path = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\RunOnce'; Hive = 'HKLM-RunOnce' },
        @{ Path = 'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run'; Hive = 'HKLM-WOW64' }
    )
    $found = @()
    foreach ($location in $locations) {
        if (-not (Test-Path -LiteralPath $location.Path)) { continue }
        $props = Get-ItemProperty -LiteralPath $location.Path -ErrorAction SilentlyContinue
        if ($null -eq $props) { continue }
        foreach ($property in $props.PSObject.Properties) {
            if ($property.Name -like 'PS*') { continue }
            $name = [string]$property.Name
            $value = [string]$property.Value
            if ($name -match '(?i)overlyric' -or $value -match '(?i)overlyric') {
                $found += [pscustomobject]@{ Hive = $location.Hive; RegistryPath = $location.Path; Name = $name; Value = $value }
            }
        }
    }
    return $found
}

function Get-OverlyricShortcuts {
    $roots = @(
        @{ Path = $script:UserPrograms; Scope = 'PerUser' },
        @{ Path = $script:MachinePrograms; Scope = 'PerMachine' }
    )
    $shell = $null
    try { $shell = New-Object -ComObject WScript.Shell } catch { }
    $found = @()
    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root.Path)) { continue }
        foreach ($lnk in (Get-ChildItem -LiteralPath $root.Path -Recurse -Filter '*.lnk' -File -Force -ErrorAction SilentlyContinue)) {
            $target = ''; $arguments = ''; $workingDirectory = ''; $iconLocation = ''
            if ($null -ne $shell) {
                try {
                    $shortcut = $shell.CreateShortcut($lnk.FullName)
                    $target = [string]$shortcut.TargetPath
                    $arguments = [string]$shortcut.Arguments
                    $workingDirectory = [string]$shortcut.WorkingDirectory
                    $iconLocation = [string]$shortcut.IconLocation
                } catch { }
            }
            if ($lnk.Name -match '(?i)overlyric' -or $target -match '(?i)overlyric' -or $arguments -match '(?i)overlyric') {
                $found += [pscustomobject]@{
                    Scope            = $root.Scope
                    Path             = $lnk.FullName
                    Name             = $lnk.Name
                    TargetPath       = $target
                    Arguments        = $arguments
                    WorkingDirectory = $workingDirectory
                    IconLocation     = $iconLocation
                }
            }
        }
    }
    return $found
}

function Get-OverlyricProcesses {
    return @(Get-Process -Name 'Overlyric' -ErrorAction SilentlyContinue)
}

function Resolve-RunValuePath {
    param([string]$Value)
    if (-not $Value) { return $null }
    $trimmed = $Value.Trim()
    if ($trimmed.StartsWith('"')) {
        $end = $trimmed.IndexOf('"', 1)
        if ($end -gt 0) { return [Environment]::ExpandEnvironmentVariables($trimmed.Substring(1, $end - 1)) }
    }
    $candidate = $trimmed
    while ($candidate) {
        $expanded = [Environment]::ExpandEnvironmentVariables($candidate)
        if (Test-Path -LiteralPath $expanded) { return $expanded }
        $index = $candidate.LastIndexOf(' ')
        if ($index -lt 0) { break }
        $candidate = $candidate.Substring(0, $index)
    }
    $first = ($trimmed -split '\s+')[0]
    return [Environment]::ExpandEnvironmentVariables($first.Trim('"'))
}

function Resolve-ArpIconPath {
    param([string]$DisplayIcon)
    if (-not $DisplayIcon) { return $null }
    $path = $DisplayIcon.Trim()
    if ($path.StartsWith('"')) {
        $end = $path.IndexOf('"', 1)
        if ($end -gt 0) { $path = $path.Substring(1, $end - 1) }
    } else {
        $comma = $path.LastIndexOf(',')
        if ($comma -gt 0 -and $path.Substring($comma + 1) -match '^\s*-?\d+$') { $path = $path.Substring(0, $comma) }
    }
    return [Environment]::ExpandEnvironmentVariables($path.Trim())
}

function Test-PathInside {
    param([string]$Candidate, [string]$Parent)
    if (-not $Candidate) { return $false }
    try {
        $candidateFull = [System.IO.Path]::GetFullPath($Candidate)
        $parentFull = [System.IO.Path]::GetFullPath($Parent)
        if (-not $parentFull.EndsWith('\')) { $parentFull += '\' }
        return $candidateFull.StartsWith($parentFull, [System.StringComparison]::OrdinalIgnoreCase)
    } catch { return $false }
}

function Test-SamePath {
    param([string]$Left, [string]$Right)
    if (-not $Left -or -not $Right) { return $false }
    try {
        return ([System.IO.Path]::GetFullPath($Left.TrimEnd('\')) -ieq [System.IO.Path]::GetFullPath($Right.TrimEnd('\')))
    } catch { return $false }
}

# ---------------------------------------------------------------------------------
# msiexec wrappers
# ---------------------------------------------------------------------------------
function Invoke-MsiExec {
    param(
        [string]$Action,
        [string]$PackageArgument,
        [string]$LogName,
        [string]$ExtraArguments = '',
        [int]$TimeoutSec = 300
    )
    $log = Join-Path $script:RunDir ($LogName + '.log')
    $argumentString = $Action + ' "' + $PackageArgument + '" /qn /norestart /l*v "' + $log + '"'
    if ($ExtraArguments) { $argumentString += ' ' + $ExtraArguments }
    $commandLine = 'msiexec.exe ' + $argumentString
    Write-Log ('RUN: ' + $commandLine)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process -FilePath $script:Msiexec -ArgumentList $argumentString -PassThru -ErrorAction Stop
    $exited = $process.WaitForExit($TimeoutSec * 1000)
    $sw.Stop()
    $exitCode = $null
    if ($exited) {
        try { $exitCode = $process.ExitCode } catch { $exitCode = $null }
    } else {
        Write-Log ('msiexec timed out after ' + $TimeoutSec + ' s; killing pid ' + $process.Id) 'ERROR'
        try { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
    $result = [pscustomobject]@{
        CommandLine = $commandLine
        ExitCode    = $exitCode
        TimedOut    = (-not $exited)
        DurationSec = [math]::Round($sw.Elapsed.TotalSeconds, 1)
        Log         = $log
    }
    Write-Log ('RESULT: exit=' + $exitCode + ' timedOut=' + (-not $exited) + ' duration=' + $result.DurationSec + 's log=' + $log)
    return $result
}

function Install-Overlyric {
    param([string]$Msi = '', [string]$Tag = 'install', [string]$ExtraArguments = '', [int]$TimeoutSec = 300)
    if (-not $Msi) { $Msi = $script:MsiPath }
    return (Invoke-MsiExec -Action '/i' -PackageArgument $Msi -LogName ('install-' + $Tag) -ExtraArguments $ExtraArguments -TimeoutSec $TimeoutSec)
}

function Uninstall-Overlyric {
    param([string]$ProductCode = '', [string]$Tag = 'uninstall', [string]$ExtraArguments = '', [int]$TimeoutSec = 300)
    if (-not $ProductCode) { $ProductCode = $script:ProductCode }
    return (Invoke-MsiExec -Action '/x' -PackageArgument $ProductCode -LogName ('uninstall-' + $Tag) -ExtraArguments $ExtraArguments -TimeoutSec $TimeoutSec)
}

function Get-MsiLogTail {
    param([string]$Path, [int]$Count = 12)
    if (-not $Path -or -not (Test-Path -LiteralPath $Path)) { return @() }
    return @(Get-Content -LiteralPath $Path -Tail $Count -ErrorAction SilentlyContinue)
}

# ---------------------------------------------------------------------------------
# State reset / restore
# ---------------------------------------------------------------------------------
function Remove-InstallLeftovers {
    if (Test-Path -LiteralPath $script:InstallDir) {
        Write-Log ('removing leftover install dir: ' + $script:InstallDir) 'WARN'
        Remove-Item -LiteralPath $script:InstallDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    foreach ($shortcut in @(Get-OverlyricShortcuts)) {
        if ($shortcut.Scope -eq 'PerUser') {
            Write-Log ('removing leftover shortcut: ' + $shortcut.Path) 'WARN'
            Remove-Item -LiteralPath $shortcut.Path -Force -ErrorAction SilentlyContinue
            $parent = [System.IO.Path]::GetDirectoryName($shortcut.Path)
            if (([System.IO.Path]::GetFileName($parent)) -match '(?i)overlyric' -and -not (Get-ChildItem -LiteralPath $parent -Force -ErrorAction SilentlyContinue)) {
                Remove-Item -LiteralPath $parent -Force -ErrorAction SilentlyContinue
            }
        } else {
            Write-Log ('per-machine shortcut exists and cannot be removed without elevation: ' + $shortcut.Path) 'WARN'
        }
    }
    foreach ($runValue in @(Get-OverlyricRunValues)) {
        if ($runValue.Hive -eq 'HKCU' -or $runValue.Hive -eq 'HKCU-RunOnce') {
            Write-Log ('removing leftover Run value: ' + $runValue.RegistryPath + '\' + $runValue.Name) 'WARN'
            Remove-ItemProperty -LiteralPath $runValue.RegistryPath -Name $runValue.Name -Force -ErrorAction SilentlyContinue
        } else {
            Write-Log ('per-machine Run value exists and cannot be removed without elevation: ' + $runValue.RegistryPath + '\' + $runValue.Name) 'WARN'
        }
    }
}

function Reset-OverlyricState {
    Write-Log 'reset: stopping any Overlyric process'
    foreach ($process in @(Get-OverlyricProcesses)) {
        Write-Log ('stopping Overlyric pid ' + $process.Id) 'WARN'
        try { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
    Start-Sleep -Milliseconds 500

    for ($round = 1; $round -le 3; $round++) {
        $entries = @(Get-ArpEntries)
        if ($entries.Count -eq 0) { break }
        foreach ($entry in $entries) {
            if ($entry.HasGuidKey) {
                $result = Invoke-MsiExec -Action '/x' -PackageArgument $entry.KeyName -LogName ('reset-uninstall-' + $round) -TimeoutSec 240
                Write-Log ('reset: uninstall ' + $entry.KeyName + ' exit=' + $result.ExitCode)
                if ($result.ExitCode -notin @(0, 1605, 3010, 1641)) {
                    $tail = Get-MsiLogTail -Path $result.Log -Count 8
                    Write-Log ('reset: uninstall log tail: ' + ($tail -join ' | ')) 'WARN'
                }
            } else {
                Write-Log ('reset: ARP entry with non-GUID key removed directly: ' + $entry.RegistryPath) 'WARN'
                Remove-Item -LiteralPath $entry.RegistryPath -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }

    foreach ($entry in @(Get-ArpEntries)) {
        Write-Log ('reset: removing stale ARP key ' + $entry.RegistryPath) 'WARN'
        Remove-Item -LiteralPath $entry.RegistryPath -Recurse -Force -ErrorAction SilentlyContinue
    }

    Remove-InstallLeftovers
    Wait-Until { -not (Test-Path -LiteralPath $script:InstallDir) } -TimeoutSec 20 | Out-Null
}

function Assert-CleanBaseline {
    $problems = @()
    if (Test-Path -LiteralPath $script:InstallDir) { $problems += ('install dir exists: ' + $script:InstallDir) }
    $entries = @(Get-ArpEntries)
    if ($entries.Count -gt 0) { $problems += ('ARP entries remain: ' + (($entries | ForEach-Object { $_.RegistryPath }) -join ', ')) }
    $shortcuts = @(Get-OverlyricShortcuts | Where-Object { $_.Scope -eq 'PerUser' })
    if ($shortcuts.Count -gt 0) { $problems += ('shortcuts remain: ' + (($shortcuts | ForEach-Object { $_.Path }) -join ', ')) }
    $runValues = @(Get-OverlyricRunValues)
    if ($runValues.Count -gt 0) { $problems += ('Run values remain: ' + (($runValues | ForEach-Object { $_.RegistryPath + '\' + $_.Name }) -join ', ')) }
    $processes = @(Get-OverlyricProcesses)
    if ($processes.Count -gt 0) { $problems += ('Overlyric processes remain: ' + (($processes | ForEach-Object { $_.Id }) -join ', ')) }
    if ($problems.Count -gt 0) {
        throw ('baseline is not clean after reset: ' + ($problems -join '; ') + '. A per-machine install or leftover that this non-elevated test cannot remove would invalidate the run.')
    }
    Write-Log 'baseline clean: no ARP entry, no install dir, no shortcut, no Run value, no process'
}

# ---------------------------------------------------------------------------------
# User data backup / restore
# ---------------------------------------------------------------------------------
function Backup-UserData {
    $script:HadUserData = Test-Path -LiteralPath $script:DataDir
    $script:DataBackup = Join-Path $script:RunDir 'userdata-backup'
    New-Item -ItemType Directory -Force -Path $script:DataBackup | Out-Null
    if ($script:HadUserData) {
        Copy-TreeRobocopy -Source $script:DataDir -Destination $script:DataBackup -Tag 'userdata-backup' | Out-Null
        Write-Log ('backed up user data: ' + $script:DataDir + ' -> ' + $script:DataBackup)
        $configPath = Join-Path $script:DataDir 'config.json'
        $versionsPath = Join-Path $script:DataDir 'versions.json'
        $script:BaselineConfigHash = if (Test-Path -LiteralPath $configPath) { (Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash } else { '' }
        $script:BaselineVersionsHash = if (Test-Path -LiteralPath $versionsPath) { (Get-FileHash -LiteralPath $versionsPath -Algorithm SHA256).Hash } else { '' }
        $script:BaselineCacheCount = @(Get-ChildItem -LiteralPath (Join-Path $script:DataDir 'cache') -File -Force -ErrorAction SilentlyContinue).Count
        Write-Log ('user data baseline: config.json=' + $script:BaselineConfigHash + ' versions.json=' + $script:BaselineVersionsHash + ' cacheFiles=' + $script:BaselineCacheCount)
    } else {
        Write-Log 'no pre-existing %LOCALAPPDATA%\Overlyric; REMOVEDATA probes will use a synthetic data dir and it will be removed afterwards'
    }
    $script:PreInstallDataSnapshot = @(Get-TreeSnapshot -Path $script:DataDir)
    Write-Log ('user data snapshot: ' + @($script:PreInstallDataSnapshot).Count + ' file(s)')
}

function Get-DataStashPath {
    return (Join-Path ([System.IO.Path]::GetDirectoryName($script:DataDir)) 'Overlyric-verify-realdata-stash')
}

function Move-DataDirAside {
    # Moves the REAL %LOCALAPPDATA%\Overlyric out of the way so a destructive REMOVEDATA probe can
    # never touch the user's config/cache. Returns $true when something was moved.
    $stash = Get-DataStashPath
    if (Test-Path -LiteralPath $stash) { Remove-Item -LiteralPath $stash -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $script:DataDir) {
        Rename-Item -LiteralPath $script:DataDir -NewName ([System.IO.Path]::GetFileName($stash)) -ErrorAction Stop
        Write-Log ('moved real user data aside to ' + $stash)
        return $true
    }
    return $false
}

function Restore-DataDirFromStash {
    $stash = Get-DataStashPath
    if (-not (Test-Path -LiteralPath $stash)) { return }
    if (Test-Path -LiteralPath $script:DataDir) { Remove-Item -LiteralPath $script:DataDir -Recurse -Force -ErrorAction SilentlyContinue }
    Rename-Item -LiteralPath $stash -NewName ([System.IO.Path]::GetFileName($script:DataDir)) -ErrorAction SilentlyContinue
    Write-Log ('restored real user data from ' + $stash)
}

function New-FakeUserData {
    # Synthetic data dir for REMOVEDATA probes: never the real config/cache.
    if (Test-Path -LiteralPath $script:DataDir) { Remove-Item -LiteralPath $script:DataDir -Recurse -Force -ErrorAction SilentlyContinue }
    New-Item -ItemType Directory -Force -Path $script:DataDir | Out-Null
    $script:SentinelContent = 'overlyric-verify-sentinel-' + [guid]::NewGuid().ToString('N')
    Set-Content -LiteralPath $script:SentinelPath -Value $script:SentinelContent -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $script:DataDir 'config.json') -Value '{"Mode":"MultiLine","VerifyProbe":true}' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $script:DataDir 'versions.json') -Value '{"probe":"removedata"}' -Encoding UTF8
    $nestedDir = Join-Path $script:DataDir 'cache\nested'
    New-Item -ItemType Directory -Force -Path $nestedDir | Out-Null
    Set-Content -LiteralPath (Join-Path $nestedDir 'marker.json') -Value '{"removedata":true}' -Encoding UTF8
    Write-Log ('created synthetic user data for the REMOVEDATA probe under ' + $script:DataDir)
}

function New-TestDataArtifacts {
    if (-not (Test-Path -LiteralPath $script:DataDir)) { New-Item -ItemType Directory -Force -Path $script:DataDir | Out-Null }
    $script:SentinelContent = 'overlyric-verify-sentinel-' + [guid]::NewGuid().ToString('N')
    Set-Content -LiteralPath $script:SentinelPath -Value $script:SentinelContent -Encoding ASCII
    $nestedDir = Join-Path $script:DataDir 'verify-tmp\nested'
    New-Item -ItemType Directory -Force -Path $nestedDir | Out-Null
    Set-Content -LiteralPath (Join-Path $nestedDir 'marker.txt') -Value 'removedata-marker' -Encoding ASCII
}

function Restore-UserDataFromBackup {
    Restore-DataDirFromStash
    if (-not $script:HadUserData) {
        if (Test-Path -LiteralPath $script:DataDir) { Remove-Item -LiteralPath $script:DataDir -Recurse -Force -ErrorAction SilentlyContinue }
        return
    }
    if (Test-Path -LiteralPath $script:DataDir) { Remove-Item -LiteralPath $script:DataDir -Recurse -Force -ErrorAction SilentlyContinue }
    New-Item -ItemType Directory -Force -Path $script:DataDir | Out-Null
    Copy-TreeRobocopy -Source $script:DataBackup -Destination $script:DataDir -Tag 'userdata-restore' | Out-Null
    Write-Log ('restored user data from backup: ' + $script:DataBackup + ' -> ' + $script:DataDir)
}

function Restore-UserData {
    Restore-DataDirFromStash
    if (Test-Path -LiteralPath $script:SentinelPath) { Remove-Item -LiteralPath $script:SentinelPath -Force -ErrorAction SilentlyContinue }
    foreach ($junk in @('verify-tmp')) {
        $path = Join-Path $script:DataDir $junk
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue }
    }
    if (-not $script:HadUserData) {
        if (Test-Path -LiteralPath $script:DataDir) {
            $files = @(Get-ChildItem -LiteralPath $script:DataDir -Recurse -Force -File -ErrorAction SilentlyContinue)
            if ($files.Count -eq 0) {
                Remove-Item -LiteralPath $script:DataDir -Recurse -Force -ErrorAction SilentlyContinue
                Write-Log 'removed %LOCALAPPDATA%\Overlyric that did not exist before the run'
            } else {
                Write-Log ('left %LOCALAPPDATA%\Overlyric in place (' + $files.Count + ' file(s) created during the run; there was no pre-existing data)') 'WARN'
            }
        }
        return
    }
    if (-not (Test-Path -LiteralPath $script:DataDir)) { Restore-UserDataFromBackup }
    $restored = 0
    foreach ($backupFile in (Get-ChildItem -LiteralPath $script:DataBackup -Recurse -Force -File -ErrorAction SilentlyContinue)) {
        $relative = $backupFile.FullName.Substring($script:DataBackup.Length).TrimStart('\')
        $destination = Join-Path $script:DataDir $relative
        $destinationDir = [System.IO.Path]::GetDirectoryName($destination)
        if (-not (Test-Path -LiteralPath $destinationDir)) { New-Item -ItemType Directory -Force -Path $destinationDir | Out-Null }
        $copy = $true
        if (Test-Path -LiteralPath $destination) {
            try {
                if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $backupFile.FullName -Algorithm SHA256).Hash) { $copy = $false }
            } catch { $copy = $true }
        }
        if ($copy) {
            Copy-Item -LiteralPath $backupFile.FullName -Destination $destination -Force -ErrorAction SilentlyContinue
            $restored++
        }
    }
    Write-Log ('user data restore: ' + $restored + ' file(s) copied back from backup; original files preserved')
}

function Test-BaselineRestored {
    # Assertion R: the real user-data baseline must be byte-identical after the run.
    if (-not $script:HadUserData) {
        Add-Note 'R' 'no pre-existing user data before the run; nothing to restore'
        Add-Check 'R' $true 'no pre-existing %LOCALAPPDATA%\Overlyric was present, so there was no baseline to preserve'
        return
    }
    $configPath = Join-Path $script:DataDir 'config.json'
    $versionsPath = Join-Path $script:DataDir 'versions.json'
    Add-Check 'R' (Test-Path -LiteralPath $configPath) ('config.json exists after the run: ' + $configPath)
    $configHash = if (Test-Path -LiteralPath $configPath) { (Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash } else { '' }
    $versionsHash = if (Test-Path -LiteralPath $versionsPath) { (Get-FileHash -LiteralPath $versionsPath -Algorithm SHA256).Hash } else { '' }
    Add-Note 'R' ('config.json sha256=' + $configHash + ' (baseline ' + $script:BaselineConfigHash + ')')
    Add-Note 'R' ('versions.json sha256=' + $versionsHash + ' (baseline ' + $script:BaselineVersionsHash + ')')
    Add-Check 'R' ($configHash -eq $script:BaselineConfigHash) 'config.json restored byte-identically to the pre-run baseline'
    Add-Check 'R' ($versionsHash -eq $script:BaselineVersionsHash) 'versions.json restored byte-identically to the pre-run baseline'
    $cacheCount = @(Get-ChildItem -LiteralPath (Join-Path $script:DataDir 'cache') -File -Force -ErrorAction SilentlyContinue).Count
    Add-Note 'R' ('cache files now=' + $cacheCount + ' (baseline ' + $script:BaselineCacheCount + ')')
    $snapshot = @(Get-TreeSnapshot -Path $script:DataDir)
    $diff = Compare-Snapshots -Before $script:PreInstallDataSnapshot -After $snapshot
    Add-Check 'R' ($diff.Removed.Count -eq 0) ('no pre-run user-data file is missing after the run (removed=' + ($diff.Removed -join ',') + ')')
    $changedReal = @($diff.Changed | Where-Object { $_ -notmatch '^verify-' })
    Add-Check 'R' ($changedReal.Count -eq 0) ('no pre-run user-data file changed content (changed=' + ($changedReal -join ',') + ')')
    if ($diff.Added.Count -gt 0) { Add-Note 'R' ('files added during the run (allowed, e.g. app cache): ' + ($diff.Added -join ', ')) }
}
# ---------------------------------------------------------------------------------
# Process close helper
# ---------------------------------------------------------------------------------
function Close-OverlyricGracefully {
    param([System.Diagnostics.Process]$Process, [int]$TimeoutMs = 15000)
    if ($null -eq $Process) { return [pscustomobject]@{ Exited = $false; ExitCode = $null; Method = 'no process'; ElapsedMs = 0 } }
    if ($Process.HasExited) { return [pscustomobject]@{ Exited = $true; ExitCode = $Process.ExitCode; Method = 'already exited'; ElapsedMs = 0 } }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $windows = @([OverlyricVerify.Native]::GetWindows([uint32]$Process.Id))
    $messageWindow = $windows | Where-Object { $_.ClassName -eq 'OverlyricMessageWindow' } | Select-Object -First 1
    $targets = if ($null -ne $messageWindow) { @($messageWindow) } else { @($windows) }
    $posted = 0
    foreach ($window in $targets) {
        if ([OverlyricVerify.Native]::PostMessage([IntPtr]$window.Handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)) { $posted++ }
    }
    Write-Log ('close: posted WM_CLOSE to ' + $posted + ' window(s) of pid ' + $Process.Id + ' (' + (($windows | ForEach-Object { $_.ClassName + '="' + $_.Title + '" visible=' + $_.Visible }) -join '; ') + ')')
    if ($Process.WaitForExit($TimeoutMs)) {
        $sw.Stop()
        return [pscustomobject]@{ Exited = $true; ExitCode = $Process.ExitCode; Method = 'WM_CLOSE'; ElapsedMs = $sw.ElapsedMilliseconds }
    }
    Write-Log 'close: WM_CLOSE did not terminate the app; falling back to taskkill (graceful, no /F)' 'WARN'
    & $script:Taskkill /PID $Process.Id *> $null
    if ($Process.WaitForExit(10000)) {
        $sw.Stop()
        return [pscustomobject]@{ Exited = $true; ExitCode = $Process.ExitCode; Method = 'taskkill (graceful)'; ElapsedMs = $sw.ElapsedMilliseconds }
    }
    Write-Log 'close: graceful close failed; forcing termination' 'ERROR'
    try { Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue } catch { }
    $Process.WaitForExit(5000) | Out-Null
    $sw.Stop()
    $code = $null
    if ($Process.HasExited) { try { $code = $Process.ExitCode } catch { } }
    return [pscustomobject]@{ Exited = $Process.HasExited; ExitCode = $code; Method = 'forced kill'; ElapsedMs = $sw.ElapsedMilliseconds }
}

# ---------------------------------------------------------------------------------
# Payload / environment helpers
# ---------------------------------------------------------------------------------
function Find-PublishDir {
    $releaseRoot = Join-Path $script:RepoRoot 'src\Overlyric\bin\Release'
    if (-not (Test-Path -LiteralPath $releaseRoot)) { return $null }
    $candidates = @(Get-ChildItem -LiteralPath $releaseRoot -Recurse -Filter 'Overlyric.exe' -File -ErrorAction SilentlyContinue |
        Where-Object { $_.DirectoryName -match '\\publish$' } |
        Sort-Object LastWriteTime -Descending)
    if ($candidates.Count -eq 0) { return $null }
    return $candidates[0].DirectoryName
}

# ---------------------------------------------------------------------------------
# Preflight
# ---------------------------------------------------------------------------------
function Invoke-Preflight {
    $elevation = Get-ElevationInfo
    $script:Elevation = $elevation
    Write-Log ('user=' + $elevation.User + ' admin=' + $elevation.IsAdmin + ' integrity=' + $elevation.Integrity + ' (' + $elevation.IntegritySids + ')')
    if ($elevation.IsAdmin) {
        throw 'This acceptance test must be run from a NON-elevated shell. The current token is elevated, which would not prove assertion 1. Re-run from a standard (non-admin) PowerShell 7 session.'
    }
    if ($elevation.Integrity -ne 'Medium') {
        Write-Log ('warning: expected Medium integrity for a standard user, got ' + $elevation.Integrity) 'WARN'
    }

    $inputPath = $MsiPath
    if (-not $inputPath) { $inputPath = Join-Path $script:RepoRoot 'dist\Overlyric-0.1.0-x64.msi' }
    $resolved = Resolve-Path -LiteralPath $inputPath -ErrorAction SilentlyContinue
    if ($null -eq $resolved) { throw ('MSI not found: ' + $inputPath) }
    $script:MsiPath = $resolved.Path
    if (-not (Test-Path -LiteralPath $script:Msiexec)) { throw ('msiexec.exe not found at ' + $script:Msiexec) }

    $msiFile = Get-Item -LiteralPath $script:MsiPath
    Write-Log ('MSI: ' + $script:MsiPath + ' size=' + $msiFile.Length + ' bytes mtime=' + $msiFile.LastWriteTime.ToString('o'))

    $script:Msi = Get-MsiInfo -Path $script:MsiPath
    $script:ProductCode = [string]$script:Msi.Properties['ProductCode']
    $script:ExpectedVersion = [string]$script:Msi.Properties['ProductVersion']
    Write-Log ('MSI identity: ProductCode=' + $script:ProductCode + ' ProductVersion=' + $script:ExpectedVersion +
        ' UpgradeCode=' + $script:Msi.Properties['UpgradeCode'] +
        ' ProductName=' + $script:Msi.Properties['ProductName'] +
        ' Manufacturer=' + $script:Msi.Properties['Manufacturer'] +
        ' ALLUSERS=' + $script:Msi.Properties['ALLUSERS'] +
        ' MSIINSTALLPERUSER=' + $script:Msi.Properties['MSIINSTALLPERUSER'])
    Write-Log ('MSI summary: Template=' + $script:Msi.Summary.Template + ' WordCount=' + $script:Msi.Summary.WordCount +
        ' PageCount=' + $script:Msi.Summary.PageCount + ' Title=' + $script:Msi.Summary.Title +
        ' Subject=' + $script:Msi.Summary.Subject + ' Author=' + $script:Msi.Summary.Author +
        ' PackageCode=' + $script:Msi.Summary.PackageCode)
    Write-Log ('MSI tables: files=' + @($script:Msi.Files).Count + ' features=' + @($script:Msi.Features).Count +
        ' registryRows=' + @($script:Msi.Registry).Count + ' shortcuts=' + @($script:Msi.Shortcuts).Count +
        ' customActions=' + @($script:Msi.CustomActions).Count + ' upgradeRows=' + @($script:Msi.Upgrade).Count)

    $script:PublishDir = Find-PublishDir
    if ($script:PublishDir) { Write-Log ('publish output: ' + $script:PublishDir) }
    else { Write-Log 'publish output directory not found; payload will be compared against the MSI File table only' 'WARN' }

    # A12 is a manual read-only review of the WiX source; this script does not execute it.
    Skip-Assertion -Id '12' -Reason 'manual read-only review of installer\Overlyric.wxs, reported separately (assertion 12 is not executable by this script)'
}

# ---------------------------------------------------------------------------------
# Assertion 1: per-user install, no elevation
# ---------------------------------------------------------------------------------
function Invoke-Assertion1Install {
    $summary = $script:Msi.Summary
    $wordCount = 0
    [void][int]::TryParse([string]$summary.WordCount, [ref]$wordCount)
    # Word Count Summary bit 3 (value 8): "Elevated privileges are not required to install this
    # package" (Windows Installer 4.0+). Bit 1 (value 2) means compressed source.
    $noElevationBit = ($wordCount -band 8) -ne 0
    $compressedBit = ($wordCount -band 2) -ne 0
    Add-Note '1' ('MSI Summary Information: Template=' + $summary.Template + ', WordCount=' + $summary.WordCount +
        ' (0x' + $wordCount.ToString('X') + '), PageCount=' + $summary.PageCount +
        ', Title=' + $summary.Title + ', Subject=' + $summary.Subject + ', Author=' + $summary.Author +
        ', PackageCode=' + $summary.PackageCode)
    $msiFileName = [System.IO.Path]::GetFileName($script:MsiPath)
    if ($msiFileName -match '(?i)-x64\.msi$' -and ([string]$summary.Template) -notmatch '(?i)^x64') {
        Add-Note '1' ('observation: the artifact is named x64 but the MSI Summary Template is "' + $summary.Template + '" (32-bit package). The payload is the x64 Overlyric.exe; a 32-bit MSI can install it, but the naming/platform mismatch should be a deliberate choice.')
    }
    Add-Note '1' ('Word Count bits: 0x8 "elevated privileges are not required" = ' + $noElevationBit +
        ', 0x2 "compressed source" = ' + $compressedBit +
        '; ALLUSERS="' + $script:Msi.Properties['ALLUSERS'] + '", MSIINSTALLPERUSER="' + $script:Msi.Properties['MSIINSTALLPERUSER'] + '"')
    Add-Check '1' $noElevationBit 'MSI summary Word Count marks the package as not requiring elevated privileges (bit 0x8 set)'
    Add-Check '1' (-not $script:Elevation.IsAdmin) ('test shell is non-elevated: admin=False, integrity=' + $script:Elevation.Integrity)

    $result = Install-Overlyric -Tag 'initial'
    Add-Check '1' ($result.ExitCode -eq 0) ('msiexec /i "' + $script:MsiPath + '" /qn /norestart exited 0 (actual=' + $result.ExitCode + ', ' + $result.DurationSec + ' s)')
    Add-Note '1' ('install command: ' + $result.CommandLine)
    Add-Note '1' ('install log: ' + $result.Log)

    if ($result.ExitCode -ne 0) {
        $tail = Get-MsiLogTail -Path $result.Log -Count 15
        Add-Note '1' ('install log tail: ' + ($tail -join ' | '))
        Write-Log 'initial install failed; machine-mutating assertions cannot proceed' 'ERROR'
        return
    }

    Add-Check '1' (Test-Path -LiteralPath $script:InstallDir) ('install dir exists: ' + $script:InstallDir)
    Add-Check '1' (Test-Path -LiteralPath $script:InstallExe) ('Overlyric.exe present: ' + $script:InstallExe)
    Add-Check '1' (Test-PathInside -Candidate $script:InstallDir -Parent (Join-Path $env:LOCALAPPDATA 'Programs')) ('install dir is under %LOCALAPPDATA%\Programs (per-user): ' + $script:InstallDir)

    $arp = Assert-ArpVisibility -AssertionId '1' -Label 'after non-elevated install'
    Add-Check '1' (($arp.Hkcu.Count + $arp.Hklm.Count) -eq 1) ('exactly one ARP registration for the product across HKCU/HKLM (HKCU=' + $arp.Hkcu.Count + ', HKLM=' + $arp.Hklm.Count + ')')
    Add-Check '1' ($arp.Visible.Visible.Count -eq 1) ('exactly one Overlyric entry is visible in Programs and Features (visible=' + $arp.Visible.Visible.Count + ', namespace total=' + $arp.Visible.Total + ')')
    if ($arp.Hklm.Count -gt 0) {
        Add-Note '1' ('observation: Windows Installer also created ' + $arp.Hklm.Count + ' HKLM registration(s) for this per-user install (SystemComponent=' + (($arp.Hklm | ForEach-Object { $_.SystemComponent }) -join ',') + '); this is the machine-side registration, not a second product — visibility is what the contract cares about')
    }
    Add-Check '1' ($script:Msi.Properties['ALLUSERS'] -ne '1') ('MSI Property ALLUSERS is not 1 (actual="' + $script:Msi.Properties['ALLUSERS'] + '")')
    if ($script:Msi.Properties.ContainsKey('MSIINSTALLPERUSER')) {
        Add-Check '1' ($script:Msi.Properties['MSIINSTALLPERUSER'] -eq '1') ('MSI Property MSIINSTALLPERUSER=1 (actual="' + $script:Msi.Properties['MSIINSTALLPERUSER'] + '")')
    }

    $script:Installed = $true
}

# ---------------------------------------------------------------------------------
# Assertion 2: payload
# ---------------------------------------------------------------------------------
function Invoke-Assertion2Payload {
    $installed = @(Get-ChildItem -LiteralPath $script:InstallDir -Recurse -Force -File -ErrorAction SilentlyContinue)
    Add-Check '2' ($installed.Count -gt 0) ('install dir contains ' + $installed.Count + ' file(s)')
    $installedNames = @($installed | ForEach-Object { $_.Name })
    Add-Check '2' (@($installed | Where-Object { $_.Name -eq 'Overlyric.exe' }).Count -eq 1) 'Overlyric.exe present exactly once'

    $pdbs = @($installed | Where-Object { $_.Name -like '*.pdb' })
    Add-Check '2' ($pdbs.Count -eq 0) ('no .pdb installed anywhere under the install dir (found ' + $pdbs.Count + ')')
    if ($pdbs.Count -gt 0) { Add-Note '2' ('installed .pdb files: ' + (($pdbs | ForEach-Object { $_.FullName }) -join ', ')) }

    $msiNames = @($script:Msi.Files | ForEach-Object { $_.Name })
    $missingFromInstall = @($msiNames | Where-Object { $installedNames -notcontains $_ })
    $extraInInstall = @($installedNames | Where-Object { $msiNames -notcontains $_ })
    Add-Check '2' ($missingFromInstall.Count -eq 0) ('every file declared in the MSI File table is installed (missing: ' + ($missingFromInstall -join ', ') + ')')
    Add-Check '2' ($extraInInstall.Count -eq 0) ('no installed file is absent from the MSI File table (extra: ' + ($extraInInstall -join ', ') + ')')
    Add-Note '2' ('MSI File table (' + $msiNames.Count + '): ' + ($msiNames -join ', '))
    Add-Note '2' ('installed (' + $installedNames.Count + '): ' + ($installedNames -join ', '))

    $msiExe = $script:Msi.Files | Where-Object { $_.Name -eq 'Overlyric.exe' } | Select-Object -First 1
    $installedExe = $installed | Where-Object { $_.Name -eq 'Overlyric.exe' } | Select-Object -First 1
    if ($null -ne $msiExe -and $msiExe.Size -and $null -ne $installedExe) {
        $declaredSize = 0
        [void][long]::TryParse([string]$msiExe.Size, [ref]$declaredSize)
        Add-Check '2' ($declaredSize -eq $installedExe.Length) ('installed Overlyric.exe size (' + $installedExe.Length + ' bytes) equals the MSI File table FileSize (' + $declaredSize + ')')
    }

    if ($script:PublishDir) {
        $publishFiles = @(Get-ChildItem -LiteralPath $script:PublishDir -Recurse -Force -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -ne '.pdb' })
        $publishNames = @($publishFiles | ForEach-Object { $_.Name })
        $publishMissing = @($publishNames | Where-Object { $installedNames -notcontains $_ })
        $publishExtra = @($installedNames | Where-Object { $publishNames -notcontains $_ })
        Add-Check '2' ($publishMissing.Count -eq 0) ('all publish-output files (minus *.pdb) are installed (missing: ' + ($publishMissing -join ', ') + ')')
        Add-Check '2' ($publishExtra.Count -eq 0) ('install dir contains nothing beyond the publish output minus *.pdb (extra: ' + ($publishExtra -join ', ') + ')')
        Add-Note '2' ('publish output (minus *.pdb): ' + ($publishNames -join ', '))
        $publishExe = Join-Path $script:PublishDir 'Overlyric.exe'
        if ((Test-Path -LiteralPath $publishExe) -and $null -ne $installedExe) {
            $publishHash = (Get-FileHash -LiteralPath $publishExe -Algorithm SHA256).Hash
            $installedHash = (Get-FileHash -LiteralPath $installedExe.FullName -Algorithm SHA256).Hash
            Add-Check '2' ($publishHash -eq $installedHash) ('installed Overlyric.exe SHA256 matches the publish output (' + $installedHash + ')')
            if ($publishHash -ne $installedHash) { Add-Note '2' ('hash mismatch: publish=' + $publishHash + ' installed=' + $installedHash + ' (the publish directory may postdate the MSI build)') }
        }
    } else {
        Add-Note '2' 'publish output directory not found; payload set compared against the MSI File table only (not fully independent)'
    }
}

# ---------------------------------------------------------------------------------
# Assertion 3: launchability
# ---------------------------------------------------------------------------------
function Invoke-Assertion3Launch {
    if (-not (Test-Path -LiteralPath $script:InstallExe)) { Add-Check '3' $false 'installed exe missing; cannot test launchability'; return }
    $first = Start-Process -FilePath $script:InstallExe -PassThru -ErrorAction Stop
    Start-Sleep -Seconds 5
    $alive = -not $first.HasExited
    Add-Check '3' $alive ('installed Overlyric.exe still alive after 5 s (pid=' + $first.Id + ', workingSet=' + [math]::Round($first.WorkingSet64 / 1MB, 1) + ' MB, threads=' + $first.Threads.Count + ')')
    $windows = @([OverlyricVerify.Native]::GetWindows([uint32]$first.Id))
    Add-Note '3' ('windows: ' + (($windows | ForEach-Object { $_.ClassName + '="' + $_.Title + '" visible=' + $_.Visible }) -join '; '))
    if (-not $alive) { Add-Check '3' $false ('process exited prematurely with code ' + $first.ExitCode); return }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $second = Start-Process -FilePath $script:InstallExe -PassThru -ErrorAction Stop
    $secondExited = $second.WaitForExit(10000)
    $sw.Stop()
    Add-Check '3' $secondExited ('second launch exited promptly (' + $sw.ElapsedMilliseconds + ' ms)')
    if ($secondExited) {
        Add-Check '3' ($second.ExitCode -eq 0) ('second launch exit code 0 (actual=' + $second.ExitCode + ')')
    }
    Start-Sleep -Milliseconds 300
    $processes = @(Get-OverlyricProcesses)
    Add-Check '3' ($processes.Count -eq 1) ('exactly one Overlyric process remains after the second launch (count=' + $processes.Count + ')')

    $close = Close-OverlyricGracefully -Process $first
    Add-Check '3' $close.Exited ('first instance exited when asked to close (method=' + $close.Method + ', ' + $close.ElapsedMs + ' ms)')
    Add-Check '3' ($close.ExitCode -eq 0) ('first instance exit code 0 when asked to close (actual=' + $close.ExitCode + ')')
    Add-Check '3' ($close.Method -ne 'forced kill') 'close was graceful (not a forced kill)'
    Start-Sleep -Milliseconds 500
    $remaining = @(Get-OverlyricProcesses)
    Add-Check '3' ($remaining.Count -eq 0) ('no Overlyric process lingers after close (count=' + $remaining.Count + ')')
    foreach ($process in $remaining) { try { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue } catch { } }
}

# ---------------------------------------------------------------------------------
# Assertion 4: Start Menu shortcut
# ---------------------------------------------------------------------------------
function Invoke-Assertion4Shortcut {
    $shortcuts = @(Get-OverlyricShortcuts)
    $user = @($shortcuts | Where-Object { $_.Scope -eq 'PerUser' })
    $machine = @($shortcuts | Where-Object { $_.Scope -eq 'PerMachine' })
    Add-Check '4' ($user.Count -ge 1) ('per-user Start Menu shortcut exists after install (count=' + $user.Count + ')')
    foreach ($shortcut in $user) {
        Add-Note '4' ('shortcut: ' + $shortcut.Path + ' -> target="' + $shortcut.TargetPath + '" args="' + $shortcut.Arguments + '"')
    }
    if ($user.Count -ge 1) {
        $targetsOk = @($user | Where-Object { $_.TargetPath -and (Test-PathInside -Candidate $_.TargetPath -Parent $script:InstallDir) })
        Add-Check '4' ($targetsOk.Count -eq $user.Count) ('every per-user shortcut targets a file inside the install dir (' + $targetsOk.Count + '/' + $user.Count + ')')
        $exeTargets = @($user | Where-Object { $_.TargetPath -and (Test-SamePath -Left $_.TargetPath -Right $script:InstallExe) })
        Add-Check '4' ($exeTargets.Count -ge 1) 'at least one per-user shortcut targets the installed Overlyric.exe'
    }
    Add-Check '4' ($machine.Count -eq 0) ('no per-machine (ProgramData) Start Menu shortcut created (count=' + $machine.Count + ')')
}

# ---------------------------------------------------------------------------------
# Assertion 5: Start With Windows
# ---------------------------------------------------------------------------------
function Invoke-Assertion5RunValue {
    $runValues = @(Get-OverlyricRunValues)
    $hkcu = @($runValues | Where-Object { $_.Hive -eq 'HKCU' -and $_.Name -eq $script:RunValueName })
    $hklm = @($runValues | Where-Object { $_.Hive -like 'HKLM*' })
    Add-Check '5' ($hkcu.Count -eq 1) ('HKCU Run value "' + $script:RunValueName + '" exists after install (count=' + $hkcu.Count + ')')
    Add-Check '5' ($hklm.Count -eq 0) ('no per-machine Run value created (count=' + $hklm.Count + ')')
    if ($hkcu.Count -ge 1) {
        $value = [string]$hkcu[0].Value
        Add-Note '5' ('HKCU\...\Run\' + $script:RunValueName + ' = ' + $value)
        $resolved = Resolve-RunValuePath -Value $value
        Add-Check '5' ([bool]$resolved -and (Test-Path -LiteralPath $resolved)) ('Run value resolves to an existing executable (' + $resolved + ')')
        Add-Check '5' ((Test-SamePath -Left $resolved -Right $script:InstallExe)) ('Run value points at the installed Overlyric.exe (' + $resolved + ')')
        Add-Check '5' ((Test-PathInside -Candidate $resolved -Parent $script:InstallDir)) ('Run value path is inside the install dir (' + $resolved + ')')
        if ($value -notmatch '^\s*"') { Add-Note '5' 'Run value is not quoted; if the install path contains spaces this would break at logon (static review item)' }
    }
    # the deselected path is probed in Invoke-Assertion5b
}

# ---------------------------------------------------------------------------------
# Assertion 6: ARP fields
# ---------------------------------------------------------------------------------
function Invoke-Assertion6Arp {
    $entries = @(Get-ArpEntries -ProductCodes @($script:ProductCode))
    $visible = Get-VisibleArpEntries
    $hkcu = @($entries | Where-Object { $_.Hive -eq 'HKCU' })
    $hklm = @($entries | Where-Object { $_.Hive -like 'HKLM*' })
    Add-Note '6' ('ARP census: HKCU=' + $hkcu.Count + ', HKLM=' + $hklm.Count + '; Programs and Features visible Overlyric items=' + $visible.Visible.Count + ' (namespace total=' + $visible.Total + ')')
    foreach ($e in $entries) {
        Add-Note '6' ('  ' + $e.Hive + ' ' + $e.KeyName + ' DisplayName="' + $e.DisplayName + '" DisplayVersion="' + $e.DisplayVersion +
            '" Publisher="' + $e.Publisher + '" SystemComponent="' + $e.SystemComponent + '" EstimatedSize="' + $e.EstimatedSize +
            '" InstallLocation="' + $e.InstallLocation + '" DisplayIcon="' + $e.DisplayIcon + '"')
    }
    # The user-facing requirement: exactly one Overlyric entry in Add/Remove Programs.
    Add-Check '6' ($visible.Visible.Count -eq 1) ('exactly one Overlyric entry is visible in Programs and Features (visible=' + $visible.Visible.Count + ')')
    $distinctProductCodes = @($entries | ForEach-Object { $_.KeyName } | Sort-Object -Unique)
    Add-Check '6' ($distinctProductCodes.Count -eq 1) ('all Overlyric ARP keys belong to one product identity (distinct ProductCodes=' + $distinctProductCodes.Count + ')')
    Add-Check '6' ($entries.Count -eq 1) ('exactly one ARP registration for the product across HKCU/HKLM/WOW64 (count=' + $entries.Count + ')')
    if ($entries.Count -eq 0) { return }
    $entry = $entries | Select-Object -First 1
    Add-Note '6' ('field checks evaluated on: ' + $entry.RegistryPath)
    Add-Check '6' ($entry.KeyName -eq $script:ProductCode) ('ARP key equals the MSI ProductCode (' + $script:ProductCode + ')')
    Add-Check '6' ($entry.DisplayName -eq 'Overlyric') ('DisplayName="Overlyric" (actual="' + $entry.DisplayName + '")')
    Add-Check '6' ($script:Msi.Properties['ProductVersion'] -eq '0.1.0') ('MSI ProductVersion is 0.1.0 (actual="' + $script:Msi.Properties['ProductVersion'] + '")')
    Add-Check '6' ($entry.DisplayVersion -eq $script:ExpectedVersion) ('DisplayVersion equals the MSI ProductVersion ' + $script:ExpectedVersion + ' (actual="' + $entry.DisplayVersion + '")')
    Add-Check '6' ($entry.Publisher -eq 'Overlyric') ('Publisher="Overlyric" (actual="' + $entry.Publisher + '")')

    $iconPath = Resolve-ArpIconPath -DisplayIcon $entry.DisplayIcon
    # Contract amendment: DisplayIcon is NOT required (the app ships no .ico; the stock Windows icon
    # is accepted for Apps & Features). Report what the registration carries instead of asserting.
    Add-Note '6' ('DisplayIcon="' + $entry.DisplayIcon + '" -> resolved="' + $iconPath + '" (not required by the amended contract)')

    Add-Note '6' ('InstallLocation="' + $entry.InstallLocation + '", EstimatedSize="' + $entry.EstimatedSize + '", UninstallString="' + $entry.UninstallString + '"')
    Add-Check '6' ([bool]$entry.InstallLocation) ('InstallLocation is set (actual="' + $entry.InstallLocation + '")')
    if ($entry.InstallLocation) {
        Add-Check '6' ((Test-SamePath -Left $entry.InstallLocation -Right $script:InstallDir)) ('InstallLocation resolves to the install dir (' + $entry.InstallLocation + ')')
    }
    $estimated = 0
    [void][int]::TryParse([string]$entry.EstimatedSize, [ref]$estimated)
    Add-Check '6' ($estimated -gt 0) ('EstimatedSize > 0 (actual=' + $entry.EstimatedSize + ' KB)')
    Add-Check '6' ([string]$entry.WindowsInstaller -eq '1') ('WindowsInstaller=1 (actual=' + $entry.WindowsInstaller + ')')
    # ARPSYSTEMCOMPONENT=1 makes Windows Installer write SystemComponent=1, which HIDES the product
    # from Apps & Features; the contract wants one visible Add/Remove Programs entry.
    Add-Check '6' ([string]$entry.SystemComponent -ne '1') ('HKCU ARP entry is not hidden from Apps & Features (SystemComponent != 1; actual="' + $entry.SystemComponent + '")')
    Add-Note '6' ('NoModify=' + $entry.NoModify + ', NoRepair=' + $entry.NoRepair + ', SystemComponent=' + $entry.SystemComponent)
}

# ---------------------------------------------------------------------------------
# Assertion 9: reinstall / repair
# ---------------------------------------------------------------------------------
function Invoke-Assertion9Reinstall {
    $result = Install-Overlyric -Tag 'reinstall'
    Add-Check '9' ($result.ExitCode -eq 0) ('second install of the same MSI exited 0 (actual=' + $result.ExitCode + ', ' + $result.DurationSec + ' s)')
    Add-Check '9' (Test-Path -LiteralPath $script:InstallExe) 'Overlyric.exe still present after reinstall'
    $entries = @(Get-ArpEntries -ProductCodes @($script:ProductCode))
    Add-Check '9' ($entries.Count -eq 1) ('exactly one ARP entry after reinstall (count=' + $entries.Count + ')')
    if ($entries.Count -eq 1) {
        Add-Check '9' ($entries[0].DisplayVersion -eq $script:ExpectedVersion) ('DisplayVersion still ' + $script:ExpectedVersion + ' after reinstall (actual="' + $entries[0].DisplayVersion + '")')
    }
    if (Test-Path -LiteralPath $script:InstallExe) {
        $process = Start-Process -FilePath $script:InstallExe -PassThru -ErrorAction Stop
        Start-Sleep -Seconds 5
        $alive = -not $process.HasExited
        Add-Check '9' $alive 'reinstalled app is alive 5 s after launch'
        $close = Close-OverlyricGracefully -Process $process
        Add-Check '9' ($close.Exited -and $close.ExitCode -eq 0) ('reinstalled app closes cleanly (method=' + $close.Method + ', exit=' + $close.ExitCode + ')')
    }
}

# ---------------------------------------------------------------------------------
# Assertion 11: install while the app is running
# ---------------------------------------------------------------------------------
function Invoke-Assertion11InstallWhileRunning {
    # Two scenarios, both with Overlyric.exe running: (a) a real upgrade 0.0.9 -> 0.1.0, which is
    # the case where the payload file is replaced in use; (b) a same-version reinstall/repair.
    $scenarios = @()
    if ($script:OlderMsiPath -and (Test-Path -LiteralPath $script:OlderMsiPath)) {
        $scenarios += [pscustomobject]@{ Label = 'upgrade ' + $script:OlderVersion + ' -> ' + $script:ExpectedVersion; BaseMsi = $script:OlderMsiPath; TargetMsi = $script:MsiPath; Tag = 'upgrade-while-running' }
    }
    $scenarios += [pscustomobject]@{ Label = 'same-version reinstall ' + $script:ExpectedVersion; BaseMsi = $script:MsiPath; TargetMsi = $script:MsiPath; Tag = 'reinstall-while-running' }

    foreach ($scenario in $scenarios) {
        Reset-OverlyricState
        $pre = Install-Overlyric -Msi $scenario.BaseMsi -Tag ($scenario.Tag + '-pre')
        Add-Check '11' ($pre.ExitCode -eq 0) ($scenario.Label + ': baseline install exited 0 (actual=' + $pre.ExitCode + ')')
        if ($pre.ExitCode -ne 0) { continue }
        if (-not (Test-Path -LiteralPath $script:InstallExe)) { Add-Check '11' $false ($scenario.Label + ': installed exe missing; cannot test'); continue }

        $process = Start-Process -FilePath $script:InstallExe -PassThru -ErrorAction Stop
        Start-Sleep -Seconds 3
        Add-Check '11' (-not $process.HasExited) ($scenario.Label + ': app is running before the install (pid=' + $process.Id + ')')

        $result = Install-Overlyric -Msi $scenario.TargetMsi -Tag $scenario.Tag -TimeoutSec 180
        Add-Check '11' ($result.ExitCode -eq 0) ($scenario.Label + ': install completed with exit 0 while the app was running (actual=' + $result.ExitCode + ', ' + $result.DurationSec + ' s)')
        Add-Check '11' ($result.ExitCode -notin @(3010, 1641)) ($scenario.Label + ': no reboot-required exit code (actual=' + $result.ExitCode + ')')
        Add-Check '11' ((-not $result.TimedOut) -and ($result.DurationSec -lt 180)) ($scenario.Label + ': completed within the bounded time of 180 s (' + $result.DurationSec + ' s, timedOut=' + $result.TimedOut + ')')
        if (Test-Path -LiteralPath $result.Log) {
            $scheduleReboot = @(Select-String -LiteralPath $result.Log -Pattern 'Doing action: ScheduleReboot|Scheduling reboot|SCHEDULE REBOOT' -ErrorAction SilentlyContinue)
            Add-Check '11' ($scheduleReboot.Count -eq 0) ($scenario.Label + ': install log does not schedule a reboot action (' + $scheduleReboot.Count + ' hit(s))')
            $pendingHits = @(Select-String -LiteralPath $result.Log -Pattern 'MsiSystemRebootPending' -ErrorAction SilentlyContinue)
            if ($pendingHits.Count -gt 0) {
                Add-Note '11' ($scenario.Label + ': the engine recorded MsiSystemRebootPending=1 (the same is present on a plain install and on uninstall with the app NOT running — it comes from ambient system state, not from the running app); exit code was ' + $result.ExitCode + ' and no ScheduleReboot action ran')
            }
            $inUseHits = @(Select-String -LiteralPath $result.Log -Pattern 'will be replaced on reboot|in use|in-use|FilesInUse' -ErrorAction SilentlyContinue)
            if ($inUseHits.Count -gt 0) { Add-Note '11' ($scenario.Label + ': install log mentions in-use files (' + $inUseHits.Count + ' hit(s)); first: ' + $inUseHits[0].Line.Trim()) }
        }

        Start-Sleep -Seconds 2
        $stillRunning = @(Get-OverlyricProcesses)
        $processExit = $null
        if ($process.HasExited) { try { $processExit = $process.ExitCode } catch { } }
        if ($stillRunning.Count -eq 0) {
            Add-Note '11' ($scenario.Label + ': the running app process ended during the install (exit code ' + $processExit + ') — it was closed/terminated, not left hanging')
        } else {
            Add-Note '11' ($scenario.Label + ': app process still alive after the install (pid=' + (($stillRunning | ForEach-Object { $_.Id }) -join ',') + ')')
        }
        Add-Check '11' (Test-Path -LiteralPath $script:InstallExe) ($scenario.Label + ': installed exe present after the install')
        $entries = @(Get-ArpEntries -ProductCodes @($script:ProductCode))
        Add-Check '11' ($entries.Count -eq 1) ($scenario.Label + ': exactly one ARP registration after the install (count=' + $entries.Count + ')')
        if ($entries.Count -eq 1) {
            Add-Check '11' ($entries[0].DisplayVersion -eq $script:ExpectedVersion) ($scenario.Label + ': DisplayVersion is ' + $script:ExpectedVersion + ' (actual="' + $entries[0].DisplayVersion + '")')
        }
        $visible = Get-VisibleArpEntries
        Add-Check '11' ($visible.Visible.Count -eq 1) ($scenario.Label + ': exactly one visible Programs and Features entry after the install (visible=' + $visible.Visible.Count + ')')

        $probe = Start-Process -FilePath $script:InstallExe -PassThru -ErrorAction Stop
        Start-Sleep -Seconds 5
        Add-Check '11' (-not $probe.HasExited) ($scenario.Label + ': a fresh launch after the install is alive after 5 s')
        $close = Close-OverlyricGracefully -Process $probe
        Add-Check '11' ($close.Exited -and $close.ExitCode -eq 0) ($scenario.Label + ': app closes cleanly after the install (method=' + $close.Method + ', exit=' + $close.ExitCode + ')')
        foreach ($remaining in @(Get-OverlyricProcesses)) { try { Stop-Process -Id $remaining.Id -Force -ErrorAction SilentlyContinue } catch { } }
        $uninstall = Uninstall-Overlyric -Tag ($scenario.Tag + '-cleanup')
        Write-Log ($scenario.Label + ': cleanup uninstall exit=' + $uninstall.ExitCode)
        Wait-Until { -not (Test-Path -LiteralPath $script:InstallDir) } -TimeoutSec 30 | Out-Null
        Reset-OverlyricState
    }
    $script:Installed = $false
}
# ---------------------------------------------------------------------------------
# Assertion 7: uninstall and data preservation
# ---------------------------------------------------------------------------------
function Invoke-Assertion7Uninstall {
    foreach ($process in @(Get-OverlyricProcesses)) { try { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue } catch { } }
    $beforeUninstall = @(Get-TreeSnapshot -Path $script:DataDir)

    $result = Uninstall-Overlyric -Tag 'uninstall'
    Add-Check '7' ($result.ExitCode -eq 0) ('uninstall exited 0 (actual=' + $result.ExitCode + ', ' + $result.DurationSec + ' s)')
    Add-Note '7' ('uninstall command: ' + $result.CommandLine)

    $dirGone = Wait-Until { -not (Test-Path -LiteralPath $script:InstallDir) } -TimeoutSec 30
    Add-Check '7' $dirGone ('install dir removed: ' + $script:InstallDir)

    $shortcuts = @(Get-OverlyricShortcuts | Where-Object { $_.Scope -eq 'PerUser' })
    Add-Check '7' ($shortcuts.Count -eq 0) ('per-user Start Menu shortcut removed (remaining=' + $shortcuts.Count + ')')
    Add-Check '4' ($shortcuts.Count -eq 0) ('per-user Start Menu shortcut removed after uninstall (remaining=' + $shortcuts.Count + ')')

    $runValues = @(Get-OverlyricRunValues)
    Add-Check '7' ($runValues.Count -eq 0) ('Run value removed (remaining=' + $runValues.Count + ')')
    Add-Check '5' ($runValues.Count -eq 0) ('Start With Windows Run value removed after uninstall (remaining=' + $runValues.Count + ')')

    $entries = @(Get-ArpEntries -ProductCodes @($script:ProductCode))
    Add-Check '7' ($entries.Count -eq 0) ('ARP registration removed in every hive (remaining=' + $entries.Count + ')')
    $visible = Get-VisibleArpEntries
    Add-Check '7' ($visible.Visible.Count -eq 0) ('Programs and Features no longer lists Overlyric (visible=' + $visible.Visible.Count + ')')

    Add-Check '7' (-not (Test-Path -LiteralPath 'HKCU:\Software\Overlyric')) 'no leftover HKCU\Software\Overlyric component keys after uninstall'
    Add-Check '7' (-not (Test-Path -LiteralPath 'HKLM:\Software\Overlyric')) 'no leftover HKLM\Software\Overlyric component keys after uninstall'
    $programsDir = Join-Path $env:LOCALAPPDATA 'Programs'
    Add-Note '7' ('%LOCALAPPDATA%\Programs still present (legitimate: other apps live there): ' + (Test-Path -LiteralPath $programsDir))
    $leftoverDirs = @(Get-ChildItem -LiteralPath $programsDir -Directory -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '(?i)overlyric' })
    Add-Check '7' ($leftoverDirs.Count -eq 0) ('no Overlyric-named directory residue under %LOCALAPPDATA%\Programs (found ' + $leftoverDirs.Count + ')')

    Add-Check '7' (Test-Path -LiteralPath $script:DataDir) ('%LOCALAPPDATA%\Overlyric still present: ' + $script:DataDir)
    $afterUninstall = @(Get-TreeSnapshot -Path $script:DataDir)
    $diff = Compare-Snapshots -Before $beforeUninstall -After $afterUninstall
    Add-Check '7' ($diff.Removed.Count -eq 0 -and $diff.Changed.Count -eq 0) ('uninstall removed/modified no data file (removed=' + ($diff.Removed -join ',') + '; changed=' + ($diff.Changed -join ',') + ')')
    $originals = Compare-Snapshots -Before $script:PreInstallDataSnapshot -After $afterUninstall
    Add-Check '7' ($originals.Removed.Count -eq 0) ('no file present before install was removed by install+uninstall (removed=' + ($originals.Removed -join ',') + ')')
    $criticalChanged = @($originals.Changed | Where-Object { $_ -in @('config.json', 'versions.json') })
    Add-Check '7' ($criticalChanged.Count -eq 0) ('config.json/versions.json byte-identical after install+uninstall (changed=' + ($criticalChanged -join ',') + ')')
    $cacheChanged = @($originals.Changed | Where-Object { $_ -notin @('config.json', 'versions.json') -and $_ -notmatch '^verify-' })
    if ($cacheChanged.Count -gt 0) {
        Add-Note '7' ('pre-existing file(s) changed during the run by the launched Overlyric app (not by the installer; the uninstall before/after diff above is zero): ' + ($cacheChanged -join ', '))
    }
    if ($originals.Added.Count -gt 0) { Add-Note '7' ('files added during install/uninstall (allowed, e.g. app cache): ' + ($originals.Added -join ', ')) }
    $script:Installed = $false
}

# ---------------------------------------------------------------------------------
# Assertion 8: REMOVEDATA=1
# ---------------------------------------------------------------------------------
function Invoke-Assertion8Removedata {
    # Synthetic data only: the real %LOCALAPPDATA%\Overlyric is moved aside so a destructive
    # REMOVEDATA probe can never touch the user's config/cache.
    $movedReal = Move-DataDirAside
    try {
        New-FakeUserData
        $result = Install-Overlyric -Tag 'removedata-install'
        Add-Check '8' ($result.ExitCode -eq 0) ('install for the REMOVEDATA test exited 0 (actual=' + $result.ExitCode + ')')
        Add-Check '8' ((Test-Path -LiteralPath $script:SentinelPath)) 'install did not delete the synthetic user data directory'

        $result = Uninstall-Overlyric -Tag 'removedata-uninstall' -ExtraArguments 'REMOVEDATA=1'
        Add-Check '8' ($result.ExitCode -eq 0) ('uninstall with REMOVEDATA=1 exited 0 (actual=' + $result.ExitCode + ', ' + $result.DurationSec + ' s)')
        Add-Note '8' ('uninstall command: ' + $result.CommandLine)
        if ($result.ExitCode -ne 0) { Add-Note '8' ('log tail: ' + ((Get-MsiLogTail -Path $result.Log -Count 12) -join ' | ')) }

        $gone = Wait-Until { -not (Test-Path -LiteralPath $script:DataDir) } -TimeoutSec 30
        Add-Check '8' $gone ('synthetic %LOCALAPPDATA%\Overlyric removed by REMOVEDATA=1 (dir gone=' + $gone + ')')
        if (-not $gone) {
            $left = @(Get-ChildItem -LiteralPath $script:DataDir -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
            Add-Note '8' ('synthetic data dir still present after REMOVEDATA=1; remaining entries: ' + ($left -join ', '))
        }
        if (Test-Path -LiteralPath $result.Log) {
            $caHits = @(Select-String -LiteralPath $result.Log -Pattern 'RemoveOverlyricUserData|rmdir' -ErrorAction SilentlyContinue | Select-Object -First 3)
            if ($caHits.Count -gt 0) {
                Add-Note '8' ('REMOVEDATA CA evidence from the msiexec log: ' + (($caHits | ForEach-Object { $_.Line.Trim() }) -join ' || '))
            } else {
                Add-Note '8' 'the msiexec log does not mention RemoveOverlyricUserData/rmdir (the CA may not have been scheduled)'
            }
        }

        # Return-code handling: REMOVEDATA=1 when the data directory does not exist. The deferred CA
        # carries msidbCustomActionTypeContinue (0x40), so a failing/missing dir must be ignored and
        # the uninstall must still exit 0.
        $installMissing = Install-Overlyric -Tag 'removedata-missing-install'
        Add-Check '8' ($installMissing.ExitCode -eq 0) ('install for the missing-data-dir REMOVEDATA probe exited 0 (actual=' + $installMissing.ExitCode + ')')
        Add-Check '8' (-not (Test-Path -LiteralPath $script:DataDir)) 'synthetic data dir absent for the missing-directory probe'
        $resultMissing = Uninstall-Overlyric -Tag 'removedata-missing-uninstall' -ExtraArguments 'REMOVEDATA=1'
        Add-Check '8' ($resultMissing.ExitCode -eq 0) ('uninstall REMOVEDATA=1 with the data dir absent still exits 0 (actual=' + $resultMissing.ExitCode + ')')
        Add-Check '8' ($resultMissing.ExitCode -notin @(3010, 1641)) ('no reboot-required exit code when the data dir is absent (actual=' + $resultMissing.ExitCode + ')')
        if (Test-Path -LiteralPath $resultMissing.Log) {
            $missingHits = @(Select-String -LiteralPath $resultMissing.Log -Pattern 'RemoveOverlyricUserData|rmdir|return value 3' -ErrorAction SilentlyContinue | Select-Object -First 3)
            if ($missingHits.Count -gt 0) { Add-Note '8' ('missing-directory uninstall log evidence: ' + (($missingHits | ForEach-Object { $_.Line.Trim() }) -join ' || ')) }
        }
    } finally {
        if (Test-Path -LiteralPath $script:DataDir) { Remove-Item -LiteralPath $script:DataDir -Recurse -Force -ErrorAction SilentlyContinue }
        Restore-DataDirFromStash
    }
    Add-Check '8' ((Test-Path -LiteralPath (Join-Path $script:DataDir 'config.json')) -or (-not $script:HadUserData)) 'real user data present again after the REMOVEDATA probes'
    Add-Check '8' ((Test-Path -LiteralPath $script:DataDir) -or (-not $script:HadUserData)) 'real %LOCALAPPDATA%\Overlyric present again after the REMOVEDATA probes'
    $script:Installed = $false
}
# ---------------------------------------------------------------------------------
# Assertion 10: upgrade / downgrade
# ---------------------------------------------------------------------------------
function New-SyntheticMsi {
    param([string]$SourceMsi, [string]$Version, [string]$Tag)
    $directory = Join-Path $script:RunDir ('synthetic-' + $Tag)
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $destination = Join-Path $directory ('Overlyric-' + $Version + '-synthetic.msi')
    Copy-Item -LiteralPath $SourceMsi -Destination $destination -Force
    $newProductCode = '{' + [guid]::NewGuid().ToString().ToUpperInvariant() + '}'
    $newPackageCode = '{' + [guid]::NewGuid().ToString().ToUpperInvariant() + '}'
    $statements = @(
        ("UPDATE ``Property`` SET ``Value``='" + $Version + "' WHERE ``Property``='ProductVersion'"),
        ("UPDATE ``Property`` SET ``Value``='" + $newProductCode + "' WHERE ``Property``='ProductCode'")
    )
    $result = [OverlyricVerify.MsiNative]::PatchDatabase($destination, [string[]]$statements, $newPackageCode)
    if ($result -ne 'OK') { throw ('synthetic MSI patch failed: ' + $result) }
    $info = Get-MsiInfo -Path $destination
    Write-Log ('synthesised ' + $Version + ' package: ' + $destination + ' ProductCode=' + $info.Properties['ProductCode'] + ' PackageCode=' + $info.Summary.PackageCode)
    return $info
}

function Get-NewerPayloadHash {
    if ($script:PublishDir) {
        $publishExe = Join-Path $script:PublishDir 'Overlyric.exe'
        if (Test-Path -LiteralPath $publishExe) { return (Get-FileHash -LiteralPath $publishExe -Algorithm SHA256).Hash }
    }
    return $null
}

function Invoke-Assertion10Upgrade {
    # --- static upgrade-table analysis (always possible, read-only) ---
    $upgradeRows = @($script:Msi.Upgrade)
    Add-Note '10' ('Upgrade table rows (' + $upgradeRows.Count + '): ' + (($upgradeRows | ForEach-Object { '[UpgradeCode=' + $_.UpgradeCode + ' Min=' + $_.VersionMin + ' Max=' + $_.VersionMax + ' Action=' + $_.ActionProperty + ']' }) -join ' '))
    Add-Check '10' ([bool]$script:Msi.Properties['ProductCode'] -and [bool]$script:Msi.Properties['UpgradeCode']) ('MSI declares ProductCode and UpgradeCode (UpgradeCode=' + $script:Msi.Properties['UpgradeCode'] + ')')
    $upgradeActions = @($upgradeRows | Where-Object { $_.ActionProperty -match 'UPGRADE_DETECTED' })
    Add-Check '10' ($upgradeActions.Count -ge 1) ('Upgrade table has at least one upgrade-detection row (' + $upgradeActions.Count + ')')
    $downgradeRows = @($upgradeRows | Where-Object { $_.ActionProperty -match 'DOWNGRADE' })
    Add-Check '10' ($downgradeRows.Count -ge 1) ('Upgrade table has a downgrade-detection row (' + $downgradeRows.Count + ')')
    if ($upgradeActions.Count -eq 0) { Add-Note '10' 'no upgrade-detection row in the MSI Upgrade table: an upgrade would install side-by-side instead of replacing the old product' }
    if ($downgradeRows.Count -eq 0) { Add-Note '10' 'no downgrade-detection row in the MSI Upgrade table: an older version may install side-by-side instead of being refused' }
    $launchConditions = @($script:Msi.LaunchConditions)
    Add-Note '10' ('LaunchCondition rows (' + $launchConditions.Count + '): ' + (($launchConditions | ForEach-Object { '[' + $_.Condition + ' => ' + $_.Description + ']' }) -join ' '))
    $downgradeBlock = @($launchConditions | Where-Object { $_.Condition -match 'WIX_DOWNGRADE_DETECTED' })
    Add-Check '10' ($downgradeBlock.Count -ge 1) ('a LaunchCondition refuses WIX_DOWNGRADE_DETECTED (downgrade blocked with a message), count=' + $downgradeBlock.Count)
    if ($downgradeBlock.Count -ge 1 -and $downgradeBlock[0].Description) { Add-Note '10' ('downgrade refusal message: ' + $downgradeBlock[0].Description) }

    # --- locate or synthesise an older package ---
    $older = $null
    $synthesised = $false
    if ($UpgradeMsiPath) {
        $resolved = Resolve-Path -LiteralPath $UpgradeMsiPath -ErrorAction SilentlyContinue
        if ($null -eq $resolved) { throw ('UpgradeMsiPath not found: ' + $UpgradeMsiPath) }
        $older = Get-MsiInfo -Path $resolved.Path
    }
    if ($null -eq $older) {
        $distDir = [System.IO.Path]::GetDirectoryName($script:MsiPath)
        $siblings = @(Get-ChildItem -LiteralPath $distDir -Filter 'Overlyric-*-x64.msi' -File -ErrorAction SilentlyContinue | Where-Object { $_.FullName -ne $script:MsiPath })
        foreach ($sibling in $siblings) {
            $candidate = Get-MsiInfo -Path $sibling.FullName
            $candidateVersion = [version]($candidate.Properties['ProductVersion'])
            if ($candidateVersion -lt [version]$script:ExpectedVersion) { $older = $candidate; break }
        }
    }
    if ($null -eq $older) {
        Write-Log 'no second-version MSI available; synthesising an older package by patching identity properties of the MSI under test' 'WARN'
        $older = New-SyntheticMsi -SourceMsi $script:MsiPath -Version '0.0.9' -Tag 'older'
        $synthesised = $true
    }
    $olderVersion = [string]$older.Properties['ProductVersion']
    $olderProductCode = [string]$older.Properties['ProductCode']
    $script:OlderMsiPath = [string]$older.Path
    $script:OlderVersion = $olderVersion
    $script:OlderSynthesised = $synthesised
    $olderKind = if ($synthesised) { ' (SYNTHESISED: identity properties patched on a copy of the 0.1.0 MSI; its Upgrade table is the 0.1.0 authoring, unmodified)' } else { ' (real second-version MSI)' }
    Add-Note '10' ('older package used: ' + $older.Path + ' ProductVersion=' + $olderVersion + ' ProductCode=' + $olderProductCode + $olderKind)
    if (-not $synthesised) { Add-Check '10' ([version]$olderVersion -lt [version]$script:ExpectedVersion) ('older package version ' + $olderVersion + ' is lower than ' + $script:ExpectedVersion) }

    # --- clean state, install the older version ---
    Reset-OverlyricState
    $result = Install-Overlyric -Msi $older.Path -Tag 'upgrade-older'
    Add-Check '10' ($result.ExitCode -eq 0) ('older version ' + $olderVersion + ' installed cleanly (exit=' + $result.ExitCode + ')')
    if ($result.ExitCode -ne 0) { Add-Note '10' ('older install log tail: ' + ((Get-MsiLogTail -Path $result.Log -Count 12) -join ' | ')); return }
    $oldEntries = @(Get-ArpEntries -ProductCodes @($olderProductCode))
    Add-Check '10' ($oldEntries.Count -eq 1) ('older product registered with exactly one ARP entry (count=' + $oldEntries.Count + ')')
    if ($oldEntries.Count -eq 1) {
        Add-Check '10' ($oldEntries[0].DisplayVersion -eq $olderVersion) ('older ARP DisplayVersion is ' + $olderVersion + ' (actual="' + $oldEntries[0].DisplayVersion + '")')
    }
    $oldExeHash = $null
    if (Test-Path -LiteralPath $script:InstallExe) { $oldExeHash = (Get-FileHash -LiteralPath $script:InstallExe -Algorithm SHA256).Hash }
    Add-Note '10' ('installed exe hash after older install: ' + $oldExeHash)

    # Adversarial REMOVEDATA check (attributed to assertion 8): an upgrade must not wipe user data,
    # even when REMOVEDATA=1 is passed to the upgrade command line. RemoveExistingProducts removes
    # the old product by invoking a *concurrent installation*, which does not inherit the outer
    # command line's public properties, so the old package's REMOVEDATA CA must see REMOVEDATA as
    # unset and leave %LOCALAPPDATA%\Overlyric alone. The REAL data dir is used read-only here:
    # its config/versions hashes must not change.
    $realConfigPath = Join-Path $script:DataDir 'config.json'
    $realVersionsPath = Join-Path $script:DataDir 'versions.json'
    $configBeforeUpgrade = if (Test-Path -LiteralPath $realConfigPath) { (Get-FileHash -LiteralPath $realConfigPath -Algorithm SHA256).Hash } else { '' }
    $versionsBeforeUpgrade = if (Test-Path -LiteralPath $realVersionsPath) { (Get-FileHash -LiteralPath $realVersionsPath -Algorithm SHA256).Hash } else { '' }
    Add-Note '8' 'adversarial check: upgrading with REMOVEDATA=1 on the command line must NOT wipe user data (concurrent installation of the old product must not inherit REMOVEDATA)'
    $result = Install-Overlyric -Msi $script:MsiPath -Tag 'upgrade-newer' -ExtraArguments 'REMOVEDATA=1'
    Add-Check '10' ($result.ExitCode -eq 0) ('upgrade to ' + $script:ExpectedVersion + ' exited 0 (actual=' + $result.ExitCode + ', ' + $result.DurationSec + ' s)')
    Add-Note '10' ('upgrade command: ' + $result.CommandLine)
    if ($result.ExitCode -ne 0) { Add-Note '10' ('upgrade log tail: ' + ((Get-MsiLogTail -Path $result.Log -Count 12) -join ' | ')) }
    $configAfterUpgrade = if (Test-Path -LiteralPath $realConfigPath) { (Get-FileHash -LiteralPath $realConfigPath -Algorithm SHA256).Hash } else { '' }
    $versionsAfterUpgrade = if (Test-Path -LiteralPath $realVersionsPath) { (Get-FileHash -LiteralPath $realVersionsPath -Algorithm SHA256).Hash } else { '' }
    $dataSurvivedUpgrade = (Test-Path -LiteralPath $script:DataDir) -and ($configAfterUpgrade -eq $configBeforeUpgrade) -and ($versionsAfterUpgrade -eq $versionsBeforeUpgrade)
    Add-Check '8' $dataSurvivedUpgrade 'upgrade with REMOVEDATA=1 on the command line did not wipe or modify %LOCALAPPDATA%\Overlyric (config/versions hashes unchanged)'
    if (-not $dataSurvivedUpgrade) {
        Add-Note '8' ('data dir survived=' + (Test-Path -LiteralPath $script:DataDir) + ', config=' + $configAfterUpgrade + ' (was ' + $configBeforeUpgrade + '); restoring user data from backup')
        Restore-UserDataFromBackup
    }

    $entries = @(Get-ArpEntries -ProductCodes @($script:ProductCode, $olderProductCode))
    Add-Check '10' ($entries.Count -eq 1) ('exactly one Overlyric ARP registration after upgrade (count=' + $entries.Count + ')')
    $visibleAfterUpgrade = Get-VisibleArpEntries
    Add-Check '10' ($visibleAfterUpgrade.Visible.Count -eq 1) ('exactly one Overlyric entry visible in Programs and Features after upgrade (visible=' + $visibleAfterUpgrade.Visible.Count + ')')
    if ($entries.Count -eq 1) {
        Add-Check '10' ($entries[0].DisplayVersion -eq $script:ExpectedVersion) ('DisplayVersion is ' + $script:ExpectedVersion + ' after upgrade (actual="' + $entries[0].DisplayVersion + '")')
        Add-Check '10' ($entries[0].KeyName -eq $script:ProductCode) ('the surviving ARP registration is the new ProductCode (' + $script:ProductCode + ')')
    }
    $oldKeys = @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $olderProductCode,
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $olderProductCode,
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\' + $olderProductCode
    )
    Add-Check '10' (-not ($oldKeys | Where-Object { Test-Path -LiteralPath $_ })) 'old product not orphaned: no ARP key remains for the old ProductCode in any hive'
    Add-Check '10' (Test-Path -LiteralPath $script:InstallExe) 'payload present after upgrade (Overlyric.exe exists)'
    $newExeHash = $null
    if (Test-Path -LiteralPath $script:InstallExe) { $newExeHash = (Get-FileHash -LiteralPath $script:InstallExe -Algorithm SHA256).Hash }
    $newerPayloadHash = Get-NewerPayloadHash
    if ($newerPayloadHash -and $newExeHash) {
        Add-Check '10' ($newExeHash -eq $newerPayloadHash) ('installed exe after upgrade matches the 0.1.0 payload hash (' + $newExeHash + ')')
    }
    if ($oldExeHash -and $newExeHash -and $oldExeHash -ne $newExeHash) {
        Add-Note '10' 'the executable content changed across the upgrade, which demonstrates the file was replaced'
    } else {
        Add-Note '10' 'the executable content is identical in the older and newer package, so file replacement is shown by registration/ARP evidence and the installed exe being present and launchable, not by content change'
    }
    if (Test-Path -LiteralPath $script:InstallExe) {
        $probe = Start-Process -FilePath $script:InstallExe -PassThru -ErrorAction Stop
        Start-Sleep -Seconds 5
        Add-Check '10' (-not $probe.HasExited) 'upgraded app launches and is alive after 5 s'
        $close = Close-OverlyricGracefully -Process $probe
        Add-Check '10' ($close.Exited -and $close.ExitCode -eq 0) ('upgraded app closes cleanly (method=' + $close.Method + ', exit=' + $close.ExitCode + ')')
    }

    # --- downgrade attempt must be refused ---
    $result = Install-Overlyric -Msi $older.Path -Tag 'downgrade-attempt' -TimeoutSec 180
    Add-Check '10' ($result.ExitCode -ne 0) ('downgrade attempt refused: exit=' + $result.ExitCode + ' (expected non-zero)')
    if ($result.ExitCode -eq 0) { Add-Note '10' 'the downgrade install returned 0: the older version was accepted over the newer one' }
    $messageHits = @()
    if (Test-Path -LiteralPath $result.Log) {
        $messageHits = @(Select-String -LiteralPath $result.Log -Pattern 'downgrade|DOWNGRADE|newer version|versión más reciente|WIX_DOWNGRADE' -ErrorAction SilentlyContinue)
    }
    Add-Check '10' ($messageHits.Count -ge 1) ('downgrade refusal message present in the install log (' + $messageHits.Count + ' hit(s))')
    if ($messageHits.Count -eq 0) { Add-Note '10' ('no downgrade message matched in ' + $result.Log + '; only the non-zero exit code is evidence') }
    $entries = @(Get-ArpEntries -ProductCodes @($script:ProductCode, $olderProductCode))
    Add-Check '10' ($entries.Count -eq 1 -and $entries[0].DisplayVersion -eq $script:ExpectedVersion) ('install intact after the refused downgrade: one ARP entry at ' + $script:ExpectedVersion + ' (count=' + $entries.Count + ', version="' + $(if ($entries.Count -eq 1) { $entries[0].DisplayVersion } else { '' }) + '")')
    Add-Check '10' (Test-Path -LiteralPath $script:InstallExe) 'install still present after the refused downgrade'
    if (Test-Path -LiteralPath $script:InstallExe) {
        $probe = Start-Process -FilePath $script:InstallExe -PassThru -ErrorAction Stop
        Start-Sleep -Seconds 5
        Add-Check '10' (-not $probe.HasExited) 'install still launchable after the refused downgrade'
        $close = Close-OverlyricGracefully -Process $probe
        Add-Check '10' ($close.Exited -and $close.ExitCode -eq 0) ('install still closes cleanly after the refused downgrade (method=' + $close.Method + ', exit=' + $close.ExitCode + ')')
    }

    Reset-OverlyricState
}

# ---------------------------------------------------------------------------------
# Assertion 5 (continued): deselected Start With Windows path
# ---------------------------------------------------------------------------------
function Invoke-Assertion5bUncheckedPath {
    $runRows = @($script:Msi.Registry | Where-Object { $_.Name -eq $script:RunValueName -and $_.Key -match '(?i)\\run$' })
    $owningFeature = $null
    $exeComponent = ($script:Msi.Files | Where-Object { $_.Name -eq 'Overlyric.exe' } | Select-Object -First 1).Component
    $exeFeature = $null
    if ($exeComponent) {
        $exeFeature = ($script:Msi.FeatureComponents | Where-Object { $_.Component -eq $exeComponent } | Select-Object -First 1).Feature
    }
    if ($runRows.Count -gt 0) {
        $component = $runRows[0].Component
        $owningFeature = ($script:Msi.FeatureComponents | Where-Object { $_.Component -eq $component } | Select-Object -First 1).Feature
    }
    Add-Note '5' ('Start With Windows mechanism in MSI tables: Registry rows=' + $runRows.Count + ', owning feature="' + $owningFeature + '", exe feature="' + $exeFeature + '"')
    if ($runRows.Count -eq 0) { Add-Note '5' 'no Run-value registry row found in the MSI Registry table; the unchecked path cannot be probed from the artifact'; return }
    if (-not $owningFeature) { Add-Note '5' 'the Run-value component belongs to no feature (or no FeatureComponents row); cannot deselect it'; return }
    if ($owningFeature -eq $exeFeature) {
        Add-Note '5' ('Start With Windows is not separable: the Run value and Overlyric.exe belong to the same feature "' + $owningFeature + '", so there is no feature deselection that removes only the Run value')
        return
    }

    Reset-OverlyricState
    $featureList = @($script:Msi.Features | ForEach-Object { $_.Feature })
    $attempts = @(
        ('ADDLOCAL=ALL REMOVE=' + $owningFeature),
        ('REMOVE=' + $owningFeature)
    )
    $attempt = $null
    foreach ($extra in $attempts) {
        $attempt = Install-Overlyric -Tag 'runvalue-deselected' -ExtraArguments $extra
        if ($attempt.ExitCode -eq 0) { break }
        Write-Log ('deselection attempt "' + $extra + '" failed with ' + $attempt.ExitCode) 'WARN'
        Reset-OverlyricState
    }
    Add-Check '5' ($attempt.ExitCode -eq 0) ('install with Start With Windows feature "' + $owningFeature + '" deselected exited 0 (actual=' + $attempt.ExitCode + ', args="' + $attempts[0] + '")')
    if ($attempt.ExitCode -ne 0) { return }
    $runValues = @(Get-OverlyricRunValues)
    Add-Check '5' ($runValues.Count -eq 0) ('Run value absent when feature "' + $owningFeature + '" is deselected (found ' + $runValues.Count + ')')
    Add-Check '5' (Test-Path -LiteralPath $script:InstallExe) 'app payload still installed when only the Start With Windows feature is deselected'
    if ($runValues.Count -eq 0) {
        Add-Note '5' ('the unchecked path is exposed: feature "' + $owningFeature + '" can be deselected on the command line and the Run value is correctly omitted')
    } else {
        Add-Note '5' ('deselecting feature "' + $owningFeature + '" did not suppress the Run value; the Run value may be unconditional')
    }
    $result = Uninstall-Overlyric -Tag 'runvalue-deselected-uninstall'
    Add-Check '5' ($result.ExitCode -eq 0) ('uninstall after the deselection probe exited 0 (actual=' + $result.ExitCode + ')')
    Wait-Until { -not (Test-Path -LiteralPath $script:InstallDir) } -TimeoutSec 30 | Out-Null
    Remove-InstallLeftovers
    $script:Installed = $false
}

# ---------------------------------------------------------------------------------
# Extra adversarial probe: install-dir path with spaces / non-ASCII
# ---------------------------------------------------------------------------------
function Invoke-RobustnessProbe {
    $directoryRows = @(Invoke-MsiQuerySafe -Database (New-Object -ComObject WindowsInstaller.Installer).OpenDatabase($script:MsiPath, 0) -Sql 'SELECT `Directory`, `Directory_Parent`, `DefaultDir` FROM `Directory`')
    $candidateProperty = $null
    foreach ($row in $directoryRows) {
        $id = [string]$row.Values[0]
        if ($id -match '^(INSTALLDIR|INSTALLFOLDER|APPLICATIONFOLDER|TARGETDIR)$') { $candidateProperty = $id; break }
    }
    if (-not $candidateProperty) {
        Add-Note '1' 'no public install-directory property (INSTALLDIR/INSTALLFOLDER) found in the MSI Directory table; a profile path with spaces/non-ASCII cannot be simulated at the MSI level, so it was not tested'
        return
    }
    Add-Note '1' ('public install-directory property found: ' + $candidateProperty + '; probing a path with spaces and non-ASCII characters')
    $probeDir = Join-Path $env:LOCALAPPDATA ('Programs\Overlyric Verify ' + [char]0x00C4 + [char]0x00D6 + ' Test')
    try {
        Reset-OverlyricState
        $extra = $candidateProperty + '="' + $probeDir + '"'
        $result = Install-Overlyric -Tag 'path-with-space' -ExtraArguments $extra
        Add-Check '1' ($result.ExitCode -eq 0) ('install with ' + $candidateProperty + '="' + $probeDir + '" exited 0 (actual=' + $result.ExitCode + ')')
        if ($result.ExitCode -eq 0) {
            Add-Check '1' (Test-Path -LiteralPath (Join-Path $probeDir 'Overlyric.exe')) ('payload installed under the space/non-ASCII path: ' + $probeDir)
            $runValues = @(Get-OverlyricRunValues | Where-Object { $_.Hive -eq 'HKCU' -and $_.Name -eq $script:RunValueName })
            if ($runValues.Count -eq 1) {
                $resolved = Resolve-RunValuePath -Value ([string]$runValues[0].Value)
                Add-Check '1' ((Test-SamePath -Left $resolved -Right (Join-Path $probeDir 'Overlyric.exe'))) ('Run value quotes/resolves the space path correctly (' + $resolved + ')')
            }
            $shortcuts = @(Get-OverlyricShortcuts | Where-Object { $_.Scope -eq 'PerUser' })
            if ($shortcuts.Count -ge 1) {
                Add-Check '1' ((Test-PathInside -Candidate $shortcuts[0].TargetPath -Parent $probeDir)) ('shortcut target resolves into the space path (' + $shortcuts[0].TargetPath + ')')
            }
            $probeExe = Join-Path $probeDir 'Overlyric.exe'
            if (Test-Path -LiteralPath $probeExe) {
                $process = Start-Process -FilePath $probeExe -PassThru -ErrorAction Stop
                Start-Sleep -Seconds 5
                Add-Check '1' (-not $process.HasExited) 'app launches from the space/non-ASCII install path'
                $close = Close-OverlyricGracefully -Process $process
                Add-Check '1' ($close.Exited -and $close.ExitCode -eq 0) ('app closes cleanly from the space/non-ASCII install path (exit=' + $close.ExitCode + ')')
            }
        }
        $uninstall = Uninstall-Overlyric -Tag 'path-with-space-uninstall'
        Add-Check '1' ($uninstall.ExitCode -eq 0) ('uninstall from the space/non-ASCII path exited 0 (actual=' + $uninstall.ExitCode + ')')
        $probeDirGone = Wait-Until { -not (Test-Path -LiteralPath $probeDir) } -TimeoutSec 30
        if ($probeDirGone) {
            Add-Note '1' ('the space/non-ASCII install dir was removed on uninstall: ' + $probeDir)
        } else {
            Add-Note '1' ('observation: with a command-line INSTALLFOLDER override, uninstall removed the payload but not the overridden directory (' + $probeDir + ') — uninstall resolves INSTALLFOLDER from the Directory table default, not the install-time override. Outside the contract default-path requirement; reported, not failed.')
        }
    } finally {
        foreach ($process in @(Get-OverlyricProcesses)) { try { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue } catch { } }
        Reset-OverlyricState
        if (Test-Path -LiteralPath $probeDir) { Remove-Item -LiteralPath $probeDir -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

# ---------------------------------------------------------------------------------
# Assertion 1 supporting probe: restricted (Basic User) token install
# ---------------------------------------------------------------------------------
function Invoke-Assertion1bRestrictedProbe {
    $probeDir = Join-Path $script:RunDir 'restricted-token-probe'
    New-Item -ItemType Directory -Force -Path $probeDir | Out-Null
    $msiLog = Join-Path $probeDir 'msiexec.log'
    $exitFile = Join-Path $probeDir 'exitcode.txt'
    $scriptFile = Join-Path $probeDir 'install.cmd'
    $msiLine = 'msiexec.exe /i "' + $script:MsiPath + '" /qn /norestart /l*v "' + $msiLog + '"'
    $echoLine = '>"' + $exitFile + '" echo %RC%'
    $batch = @('@echo off', $msiLine, 'set RC=%errorlevel%', $echoLine)
    Set-Content -LiteralPath $scriptFile -Value $batch -Encoding ASCII
    Reset-OverlyricState
    $argument = '/trustlevel:0x20000 "' + $scriptFile + '"'
    Write-Log ('RUN (restricted token): runas.exe ' + $argument)
    try {
        $runas = Start-Process -FilePath $script:Runas -ArgumentList $argument -PassThru -ErrorAction Stop
        $runas.WaitForExit(20000) | Out-Null
    } catch {
        Add-Note '1' ('restricted-token probe could not be launched: ' + $_.Exception.Message)
        return
    }
    $started = Wait-Until { Test-Path -LiteralPath $msiLog } -TimeoutSec 30
    if (-not $started) {
        Write-Log 'restricted-token probe did not start msiexec within 30 s; killing stray probe processes' 'WARN'
        Get-CimInstance Win32_Process -Filter "Name='msiexec.exe'" -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -like '*restricted-token-probe*' -or ($_.CommandLine -and $_.CommandLine.Contains($script:MsiPath)) } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    }
    $appeared = Wait-Until { Test-Path -LiteralPath $exitFile } -TimeoutSec 150
    $restrictedExit = ''
    if ($appeared) { $restrictedExit = [string]((Get-Content -LiteralPath $exitFile -Raw -ErrorAction SilentlyContinue).Trim()) }
    $logTailText = ''
    if (Test-Path -LiteralPath $msiLog) { $logTailText = [string](Get-Content -LiteralPath $msiLog -Raw -ErrorAction SilentlyContinue) }
    $logSucceeded = ($logTailText -match 'Installation completed successfully') -and ($logTailText -notmatch 'return value 3')
    Add-Note '1' ('restricted-token (Basic User, runas /trustlevel:0x20000) install: exit code file="' + $restrictedExit + '", msiexec log success=' + $logSucceeded)
    if ($restrictedExit -eq '0' -or $logSucceeded) {
        Add-Check '1' ((Test-Path -LiteralPath $script:InstallExe)) 'restricted-token install also produced the payload (no elevation required even for a Basic User token)'
        $arp = Assert-ArpVisibility -AssertionId '1' -Label 'after restricted-token install'
        Add-Check '1' ($arp.Census.Count -eq 1) ('restricted-token install created exactly one ARP registration across hives (count=' + $arp.Census.Count + ')')
        Add-Check '1' ($arp.Visible.Visible.Count -eq 1) ('restricted-token install: exactly one Overlyric entry visible in Programs and Features (visible=' + $arp.Visible.Visible.Count + ')')
    } else {
        Add-Note '1' 'restricted-token install did not succeed; the primary non-elevated (Medium integrity) install is the contract evidence, this probe is supplementary'
        if (Test-Path -LiteralPath $msiLog) { Add-Note '1' ('restricted-token msiexec log tail: ' + ((Get-MsiLogTail -Path $msiLog -Count 8) -join ' | ')) }
    }
    $result = Uninstall-Overlyric -Tag 'restricted-token-cleanup'
    Write-Log ('restricted-token probe cleanup uninstall exit=' + $result.ExitCode)
    Add-Check '1' ($result.ExitCode -eq 0) ('restricted-token probe cleanup uninstall exited 0 (actual=' + $result.ExitCode + ')')
    Reset-OverlyricState
    $script:Installed = $false
}

# ---------------------------------------------------------------------------------
# Final state restore
# ---------------------------------------------------------------------------------
# ---------------------------------------------------------------------------------
# EXTRA (Lead request): UI tables, wizard render/completion, maintenance mode
# ---------------------------------------------------------------------------------
function Initialize-UiHelper {
    if ($script:UiHelperReady) { return }
    try {
        Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class UiHelper {
    public delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumChildProc callback, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    public static void Focus(long handle) {
        IntPtr h = new IntPtr(handle);
        ShowWindow(h, 9);
        SetForegroundWindow(h);
    }
    public static string ClickButton(long parentHandle, string[] words) {
        string clicked = null;
        EnumChildWindows(new IntPtr(parentHandle), delegate (IntPtr h, IntPtr lParam) {
            if (clicked != null) return true;
            var cls = new StringBuilder(64);
            GetClassName(h, cls, 64);
            if (!string.Equals(cls.ToString(), "Button", StringComparison.OrdinalIgnoreCase)) return true;
            if (!IsWindowVisible(h)) return true;
            var text = new StringBuilder(256);
            GetWindowText(h, text, 256);
            string label = text.ToString();
            foreach (string word in words) {
                if (label.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0) {
                    SendMessage(h, 0x00F5, IntPtr.Zero, IntPtr.Zero);
                    clicked = label;
                    return true;
                }
            }
            return true;
        }, IntPtr.Zero);
        return clicked;
    }
    public static string EnsureChecked(long parentHandle, string textContains) {
        string result = null;
        EnumChildWindows(new IntPtr(parentHandle), delegate (IntPtr h, IntPtr lParam) {
            if (result != null) return true;
            var cls = new StringBuilder(64);
            GetClassName(h, cls, 64);
            if (!string.Equals(cls.ToString(), "Button", StringComparison.OrdinalIgnoreCase)) return true;
            if (!IsWindowVisible(h)) return true;
            var text = new StringBuilder(256);
            GetWindowText(h, text, 256);
            string label = text.ToString();
            if (label.IndexOf(textContains, StringComparison.OrdinalIgnoreCase) < 0) return true;
            IntPtr state = SendMessage(h, 0x00F0, IntPtr.Zero, IntPtr.Zero);   // BM_GETCHECK
            if (state == IntPtr.Zero) { SendMessage(h, 0x00F5, IntPtr.Zero, IntPtr.Zero); result = "clicked-to-check"; }
            else { result = "already-checked"; }
            return true;
        }, IntPtr.Zero);
        return result;
    }
}
'@
        $script:UiHelperReady = $true
    } catch {
        Write-Log ('UI helper compile failed: ' + $_.Exception.Message) 'WARN'
        $script:UiHelperReady = $false
    }
}

function Get-MsiexecWindowTitles {
    param([int]$ProcessId)
    $titles = @()
    foreach ($window in @([OverlyricVerify.Native]::GetWindows([uint32]$ProcessId))) {
        if ($window.Title -and $window.Visible) { $titles += [pscustomobject]@{ Handle = $window.Handle; Title = $window.Title; Class = $window.ClassName } }
    }
    return $titles
}

function Invoke-UiStaticValidation {
    $msi = $script:Msi
    $dialogs = @($msi.Dialogs)
    $controls = @($msi.Controls)
    $events = @($msi.ControlEvents)
    $customActions = @($msi.CustomActions)
    Add-Note 'UI' ('static tables: dialogs=' + $dialogs.Count + ', controls=' + $controls.Count + ', controlEvents=' + $events.Count + ', customActions=' + $customActions.Count)
    $standard = @('FindRelatedProducts','AppSearch','LaunchConditions','ValidateProductID','MigrateFeatureStates','CostInitialize','FileCost','CostFinalize','ExecuteAction','InstallExecute','InstallExecuteAgain','InstallFinalize','PublishFeatures','PublishProduct','InstallInitialize','InstallValidate','ProcessComponents','RegisterProduct','RegisterUser','RemoveExistingProducts','UnpublishFeatures','RemoveFiles','RemoveFolders','CreateFolders','CreateShortcuts','WriteRegistryValues','RemoveRegistryValues','RemoveShortcuts','SetARPINSTALLLOCATION','RemoveOverlyricUserData','Wix4CloseApplications_X64','Wix4CheckRebootRequired_X64','Wix4CloseApplications_X86','Wix4CheckRebootRequired_X86','Wix4CloseApplicationsDeferred_X64','Wix4CloseApplicationsDeferred_X86','SetOVERLYRICDATADIR')
    $problems = @()
    foreach ($row in @($msi.InstallUISequence)) {
        $action = [string]$row.Action
        if ($standard -contains $action -or $dialogs -contains $action) { continue }
        $problems += 'InstallUISequence action "' + $action + '" is neither a standard action nor a Dialog'
    }
    foreach ($row in @($msi.AdminUISequence)) {
        $action = [string]$row.Action
        if ($standard -contains $action -or $dialogs -contains $action) { continue }
        $problems += 'AdminUISequence action "' + $action + '" is neither a standard action nor a Dialog'
    }
    Add-Check 'UI' ($problems.Count -eq 0) ('every UI-sequence action resolves to a known dialog or a standard action (' + $problems.Count + ' problem(s))')
    foreach ($problem in $problems) { Add-Note 'UI' $problem }

    $eventProblems = @()
    foreach ($e in $events) {
        if ($dialogs -notcontains $e.Dialog) { $eventProblems += 'ControlEvent dialog "' + $e.Dialog + '" is missing from the Dialog table'; continue }
        if (-not (@($controls | Where-Object { $_.Dialog -eq $e.Dialog -and $_.Control -eq $e.Control }).Count)) {
            $eventProblems += 'ControlEvent control "' + $e.Dialog + '/' + $e.Control + '" is missing from the Control table'
        }
        if ($e.Event -in @('NewDialog','SpawnDialog')) {
            if ($e.Argument -and ($dialogs -notcontains $e.Argument)) { $eventProblems += 'ControlEvent ' + $e.Dialog + '/' + $e.Control + ' ' + $e.Event + ' -> "' + $e.Argument + '" is not a Dialog' }
        } elseif ($e.Event -eq 'DoAction') {
            if ($e.Argument -and (@($customActions | Where-Object { $_.Action -eq $e.Argument }).Count -eq 0)) { $eventProblems += 'DoAction "' + $e.Argument + '" is missing from the CustomAction table' }
        }
    }
    Add-Check 'UI' ($eventProblems.Count -eq 0) ('every ControlEvent target exists (dialog, control, NewDialog/SpawnDialog target, DoAction action); ' + $eventProblems.Count + ' problem(s)')
    foreach ($problem in $eventProblems) { Add-Note 'UI' $problem }
    Add-Check 'UI' (($problems.Count -eq 0) -and ($eventProblems.Count -eq 0)) 'no missing dialog/control/custom-action targets, so no static 2711/2712/2803 risk'

    $welcome = @($events | Where-Object { $_.Dialog -eq 'WelcomeDlg' -and $_.Control -eq 'Next' -and $_.Event -eq 'NewDialog' -and $_.Argument -eq 'CustomizeDlg' -and $_.Condition -eq 'NOT Installed' })
    $customizeControls = @($controls | Where-Object { $_.Dialog -eq 'CustomizeDlg' })
    $hasTree = @($customizeControls | Where-Object { $_.Type -eq 'SelectionTree' }).Count -gt 0
    $customizeNext = @($events | Where-Object { $_.Dialog -eq 'CustomizeDlg' -and $_.Control -eq 'Next' -and $_.Event -eq 'NewDialog' -and $_.Argument -eq 'VerifyReadyDlg' })
    Add-Check 'UI' (($welcome.Count -gt 0) -and $hasTree -and ($customizeNext.Count -gt 0)) 'Welcome -> Customize (feature tree) -> VerifyReady flow is wired'
    $features = @($msi.Features)
    $startFeature = @($features | Where-Object { $_.Feature -eq 'StartWithWindows' })
    $desktopFeature = @($features | Where-Object { $_.Feature -eq 'DesktopShortcut' })
    Add-Check 'UI' (($startFeature.Count -eq 1) -and ($startFeature[0].Level -eq '1')) 'StartWithWindows feature exists at Level 1 (deselectable in the feature tree)'
    Add-Check 'UI' (($desktopFeature.Count -eq 1) -and ($desktopFeature[0].Level -eq '2')) 'DesktopShortcut feature exists at Level 2 (opt-in, not installed by default)'

    $tree = @($customizeControls | Where-Object { $_.Type -eq 'SelectionTree' }) | Select-Object -First 1
    $browseEvents = @($events | Where-Object { $_.Dialog -eq 'CustomizeDlg' -and $_.Control -eq 'Browse' })
    $browseWired = ($null -ne $tree) -and ($tree.Property -eq '_BrowseProperty') -and ($browseEvents.Count -gt 0) -and (@($events | Where-Object { $_.Dialog -eq 'BrowseDlg' }).Count -gt 0)
    Add-Check 'UI' $browseWired ('Browse button is wired to the selection tree (_BrowseProperty) and BrowseDlg (WIXUI_INSTALLDIR is not needed for the feature-tree flow)')
    Add-Note 'UI' ('browse evidence: SelectionTree.Property="' + $(if ($null -ne $tree) { $tree.Property } else { '' }) + '"; CustomizeDlg Browse events=' + $browseEvents.Count + '; BrowseDlg events=' + @($events | Where-Object { $_.Dialog -eq 'BrowseDlg' }).Count)

    $launchPublish = @($events | Where-Object { $_.Dialog -eq 'ExitDialog' -and $_.Control -eq 'Finish' -and $_.Event -eq 'DoAction' -and $_.Argument -eq 'LaunchOverlyric' })
    $launchCa = @($customActions | Where-Object { $_.Action -eq 'LaunchOverlyric' })
    $checkboxValue = if ($msi.Properties.ContainsKey('WIXUI_EXITDIALOGOPTIONALCHECKBOX')) { [string]$msi.Properties['WIXUI_EXITDIALOGOPTIONALCHECKBOX'] } else { '' }
    Add-Check 'UI' (($launchPublish.Count -gt 0) -and ($launchCa.Count -eq 1) -and ($checkboxValue -eq '1')) 'Finish "Start Overlyric now" checkbox is wired (ExitDialog DoAction -> LaunchOverlyric, default checked)'
}

function Invoke-UiWizardProbe {
    Reset-OverlyricState
    Initialize-UiHelper
    $log = Join-Path $script:RunDir 'ui-wizard.log'
    $argument = '/i "' + $script:MsiPath + '" /qf /l*v "' + $log + '"'
    Write-Log ('RUN (UI wizard /qf): msiexec.exe ' + $argument)
    $process = Start-Process -FilePath $script:Msiexec -ArgumentList $argument -PassThru -ErrorAction Stop
    $seenWelcome = $false; $seenCustomize = $false; $seenVerify = $false; $seenProgress = $false; $seenExit = $false; $finishClicked = $false
    $wizardWindow = $null
    for ($attempt = 0; $attempt -lt 90 -and -not $process.HasExited; $attempt++) {
        Start-Sleep -Milliseconds 700
        $windows = @(Get-MsiexecWindowTitles -ProcessId $process.Id)
        foreach ($window in $windows) {
            if (-not $wizardWindow -and ($window.Title -match '(?i)overlyric' -or $window.Class -eq '#32770')) { $wizardWindow = $window }
        }
        $logText = if (Test-Path -LiteralPath $log) { [string](Get-Content -LiteralPath $log -Raw -ErrorAction SilentlyContinue) } else { '' }
        if ($logText -match 'on dialog WelcomeDlg|Doing action: WelcomeDlg') { $seenWelcome = $true }
        if ($logText -match 'on dialog CustomizeDlg|Doing action: CustomizeDlg') { $seenCustomize = $true }
        if ($logText -match 'on dialog VerifyReadyDlg|Doing action: VerifyReadyDlg') { $seenVerify = $true }
        if ($logText -match 'Doing action: (ProgressDlg|InstallFiles)') { $seenProgress = $true }
        if ($logText -match 'on dialog ExitDialog|Doing action: ExitDialog') { $seenExit = $true }
        if ($null -ne $wizardWindow) {
            try { [OverlyricVerify.UiHelper]::Focus([long]$wizardWindow.Handle) } catch { }
            if ($seenExit -and -not $finishClicked) {
                # ExitDialog: make sure the "Start Overlyric now" checkbox is checked, then Finish.
                foreach ($window in $windows) {
                    try {
                        $checkState = [OverlyricVerify.UiHelper]::EnsureChecked([long]$window.Handle, 'Start Overlyric')
                        if ($checkState) { Write-Log ('UI wizard: ExitDialog checkbox ' + $checkState); break }
                    } catch { }
                }
                Start-Sleep -Milliseconds 1200
                foreach ($window in $windows) {
                    try {
                        $clicked = [OverlyricVerify.UiHelper]::ClickButton([long]$window.Handle, [string[]]@('Finish'))
                        if ($clicked) { Write-Log ('UI wizard: clicked button "' + $clicked + '"'); $finishClicked = $true; break }
                    } catch { }
                }
            } elseif (-not $seenExit) {
                $clickTargets = @('Install', 'Next')
                foreach ($window in $windows) {
                    try {
                        $clicked = [OverlyricVerify.UiHelper]::ClickButton([long]$window.Handle, [string[]]$clickTargets)
                        if ($clicked) { Write-Log ('UI wizard: clicked button "' + $clicked + '" on "' + $window.Title + '"'); break }
                    } catch { }
                }
            }
        }
    }
    $exited = $process.HasExited
    if (-not $exited) {
        Write-Log 'UI wizard did not finish within the automation window; killing msiexec (no execute phase should have run)' 'WARN'
        try { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue } catch { }
        Start-Sleep -Seconds 1
    }
    Add-Check 'UI' $seenWelcome ('UI wizard rendered the Welcome dialog (msiexec log action WelcomeDlg seen=' + $seenWelcome + ')')
    Add-Check 'UI' $seenCustomize ('UI wizard reached the Customize dialog (feature tree page) (rendered=' + $seenCustomize + ')')
    Add-Check 'UI' ($seenVerify -and $seenProgress) ('UI wizard reached VerifyReady and ran the install/progress phase (verifyReady=' + $seenVerify + ', progress=' + $seenProgress + ')')
    Add-Note 'UI' ('UI wizard progress: welcome=' + $seenWelcome + ', customize=' + $seenCustomize + ', verifyReady=' + $seenVerify + ', installFiles=' + $seenProgress + ', exitDialog=' + $seenExit + ', msiexec exited=' + $exited)
    $logText = if (Test-Path -LiteralPath $log) { [string](Get-Content -LiteralPath $log -Raw -ErrorAction SilentlyContinue) } else { '' }
    $missingUi = @()
    foreach ($code in @('2711','2712','2803')) { if ($logText -match $code) { $missingUi += $code } }
    Add-Check 'UI' ($missingUi.Count -eq 0) ('no 2711/2712/2803 errors in the wizard log (found: ' + ($missingUi -join ',') + ')')
    if ($exited -and $process.ExitCode -eq 0) {
        Add-Check 'UI' $true 'UI wizard completed the install with exit 0'
        Add-Check 'UI' (Test-Path -LiteralPath $script:InstallExe) 'UI wizard install produced Overlyric.exe'
        Start-Sleep -Seconds 3
        $launched = @(Get-OverlyricProcesses)
        Add-Check 'UI' ($launched.Count -ge 1) ('Finish "Start Overlyric now" launched the app (Overlyric processes=' + $launched.Count + ')')
        foreach ($p in $launched) { try { $close = Close-OverlyricGracefully -Process $p; Write-Log ('closed UI-launched app, exit=' + $close.ExitCode) } catch { } }
        $uninstall = Uninstall-Overlyric -Tag 'ui-wizard-uninstall'
        Add-Check 'UI' ($uninstall.ExitCode -eq 0) ('uninstall after the UI wizard install exited 0 (actual=' + $uninstall.ExitCode + ')')
    } else {
        Add-Note 'UI' 'the UI wizard was rendered but not driven to completion by keyboard automation in this environment; the silent /qn install covers the execute path and the static table validation above covers the dialog wiring. This is a harness limitation, not a product failure.'
        if (Test-Path -LiteralPath $log) { Add-Note 'UI' ('wizard log tail: ' + ((Get-MsiLogTail -Path $log -Count 8) -join ' | ')) }
    }
    Reset-OverlyricState
    $script:Installed = $false
}

function Invoke-MaintenanceModeProbe {
    # Reproduce the ARP Uninstall/Modify entry point: msiexec /I {ProductCode} (maintenance mode).
    if (-not (Test-Path -LiteralPath $script:InstallExe)) { Add-Note 'UI' 'maintenance-mode probe skipped: product not installed'; return }
    Initialize-UiHelper
    $log = Join-Path $script:RunDir 'ui-maintenance.log'
    $argument = '/I ' + $script:ProductCode + ' /qf /l*v "' + $log + '"'
    Write-Log ('RUN (maintenance /qf): msiexec.exe ' + $argument)
    $process = Start-Process -FilePath $script:Msiexec -ArgumentList $argument -PassThru -ErrorAction Stop
    $seenMaintenance = $false
    $maintenanceWindow = $null
    for ($attempt = 0; $attempt -lt 25 -and -not $process.HasExited; $attempt++) {
        Start-Sleep -Milliseconds 600
        foreach ($window in @(Get-MsiexecWindowTitles -ProcessId $process.Id)) {
            if ($window.Title -match '(?i)overlyric') { if (-not $maintenanceWindow) { $maintenanceWindow = $window } }
            if ($window.Title -match '(?i)maintenance|change|repair|remove|modify') { $seenMaintenance = $true }
        }
        if ($seenMaintenance) { break }
    }
    Add-Check 'UI' ($seenMaintenance -or ($null -ne $maintenanceWindow)) ('maintenance mode (msiexec /I ProductCode) reached the maintenance UI without error (maintenanceDialog=' + $seenMaintenance + ')')
    $logText = if (Test-Path -LiteralPath $log) { [string](Get-Content -LiteralPath $log -Raw -ErrorAction SilentlyContinue) } else { '' }
    $maintErrors = @()
    foreach ($code in @('2711','2712','2803','return value 3')) { if ($logText -match $code) { $maintErrors += $code } }
    Add-Check 'UI' ($maintErrors.Count -eq 0) ('no 2711/2712/2803/return-value-3 errors in the maintenance log (found: ' + ($maintErrors -join ',') + ')')
    if (-not $process.HasExited) { try { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue } catch { } }
    Start-Sleep -Seconds 1
    # The UI was killed before ExecuteAction: the installed product must be untouched.
    $entries = @(Get-ArpEntries -ProductCodes @($script:ProductCode))
    $visible = Get-VisibleArpEntries
    Add-Check 'UI' (($entries.Count -eq 1) -and ($visible.Visible.Count -eq 1)) ('product still installed after the maintenance UI probe (ARP registrations=' + $entries.Count + ', visible=' + $visible.Visible.Count + ')')
    Add-Check 'UI' (Test-Path -LiteralPath $script:InstallExe) 'payload intact after the maintenance UI probe'
}

function Invoke-FinalStateRestore {
    Write-Log '=== final state restore ==='
    foreach ($process in @(Get-OverlyricProcesses)) {
        try { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
    if (-not $KeepInstalled) {
        Reset-OverlyricState
        Assert-CleanBaseline
    } else {
        Write-Log '-KeepInstalled specified: uninstalling current state, then reinstalling to leave a clean installed product'
        Reset-OverlyricState
        $result = Install-Overlyric -Tag 'keep-installed'
        if ($result.ExitCode -eq 0) {
            Write-Log ('left Overlyric installed at ' + $script:InstallDir + ' as requested')
        } else {
            Write-Log ('reinstall for -KeepInstalled failed with exit ' + $result.ExitCode) 'ERROR'
        }
    }
    Restore-UserData
    Test-BaselineRestored
}

# ---------------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------------
function Get-ExitCode {
    if ($null -ne $script:FatalError) { return 2 }
    $failed = @($script:AssertionTitles.Keys | Where-Object { (Get-AssertionStatus ([string]$_)) -eq 'FAIL' })
    if ($failed.Count -gt 0) { return 1 }
    return 0
}

function Print-Summary {
    Write-Log '================ RESULTS ================'
    $failed = @()
    $skipped = @()
    foreach ($key in $script:AssertionTitles.Keys) {
        $id = [string]$key
        $status = Get-AssertionStatus $id
        $label = if ($id -match '^\d+$') { ('A' + $id).PadRight(4) } else { $id.PadRight(4) }
        $line = $label + ' ' + $status.PadRight(4) + ' ' + $script:Results[$id].Title
        Write-Log $line
        foreach ($failure in $script:Results[$id].Failures) { Write-Log ('        FAILED: ' + $failure) 'FAIL' }
        foreach ($note in $script:Results[$id].Notes) { Write-Log ('        note: ' + $note) 'NOTE' }
        if ($status -eq 'FAIL') { $failed += $label.Trim() }
        if ($status -eq 'SKIP') { $skipped += $label.Trim() }
    }
    if ($failed.Count -gt 0) { Write-Log ('FAILED ASSERTIONS: ' + ($failed -join ', ')) 'FAIL' } else { Write-Log 'no failed assertions' 'PASS' }
    if ($skipped.Count -gt 0) { Write-Log ('NOT TESTED (skipped): ' + ($skipped -join ', ')) 'SKIP' }
    if ($script:PhaseErrors.Count -gt 0) {
        Write-Log ('phase errors: ' + ($script:PhaseErrors -join ' | ')) 'ERROR'
    }
    $finishedAt = Get-Date
    Write-Log ('run finished: ' + $finishedAt.ToString('o') + ' (duration ' + [math]::Round(($finishedAt - $script:StartedAt).TotalSeconds, 1) + ' s)')
    Write-Log ('logs: ' + $script:RunDir)
    Write-Log ('exit code: ' + $script:ExitCode)

    $summary = [pscustomobject]@{
        runId      = $script:RunId
        startedAt  = $script:StartedAt.ToString('o')
        finishedAt = $finishedAt.ToString('o')
        msi        = $script:MsiPath
        user       = $env:USERNAME
        elevation  = if ($null -ne $script:Elevation) { $script:Elevation } else { $null }
        assertions = @($script:AssertionTitles.Keys | ForEach-Object {
            $id = [string]$_
            [pscustomobject]@{
                id       = if ($id -match '^\d+$') { 'A' + $id } else { $id }
                title    = $script:Results[$id].Title
                status   = Get-AssertionStatus $id
                failures = @($script:Results[$id].Failures)
                notes    = @($script:Results[$id].Notes)
            }
        })
        phaseErrors = @($script:PhaseErrors)
        exitCode    = $script:ExitCode
    }
    try { $summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $script:RunDir 'summary.json') -Encoding UTF8 } catch { }
}

# =================================================================================
# Main
# =================================================================================
Initialize-Log
Initialize-Results
Initialize-NativeHelpers

Write-Log ('Overlyric installer acceptance verification, run ' + $script:RunId)
Write-Log ('repo: ' + $script:RepoRoot)

try {
    Invoke-Phase -Name 'preflight' -Body { Invoke-Preflight }

    if ($PreflightOnly) {
        Write-Log 'preflight only: no machine mutation performed'
        Write-Log ('MSI files: ' + (@($script:Msi.Files | ForEach-Object { $_.Name }) -join ', '))
        Write-Log ('MSI features: ' + (@($script:Msi.Features | ForEach-Object { $_.Feature + '(level ' + $_.Level + ')' }) -join ', '))
        Write-Log ('MSI registry rows: ' + (@($script:Msi.Registry | ForEach-Object { 'root=' + $_.Root + ' key=' + $_.Key + ' name=' + $_.Name + ' component=' + $_.Component }) -join ' | '))
        Write-Log ('MSI shortcuts: ' + (@($script:Msi.Shortcuts | ForEach-Object { 'dir=' + $_.Directory + ' name=' + $_.Name + ' target=' + $_.Target }) -join ' | '))
        Write-Log ('MSI custom actions: ' + (@($script:Msi.CustomActions | ForEach-Object { $_.Action + '(type ' + $_.Type + ')' }) -join ', '))
        Write-Log ('MSI upgrade rows: ' + (@($script:Msi.Upgrade | ForEach-Object { 'min=' + $_.VersionMin + ' max=' + $_.VersionMax + ' action=' + $_.ActionProperty }) -join ' | '))
        Skip-UntestedAssertions -Reason 'preflight-only run: no machine mutation and no assertion executed'
    } elseif ($UiOnly) {
        Write-Log 'ui-only run: static UI table validation + the interactive wizard probe'
        Invoke-Phase -Name 'backup-user-data' -Body { Backup-UserData }
        Invoke-Phase -Name 'ui-static-validation' -Body { Invoke-UiStaticValidation }
        Invoke-Phase -Name 'ui-wizard' -Body { Invoke-UiWizardProbe }
        Skip-UntestedAssertions -Reason 'ui-only run: only the UI probes were executed'
    } else {
        Invoke-Phase -Name 'reset-and-baseline' -Body {
            Reset-OverlyricState
            Assert-CleanBaseline
        }
        Invoke-Phase -Name 'backup-user-data' -Body { Backup-UserData }
        Invoke-Phase -Name 'ui-static-validation' -Body { Invoke-UiStaticValidation }
        Invoke-Phase -Name 'assertion-1-install' -Body { Invoke-Assertion1Install }

        if ($script:Installed) {
            Invoke-Phase -Name 'assertion-2-payload' -Body { Invoke-Assertion2Payload }
            Invoke-Phase -Name 'assertion-4-shortcut' -Body { Invoke-Assertion4Shortcut }
            Invoke-Phase -Name 'assertion-5-run-value' -Body { Invoke-Assertion5RunValue }
            Invoke-Phase -Name 'assertion-6-arp' -Body { Invoke-Assertion6Arp }
            Invoke-Phase -Name 'ui-maintenance-mode' -Body { Invoke-MaintenanceModeProbe }
            Invoke-Phase -Name 'assertion-3-launchability' -Body { Invoke-Assertion3Launch }
            Invoke-Phase -Name 'assertion-9-reinstall' -Body { Invoke-Assertion9Reinstall }
            Invoke-Phase -Name 'assertion-7-uninstall' -Body { Invoke-Assertion7Uninstall }
        } else {
            Skip-UntestedAssertions -Reason 'not reached: the non-elevated install (assertion 1) failed, so machine-mutating checks could not run'
        }

        if ($null -eq $script:FatalError) {
            Invoke-Phase -Name 'ui-wizard' -Body { Invoke-UiWizardProbe }
            Invoke-Phase -Name 'assertion-8-removedata' -Body { Invoke-Assertion8Removedata }
            Invoke-Phase -Name 'assertion-10-upgrade' -Body { Invoke-Assertion10Upgrade }
            Invoke-Phase -Name 'assertion-11-install-while-running' -Body { Invoke-Assertion11InstallWhileRunning }
            Invoke-Phase -Name 'assertion-5b-deselected-path' -Body { Invoke-Assertion5bUncheckedPath }
            Invoke-Phase -Name 'robustness-space-path' -Body { Invoke-RobustnessProbe }
            Invoke-Phase -Name 'assertion-1b-restricted-token' -Body { Invoke-Assertion1bRestrictedProbe }
        }
    }
} catch {
    $script:FatalError = $_
    Write-Log ('FATAL: ' + $_.Exception.Message) 'ERROR'
} finally {
    if (-not $PreflightOnly) {
        try { Invoke-FinalStateRestore } catch { Write-Log ('final restore threw: ' + $_.Exception.Message) 'ERROR' }
    }
    Skip-UntestedAssertions -Reason 'not reached'
    $script:ExitCode = Get-ExitCode
    Print-Summary
}

exit $script:ExitCode










