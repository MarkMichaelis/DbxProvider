---
external help file: DbxProvider.dll-Help.xml
Module Name: DbxProvider
online version:
schema: 2.0.0
---

# Find-DropboxConflict

## SYNOPSIS

Finds Dropbox "conflicted copy" files, using a saved cursor to make repeat scans cheap.

## SYNTAX

```
Find-DropboxConflict [[-Path] <String>] [-Pattern <String>] [-IncludeNonZero] [-StatePath <String>]
 [-Full] [-DriveName <String>] [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

## DESCRIPTION

Scans a Dropbox subtree for zero-byte (or, with `-IncludeNonZero`, all)
"conflicted copy" files. The first run does a full recursive enumeration and
persists the resulting account cursor and match set to a JSON sidecar file.
Subsequent runs fetch only the delta (adds, updates, removes) since that cursor
instead of re-walking the whole tree, which makes repeat scans dramatically
cheaper on large accounts.

The scan transparently falls back to a full pass when the saved cursor is
rejected by Dropbox (expired/reset) or when any scan parameter (`-Path`,
`-Pattern`, `-IncludeNonZero`, or the account) differs from the saved state.
Pass `-Full` to ignore saved state and force a full enumeration.

Each match is emitted as an object with `Path` and `Bytes` properties, so the
results compose directly with `Remove-DropboxItemBatch`.

## EXAMPLES

### Example 1
```powershell
PS> Find-DropboxConflict
```

Scans the whole drive. The first run is a full pass; later runs are incremental.

### Example 2
```powershell
PS> Find-DropboxConflict -Path 'Dbx:\Projects' | Remove-DropboxItemBatch -WhatIf
```

Scans just the `Projects` subtree and previews deleting the matches.

### Example 3
```powershell
PS> Find-DropboxConflict -Full -IncludeNonZero
```

Forces a full re-enumeration and also captures non-zero-byte conflict files.

## PARAMETERS

### -Path

Dropbox path -- or a drive-qualified path such as `Dbx:\Folder` -- to scan.
Defaults to the account root.

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

### -Pattern

Filename `-like` wildcard that identifies a conflict file. Defaults to
`*'s conflicted copy*`. Changing it invalidates any saved incremental state.

```yaml
Type: String
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: *'s conflicted copy*
Accept pipeline input: False
Accept wildcard characters: False
```

### -IncludeNonZero

Also capture conflict files that are not zero bytes. By default only zero-byte
conflict files are captured.

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

### -StatePath

Path to the JSON sidecar state file that holds the saved cursor and match set.
Defaults to a per-account/path/pattern file under the system temp folder.

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

### -Full

Ignore any saved state and force a full recursive enumeration, then save fresh
state for the next incremental run.

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

Name of the Dropbox PSDrive previously created by `Connect-Dropbox`. Defaults
to `Dbx`.

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

### IntelliTect.Dropbox.ConflictMatch

## NOTES

## RELATED LINKS