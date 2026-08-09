BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\Helpers\TestEnvironment.psm1') -Force
    Import-Module (Get-DbxProviderModulePath) -Force -DisableNameChecking -Global

    $script:ModuleName = 'DbxProvider'
    $psd = Import-PowerShellDataFile (Join-Path (Split-Path (Get-DbxProviderModulePath) -Parent) ((Split-Path (Get-DbxProviderModulePath) -LeafBase) + '.psd1')) -ErrorAction SilentlyContinue
    if (-not $psd) {
        $psdPath = (Get-Module $ModuleName).Path -replace '\.dll$', '.psd1'
        $psd = Import-PowerShellDataFile $psdPath
    }
    $script:ExportedCmdlets = @($psd.CmdletsToExport) | Where-Object { $_ -and $_ -ne '*' }
}

Describe 'DbxProvider help' {

    It 'has MAML help xml next to the module DLL' {
        $dllPath = (Get-Module $ModuleName).Path
        $mamlPath = Join-Path (Split-Path $dllPath -Parent) "en-US\$ModuleName.dll-Help.xml"
        Test-Path -LiteralPath $mamlPath | Should -BeTrue -Because "the build should produce $mamlPath"
    }

    It 'exports at least one cmdlet' {
        $ExportedCmdlets.Count | Should -BeGreaterThan 0
    }

    Context 'every exported cmdlet has rich help' {

        BeforeDiscovery {
            Import-Module (Join-Path $PSScriptRoot '..\Helpers\TestEnvironment.psm1') -Force
            Import-Module (Get-DbxProviderModulePath) -Force -DisableNameChecking -Global
            $psdPath = (Get-Module 'DbxProvider').Path -replace '\.dll$', '.psd1'
            $script:Cmdlets = @((Import-PowerShellDataFile $psdPath).CmdletsToExport) |
                Where-Object { $_ -and $_ -ne '*' }
        }

        It 'Get-Help <_> returns a non-placeholder Synopsis' -ForEach $Cmdlets {
            $h = Get-Help $_ -ErrorAction Stop
            $h.Synopsis | Should -Not -BeNullOrEmpty
            $h.Synopsis | Should -Not -Match '\{\{\s*Fill'
            # PowerShell auto-generates a syntax-only synopsis when no MAML is
            # found; that synopsis equals the cmdlet's syntax line. Detect that
            # case by checking the synopsis isn't just the command name plus
            # parameter list.
            $h.Synopsis | Should -Not -Match "^\s*$_(\s+\[?-)"
        }

        It 'Get-Help <_> returns a non-placeholder Description' -ForEach $Cmdlets {
            $h = Get-Help $_ -Full -ErrorAction Stop
            $descText = ($h.Description | ForEach-Object { $_.Text }) -join "`n"
            $descText | Should -Not -BeNullOrEmpty
            $descText | Should -Not -Match '\{\{\s*Fill'
        }

        It 'Get-Help <_> returns at least one Example with code' -ForEach $Cmdlets {
            $h = Get-Help $_ -Examples -ErrorAction Stop
            $h.examples | Should -Not -BeNullOrEmpty
            $examples = @($h.examples.example)
            $examples.Count | Should -BeGreaterOrEqual 1
            $hasCode = $false
            foreach ($e in $examples) {
                if ($e.code -and $e.code.ToString().Trim()) { $hasCode = $true; break }
            }
            $hasCode | Should -BeTrue
        }
    }

    Context 'about_DbxProvider_Mode conceptual topic' {

        It 'is discoverable via Get-Help' {
            $text = Get-Help about_DbxProvider_Mode -ErrorAction Stop | Out-String
            $text | Should -Not -BeNullOrEmpty
            $text | Should -Match 'flag mask'
        }

        It 'documents the <Flag> flag (<Meaning>)' -ForEach @(
            @{ Flag = 'd'; Meaning = 'Folder' }
            @{ Flag = 's'; Meaning = 'Shared' }
            @{ Flag = 'l'; Meaning = 'Symlink' }
            @{ Flag = 'c'; Meaning = 'Cloud-only' }
            @{ Flag = 'z'; Meaning = 'Zero-byte' }
            @{ Flag = 'x'; Meaning = 'Conflicted copy' }
        ) {
            $text = Get-Help about_DbxProvider_Mode -ErrorAction Stop | Out-String
            $text | Should -Match ([regex]::Escape($Meaning))
        }

        It 'shows the conflicted-copy filter example' {
            $text = Get-Help about_DbxProvider_Mode -ErrorAction Stop | Out-String
            $text | Should -Match "Where-Object Mode -match 'x'"
        }
    }
}
