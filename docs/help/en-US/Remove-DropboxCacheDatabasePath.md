---
external help file: DbxProvider.dll-Help.xml
Module Name: DbxProvider
online version:
schema: 2.0.0
---

# Remove-DropboxCacheDatabasePath

## SYNOPSIS

Removes a per-email cache database path override and persists the change.

## SYNTAX

```
Remove-DropboxCacheDatabasePath [-Email] <String> [-ProgressAction <ActionPreference>]
 [<CommonParameters>]
```

## DESCRIPTION

Deletes the cache database path override for the specified account email from
the process-wide options and from `%LOCALAPPDATA%\DbxProvider\config.json`. After
removal the account falls back to the default
`<cacheRoot>\DropboxCache.<email>.db` path on the next connect. Removing an email
that has no override is a no-op.

## EXAMPLES

### Example 1
```powershell
PS> Remove-DropboxCacheDatabasePath -Email 'me@example.com'
```

Drops the override for `me@example.com` and saves the updated configuration.

## PARAMETERS

### -Email

Dropbox account email whose override should be removed. Matching is
case-insensitive.

```yaml
Type: String
Parameter Sets: (All)
Aliases:

Required: True
Position: 0
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

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

### None

## NOTES

## RELATED LINKS