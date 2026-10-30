#Requires -Modules Pester
param(
    [switch]$Debug
)

$env:E2E_DEBUG = if ($Debug) { '1' } else { '0' }

BeforeAll {
    . "$PSScriptRoot/../Helpers.ps1"

    if (-not (Test-Path $l5xgit)) {
        throw "l5xgit.exe not found at '$l5xgit'. Run 'dotnet build -c Release' first."
    }

    function New-TwoCommitRepo {
        param([string]$Path)
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
        git -C $Path init -q
        Set-Content -Path (Join-Path $Path 'a.txt') -Value 'one'
        git -C $Path add -A
        git -C $Path -c user.name=e2e -c user.email=e2e@example.com commit -q -m one
        Set-Content -Path (Join-Path $Path 'a.txt') -Value 'two'
        git -C $Path add -A
        git -C $Path -c user.name=e2e -c user.email=e2e@example.com commit -q -m two
    }

    function New-ProjectFolder {
        param([string]$Root, [string]$DestinationPath)
        $acd = Join-Path $Root 'Line1.ACD'
        Set-Content -Path $acd -Value 'not a real ACD; difftool only reads the sidecar'
        Set-Content -Path (Join-Path $Root 'Line1_L5xGit.yml') -Value @(
            "destination_path: '$DestinationPath'",
            'prompt_for_commit_message: false'
        )
        return $acd
    }
}

Describe 'l5xgit difftool with an untrusted project folder' {

    BeforeEach {
        $tempDir = New-TestTempDir -Prefix 'l5xgit_difftool'
        $env:L5XGIT_TRUSTED_DESTINATIONS_FILE = Join-Path $tempDir 'trusted_destinations.json'
        $repo = Join-Path $tempDir 'repo'
        New-TwoCommitRepo -Path $repo
        $marker = Join-Path $tempDir 'pwned.txt'
        Add-Content -Path (Join-Path $repo '.git/config') -Value @(
            '[diff]',
            "`ttool = x",
            '[difftool "x"]',
            "`tcmd = touch '$($marker -replace '\\', '/')'"
        )
    }

    AfterEach {
        Remove-Item Env:L5XGIT_TRUSTED_DESTINATIONS_FILE -ErrorAction SilentlyContinue
        $ProgressPreference = 'SilentlyContinue'
        if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
    }

    It 'does not run git when the destination has not been approved and no one can answer the prompt' {
        $acd = New-ProjectFolder -Root $tempDir -DestinationPath $repo
        $result = Invoke-L5xgit @('difftool', '--acd', $acd)
        $result.ExitCode | Should -Not -Be 0
        $result.StdErr | Should -Match 'standard input has ended'
        Start-Sleep -Seconds 2
        Test-Path $marker | Should -BeFalse
    }

    It 'rejects a relative destination_path' {
        $acd = New-ProjectFolder -Root $tempDir -DestinationPath '.'
        $result = Invoke-L5xgit @('difftool', '--acd', $acd, '--trust-destination')
        $result.ExitCode | Should -Not -Be 0
        $result.StdErr | Should -Match 'not an absolute path'
        Test-Path $marker | Should -BeFalse
    }

    It 'refuses a repository whose own config sets the diff tool, even once approved' {
        $acd = New-ProjectFolder -Root $tempDir -DestinationPath $repo
        $result = Invoke-L5xgit @('difftool', '--acd', $acd, '--trust-destination')
        $result.ExitCode | Should -Not -Be 0
        $result.StdErr | Should -Match 'Refusing to run git difftool'
        $result.StdErr | Should -Match 'difftool\.x\.cmd'
        Start-Sleep -Seconds 2
        Test-Path $marker | Should -BeFalse
    }
}
