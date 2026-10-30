#Requires -Modules Pester
param(
    [switch]$Debug
)

$env:E2E_DEBUG = if ($Debug) { '1' } else { '0' }

BeforeAll {
    . "$PSScriptRoot/../Helpers.ps1"

    if (-not (Test-Path $l5xplode)) {
        throw "l5xplode.exe not found at '$l5xplode'. Run 'dotnet build -c Release' first."
    }
}

# ─────────────────────────────────────────────────────────────────────────────
# Structured text routines are stored as .st files. Everything else about the routine
# lives in a sibling element file so it survives the round trip.
# ─────────────────────────────────────────────────────────────────────────────
Describe 'Structured text routine round trip' {

    Context 'exploded layout' {
        BeforeAll {
            $script:tempDir = New-TestTempDir -Prefix 'st_layout'
            $l5xFile = Join-Path $fixturesDir 'sample_structured_text.L5X'

            $script:explodeResult = Invoke-L5xplode @('explode', '--l5x', $l5xFile, '--dir', $script:tempDir, '--force')
            if ($script:explodeResult.ExitCode -ne 0) {
                throw "Explode failed: $($script:explodeResult.StdErr)"
            }

            $script:aoiRoutines     = Join-Path $script:tempDir 'RSLogix5000Content/AddOnInstructionDefinitions/StAoi/Routines'
            $script:programRoutines = Join-Path $script:tempDir 'RSLogix5000Content/Programs/MainProgram/Routines'
        }

        AfterAll {
            $ProgressPreference = 'SilentlyContinue'
            if (Test-Path $script:tempDir) { Remove-Item $script:tempDir -Recurse -Force }
        }

        It 'writes the structured text to a .st file' {
            Join-Path $script:aoiRoutines 'AoiStLogic.st' | Should -Exist
            Join-Path $script:aoiRoutines 'AoiStEdgeCases.st' | Should -Exist
            Join-Path $script:programRoutines 'ProgramStLogic.st' | Should -Exist
        }

        It 'writes the routine element alongside it' {
            Join-Path $script:aoiRoutines 'AoiStLogic.xml' | Should -Exist
            Join-Path $script:aoiRoutines 'AoiStEdgeCases.xml' | Should -Exist
            Join-Path $script:programRoutines 'ProgramStLogic.xml' | Should -Exist
        }

        It 'keeps the description in the routine element, not the .st file' {
            $xml = Get-Content (Join-Path $script:aoiRoutines 'AoiStLogic.xml') -Raw
            $xml | Should -Match 'AOI structured text routine description'

            $st = Get-Content (Join-Path $script:aoiRoutines 'AoiStLogic.st') -Raw
            $st | Should -Not -Match 'description'
        }

        It 'moves the Line elements out of the routine element' {
            $xml = Get-Content (Join-Path $script:aoiRoutines 'AoiStLogic.xml') -Raw
            $xml | Should -Not -Match '<Line'
        }

        It 'writes the structured text verbatim' {
            $st = Get-Content (Join-Path $script:aoiRoutines 'AoiStLogic.st') -Raw
            $st | Should -Match ([regex]::Escape('Counter := Counter + 1;'))
            $st | Should -Match ([regex]::Escape('END_IF;'))
        }

        It 'writes XML-hostile characters to the .st file as plain text' {
            $lines = Get-Content (Join-Path $script:aoiRoutines 'AoiStEdgeCases.st')
            $lines[0] | Should -BeExactly 'IF x < 5 AND y > 3 THEN'
            $lines[1] | Should -BeExactly "s := 'A & B';"
            $lines[2] | Should -BeExactly 'IF a[b[i]]> 0 THEN'
            $lines[3] | Should -BeExactly "tag := '<![CDATA[not really]]>';"
            $lines[4] | Should -BeExactly '(* trailing spaces   *)'
        }
    }

    Context 'implode restores the routine' {
        BeforeAll {
            $script:tempDir = New-TestTempDir -Prefix 'st_roundtrip'
            $l5xFile = Join-Path $fixturesDir 'sample_structured_text.L5X'

            $explodeResult = Invoke-L5xplode @('explode', '--l5x', $l5xFile, '--dir', $script:tempDir, '--force')
            if ($explodeResult.ExitCode -ne 0) { throw "Explode failed: $($explodeResult.StdErr)" }

            $script:outputL5x = Join-Path $script:tempDir 'st_out.L5X'
            $implodeResult = Invoke-L5xplode @('implode', '--dir', $script:tempDir, '--l5x', $script:outputL5x, '--force')
            if ($implodeResult.ExitCode -ne 0) { throw "Implode failed: $($implodeResult.StdErr)" }

            [xml]$script:xml = Get-Content $script:outputL5x
            $aoiRoutines = @($script:xml.RSLogix5000Content.Controller.AddOnInstructionDefinitions.AddOnInstruction.Routines.Routine)
            $script:aoiRoutine  = $aoiRoutines | Where-Object { $_.Name -eq 'AoiStLogic' }
            $script:edgeRoutine = $aoiRoutines | Where-Object { $_.Name -eq 'AoiStEdgeCases' }
            $script:programRoutine = $script:xml.RSLogix5000Content.Controller.Programs.Program.Routines.Routine
        }

        AfterAll {
            $ProgressPreference = 'SilentlyContinue'
            if (Test-Path $script:tempDir) { Remove-Item $script:tempDir -Recurse -Force }
        }

        It 'restores the routine name and type' {
            $script:aoiRoutine.Name | Should -Be 'AoiStLogic'
            $script:aoiRoutine.Type | Should -Be 'ST'
        }

        It 'restores the AOI routine description' {
            $script:aoiRoutine.Description.InnerText | Should -Match 'AOI structured text routine description'
        }

        It 'restores the program routine description' {
            $script:programRoutine.Description.InnerText | Should -Match 'Program structured text routine description'
        }

        It 'restores every line in order' {
            $lines = @($script:aoiRoutine.STContent.Line)
            $lines.Count | Should -Be 4
            $lines[0].Number | Should -Be '0'
            $lines[0].'#cdata-section' | Should -Be 'Counter := Counter + 1;'
            $lines[3].'#cdata-section' | Should -Be 'END_IF;'
        }

        # InnerText rather than #cdata-section: a line containing ]]> cannot live in one CDATA
        # section, so it comes back split across two and must be read as the concatenation.
        It 'restores XML-hostile lines byte for byte' {
            $lines = @($script:edgeRoutine.STContent.Line)
            $lines.Count | Should -Be 5
            $lines[0].InnerText | Should -BeExactly 'IF x < 5 AND y > 3 THEN'
            $lines[1].InnerText | Should -BeExactly "s := 'A & B';"
            $lines[2].InnerText | Should -BeExactly 'IF a[b[i]]> 0 THEN'
            $lines[3].InnerText | Should -BeExactly "tag := '<![CDATA[not really]]>';"
            $lines[4].InnerText | Should -BeExactly '(* trailing spaces   *)'
        }

        It 'keeps the line content in CDATA rather than escaping it' {
            $raw = Get-Content $script:outputL5x -Raw
            $raw | Should -Match ([regex]::Escape('<![CDATA[IF x < 5 AND y > 3 THEN]]>'))
            $raw | Should -Not -Match ([regex]::Escape('IF x &lt; 5'))
        }
    }

    # Directories exploded before the routine element was persisted contain only the .st file.
    # Those must still implode, even though the discarded metadata cannot be recovered.
    Context 'legacy exploded directory with no routine element file' {
        BeforeAll {
            $script:tempDir = New-TestTempDir -Prefix 'st_legacy'
            $l5xFile = Join-Path $fixturesDir 'sample_structured_text.L5X'

            $explodeResult = Invoke-L5xplode @('explode', '--l5x', $l5xFile, '--dir', $script:tempDir, '--force')
            if ($explodeResult.ExitCode -ne 0) { throw "Explode failed: $($explodeResult.StdErr)" }

            # Reproduce the old layout by removing the element file next to each .st file.
            Get-ChildItem $script:tempDir -Recurse -Filter '*.st' | ForEach-Object {
                $sibling = [System.IO.Path]::ChangeExtension($_.FullName, '.xml')
                if (Test-Path $sibling) { Remove-Item $sibling -Force }
            }

            $script:outputL5x = Join-Path $script:tempDir 'st_legacy_out.L5X'
            $script:implodeResult = Invoke-L5xplode @('implode', '--dir', $script:tempDir, '--l5x', $script:outputL5x, '--force')
        }

        AfterAll {
            $ProgressPreference = 'SilentlyContinue'
            if (Test-Path $script:tempDir) { Remove-Item $script:tempDir -Recurse -Force }
        }

        It 'still implodes' {
            $script:implodeResult.ExitCode | Should -Be 0
        }

        It 'rebuilds the routine from the .st file alone' {
            [xml]$xml = Get-Content $script:outputL5x
            $routines = @($xml.RSLogix5000Content.Controller.AddOnInstructionDefinitions.AddOnInstruction.Routines.Routine)
            $routine = $routines | Where-Object { $_.Name -eq 'AoiStLogic' }
            $routine.Type | Should -Be 'ST'
            @($routine.STContent.Line).Count | Should -Be 4
        }

        It 'still round-trips XML-hostile lines without the routine element' {
            [xml]$xml = Get-Content $script:outputL5x
            $routines = @($xml.RSLogix5000Content.Controller.AddOnInstructionDefinitions.AddOnInstruction.Routines.Routine)
            $edge = $routines | Where-Object { $_.Name -eq 'AoiStEdgeCases' }
            $lines = @($edge.STContent.Line)
            $lines.Count | Should -Be 5
            $lines[2].InnerText | Should -BeExactly 'IF a[b[i]]> 0 THEN'
            $lines[3].InnerText | Should -BeExactly "tag := '<![CDATA[not really]]>';"
        }

        It 'cannot recover the description that the old layout never stored' {
            $content = Get-Content $script:outputL5x -Raw
            $content | Should -Not -Match 'AOI structured text routine description'
        }
    }
}

