---
external help file: DbxProvider.dll-Help.xml
Module Name: DbxProvider
online version:
schema: 2.0.0
---

# Set-DropboxCacheOption

## SYNOPSIS

Tunes the metadata cache at runtime: enable/disable, max entries,
flush cadence.

## SYNTAX

```
Set-DropboxCacheOption [-Disable] [-Enable] [-MaxEntries <Int32>] [-FlushIntervalSeconds <Int32>]
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
PS> Set-DropboxCacheOption -MaxEntries 5000 -FlushIntervalSeconds 10
```

Lower the cap and slow the disk flush cadence.

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

### -MaxEntries

Soft cap on cached folders before LRU eviction kicks in.

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

### DbxProvider.Services.CacheOptions

## NOTES

## RELATED LINKS
