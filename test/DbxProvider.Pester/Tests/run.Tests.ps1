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
