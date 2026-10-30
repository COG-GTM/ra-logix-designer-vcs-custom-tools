<#
.SYNOPSIS
    Installs l5xgit into an administrator-only folder and registers it as Logix Designer custom tools.

.DESCRIPTION
    CustomToolsMenu.xml in the Logix Designer common directory applies to every user of the machine,
    so every program it launches must live in a folder that only administrators can modify. This script:

      1. Copies the contents of artifacts\bin\<Configuration>\ (l5xgit.exe, its DLLs, runtime config
         files and Assets) into -InstallDir, which is created (or reset) with an access control list
         that grants write access only to Administrators and SYSTEM.
      2. Refuses to continue if -InstallDir, anything inside it, or any of its parent folders can be
         modified, renamed or re-permissioned by any other principal.
      3. Rewrites every <Command> and <ToolbarIcon> in CustomToolsMenu.xml to point at -InstallDir and
         copies the result to
         C:\Program Files (x86)\Rockwell Software\RSLogix 5000\Common\CustomToolsMenu.xml

    The script self-elevates to Administrator if not already running elevated.
    Re-run it after every rebuild; Logix Designer never runs l5xgit from the build output folder.

.PARAMETER Configuration
    Build configuration to install from. Defaults to 'Release'.

.PARAMETER InstallDir
    Administrator-only folder to install l5xgit into.
    Defaults to "$env:ProgramFiles\Rockwell Software\l5xgit".
#>
param(
    [string]$Configuration = 'Release',
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'Rockwell Software\l5xgit'),
    [switch]$Elevated
)

$ErrorActionPreference = 'Stop'

$InstallMarkerName = '.l5xgit-install'

$TrustedSids = @(
    'S-1-5-32-544'                                                 # BUILTIN\Administrators
    'S-1-5-18'                                                     # NT AUTHORITY\SYSTEM
    'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464' # NT SERVICE\TrustedInstaller
)

# Rights that let a principal change the contents of a file/folder or its permissions.
$WriteRightsMask = [int64](
    [System.Security.AccessControl.FileSystemRights]::WriteData -bor
    [System.Security.AccessControl.FileSystemRights]::AppendData -bor
    [System.Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor
    [System.Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
    [System.Security.AccessControl.FileSystemRights]::WriteAttributes -bor
    [System.Security.AccessControl.FileSystemRights]::Delete -bor
    [System.Security.AccessControl.FileSystemRights]::ChangePermissions -bor
    [System.Security.AccessControl.FileSystemRights]::TakeOwnership
) -bor 0x40000000 -bor 0x10000000 # GENERIC_WRITE, GENERIC_ALL

# Rights on a parent folder that let a principal rename/replace the install folder or re-permission
# the parent. Creating new sibling folders (e.g. the default AppendData on C:\) is harmless.
$AncestorRightsMask = [int64](
    [System.Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
    [System.Security.AccessControl.FileSystemRights]::Delete -bor
    [System.Security.AccessControl.FileSystemRights]::ChangePermissions -bor
    [System.Security.AccessControl.FileSystemRights]::TakeOwnership
) -bor 0x10000000 # GENERIC_ALL

function Get-UnsafeAccess {
    <#
    .SYNOPSIS
        Returns a description of every way a non-administrator principal can tamper with $Path.
        An empty result means the path is administrator-only for the given rights mask.
    #>
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][int64]$RightsMask,
        [string[]]$AdditionalTrustedOwners = @()
    )

    $problems = [System.Collections.Generic.List[string]]::new()
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
        $problems.Add("'$Path' is a symbolic link or junction")
        return $problems
    }

    $acl = Get-Acl -LiteralPath $Path
    $sidType = [System.Security.Principal.SecurityIdentifier]

    $owner = $acl.GetOwner($sidType).Value
    if ($owner -notin ($TrustedSids + $AdditionalTrustedOwners)) {
        $problems.Add("'$Path' is owned by $(Resolve-Sid $owner)")
    }

    foreach ($rule in $acl.GetAccessRules($true, $true, $sidType)) {
        if ($rule.AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Allow) { continue }
        if ($rule.PropagationFlags -band [System.Security.AccessControl.PropagationFlags]::InheritOnly) { continue }
        if ($rule.IdentityReference.Value -in $TrustedSids) { continue }
        if (([int64]$rule.FileSystemRights -band $RightsMask) -ne 0) {
            $problems.Add("'$Path' grants $($rule.FileSystemRights) to $(Resolve-Sid $rule.IdentityReference.Value)")
        }
    }
    return $problems
}

function Resolve-Sid {
    param([string]$Sid)
    try {
        $name = ([System.Security.Principal.SecurityIdentifier]$Sid).Translate([System.Security.Principal.NTAccount]).Value
        return "$name ($Sid)"
    }
    catch {
        return $Sid
    }
}

