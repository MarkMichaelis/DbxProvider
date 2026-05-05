@{
    RootModule        = ''
    ModuleVersion     = '1.0.0'
    GUID              = 'd2f1c4e0-7a55-4f1c-9c4e-0a3b9c8f5e21'
    Author            = 'DbxProvider Contributors'
    CompanyName       = 'Community'
    Copyright         = '(c) 2026 DbxProvider Contributors. All rights reserved.'
    Description       = 'Pester v5 test suite for the DbxProvider PowerShell module.'
    PowerShellVersion = '7.4'
    RequiredModules   = @(
        @{ ModuleName = 'Pester'; ModuleVersion = '5.5.0' }
    )
    FunctionsToExport = @()
    CmdletsToExport   = @()
    VariablesToExport = @()
    AliasesToExport   = @()
    PrivateData       = @{
        PSData = @{
            Tags = @('Dropbox', 'Pester', 'Tests')
        }
    }
}
