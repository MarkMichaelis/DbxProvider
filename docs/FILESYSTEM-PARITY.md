# FileSystem-Parity Policy

DbxProvider exposes Dropbox as a PowerShell drive (`Dbx:\`) and aims to feel
familiar to anyone who already uses `Get-ChildItem`, `Where-Object Length`,
`Sort-Object LastWriteTime`, etc. against the built-in FileSystem provider.

This document records **where** we chase that parity and **where** we
deliberately don't, so contributors stop relitigating the same trade-offs.

## Principle

> Match the FileSystem provider where the semantics translate cleanly.
> Diverge — visibly — where they don't. Don't paper over Dropbox semantics
> just to look like `System.IO.FileInfo`.

## What we DO mirror

| FileSystem | DbxProvider | Notes |
|------------|-------------|-------|
| `Length`            | `Length`            | Cloud size in bytes. Matches `FileInfo.Length`. |
| `FullName`          | `FullName`          | Alias for `Path` (Dropbox-style `/foo/bar`). |
| `LastWriteTime`     | `LastWriteTime`     | Alias for `ServerModified`. |
| `Extension`         | `Extension`         | Computed from `Name`. |
| `BaseName`          | `BaseName`          | Computed from `Name`. |
| `PSIsContainer`     | (set by provider)   | True for folders. |
| `-File` / `-Directory` on `Get-ChildItem` | yes | Post-filter switches. |
| `-Filter` on `Get-ChildItem`              | yes (server-side via `search_v2` when combined with `-Recurse`) | See caveat below. |
| `-Recurse`, `-Include`, `-Exclude`        | yes (PowerShell handles after listing). |  |

## What we DON'T mirror (and why)

These are pitfalls — pretending to support them would invite silent bugs.

1. **`Attributes`, `IsReadOnly`, `Hidden`, `Archive`, `System` flags.**
   Dropbox has no equivalent flag set. Sharing/permission state is a different
   model (`SharedFolderId`, `HasExplicitSharedMembers`, `AccessLevel`) and is
   exposed under its own names.

2. **`CreationTime` / `CreationTimeUtc`.**
   Dropbox tracks `ServerModified` and `ClientModified`. There is no real
   "created" timestamp from Dropbox's side. We expose `ClientModified`
   under its own name rather than aliasing it to `CreationTime`.

3. **ACLs (`Get-Acl`, `Set-Acl`), `SecurityDescriptor`, owner SIDs.**
   Dropbox's permission model is per-folder sharing membership. Use
   `Get-DropboxSharedFolder`, `Get-DropboxMember`, `Add-DropboxMember`.

4. **Hard links, junctions, symbolic links, reparse points, alternate
   data streams.** None of these exist in Dropbox.

5. **`Refresh()` / `Exists` semantics from `FileInfo`.**
   `DropboxItem` is a snapshot from a list/search call, not a live handle.
   To refresh, re-list the parent or call `Update-DropboxCache`.

6. **Implicit type identity with `System.IO.FileInfo` /
   `System.IO.DirectoryInfo`.** A `DropboxItem` is not a `FileInfo`. Code
   that does `$item -is [System.IO.FileInfo]` will get `$false`, by design.
   We don't inherit from `FileSystemInfo` because that drags in a pile of
   members (`Attributes`, `CreationTime`, `LinkTarget`, ...) we cannot
   honor.

7. **Case sensitivity.** Dropbox paths are case-insensitive but
   case-preserving server-side. FileSystem on Windows is case-insensitive,
   on Linux it's case-sensitive. Don't write scripts that depend on a
   specific casing model.

8. **`-Filter` performance assumption.** Inside the FileSystem provider,
   `-Filter` is a server-side Win32 glob and is essentially free.
   Inside DbxProvider, `-Filter` with `-Recurse` routes to Dropbox's
   `search_v2` API (one HTTP call, server-filtered) — fast but **not**
   instantaneous. Without `-Recurse`, PowerShell post-filters a single
   folder listing. Plan recursive `-Filter` queries accordingly.

## What we DO surface that FileSystem doesn't have

These are Dropbox-native properties exposed on `DropboxItem` so scripts
can use them without losing information to a FileSystem-shaped wrapper:

- `Rev` — Dropbox revision id.
- `ContentHash` — Dropbox's custom content hash (not SHA-256 / MD5).
- `Id` — Dropbox file id (stable across moves/renames).
- `IsDeleted` — soft-deleted flag.
- `SharedFolderId`, `ParentSharedFolderId`, `HasExplicitSharedMembers`.
- `MediaInfoTag`, `SymlinkTarget`, `IsDownloadable`.
- `ClientModified` — last-modified time reported by the uploading client.
- `ServerModified` — Dropbox-side modification timestamp.

## Cost model — read before reaching for `Get-ChildItem Dbx:\ -Recurse`

Operations against `Dbx:\` are network calls, not free filesystem syscalls:

- Each folder listing is one `files/list_folder` HTTP call (paginated).
- Each delete is one `files/delete_v2` call (~100 ms). For bulk, use
  `Remove-DropboxItemBatch`.
- The DbxProvider cache reuses prior listings within a session.
- Rate limits exist (HTTP 429). Don't issue thousands of single-item calls
  in tight loops; batch where APIs exist.

## When in doubt

If you're tempted to add a property or switch to look more like FileSystem,
ask:

1. Does the underlying Dropbox semantic actually map?
2. If a user relies on the FileSystem behavior, will they get a wrong
   answer, a silent perf cliff, or a surprising no-op?
3. Is there already a first-class Dropbox-native name for this?

If any of those is "yes / would mislead", add it under its own name (or
skip it) and add a row to the **DON'T mirror** table above.
