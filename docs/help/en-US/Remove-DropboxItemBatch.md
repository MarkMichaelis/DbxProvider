---
external help file: DbxProvider.dll-Help.xml
Module Name: DbxProvider
online version:
schema: 2.0.0
---

# Remove-DropboxItemBatch

## SYNOPSIS

Deletes many Dropbox items in a single batched API call.

## SYNTAX

```
Remove-DropboxItemBatch [-Path] <String[]> [-DriveName <String>] [-SkipCacheUpdate]
 [-MaxConcurrency <Int32>] [-BatchSize <Int32>] [-ProgressAction <ActionPreference>] [-WhatIf] [-Confirm] [<CommonParameters>]
```

## DESCRIPTION

Runs Dropbox's batch-delete API on every path in `-Path`. Supports
`-WhatIf` and `-Confirm`. Items go to the Dropbox trash and can be
restored from the web UI within the account's retention window.

## EXAMPLES


### Example 1
```powershell
PS> Remove-DropboxItemBatch -Path /tmp/a.txt,/tmp/b.txt
```

Deletes two files in one batched call after a confirmation prompt.

### Example 2
```powershell
PS> Get-ChildItem Dbx:\Trash -File | Select -Expand Path | Remove-DropboxItemBatch -Confirm:$false
```

Bulk-deletes every file in a folder via pipeline, with no prompt.

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

One or more Dropbox paths to delete. Accepts pipeline input.

```yaml
Type: String[]
Parameter Sets: (All)
Aliases:

Required: True
Position: 0
Default value: None
Accept pipeline input: True (ByValue)
Accept wildcard characters: False
```

### -SkipCacheUpdate

Leaves the local metadata cache untouched after deleting. By default,
successfully deleted items are removed from the drive's cache so a later
cache-mode `Search-Dropbox` no longer lists them.

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

### -MaxConcurrency

Maximum number of Dropbox `delete_batch` jobs to run concurrently. Each
batch (up to 1000 paths) is processed asynchronously server-side, so
overlapping several in-flight jobs multiplies throughput. Defaults to 1
(serial). Range 1-32. Dropbox rate-limit (429) backoff is applied
automatically to every concurrent job.

```yaml
Type: Int32
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: 1
Accept pipeline input: False
Accept wildcard characters: False
```

### -BatchSize

Number of paths sent in each `delete_batch` API call. Defaults to 1000
(the Dropbox `delete_batch` limit). Range 1-1000. Smaller batches finish --
and so advance the progress bar -- more often, which keeps progress visible
during the multi-minute server-side wait. This is independent of
`-MaxConcurrency`: shrinking the batch makes progress finer without adding
the overlapping writes that cause namespace lock contention.

```yaml
Type: Int32
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: 1000
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

### System.String[]

## OUTPUTS

### System.Object
## NOTES

## RELATED LINKS
