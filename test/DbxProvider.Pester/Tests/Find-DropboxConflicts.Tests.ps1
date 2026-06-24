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

Describe 'Find-DropboxConflicts.ps1 Format-Eta' {

    It 'renders a same-day ETA as a bare wall-clock time, not a duration' {
        # 5:00 AM + 2h 33m lands at 7:33 AM the same day: a target clock time, never '2h 33m'.
        $now = [datetime]'2026-06-24 05:00:00'
        Format-Eta -Span ([TimeSpan]::FromMinutes(153)) -Now $now |
            Should -BeExactly '7:33 AM'
    }

    It 'qualifies an ETA that crosses midnight with the target day-of-week' {
        # 10:00 PM + 5h lands at 3:00 AM the NEXT day, so a day qualifier is required
        # to disambiguate; 2026-06-25 is a Thursday.
        $now = [datetime]'2026-06-24 22:00:00'
        Format-Eta -Span ([TimeSpan]::FromHours(5)) -Now $now |
            Should -BeExactly 'Thu 3:00 AM'
    }

    It 'qualifies an ETA a week or more out with the calendar date' {
        # 9 days out exceeds the day-of-week window, so fall back to a month/day stamp.
        $now = [datetime]'2026-06-24 12:00:00'
        Format-Eta -Span ([TimeSpan]::FromDays(9)) -Now $now |
            Should -BeExactly 'Jul 3 12:00 PM'
    }
}

Describe 'Find-DropboxConflicts.ps1 Format-ElapsedTag' {

    It 'renders the run-elapsed time as a bracketed tag without the word elapsed' {
        # 39m 46s -> '[39m 46s]': brackets convey "time running"; the label is dropped.
        $tag = Format-ElapsedTag -Span ([TimeSpan]::FromSeconds((39 * 60) + 46))
        $tag | Should -BeExactly '[39m 46s]'
        $tag | Should -Not -Match 'elapsed'
    }

    It 'keeps the running timer present (an hours-scale run is still bracketed)' {
        Format-ElapsedTag -Span ([TimeSpan]::FromMinutes(150)) |
            Should -BeExactly '[2h 30m]'
    }
}