---
external help file: DbxProvider.dll-Help.xml
Module Name: DbxProvider
online version:
schema: 2.0.0
---

# Get-DropboxCacheDatabasePath

## SYNOPSIS

Lists the configured per-email cache database path overrides and the absolute
path each resolves to.

## SYNTAX

```
Get-DropboxCacheDatabasePath [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

## DESCRIPTION

Emits one object per configured override with the account `Email`, the
`ConfiguredPath` as stored, and the `ResolvedPath` the cache database would use
after `~` and environment-variable expansion. Returns nothing when no overrides
are configured.

## EXAMPLES

### Example 1
```powershell
PS> Get-DropboxCacheDatabasePath
```

Lists every configured email-to-database-path override with its resolved path.

## PARAMETERS

### -ProgressAction

Standard PowerShell common parameter.

```yaml
Type: ActionPreference
Parameter Sets: (All)
Aliases: proga

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### CommonParameters
This cmdlet supports the common parameters: -Debug, -ErrorAction, -ErrorVariable, -InformationAction, -InformationVariable, -OutVariable, -OutBuffer, -PipelineVariable, -Verbose, -WarningAction, and -WarningVariable. For more information, see [about_CommonParameters](http://go.microsoft.com/fwlink/?LinkID=113216).

## INPUTS

### None

## OUTPUTS

### System.Management.Automation.PSObject

## NOTES

## RELATED LINKS