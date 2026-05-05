---
external help file: DbxProvider.dll-Help.xml
Module Name: DbxProvider
online version:
schema: 2.0.0
---

# Disconnect-Dropbox

## SYNOPSIS

Disconnects from Dropbox and removes the PSDrive.

## SYNTAX

```
Disconnect-Dropbox [[-DriveName] <String>] [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

## DESCRIPTION

Removes the named Dropbox PSDrive and disposes the underlying API
client. Also removes the convenience `<DriveName>:` global function
registered by `Connect-Dropbox`. Saved credentials in the credential
store are **not** affected; use `Remove-DropboxCredential` to delete
them.

## EXAMPLES


### Example 1
```powershell
PS> Disconnect-Dropbox
```

Disconnects the default ``Dbx:`` drive.

### Example 2
```powershell
PS> Disconnect-Dropbox -DriveName Work
```

Disconnects a non-default drive.

## PARAMETERS

### -DriveName


Name of the Dropbox PSDrive previously created by `Connect-Dropbox`.
Defaults to `Dbx`. Specify a different name when you have connected
to multiple Dropbox accounts in the same session.

```yaml
Type: String
Parameter Sets: (All)
Aliases:

Required: False
Position: 0
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

### System.Object
## NOTES

## RELATED LINKS