function New-AdminOnlyDirectorySecurity {
    $inherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $none = [System.Security.AccessControl.PropagationFlags]::None
    $allow = [System.Security.AccessControl.AccessControlType]::Allow
    $sid = { param($s) [System.Security.Principal.SecurityIdentifier]::new($s) }

    $security = [System.Security.AccessControl.DirectorySecurity]::new()
    $security.SetOwner((& $sid 'S-1-5-32-544'))
    $security.SetAccessRuleProtection($true, $false)
    foreach ($admin in 'S-1-5-32-544', 'S-1-5-18') {
        $security.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new(
            (& $sid $admin), [System.Security.AccessControl.FileSystemRights]::FullControl, $inherit, $none, $allow))
    }
    # BUILTIN\Users: read and run only.
    $security.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new(
        (& $sid 'S-1-5-32-545'), [System.Security.AccessControl.FileSystemRights]::ReadAndExecute, $inherit, $none, $allow))
    return $security
}

function Get-ExistingAncestors {
    param([Parameter(Mandatory)][string]$Path)
    $ancestors = @()
    $parent = Split-Path -Path $Path -Parent
    while ($parent) {
        if (Test-Path -LiteralPath $parent) { $ancestors += $parent }
        $next = Split-Path -Path $parent -Parent
        if ($next -eq $parent) { break }
        $parent = $next
    }
    return $ancestors
}

function Assert-InstallLocationIsAdminOnly {
    <#
    .SYNOPSIS
        Throws unless $Path, everything inside it, and every existing parent folder are administrator-only.
    #>
    param(
        [Parameter(Mandatory)][string]$Path,
        [string[]]$AdditionalTrustedOwners = @()
    )

    $problems = [System.Collections.Generic.List[string]]::new()
    foreach ($ancestor in Get-ExistingAncestors -Path $Path) {
        foreach ($p in Get-UnsafeAccess -Path $ancestor -RightsMask $AncestorRightsMask -AdditionalTrustedOwners $AdditionalTrustedOwners) { $problems.Add($p) }
    }
    foreach ($p in Get-UnsafeAccess -Path $Path -RightsMask $WriteRightsMask -AdditionalTrustedOwners $AdditionalTrustedOwners) { $problems.Add($p) }
    foreach ($child in Get-ChildItem -LiteralPath $Path -Recurse -Force) {
        foreach ($p in Get-UnsafeAccess -Path $child.FullName -RightsMask $WriteRightsMask -AdditionalTrustedOwners $AdditionalTrustedOwners) { $problems.Add($p) }
    }

    if ($problems.Count -gt 0) {
        throw ("Refusing to install: '$Path' is not administrator-only. Any user who can modify these locations " +
            "could run code as every user who clicks an l5xgit button in Logix Designer.`n  " + ($problems -join "`n  "))
    }
}

function Update-CustomToolsMenu {
    <#
    .SYNOPSIS
        Rewrites every <Command> and <ToolbarIcon> in a CustomToolsMenu.xml so it points into $InstallDir.
    #>
    param(
        [Parameter(Mandatory)][string]$MenuPath,
        [Parameter(Mandatory)][string]$InstallDir,
        [Parameter(Mandatory)][string]$OutputPath
    )

    $xml = [System.Xml.XmlDocument]::new()
    $xml.PreserveWhitespace = $true
    $xml.Load($MenuPath)

    $tools = $xml.SelectNodes('/CustomToolsContent/CustomTool')
    if ($tools.Count -eq 0) {
        throw "No <CustomTool> entries found in '$MenuPath'."
    }
    foreach ($tool in $tools) {
        $command = $tool.SelectSingleNode('Command')
        if (-not $command -or (Split-Path -Path $command.InnerText -Leaf) -ne 'l5xgit.exe') {
            throw "Unexpected <Command> in '$MenuPath': '$($command.InnerText)'. Only l5xgit.exe is installed."
        }
        $command.InnerText = Join-Path $InstallDir 'l5xgit.exe'

        $icon = $tool.SelectSingleNode('ToolbarIcon')
        if ($icon -and $icon.InnerText) {
            $icon.InnerText = Join-Path $InstallDir (Join-Path 'Assets' (Split-Path -Path $icon.InnerText -Leaf))
        }
    }

    $settings = [System.Xml.XmlWriterSettings]::new()
    $settings.Encoding = [System.Text.UTF8Encoding]::new($false)
    $writer = [System.Xml.XmlWriter]::Create($OutputPath, $settings)
    try { $xml.Save($writer) } finally { $writer.Dispose() }
}

