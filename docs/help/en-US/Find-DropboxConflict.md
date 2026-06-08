---
external help file: DbxProvider.dll-Help.xml
Module Name: DbxProvider
online version:
schema: 2.0.0
---

# Find-DropboxConflict

## SYNOPSIS

Finds Dropbox "conflicted copy" files from the local metadata cache, with zero API enumeration.

## SYNTAX

```
Find-DropboxConflict [[-Path] <String>] [-Pattern <String>] [-IncludeNonZero] [-StatePath <String>]
 [-DriveName <String>] [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

## DESCRIPTION

Finds zero-byte (or, with `-IncludeNonZero`, all) "conflicted copy" files under
a Dropbox subtree by reading the local metadata cache. It uses the same cache
name/zero-byte predicate as `Search-Dropbox`, with the conflict pattern and the
zero-byte filter applied, so it never performs the multi-hour recursive
enumeration the old API scan required.

Before reading, the cmdlet auto-refreshes the cache from the account delta
cursor (the shared refresh used by every cache-backed cmdlet): it drains the
changes since the last sync, shows a transient progress message, and reports how
many items were added or removed. When Dropbox rejects the saved cursor it warns
you to run `Build-DropboxCacheAll.ps1 -Rebuild`. Populate the cache first with
`Build-DropboxCacheAll.ps1` (or `Build-DropboxCache`).

Each match is emitted as an object with `Path` and `Bytes` properties, so the
results compose directly with `Remove-DropboxItemBatch`. Only files (never
folders) are returned, which keeps the result safe to delete.

If a `*.state.json` sidecar from an earlier (pre-cache) version is found, it is
archived to a `.bak` file rather than read -- the cache is now authoritative --
so upgrading neither errors nor loses the saved data.

## EXAMPLES

### Example 1
```powershell
PS> Find-DropboxConflict
```

Lists every zero-byte conflict file in the account, straight from the cache.

### Example 2
```powershell
PS> Find-DropboxConflict -Path 'Dbx:\Projects' | Remove-DropboxItemBatch -WhatIf
```

Lists conflict files under the `Projects` subtree and previews deleting them.

### Example 3
```powershell
PS> Find-DropboxConflict -IncludeNonZero
```

Also includes conflict files that are not zero bytes.

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
`*'s conflicted copy*`.

```yaml
Type: String
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: *'s conflicted copy*
Accept pipeline input: False
Accept wildcard characters: True
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

Path to a legacy `*.state.json` sidecar (written by a pre-cache version) to
migrate. When present and recognized, the sidecar is archived to a `.bak` file;
conflict finding itself is cache-backed and needs no sidecar. When omitted, the
obsolete per-account default location under the system temp folder is checked.

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

Reads only the local metadata cache; it does not contact Dropbox for the
enumeration. Build or refresh the cache with `Build-DropboxCacheAll.ps1`.

## RELATED LINKS