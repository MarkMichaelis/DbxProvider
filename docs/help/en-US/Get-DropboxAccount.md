---
external help file: DbxProvider.dll-Help.xml
Module Name: DbxProvider
online version:
schema: 2.0.0
---

# Get-DropboxAccount

## SYNOPSIS

Returns the current Dropbox account, or another account by ID.

## SYNTAX

```
Get-DropboxAccount [[-AccountId] <String>] [-DriveName <String>] [-ProgressAction <ActionPreference>]
 [<CommonParameters>]
```

## DESCRIPTION

With no parameters, returns the account associated with the current
PSDrive. Pass `-AccountId` to look up a specific account by ID
(useful when inspecting members of shared resources).

## EXAMPLES


### Example 1
```powershell
PS> Get-DropboxAccount
```

Returns the current account info (display name, email, account type, etc.).

### Example 2
```powershell
PS> Get-DropboxAccount -AccountId "dbid:AAH4f99T0taONIb..."
```

Looks up another account by its Dropbox ID.

## PARAMETERS

### -AccountId

Optional Dropbox account ID. When omitted, returns the connected account.

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

### -DriveName

Name of the Dropbox PSDrive previously created by `Connect-Dropbox`.
Defaults to `Dbx`. Specify a different name when you have connected
to multiple Dropbox accounts in the same session.

```yaml
Type: String
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

### IntelliTect.Dropbox.DropboxAccount

## NOTES

## RELATED LINKS