# ─────────────────────────────────────────────────────────────────────────────
# A routine edited online carries several <STContent> elements distinguished by an
# OnlineEditType attribute, which the exploded layout encodes as a file name infix.
# ─────────────────────────────────────────────────────────────────────────────
Describe 'Structured text routines with online edits' {

    Context 'exploded layout' {
        BeforeAll {
            $script:tempDir = New-TestTempDir -Prefix 'st_onlineedit_layout'
            $l5xFile = Join-Path $fixturesDir 'sample_structured_text_online_edits.L5X'

            $script:explodeResult = Invoke-L5xplode @('explode', '--l5x', $l5xFile, '--dir', $script:tempDir, '--force')
            if ($script:explodeResult.ExitCode -ne 0) {
                throw "Explode failed: $($script:explodeResult.StdErr)"
            }

            $script:aoiRoutines     = Join-Path $script:tempDir 'RSLogix5000Content/AddOnInstructionDefinitions/StAoi/Routines'
            $script:programRoutines = Join-Path $script:tempDir 'RSLogix5000Content/Programs/MainProgram/Routines'
        }

        AfterAll {
            $ProgressPreference = 'SilentlyContinue'
            if (Test-Path $script:tempDir) { Remove-Item $script:tempDir -Recurse -Force }
        }

        It 'exits with code 0' {
            $script:explodeResult.ExitCode | Should -Be 0
        }

        It 'names each .st file after its OnlineEditType' {
            Join-Path $script:aoiRoutines 'AoiOnlineEdit.Original.st' | Should -Exist
            Join-Path $script:aoiRoutines 'AoiOnlineEdit.Pending.st'  | Should -Exist
        }

        It 'writes no untyped .st file when every STContent is typed' {
            Join-Path $script:aoiRoutines 'AoiOnlineEdit.st' | Should -Not -Exist
        }

        It 'writes an untyped .st alongside a typed one when the routine mixes both' {
            Join-Path $script:programRoutines 'ProgramOnlineEdit.st'         | Should -Exist
            Join-Path $script:programRoutines 'ProgramOnlineEdit.Pending.st' | Should -Exist
        }

        It 'writes exactly one routine element file per routine, not one per STContent' {
            @(Get-ChildItem $script:aoiRoutines -Filter '*.xml').Count | Should -Be 1
            Join-Path $script:aoiRoutines 'AoiOnlineEdit.xml' | Should -Exist
        }

        It 'does not collide the untyped .st with the routine element file' {
            $stFiles = @(Get-ChildItem $script:programRoutines -Filter '*.st' | Select-Object -ExpandProperty Name)
            $stFiles | Should -Contain 'ProgramOnlineEdit.st'
            Join-Path $script:programRoutines 'ProgramOnlineEdit.xml' | Should -Exist
            $stFiles.Count | Should -Be 2
        }

        It 'produces a distinct file for every STContent in the fixture' {
            $all = @(Get-ChildItem $script:tempDir -Recurse -Filter '*.st')
            $all.Count | Should -Be 4
            @($all | Select-Object -ExpandProperty FullName -Unique).Count | Should -Be 4
        }

        It 'routes each STContent body to its own file' {
            $original = Get-Content (Join-Path $script:aoiRoutines 'AoiOnlineEdit.Original.st') -Raw
            $pending  = Get-Content (Join-Path $script:aoiRoutines 'AoiOnlineEdit.Pending.st') -Raw

            $original | Should -Match ([regex]::Escape('Counter := Counter + 1;'))
            $original | Should -Not -Match ([regex]::Escape('Counter := Counter + 2;'))
            $pending  | Should -Match ([regex]::Escape('Counter := Counter + 2;'))
            $pending  | Should -Not -Match ([regex]::Escape('Counter := Counter + 1;'))
        }

        It 'keeps the OnlineEditType attributes in the routine element file' {
            $xml = Get-Content (Join-Path $script:aoiRoutines 'AoiOnlineEdit.xml') -Raw
            $xml | Should -Match 'OnlineEditType="Original"'
            $xml | Should -Match 'OnlineEditType="Pending"'
            $xml | Should -Not -Match '<Line'
        }
    }

    Context 'implode restores the online edits' {
        BeforeAll {
            $script:tempDir = New-TestTempDir -Prefix 'st_onlineedit_roundtrip'
            $l5xFile = Join-Path $fixturesDir 'sample_structured_text_online_edits.L5X'

            $explodeResult = Invoke-L5xplode @('explode', '--l5x', $l5xFile, '--dir', $script:tempDir, '--force')
            if ($explodeResult.ExitCode -ne 0) { throw "Explode failed: $($explodeResult.StdErr)" }

            $script:outputL5x = Join-Path $script:tempDir 'st_onlineedit_out.L5X'
            $script:implodeResult = Invoke-L5xplode @('implode', '--dir', $script:tempDir, '--l5x', $script:outputL5x, '--force')
            if ($script:implodeResult.ExitCode -ne 0) { throw "Implode failed: $($script:implodeResult.StdErr)" }

            [xml]$script:xml = Get-Content $script:outputL5x
            $script:aoiRoutine = $script:xml.RSLogix5000Content.Controller.AddOnInstructionDefinitions.AddOnInstruction.Routines.Routine
            $script:programRoutine = $script:xml.RSLogix5000Content.Controller.Programs.Program.Routines.Routine
        }

        AfterAll {
            $ProgressPreference = 'SilentlyContinue'
            if (Test-Path $script:tempDir) { Remove-Item $script:tempDir -Recurse -Force }
        }

        It 'implode exits with code 0' {
            $script:implodeResult.ExitCode | Should -Be 0
        }

        It 'restores both STContent elements on the AOI routine' {
            @($script:aoiRoutine.STContent).Count | Should -Be 2
        }

        It 'restores the OnlineEditType attribute on each STContent' {
            $types = @($script:aoiRoutine.STContent | ForEach-Object { $_.OnlineEditType })
            $types | Should -Contain 'Original'
            $types | Should -Contain 'Pending'
        }

        It 'pairs each OnlineEditType with its own lines' {
            $original = $script:aoiRoutine.STContent | Where-Object { $_.OnlineEditType -eq 'Original' }
            $pending  = $script:aoiRoutine.STContent | Where-Object { $_.OnlineEditType -eq 'Pending' }

            @($original.Line).Count | Should -Be 2
            @($pending.Line).Count  | Should -Be 3
            @($original.Line)[0].InnerText | Should -BeExactly 'Counter := Counter + 1;'
            @($pending.Line)[0].InnerText  | Should -BeExactly 'Counter := Counter + 2;'
        }

        It 'renumbers the lines of each STContent from zero' {
            $pending = $script:aoiRoutine.STContent | Where-Object { $_.OnlineEditType -eq 'Pending' }
            @($pending.Line | ForEach-Object { $_.Number }) | Should -Be @('0', '1', '2')
        }

        It 'restores the untyped STContent without an OnlineEditType attribute' {
            $untyped = @($script:programRoutine.STContent | Where-Object { -not $_.OnlineEditType })
            $untyped.Count | Should -Be 1
            @($untyped[0].Line)[0].InnerText | Should -BeExactly '(* accepted content, no OnlineEditType *)'
        }

        It 'restores the typed STContent on the mixed routine' {
            $pending = @($script:programRoutine.STContent | Where-Object { $_.OnlineEditType -eq 'Pending' })
            $pending.Count | Should -Be 1
            @($pending[0].Line)[0].InnerText | Should -BeExactly '(* pending content *)'
        }

        It 'preserves the routine descriptions' {
            $script:aoiRoutine.Description.InnerText | Should -Match 'AOI routine with an online edit in progress'
            $script:programRoutine.Description.InnerText | Should -Match 'Program routine mixing accepted and pending content'
        }
    }
}

