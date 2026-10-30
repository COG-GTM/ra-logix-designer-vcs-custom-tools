#Requires -Modules Pester
param(
    [switch]$Debug
)

$env:E2E_DEBUG = if ($Debug) { '1' } else { '0' }

<#
.SYNOPSIS
    The destination_path in an ACD's <name>_L5xGit.yml travels with the project, so l5xgit must not act on it
    until the current user has confirmed it. With standard input closed, the confirmation prompt fails, so
    these tests check that nothing in the target repository is touched.
    No SDK needed: the ACD is a placeholder because the refusal happens before any conversion.
#>

BeforeAll {
    . "$PSScriptRoot/../Helpers.ps1"

    if (-not (Test-Path $l5xgit)) {
        throw "l5xgit.exe not found at '$l5xgit'. Run 'dotnet build -c Release' first."
    }

    function New-SidecarTestEnvironment {
        param([string]$DestinationPath)

        $rootDir     = New-TestTempDir -Prefix 'sidecar'
        $repoDir     = Join-Path $rootDir 'plant'
        $projectDir  = Join-Path $repoDir 'Boiler'
        $incomingDir = Join-Path $rootDir 'incoming'
        $keptFile    = Join-Path $projectDir 'RSLogix5000Content/RSLogix5000Content.xml'

        New-Item -ItemType Directory -Path (Split-Path $keptFile) -Force | Out-Null
        New-Item -ItemType Directory -Path $incomingDir -Force | Out-Null
        '<RSLogix5000Content />' | Set-Content -Path $keptFile -Encoding UTF8

        Push-Location $repoDir
        try {
            git init --quiet 2>&1 | Out-Null
            git config user.name  'Test User'
            git config user.email 'test@example.com'
            git add -A 2>&1 | Out-Null
            git commit --quiet -m 'Boiler' 2>&1 | Out-Null
        }
        finally { Pop-Location }

        $acdPath = Join-Path $incomingDir 'Conveyor.acd'
        'not a real ACD' | Set-Content -Path $acdPath -Encoding UTF8

        if (-not $DestinationPath) { $DestinationPath = $projectDir }
        "destination_path: $DestinationPath`nprompt_for_commit_message: false" |
            Set-Content -Path (Join-Path $incomingDir 'Conveyor_L5xGit.yml') -Encoding UTF8

        return @{
            RootDir    = $rootDir
            RepoDir    = $repoDir
            ProjectDir = $projectDir
            KeptFile   = $keptFile
            AcdPath    = $acdPath
        }
    }

    function Get-CommitCount {
        param([string]$RepoDir)
        Push-Location $RepoDir
        try { return [int](git rev-list --count HEAD) }
        finally { Pop-Location }
    }

    function Get-RepoStatus {
        param([string]$RepoDir)
        Push-Location $RepoDir
        try { return (git status --porcelain) -join "`n" }
        finally { Pop-Location }
    }
}

Describe 'l5xgit with an unconfirmed _L5xGit.yml destination' {

    AfterEach {
        $ProgressPreference = 'SilentlyContinue'
        if ($script:env -and (Test-Path $script:env.RootDir)) { Remove-Item $script:env.RootDir -Recurse -Force }
    }

    It 'commit asks before touching another project and leaves it intact when not confirmed' {
        $script:env = New-SidecarTestEnvironment
        $result = Invoke-L5xgit @('commit', '--acd', $script:env.AcdPath)

        $result.ExitCode | Should -Not -Be 0
        $result.StdOut | Should -Match ([regex]::Escape($script:env.ProjectDir))
        $result.StdOut | Should -Match 'has not been confirmed'
        $script:env.KeptFile | Should -Exist
        Get-CommitCount $script:env.RepoDir | Should -Be 1
        Get-RepoStatus $script:env.RepoDir | Should -BeNullOrEmpty
    }

    It 'difftool asks before running git in the configured repository' {
        $script:env = New-SidecarTestEnvironment
        $result = Invoke-L5xgit @('difftool', '--acd', $script:env.AcdPath)

        $result.ExitCode | Should -Not -Be 0
        $result.StdOut | Should -Match 'has not been confirmed'
    }

    It 'restoreacd asks before overwriting the ACD from the configured directory' {
        $script:env = New-SidecarTestEnvironment
        $before = Get-Content $script:env.AcdPath -Raw
        $result = Invoke-L5xgit @('restoreacd', '--acd', $script:env.AcdPath)

        $result.ExitCode | Should -Not -Be 0
        $result.StdOut | Should -Match 'has not been confirmed'
        Get-Content $script:env.AcdPath -Raw | Should -Be $before
    }

    It 'commit refuses a relative destination_path' {
        $script:env = New-SidecarTestEnvironment -DestinationPath 'Boiler'
        $result = Invoke-L5xgit @('commit', '--acd', $script:env.AcdPath, '--trust-config')

        $result.ExitCode | Should -Not -Be 0
        "$($result.StdOut)$($result.StdErr)" | Should -Match 'must be an absolute path'
        Get-CommitCount $script:env.RepoDir | Should -Be 1
    }
}
