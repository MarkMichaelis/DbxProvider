---
external help file: DbxProvider.dll-Help.xml
Module Name: DbxProvider
online version:
schema: 2.0.0
---

# Set-DropboxCacheDatabasePath

## SYNOPSIS

Pins a Dropbox account's metadata cache database to an explicit file path and
persists the override across sessions.

## SYNTAX

```
Set-DropboxCacheDatabasePath [-Email] <String> [-Path] <String> [-DriveName <String>]
 [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

## DESCRIPTION

Adds or updates a per-email override that places the named account's metadata
cache database at exactly the configured path instead of the default
`<cacheRoot>\DropboxCache.<email>.db`. The override is stored in
`%LOCALAPPDATA%\DbxProvider\config.json` so it survives module reloads and new
PowerShell sessions. The path may begin with `~` (the user profile) and may
contain environment variables; it is expanded and made absolute when the cache
is constructed.

The live cache database of an already-connected drive is fixed at construction
and is never moved. If a connected drive's account matches the email, reconnect
it (`Disconnect-Dropbox` then `Connect-Dropbox`) for the new path to take effect.

## EXAMPLES

### Example 1
```powershell
PS> Set-DropboxCacheDatabasePath -Email 'me@example.com' -Path '~\me@example.com.DropboxCache.db'
```

Stores the cache database for `me@example.com` directly under the user profile.

### Example 2
```powershell
PS> Set-DropboxCacheDatabasePath -Email 'me@example.com' -Path 'D:\Caches\dropbox\'
```

Treats a trailing-separator path as a directory and places the default
`DropboxCache.me@example.com.db` file inside it.

## PARAMETERS

### -Email

Dropbox account email whose cache database path is being overridden. Matching is
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

### -Path

Target database file path. A leading `~` expands to the user profile and
environment variables are expanded. A path that names an existing directory or
ends in a separator receives the default `DropboxCache.<email>.db` file name.

```yaml
Type: String
Parameter Sets: (All)
Aliases:

Required: True
Position: 1
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -DriveName

Name of the Dropbox PSDrive. Defaults to `Dbx`. Used only to warn when a live
drive must be reconnected for the new path to take effect.

```yaml
Type: String
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: Dbx
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

### System.Management.Automation.PSObject

## NOTES

## RELATED LINKS