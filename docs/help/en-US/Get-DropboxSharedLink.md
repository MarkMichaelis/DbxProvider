---
external help file: DbxProvider.dll-Help.xml
Module Name: DbxProvider
online version:
schema: 2.0.0
---

# Get-DropboxSharedLink

## SYNOPSIS

Lists shared links, or returns metadata for a specific link.

## SYNTAX

```
Get-DropboxSharedLink [[-Path] <String>] [-Url <String>] [-DriveName <String>]
 [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

## DESCRIPTION

Without `-Url`, lists all shared links in the account, optionally
filtered to those rooted at `-Path`. With `-Url`, returns metadata
for that specific shared link.

## EXAMPLES


### Example 1
```powershell
PS> Get-DropboxSharedLink
```

Lists every shared link in the account.

### Example 2
```powershell
PS> Get-DropboxSharedLink -Path /Reports
```

Lists shared links anchored under ``/Reports``.

### Example 3
```powershell
PS> Get-DropboxSharedLink -Url "https://www.dropbox.com/s/abc123/report.pdf"
```

Looks up metadata for a single link by URL.

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

### -Path

Restrict the listing to shared links anchored at this Dropbox path.

```yaml
Type: String
Parameter Sets: (All)
Aliases: FullName

Required: False
Position: 0
Default value: None
Accept pipeline input: True (ByPropertyName, ByValue)
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

### -Url

Public URL of a specific shared link to look up. Selects the **ByUrl** parameter set.

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

### CommonParameters
This cmdlet supports the common parameters: -Debug, -ErrorAction, -ErrorVariable, -InformationAction, -InformationVariable, -OutVariable, -OutBuffer, -PipelineVariable, -Verbose, -WarningAction, and -WarningVariable. For more information, see [about_CommonParameters](http://go.microsoft.com/fwlink/?LinkID=113216).

## INPUTS

### System.String

## OUTPUTS

### DbxProvider.Models.DropboxSharedLink

## NOTES

## RELATED LINKS
