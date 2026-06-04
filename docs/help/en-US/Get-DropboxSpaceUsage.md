---
external help file: DbxProvider.dll-Help.xml
Module Name: DbxProvider
online version:
schema: 2.0.0
---

# Get-DropboxSpaceUsage

## SYNOPSIS

Reports storage quota and usage for the connected Dropbox account.

## SYNTAX

```
Get-DropboxSpaceUsage [-DriveName <String>] [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

## DESCRIPTION

Returns a `DropboxSpaceUsage` object with `UsedBytes`,
`AllocatedBytes`, and the allocation type (individual vs. team).

## EXAMPLES


### Example 1
```powershell
PS> Get-DropboxSpaceUsage
```

Shows current storage usage.

### Example 2
```powershell
PS> $u = Get-DropboxSpaceUsage; "{0:N1} GB / {1:N1} GB" -f ($u.UsedBytes/1GB), ($u.AllocatedBytes/1GB)
```

Formats usage as gigabytes.

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

### IntelliTect.Dropbox.DropboxSpaceUsage

## NOTES

## RELATED LINKS
