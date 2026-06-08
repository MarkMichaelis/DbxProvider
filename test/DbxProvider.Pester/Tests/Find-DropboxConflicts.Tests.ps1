BeforeAll {
    # Dot-source the root script so its testable helper (Resolve-DbxDriveName)
    # loads WITHOUT running the scan body: the script returns early when it
    # detects it was dot-sourced, so no module import or Dropbox connection
    # happens and no credentials are required.
    $script:RootScript = Join-Path $PSScriptRoot '..' '..' '..' 'Find-DropboxConflicts.ps1'
    . $script:RootScript
}

Describe 'Find-DropboxConflicts.ps1 Resolve-DbxDriveName' {

    It 'derives the drive name from a leading drive qualifier (<Path>)' -TestCases @(
        @{ Path = 'Dbx:\';           Expected = 'Dbx' }
        @{ Path = 'Dbx:\SomeFolder'; Expected = 'Dbx' }
        @{ Path = 'Dbx:/A/B';        Expected = 'Dbx' }
        @{ Path = 'DbxTest:/A/B';    Expected = 'DbxTest' }
    ) {
        param($Path, $Expected)
        Resolve-DbxDriveName -Path $Path | Should -BeExactly $Expected
    }

    It 'defaults to the Dbx drive for a path with no leading qualifier (<Path>)' -TestCases @(
        @{ Path = '/A/B' }
        @{ Path = '/Project:Notes' }
        @{ Path = '' }
    ) {
        param($Path)
        Resolve-DbxDriveName -Path $Path | Should -BeExactly 'Dbx'
    }
}