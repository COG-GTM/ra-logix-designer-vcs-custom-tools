#Requires -Modules Pester
param(
    [switch]$Debug
)

$env:E2E_DEBUG = if ($Debug) { '1' } else { '0' }

<#
.SYNOPSIS
    A <name>_L5xGit.yml file shipped next to an ACD must not choose, on its own, the folder and
    Git repository that l5xgit writes to and runs git in.

.DESCRIPTION
    Each test builds the bundle an attacker could hand over: an ACD, a sidecar config naming a
    sibling folder, and a Git repository in that folder whose .git/config sets a difftool command
    that writes a marker file. stdin is closed by Invoke-Tool, so any approval prompt fails.
    None of these tests need the Logix Designer SDK because l5xgit must stop before converting.
#>

BeforeAll {
    . "$PSScriptRoot/../Helpers.ps1"

    if (-not (Test-Path $l5xgit)) {
        throw "l5xgit.exe not found at '$l5xgit'. Run 'dotnet build -c Release' first."
    }

    function New-UntrustedBundle {
        param([string]$DestinationOverride)

        $rootDir = New-TestTempDir -Prefix 'trust'
        $repoDir = Join-Path $rootDir 'repo'
        $marker  = Join-Path $rootDir 'difftool-ran.txt'
        New-Item -ItemType Directory -Path $repoDir -Force | Out-Null

        Push-Location $repoDir
        try {
            git init --quiet 2>&1 | Out-Null
            git config user.name  'Mallory'
            git config user.email 'mallory@example.com'
            Set-Content -Path (Join-Path $repoDir 'a.txt') -Value 'one'
            git add . 2>&1 | Out-Null
            git commit --quiet -m one 2>&1 | Out-Null
            Set-Content -Path (Join-Path $repoDir 'a.txt') -Value 'two'
            git commit --quiet -am two 2>&1 | Out-Null
            git config diff.tool t
            git config difftool.t.cmd "cmd /c echo pwned > `"$marker`""
        }
        finally { Pop-Location }

        $acdPath = Join-Path $rootDir 'Line4.ACD'
        Set-Content -Path $acdPath -Value 'not a real ACD'

        $destination = if ($DestinationOverride) { $DestinationOverride } else { $repoDir }
        $configPath = Join-Path $rootDir 'Line4_L5xGit.yml'
        "destination_path: $destination`nprompt_for_commit_message: false" | Set-Content -Path $configPath -Encoding UTF8

        $headBefore = (git -C $repoDir rev-parse HEAD).Trim()

        return @{
            RootDir    = $rootDir
            RepoDir    = $repoDir
            AcdPath    = $acdPath
            Marker     = $marker
            HeadBefore = $headBefore
        }
    }

    function Remove-Bundle([hashtable]$Bundle) {
        $ProgressPreference = 'SilentlyContinue'
        if ($Bundle -and (Test-Path $Bundle.RootDir)) { Remove-Item $Bundle.RootDir -Recurse -Force }
    }
}

Describe 'l5xgit destination approval' {

    Context 'difftool with an unapproved sidecar destination' {
        BeforeAll {
            $script:bundle = New-UntrustedBundle
            $script:result = Invoke-L5xgit @('difftool', '--acd', $script:bundle.AcdPath)
            Start-Sleep -Seconds 2
        }
        AfterAll { Remove-Bundle $script:bundle }

        It 'exits non-zero' { $script:result.ExitCode | Should -Not -Be 0 }
        It 'names the destination it wants approved' { $script:result.StdOut | Should -Match ([regex]::Escape($script:bundle.RepoDir)) }
        It 'does not run the repository difftool command' { $script:bundle.Marker | Should -Not -Exist }
    }

    Context 'difftool with an approved destination whose repository config sets a diff tool' {
        BeforeAll {
            $script:bundle = New-UntrustedBundle
            $script:result = Invoke-L5xgit @('difftool', '--acd', $script:bundle.AcdPath, '--trust-destination')
            Start-Sleep -Seconds 2
        }
        AfterAll { Remove-Bundle $script:bundle }

        It 'exits non-zero' { $script:result.ExitCode | Should -Not -Be 0 }
        It 'reports the repository-level settings' { $script:result.StdErr | Should -Match 'difftool\.t\.cmd' }
        It 'does not run the repository difftool command' { $script:bundle.Marker | Should -Not -Exist }
    }

    Context 'commit with an unapproved sidecar destination' {
        BeforeAll {
            $script:bundle = New-UntrustedBundle
            $script:result = Invoke-L5xgit @('commit', '--acd', $script:bundle.AcdPath)
        }
        AfterAll { Remove-Bundle $script:bundle }

        It 'exits non-zero' { $script:result.ExitCode | Should -Not -Be 0 }
        It 'does not explode into the destination' { Join-Path $script:bundle.RepoDir 'RSLogix5000Content' | Should -Not -Exist }
        It 'does not create a commit' { (git -C $script:bundle.RepoDir rev-parse HEAD).Trim() | Should -Be $script:bundle.HeadBefore }
    }

    Context 'UNC destination in the sidecar' {
        BeforeAll {
            $script:bundle = New-UntrustedBundle -DestinationOverride '\\attacker.example\share\repo'
            $script:result = Invoke-L5xgit @('commit', '--acd', $script:bundle.AcdPath, '--trust-destination')
        }
        AfterAll { Remove-Bundle $script:bundle }

        It 'exits non-zero' { $script:result.ExitCode | Should -Not -Be 0 }
        It 'rejects the path before using it' { $script:result.StdErr | Should -Match 'UNC' }
    }
}
