---
external help file: DbxProvider.dll-Help.xml
Module Name: DbxProvider
online version:
schema: 2.0.0
---

# Remove-DropboxCredential

## SYNOPSIS

Deletes the saved Dropbox credentials from disk.

## SYNTAX

### Single (Default)
```
Remove-DropboxCredential [-Account <String>] [-ProgressAction <ActionPreference>] [-WhatIf] [-Confirm]
 [<CommonParameters>]
```

### All
```
Remove-DropboxCredential [-All] [-ProgressAction <ActionPreference>] [-WhatIf] [-Confirm] [<CommonParameters>]
```

## DESCRIPTION

Removes the credential file used by `Connect-Dropbox` and
`Get-DropboxCredential`. Supports `-WhatIf` and `-Confirm`.
This does not disconnect any active PSDrive; existing in-memory
clients keep working until the session ends or
`Disconnect-Dropbox` is called.

## EXAMPLES

### Example 1
```powershell
PS> Remove-DropboxCredential
```

Prompts for confirmation, then deletes the saved credentials.

### Example 2
```powershell
PS> Remove-DropboxCredential -Confirm:$false
```

Deletes the saved credentials without prompting (suitable for automation).

## PARAMETERS

### -Account
Account selector - Dropbox `accountId`, full email, or unambiguous email local-part. When omitted (and `-All` is not specified), the default account is removed.

```yaml
Type: String
Parameter Sets: Single
Aliases:

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -All
Remove every saved account (deletes the credential file).

```yaml
Type: SwitchParameter
Parameter Sets: All
Aliases:

Required: True
Position: Named
Default value: False
Accept pipeline input: False
Accept wildcard characters: False
```

### -ProgressAction

Standard PowerShell common parameter that controls how progress records
are reported. See [about_CommonParameters](https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_commonparameters).

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

### -Confirm
Prompts you for confirmation before running the cmdlet.

```yaml
Type: SwitchParameter
Parameter Sets: (All)
Aliases: cf

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -WhatIf
Shows what would happen if the cmdlet runs.
The cmdlet is not run.

```yaml
Type: SwitchParameter
Parameter Sets: (All)
Aliases: wi

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

### System.Object
## NOTES

## RELATED LINKS