# ─────────────────────────────────────────────────────────────────────────────
# OnlineEditType ends up in a file name, so a crafted value must never be able to
# place a .st file (or create a directory) outside the exploded directory.
# ─────────────────────────────────────────────────────────────────────────────
Describe 'Structured text OnlineEditType path traversal' {

    BeforeAll {
        $script:tempDir = New-TestTempDir -Prefix 'st_onlineedit_traversal'
        $script:explodeDir = Join-Path $script:tempDir 'repo/project'
        $l5xFile = Join-Path $fixturesDir 'malicious_structured_text_online_edit_traversal.L5X'

        $script:explodeResult = Invoke-L5xplode @('explode', '--l5x', $l5xFile, '--dir', $script:explodeDir, '--force')
    }

    AfterAll {
        $ProgressPreference = 'SilentlyContinue'
        if (Test-Path $script:tempDir) { Remove-Item $script:tempDir -Recurse -Force }
    }

    It 'fails the explode' {
        $script:explodeResult.ExitCode | Should -Not -Be 0
    }

    It 'reports the rejected OnlineEditType' {
        ($script:explodeResult.StdOut + $script:explodeResult.StdErr) | Should -Match 'Invalid OnlineEditType'
    }

    It 'writes no .st file anywhere' {
        @(Get-ChildItem $script:tempDir -Recurse -Filter '*.st').Count | Should -Be 0
    }

    It 'creates nothing outside the exploded directory' {
        Join-Path $script:tempDir 'escaped.st' | Should -Not -Exist
        @(Get-ChildItem $script:tempDir | Where-Object { $_.Name -ne 'repo' }).Count | Should -Be 0
    }
}
