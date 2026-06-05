---
external help file: DbxProvider.dll-Help.xml
Module Name: DbxProvider
online version:
schema: 2.0.0
---

# Set-DropboxCacheOption

## SYNOPSIS

Tunes the metadata cache at runtime: enable/disable, in-memory budget,
flush cadence.

## SYNTAX

```
Set-DropboxCacheOption [-Disable] [-Enable] [-MaxInMemoryEntries <Int32>] [-FlushIntervalSeconds <Int32>]
 [-DriveName <String>] [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

## DESCRIPTION

Mutates the active drive's cache options and propagates the values to
the process-wide `CacheOptions.Default` so a subsequent
`Connect-Dropbox` inherits them. Use `-Disable` to bypass the cache
entirely for diagnostics.

## EXAMPLES

### Example 1
```powershell
PS> Set-DropboxCacheOption -Disable
```

Bypass the cache. Every `Get-ChildItem` performs a full enumeration.

### Example 2
```powershell
PS> Set-DropboxCacheOption -MaxInMemoryEntries 100000 -FlushIntervalSeconds 10
```

Raise the in-memory working-set budget and slow the disk flush cadence.
The on-disk SQLite cache itself is never capped.

## PARAMETERS

### -Disable

Turn the cache off.

```yaml
Type: SwitchParameter
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: False
Accept pipeline input: False
Accept wildcard characters: False
```

### -DriveName

Name of the Dropbox PSDrive. Defaults to `Dbx`.

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

### -Enable

Turn the cache on (counterpart to `-Disable`).

```yaml
Type: SwitchParameter
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: False
Accept pipeline input: False
Accept wildcard characters: False
```

### -FlushIntervalSeconds

Background disk-flush cadence. Set to `0` to disable.

```yaml
Type: Int32
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -MaxInMemoryEntries

Soft budget on how many entries stay resident in memory. The persistent
on-disk SQLite cache is never capped; when this budget is exceeded the
least-recently-used entries are flushed to disk and dropped from memory
only, then re-hydrated on demand. Set to `0` to keep every loaded entry
resident.

```yaml
Type: Int32
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
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

### IntelliTect.Dropbox.CacheOptions

## NOTES

## RELATED LINKS
