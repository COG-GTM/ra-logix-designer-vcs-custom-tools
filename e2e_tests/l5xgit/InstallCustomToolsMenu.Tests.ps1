#Requires -Modules Pester
param(
    [switch]$Debug
)

$env:E2E_DEBUG = if ($Debug) { '1' } else { '0' }

BeforeAll {
    . "$PSScriptRoot/../Helpers.ps1"
    . "$PSScriptRoot/../../Install-CustomToolsMenu.ps1"

    $currentUserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value

    function New-DirectoryWithInstallerAcl {
        param([string]$Path)
        # Same DACL the installer applies; the owner stays the (possibly non-elevated) test user.
        $sddl = (New-AdminOnlyDirectorySecurity).GetSecurityDescriptorSddlForm('Access')
        $security = [System.Security.AccessControl.DirectorySecurity]::new()
        $security.SetSecurityDescriptorSddlForm($sddl, 'Access')
        [System.IO.FileSystemAclExtensions]::Create([System.IO.DirectoryInfo]::new($Path), $security)
    }
}

Describe 'Install-CustomToolsMenu.ps1' -Tag 'Windows' {

    BeforeEach {
        $tempDir = New-TestTempDir -Prefix 'install_menu'
    }

    AfterEach {
        $ProgressPreference = 'SilentlyContinue'
        if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
    }

    Context 'Update-CustomToolsMenu' {
        It 'points every Command and ToolbarIcon at the install folder' {
            $buildOut = 'C:\src\ra-logix-designer-vcs-custom-tools\artifacts\bin\Release\'
            $template = Join-Path $repoRoot 'src/l5xgit/Assets/CustomToolsMenu.xml'
            $built = Join-Path $tempDir 'built.xml'
            (Get-Content -LiteralPath $template -Raw).Replace('@@OUTDIR@@', $buildOut) | Set-Content -LiteralPath $built -NoNewline

            $installDir = 'C:\Program Files\Rockwell Software\l5xgit'
            $output = Join-Path $tempDir 'installed.xml'
            Update-CustomToolsMenu -MenuPath $built -InstallDir $installDir -OutputPath $output

            $xml = [xml](Get-Content -LiteralPath $output -Raw)
            $tools = $xml.CustomToolsContent.CustomTool
            $tools.Count | Should -Be 4
            foreach ($tool in $tools) {
                $tool.Command | Should -Be "$installDir\l5xgit.exe"
                $tool.ToolbarIcon.'#text' | Should -BeLike "$installDir\Assets\*.bmp"
            }
            (Get-Content -LiteralPath $output -Raw) | Should -Not -Match ([regex]::Escape('artifacts\bin'))
            $xml.CustomToolsContent.CustomTool[0].Arguments | Should -Be 'commit --acd "%project_path%" --pause-if-error'
        }

        It 'rejects a Command that is not l5xgit.exe' {
            $menu = Join-Path $tempDir 'menu.xml'
            Set-Content -LiteralPath $menu -Value '<?xml version="1.0" encoding="UTF-8"?><CustomToolsContent SchemaRevision="1.0"><CustomTool><Command>C:\Windows\System32\cmd.exe</Command></CustomTool></CustomToolsContent>'
            { Update-CustomToolsMenu -MenuPath $menu -InstallDir 'C:\x' -OutputPath (Join-Path $tempDir 'out.xml') } |
                Should -Throw '*Only l5xgit.exe is installed*'
        }
    }

    Context 'Get-UnsafeAccess' {
        It 'accepts a folder with the installer ACL' {
            $dir = Join-Path $tempDir 'locked'
            New-DirectoryWithInstallerAcl -Path $dir
            @(Get-UnsafeAccess -Path $dir -RightsMask $WriteRightsMask -AdditionalTrustedOwners $currentUserSid) | Should -BeNullOrEmpty
        }

        It 'flags a folder that BUILTIN\Users can modify' {
            $dir = Join-Path $tempDir 'writable'
            New-Item -ItemType Directory -Path $dir | Out-Null
            $acl = Get-Acl -LiteralPath $dir
            $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new(
                [System.Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'), 'Modify', 'ContainerInherit, ObjectInherit', 'None', 'Allow'))
            Set-Acl -LiteralPath $dir -AclObject $acl

            $problems = @(Get-UnsafeAccess -Path $dir -RightsMask $WriteRightsMask -AdditionalTrustedOwners $currentUserSid)
            ($problems -join "`n") | Should -Match 'S-1-5-32-545'
        }

        It 'flags a folder owned by a non-administrator' {
            $dir = Join-Path $tempDir 'owned'
            New-DirectoryWithInstallerAcl -Path $dir
            @(Get-UnsafeAccess -Path $dir -RightsMask $WriteRightsMask) | Should -Not -BeNullOrEmpty
        }

        It 'flags a junction' {
            $target = Join-Path $tempDir 'target'
            New-Item -ItemType Directory -Path $target | Out-Null
            $link = Join-Path $tempDir 'link'
            New-Item -ItemType Junction -Path $link -Target $target | Out-Null

            ((Get-UnsafeAccess -Path $link -RightsMask $WriteRightsMask -AdditionalTrustedOwners $currentUserSid) -join "`n") |
                Should -Match 'symbolic link or junction'
        }
    }

    Context 'Assert-InstallLocationIsAdminOnly' {
        It 'refuses when a file inside the install folder is writable by Users' {
            $dir = Join-Path $tempDir 'install'
            New-Item -ItemType Directory -Path $dir | Out-Null
            $dll = Join-Path $dir 'l5xgit.dll'
            Set-Content -LiteralPath $dll -Value 'x'
            $acl = Get-Acl -LiteralPath $dll
            $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new(
                [System.Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'), 'Modify', 'Allow'))
            Set-Acl -LiteralPath $dll -AclObject $acl

            # Lock the folder down afterwards (DACL only, so a non-elevated test user can do it as owner).
            $sddl = (New-AdminOnlyDirectorySecurity).GetSecurityDescriptorSddlForm('Access')
            $dirAcl = Get-Acl -LiteralPath $dir
            $dirAcl.SetSecurityDescriptorSddlForm($sddl, 'Access')
            Set-Acl -LiteralPath $dir -AclObject $dirAcl

            @(Get-UnsafeAccess -Path $dir -RightsMask $WriteRightsMask -AdditionalTrustedOwners $currentUserSid) | Should -BeNullOrEmpty

            { Assert-InstallLocationIsAdminOnly -Path $dir -AdditionalTrustedOwners $currentUserSid } |
                Should -Throw '*l5xgit.dll*'
        }
    }
}