function New-AdminOnlyDirectory {
    <#
    .SYNOPSIS
        Creates $Path, and any missing parent folders, with the administrator-only ACL from the start.
    #>
    param([Parameter(Mandatory)][string]$Path)

    $missing = [System.Collections.Generic.List[string]]::new()
    $current = $Path
    while ($current -and -not (Test-Path -LiteralPath $current)) {
        $missing.Insert(0, $current)
        $current = Split-Path -Path $current -Parent
    }
    foreach ($dir in $missing) {
        # A folder that appeared in the meantime is left alone here and rejected by Assert-InstallLocationIsAdminOnly.
        [System.IO.FileSystemAclExtensions]::Create([System.IO.DirectoryInfo]::new($dir), (New-AdminOnlyDirectorySecurity))
    }
}

function Set-AdministratorsOwner {
    <#
    .SYNOPSIS
        Makes BUILTIN\Administrators the owner of $Path and everything inside it, so no individual
        account (e.g. the installing user, who may later lose admin rights) can re-permission them.
    #>
    param([Parameter(Mandatory)][string]$Path)

    $sidType = [System.Security.Principal.SecurityIdentifier]
    $admins = [System.Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $items = @(Get-Item -LiteralPath $Path -Force) + @(Get-ChildItem -LiteralPath $Path -Recurse -Force)
    foreach ($item in $items) {
        $acl = Get-Acl -LiteralPath $item.FullName
        if ($acl.GetOwner($sidType).Value -ne $admins.Value) {
            $acl.SetOwner($admins)
            Set-Acl -LiteralPath $item.FullName -AclObject $acl
        }
    }
}

function Assert-InstallDirectoryIsReplaceable {
    param([Parameter(Mandatory)][string]$Path)
    $existing = Get-Item -LiteralPath $Path -Force
    if (-not $existing.PSIsContainer -or ($existing.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
        throw "'$Path' exists and is not a regular folder; remove it manually."
    }
    $hasContent = @(Get-ChildItem -LiteralPath $Path -Force).Count -gt 0
    if ($hasContent -and -not (Test-Path -LiteralPath (Join-Path $Path $InstallMarkerName))) {
        throw "'$Path' is not empty and was not created by this script; refusing to replace it."
    }
}

function Remove-InstallDirectory {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    Assert-InstallDirectoryIsReplaceable -Path $Path
    Remove-Item -LiteralPath $Path -Recurse -Force
}

function Assert-ExistingInstallIsTrusted {
    <#
    .SYNOPSIS
        Throws unless an existing installation was created by this script and is still administrator-only,
        so it is safe to keep as the rollback copy (or restore as the live installation).
    #>
    param(
        [Parameter(Mandatory)][string]$Path,
        [string[]]$AdditionalTrustedOwners = @()
    )
    Assert-InstallDirectoryIsReplaceable -Path $Path
    try {
        Assert-InstallLocationIsAdminOnly -Path $Path -AdditionalTrustedOwners $AdditionalTrustedOwners
    }
    catch {
        throw "The existing installation in '$Path' may have been tampered with; inspect and remove it manually, then re-run.`n$_"
    }
}

function Restore-InterruptedInstall {
    <#
    .SYNOPSIS
        If a previous run stopped between moving $InstallDir to $PreviousDir and moving the new build into place,
        puts the (validated) previous installation back so the registered tools keep working.
    #>
    param(
        [Parameter(Mandatory)][string]$InstallDir,
        [Parameter(Mandatory)][string]$PreviousDir,
        [string[]]$AdditionalTrustedOwners = @()
    )
    if ((Test-Path -LiteralPath $InstallDir) -or -not (Test-Path -LiteralPath $PreviousDir)) { return }
    Assert-ExistingInstallIsTrusted -Path $PreviousDir -AdditionalTrustedOwners $AdditionalTrustedOwners
    Move-Item -LiteralPath $PreviousDir -Destination $InstallDir
    Write-Host "Restored the installation left in '$PreviousDir' by an interrupted run." -ForegroundColor Yellow
}

# Allow the functions above to be dot-sourced (e.g. by tests) without running the installer.
if ($MyInvocation.InvocationName -eq '.') { return }

$sourceDir   = Join-Path $PSScriptRoot "artifacts\bin\$Configuration"
$sourceMenu  = Join-Path $sourceDir 'Assets\CustomToolsMenu.xml'
$destination = 'C:\Program Files (x86)\Rockwell Software\RSLogix 5000\Common\CustomToolsMenu.xml'

if (-not (Test-Path -LiteralPath $sourceMenu) -or -not (Test-Path -LiteralPath (Join-Path $sourceDir 'l5xgit.exe'))) {
    Write-Error "Build output not found in: $sourceDir`nPlease build the solution first: dotnet build -c $Configuration"
    return
}

if (-not [System.IO.Path]::IsPathFullyQualified($InstallDir)) {
    Write-Error "InstallDir must be an absolute path: $InstallDir"
    return
}
$InstallDir = [System.IO.Path]::GetFullPath($InstallDir).TrimEnd('\')

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator
)

if (-not $isAdmin) {
    Write-Host 'Requesting Administrator privileges...' -ForegroundColor Yellow
    $psArgs = @(
        '-NoProfile'
        '-ExecutionPolicy', 'Bypass'
        '-File', "`"$PSCommandPath`""
        '-Configuration', "`"$Configuration`""
        '-InstallDir', "`"$InstallDir`""
        '-Elevated'
    )
    $process = Start-Process pwsh -ArgumentList $psArgs -Verb RunAs -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        Write-Error "Elevated installation failed (exit code $($process.ExitCode))."
    }
    return
}

$stagingDir  = "$InstallDir.staging"
$previousDir = "$InstallDir.previous"

try {
    $destDir = Split-Path $destination -Parent
    if (-not (Test-Path -LiteralPath $destDir)) {
        throw "Logix Designer common directory not found: $destDir`nIs Logix Designer installed?"
    }

    $sourceFull = [System.IO.Path]::GetFullPath($sourceDir).TrimEnd('\') + '\'
    foreach ($dir in $InstallDir, $stagingDir, $previousDir) {
        if (($dir + '\').StartsWith($sourceFull, [StringComparison]::OrdinalIgnoreCase) -or
            $sourceFull.StartsWith($dir + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "InstallDir must not overlap the build output folder: $dir"
        }
    }

    # Leftovers from an interrupted run. A .previous folder without a live installation is the only working copy.
    Remove-InstallDirectory -Path $stagingDir
    Restore-InterruptedInstall -InstallDir $InstallDir -PreviousDir $previousDir
    Remove-InstallDirectory -Path $previousDir
    if (Test-Path -LiteralPath $InstallDir) {
        Assert-ExistingInstallIsTrusted -Path $InstallDir
    }

    # Stage and validate the new build next to the current installation, which stays in use until the swap.
    New-AdminOnlyDirectory -Path $stagingDir
    Assert-InstallLocationIsAdminOnly -Path $stagingDir
    try {
        Copy-Item -Path (Join-Path $sourceDir '*') -Destination $stagingDir -Recurse -Force
        New-Item -ItemType File -Path (Join-Path $stagingDir $InstallMarkerName) -Force | Out-Null
        Set-AdministratorsOwner -Path $stagingDir

        $stagedMenu = Join-Path $stagingDir 'Assets\CustomToolsMenu.xml'
        Update-CustomToolsMenu -MenuPath $stagedMenu -InstallDir $InstallDir -OutputPath $stagedMenu

        # Every file Logix Designer will run or load must be administrator-only.
        Assert-InstallLocationIsAdminOnly -Path $stagingDir

        if (Test-Path -LiteralPath $InstallDir) {
            Move-Item -LiteralPath $InstallDir -Destination $previousDir
        }
        try {
            Move-Item -LiteralPath $stagingDir -Destination $InstallDir
        }
        catch {
            if (Test-Path -LiteralPath $previousDir) {
                Move-Item -LiteralPath $previousDir -Destination $InstallDir
            }
            throw
        }
    }
    catch {
        if (Test-Path -LiteralPath $stagingDir) {
            Remove-Item -LiteralPath $stagingDir -Recurse -Force
        }
        throw
    }

    Assert-InstallLocationIsAdminOnly -Path $InstallDir
    if (Test-Path -LiteralPath $previousDir) {
        Remove-Item -LiteralPath $previousDir -Recurse -Force
    }
    Write-Host "Installed l5xgit to:`n  $InstallDir" -ForegroundColor Green

    $installedMenu = Join-Path $InstallDir 'Assets\CustomToolsMenu.xml'

    # Backup existing file
    if (Test-Path -LiteralPath $destination) {
        $i = 1
        while (Test-Path -LiteralPath "$destination.bak$i") { $i++ }
        $backupPath = "$destination.bak$i"
        Move-Item -LiteralPath $destination -Destination $backupPath
        Write-Host "Backed up existing file to:`n  $backupPath" -ForegroundColor Cyan
    }

    Copy-Item -LiteralPath $installedMenu -Destination $destination
    Write-Host "Copied:`n  $installedMenu`n  -> $destination" -ForegroundColor Green
}
catch {
    Write-Host $_ -ForegroundColor Red
    if ($Elevated) { Read-Host 'Press Enter to close' | Out-Null }
    exit 1
}
