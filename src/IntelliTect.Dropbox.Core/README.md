# IntelliTect.Dropbox.Core

Standalone, PowerShell-free core for working with the Dropbox API v2.

- Comprehensive `DropboxServiceClient` wrapper over `Dropbox.Api`.
- Builds a `DropboxClient` directly from an app key/secret/refresh token (or access token).
- Metadata cache, rate-limit retry, credential persistence, and a framework-neutral
  wildcard matcher.

Multi-targets `netstandard2.0` and `net8.0`. Independent of `IntelliTect.Dropbox.Auth`.

## Metadata cache

Each cache entry holds the items from a prior non-recursive `list_folder` for a
path, together with the cursor describing that snapshot. Reads call
`list_folder/continue(cursor)` to apply deltas, so Dropbox always remains the
master.

`MetadataCache.BuildAsync(path)` pre-populates an entire subtree with a single
recursive `list_folder`: the flat result is grouped by parent folder and each
group is stored as a per-folder entry. Because a recursive listing yields only
one subtree cursor (not per-folder cursors), the built entries start with an
empty cursor. An entry with an empty cursor acquires a real per-folder cursor on
its first validated read (`GetChildrenAsync`/`UpdateAsync`) via a fresh
`list_folder`, after which it validates like any other entry. A built entry is
therefore never served without first reconciling against Dropbox.
