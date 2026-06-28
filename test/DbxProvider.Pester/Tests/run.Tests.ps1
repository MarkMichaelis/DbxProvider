BeforeAll {
    # Dot-source run.ps1 so its testable helper (New-WtTabArgumentList) loads WITHOUT
    # building the module or launching a child process: the script returns early when it
    # detects it was dot-sourced.
    $script:RunScript = Join-Path $PSScriptRoot '..' '..' '..' 'run.ps1'
    . $script:RunScript
}

Describe 'run.ps1 New-WtTabArgumentList' {

    It 'opens a new tab in the current Windows Terminal window' {
        $args = New-WtTabArgumentList -PwshPath 'C:\pwsh.exe' -ScriptPath 'C:\t.ps1' -Title 'DbxProvider'
        ($args -join ' ') |
            Should -BeExactly '-w 0 new-tab --title DbxProvider "C:\pwsh.exe" -NoExit -NoProfile -File "C:\t.ps1"'
    }

    It 'launches the child via -File (not -Command) so wt never parses the script''s semicolons' {
        $args = New-WtTabArgumentList -PwshPath 'C:\pwsh.exe' -ScriptPath 'C:\Temp\tab-1.ps1' -Title 'DbxProvider'
        $args | Should -Not -Contain '-Command'
        $fileIdx = [array]::IndexOf([object[]]$args, '-File')
        $fileIdx | Should -BeGreaterThan 0
        $args[$fileIdx + 1] | Should -BeExactly '"C:\Temp\tab-1.ps1"'
    }

    It 'quotes a pwsh path containing spaces so it survives wt command-line parsing' {
        $args = New-WtTabArgumentList -PwshPath 'C:\Program Files\PowerShell\7\pwsh.exe' -ScriptPath 'C:\t.ps1' -Title 'DbxProvider'
        $args | Should -Contain '"C:\Program Files\PowerShell\7\pwsh.exe"'
    }
}

Describe 'run.ps1 New-ChildCommand' {

    It 'defaults to importing the module interactively and never deletes' {
        $cmd = New-ChildCommand -RepoRoot 'C:\Repo' -ModulePath 'C:\Build\DbxProvider.psd1' `
            -ConflictScript 'C:\Repo\Find-DropboxConflicts.ps1'
        $cmd | Should -Match 'Import-Module'
        $cmd | Should -Not -Match '-Delete'
        $cmd | Should -Not -Match '&\s*"[^"]*Find-DropboxConflicts\.ps1"'
    }

    It 'runs the conflict-delete pass only when -FindConflicts is supplied' {
        $cmd = New-ChildCommand -RepoRoot 'C:\Repo' -ModulePath 'C:\Build\DbxProvider.psd1' `
            -ConflictScript 'C:\Repo\Find-DropboxConflicts.ps1' -FindConflicts -Limit 1000
        $cmd | Should -Match 'Find-DropboxConflicts\.ps1'
        $cmd | Should -Match '-Delete'
        $cmd | Should -Match '-Limit 1000'
        $cmd | Should -Not -Match 'Import-Module'
    }

    It 'appends -ScriptArgs to the conflict pass so the caller can override' {
        $cmd = New-ChildCommand -RepoRoot 'C:\Repo' -ModulePath 'C:\Build\DbxProvider.psd1' `
            -ConflictScript 'C:\Repo\Find-DropboxConflicts.ps1' -FindConflicts -ScriptArgs '-WhatIf'
        $cmd | Should -Match '-WhatIf'
    }
}
