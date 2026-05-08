---
external help file: DbxProvider.dll-Help.xml
Module Name: DbxProvider
online version:
schema: 2.0.0
---

# Get-DropboxCredential

## SYNOPSIS

Returns the Dropbox credentials currently saved in the credential store.

## SYNTAX

### Single (Default)
```
Get-DropboxCredential [-AsPlainText] [-Account <String>] [-ProgressAction <ActionPreference>]
 [<CommonParameters>]
```

### All
```
Get-DropboxCredential [-AsPlainText] [-All] [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

## DESCRIPTION

Reads the per-user credential file (DPAPI-encrypted on Windows) and
returns an object with the AppKey, masked AppSecret and RefreshToken,
the last save timestamp, and the file path on disk. Use
`-AsPlainText` to retrieve the unmasked secrets (e.g. when migrating
to another machine).

## EXAMPLES

### Example 1
```powershell
PS> Get-DropboxCredential
```

Shows the saved credentials with the AppSecret and RefreshToken masked.

### Example 2
```powershell
PS> Get-DropboxCredential -AsPlainText
```

Returns the credentials with secrets revealed. Pipe to ``ConvertTo-Json`` for backup.

## PARAMETERS

### -Account
Account selector - Dropbox `accountId`, full email, or unambiguous email local-part. When omitted, the default account is returned.

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
List every saved account (one PSObject per account, including an `IsDefault` column).

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

### -AsPlainText

Return the AppSecret and RefreshToken in clear text instead of masked.

```yaml
Type: SwitchParameter
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: None
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

### CommonParameters
This cmdlet supports the common parameters: -Debug, -ErrorAction, -ErrorVariable, -InformationAction, -InformationVariable, -OutVariable, -OutBuffer, -PipelineVariable, -Verbose, -WarningAction, and -WarningVariable. For more information, see [about_CommonParameters](http://go.microsoft.com/fwlink/?LinkID=113216).

## INPUTS

### None

## OUTPUTS

### System.Management.Automation.PSObject

## NOTES

## RELATED LINKS
