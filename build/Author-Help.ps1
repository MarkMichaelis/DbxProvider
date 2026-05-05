#requires -Version 7.4
<#
.SYNOPSIS
    Authors PlatyPS markdown content for all DbxProvider cmdlets by
    replacing placeholder sections with real Synopsis / Description /
    Examples / Parameter prose. Idempotent (re-runnable).

.DESCRIPTION
    Run after Build-Help.ps1 -Scaffold. Edits files under
    docs\help\en-US\ in place. Used as a one-time bulk authoring step
    so we don't hand-edit 37 markdown files. Future per-cmdlet edits
    can be made directly in the markdown.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$helpRoot = Join-Path $repoRoot 'docs\help\en-US'

if (-not (Test-Path -LiteralPath $helpRoot)) {
    throw "Help folder '$helpRoot' not found. Run build\Build-Help.ps1 -Scaffold first."
}

# Common parameter description used across all cmdlets that derive from
# DropboxCmdletBase. Authored once here.
$DriveNameDesc = @"
Name of the Dropbox PSDrive previously created by ``Connect-Dropbox``.
Defaults to ``Dbx``. Specify a different name when you have connected
to multiple Dropbox accounts in the same session.
"@

$ProgressActionDesc = @"
Standard PowerShell common parameter that controls how progress records
are reported. See [about_CommonParameters](https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_commonparameters).
"@

# --- Cmdlet content table ----------------------------------------------------
# Each entry: Synopsis, Description (multi-line), Examples (array of @{Code; Desc}),
# Parameters (hashtable of name -> description).
$Content = @{

'Connect-Dropbox' = @{
    Synopsis = 'Authenticates to Dropbox and creates a PSDrive for the account.'
    Description = @"
Authenticates against the Dropbox API and registers a PowerShell drive
that exposes the account's file tree as a navigable file system.

Three usage modes are supported:

- **Token mode**: ``Connect-Dropbox -AccessToken <token>`` uses a
  short-lived access token directly. No credentials are persisted.
- **OAuth mode** (default): ``Connect-Dropbox -AppKey <key> [-AppSecret <secret>]``
  runs an OAuth 2.0 + PKCE authorization-code flow, opens the browser,
  listens on a local redirect URI, and obtains an offline refresh token.
- **Reuse mode**: ``Connect-Dropbox`` with no parameters reuses
  credentials previously saved by ``Set-DropboxCredential`` or by an
  earlier OAuth connect.

Unless ``-NoSave`` is specified, the AppKey / AppSecret / RefreshToken
are persisted via the platform's credential store
(DPAPI-encrypted JSON on Windows) so subsequent sessions can reconnect
without a browser round-trip.

A global function named ``<DriveName>:`` is also registered so you can
switch to the drive by typing ``Dbx:`` (mirroring ``C:`` for the
filesystem provider).
"@
    Examples = @(
        @{ Code = 'Connect-Dropbox -AccessToken "sl.B-abc123..."'
           Desc = 'Connects using an existing short-lived access token. Useful for quick experiments or automation that supplies the token from a vault.' }
        @{ Code = 'Connect-Dropbox -AppKey "abc123" -AppSecret "xyz789"'
           Desc = 'Runs the full OAuth + PKCE browser flow, requests an offline refresh token, and saves credentials so future sessions reconnect silently.' }
        @{ Code = 'Connect-Dropbox'
           Desc = 'Reuses previously-saved credentials. This is the typical command at the start of a script after the first interactive setup.' }
        @{ Code = 'Connect-Dropbox -AppKey $key -DriveName Work -RedirectPort 53000'
           Desc = 'Connects a second account under a separate drive name and uses a non-default redirect port (which must be registered in the Dropbox App Console).' }
    )
    Parameters = @{
        AccessToken  = 'Short-lived Dropbox API access token. Selects the **Token** parameter set; no OAuth flow is run and no credentials are saved.'
        AppKey       = 'Dropbox app key (client ID) issued by the Dropbox App Console. Required for the OAuth flow unless one is already saved in the credential store.'
        AppSecret    = 'Dropbox app secret. Required for "Full Dropbox" / confidential apps; omit for PKCE-only public apps.'
        RefreshToken = 'Long-lived OAuth refresh token. Provide this to skip the browser flow when you have obtained the token out-of-band.'
        RedirectPort = 'Local TCP port the OAuth callback listener binds to. Must match a redirect URI registered in the Dropbox App Console (e.g. ``http://localhost:52475/``). Defaults to 52475.'
        NoSave       = 'Do not persist credentials after a successful connect. The refresh token (if any) is printed to the host so you can save it yourself.'
        DriveName    = 'Name to give the resulting PSDrive. Defaults to ``Dbx``. Use a unique name per connected account.'
    }
}

'Disconnect-Dropbox' = @{
    Synopsis = 'Disconnects from Dropbox and removes the PSDrive.'
    Description = @"
Removes the named Dropbox PSDrive and disposes the underlying API
client. Also removes the convenience ``<DriveName>:`` global function
registered by ``Connect-Dropbox``. Saved credentials in the credential
store are **not** affected; use ``Remove-DropboxCredential`` to delete
them.
"@
    Examples = @(
        @{ Code = 'Disconnect-Dropbox'
           Desc = 'Disconnects the default ``Dbx:`` drive.' }
        @{ Code = 'Disconnect-Dropbox -DriveName Work'
           Desc = 'Disconnects a non-default drive.' }
    )
    Parameters = @{
        DriveName = 'Name of the Dropbox PSDrive to remove. Defaults to ``Dbx``.'
    }
}

'Get-DropboxCredential' = @{
    Synopsis = 'Returns the Dropbox credentials currently saved in the credential store.'
    Description = @"
Reads the per-user credential file (DPAPI-encrypted on Windows) and
returns an object with the AppKey, masked AppSecret and RefreshToken,
the last save timestamp, and the file path on disk. Use
``-AsPlainText`` to retrieve the unmasked secrets (e.g. when migrating
to another machine).
"@
    Examples = @(
        @{ Code = 'Get-DropboxCredential'
           Desc = 'Shows the saved credentials with the AppSecret and RefreshToken masked.' }
        @{ Code = 'Get-DropboxCredential -AsPlainText'
           Desc = 'Returns the credentials with secrets revealed. Pipe to ``ConvertTo-Json`` for backup.' }
    )
    Parameters = @{
        AsPlainText = 'Return the AppSecret and RefreshToken in clear text instead of masked.'
    }
}

'Set-DropboxCredential' = @{
    Synopsis = 'Saves Dropbox credentials to the per-user credential store.'
    Description = @"
Persists any combination of AppKey, AppSecret, and RefreshToken to the
DPAPI-encrypted credential file used by ``Connect-Dropbox`` for silent
re-auth. Existing values are preserved when the corresponding parameter
is omitted, allowing you to update one field at a time.
"@
    Examples = @(
        @{ Code = 'Set-DropboxCredential -AppKey "abc123" -AppSecret "xyz789"'
           Desc = 'Stores the app key and secret, leaving any existing refresh token in place.' }
        @{ Code = 'Set-DropboxCredential -RefreshToken $token'
           Desc = 'Updates only the refresh token (e.g. after rotation).' }
    )
    Parameters = @{
        AppKey       = 'Dropbox app key (client ID). Pass ``$null`` or omit to leave any existing value untouched.'
        AppSecret    = 'Dropbox app secret. Omit to leave the existing value untouched.'
        RefreshToken = 'Long-lived OAuth refresh token. Omit to leave the existing value untouched.'
    }
}

'Remove-DropboxCredential' = @{
    Synopsis = 'Deletes the saved Dropbox credentials from disk.'
    Description = @"
Removes the credential file used by ``Connect-Dropbox`` and
``Get-DropboxCredential``. Supports ``-WhatIf`` and ``-Confirm``.
This does not disconnect any active PSDrive; existing in-memory
clients keep working until the session ends or
``Disconnect-Dropbox`` is called.
"@
    Examples = @(
        @{ Code = 'Remove-DropboxCredential'
           Desc = 'Prompts for confirmation, then deletes the saved credentials.' }
        @{ Code = 'Remove-DropboxCredential -Confirm:$false'
           Desc = 'Deletes the saved credentials without prompting (suitable for automation).' }
    )
    Parameters = @{}
}

'Search-Dropbox' = @{
    Synopsis = 'Searches for files and folders by name or content in Dropbox.'
    Description = @"
Calls the Dropbox ``files/search_v2`` endpoint with the given query and
returns matching items as ``DropboxSearchResult`` objects. The search
covers file and folder names; depending on the indexer state Dropbox
may also match file contents for supported types.

Use ``-Path`` to restrict the search to a subtree, ``-MaxResults`` to
cap the page size, and ``-IncludeHighlights`` to receive snippet
highlights in the result objects.
"@
    Examples = @(
        @{ Code = 'Search-Dropbox -Query "budget"'
           Desc = 'Returns up to 100 items whose name or contents match "budget" anywhere in the account.' }
        @{ Code = 'Search-Dropbox -Query "*.docx" -Path "/Reports" -MaxResults 25'
           Desc = 'Restricts the search to ``/Reports`` and limits the result set to 25 items.' }
        @{ Code = 'Search-Dropbox -Query "TODO" -IncludeHighlights | Select-Object Path, Highlights'
           Desc = 'Returns matches with content snippets so you can see why each item matched.' }
    )
    Parameters = @{
        Query             = 'Search expression. Supports plain words and simple wildcards (e.g. ``*.docx``).'
        Path              = 'Restrict the search to this Dropbox folder (e.g. ``/Reports``). Empty (the default) searches the entire account.'
        MaxResults        = 'Maximum number of results to return. Defaults to 100.'
        IncludeHighlights = 'Include match highlights / snippets on each result.'
    }
}

'Get-DropboxRevision' = @{
    Synopsis = 'Lists historical revisions of a Dropbox file.'
    Description = @"
Returns up to ``-Limit`` revisions for a file, newest first, as
``DropboxRevision`` objects. Each revision exposes its ``Rev`` ID,
which is the value passed to ``Restore-DropboxRevision`` to roll back.
"@
    Examples = @(
        @{ Code = 'Get-DropboxRevision -Path "/notes.txt"'
           Desc = 'Shows the 10 most recent revisions of ``/notes.txt``.' }
        @{ Code = 'Get-DropboxRevision -Path "/notes.txt" -Limit 50 | Format-Table Rev, ServerModified, Size'
           Desc = 'Lists up to 50 revisions in a compact table.' }
    )
    Parameters = @{
        Path  = 'Dropbox path of the file whose revisions to list. Accepts pipeline input by value or by the ``FullName`` property.'
        Limit = 'Maximum number of revisions to return. Defaults to 10.'
    }
}

'Restore-DropboxRevision' = @{
    Synopsis = 'Restores a Dropbox file to a previous revision.'
    Description = @"
Replaces the current contents of ``-Path`` with the revision identified
by ``-Rev``, returning the new ``DropboxItem`` for the restored file.
Supports ``-WhatIf`` and ``-Confirm``. Use ``Get-DropboxRevision`` to
discover ``Rev`` values.
"@
    Examples = @(
        @{ Code = 'Restore-DropboxRevision -Path "/notes.txt" -Rev "0123456789abcdef"'
           Desc = 'Restores ``/notes.txt`` to the named revision.' }
        @{ Code = 'Get-DropboxRevision /report.docx | Select -Skip 1 -First 1 | ForEach-Object { Restore-DropboxRevision /report.docx $_.Rev }'
           Desc = 'Rolls back to the second-newest revision (i.e. undoes the most recent change).' }
    )
    Parameters = @{
        Path = 'Dropbox path of the file to restore.'
        Rev  = 'Revision identifier to restore to (as returned by ``Get-DropboxRevision``).'
    }
}

'Invoke-DropboxDownload' = @{
    Synopsis = 'Downloads a file from Dropbox to local disk.'
    Description = @"
Streams the file at the given Dropbox path to a local destination.
Refuses to overwrite an existing local file unless ``-Force`` is
specified. Creates the destination directory if missing. Returns the
resulting ``System.IO.FileInfo``.
"@
    Examples = @(
        @{ Code = 'Invoke-DropboxDownload -Path /report.pdf -Destination C:\Temp\report.pdf'
           Desc = 'Downloads ``/report.pdf`` to the local path.' }
        @{ Code = 'Invoke-DropboxDownload /image.png .\image.png -Force'
           Desc = 'Overwrites an existing local file using positional arguments.' }
        @{ Code = 'Get-ChildItem Dbx:\Reports\*.pdf | Invoke-DropboxDownload -Destination .\reports\'
           Desc = 'Pipes Dropbox items into the cmdlet via the ``FullName`` alias to bulk-download a folder.' }
    )
    Parameters = @{
        Path        = 'Dropbox path of the file to download. Accepts pipeline input by value or by ``FullName`` property.'
        Destination = 'Local file or directory path to write to. Relative paths are resolved against the current PowerShell location.'
        Force       = 'Overwrite the destination file if it already exists.'
    }
}

'Invoke-DropboxUpload' = @{
    Synopsis = 'Uploads a local file to Dropbox, with automatic large-file chunking.'
    Description = @"
Uploads ``-Source`` to ``-DropboxPath``. Files larger than the
single-shot upload limit are automatically split into upload-session
chunks. Use ``-WriteMode`` to control how an existing remote file is
treated (``add``, ``overwrite``, ``update``). Returns the resulting
``DropboxItem``.
"@
    Examples = @(
        @{ Code = 'Invoke-DropboxUpload -Source .\report.docx -DropboxPath /Reports/report.docx'
           Desc = 'Uploads a local file, overwriting any existing remote file with the same name.' }
        @{ Code = 'Invoke-DropboxUpload .\photo.jpg /Photos/photo.jpg -WriteMode add'
           Desc = 'Uploads only if no file already exists at the destination (Dropbox auto-renames on conflict).' }
        @{ Code = 'Get-ChildItem .\backup -File | ForEach-Object { Invoke-DropboxUpload $_.FullName "/Backup/$($_.Name)" }'
           Desc = 'Bulk-uploads every file in a local folder.' }
    )
    Parameters = @{
        Source      = 'Local path of the file to upload. Relative paths are resolved against the current PowerShell location.'
        DropboxPath = 'Destination path inside Dropbox (e.g. ``/Reports/report.docx``).'
        Force       = 'Reserved for future use; currently has no effect (overwrite behavior is controlled by ``-WriteMode``).'
        WriteMode   = 'How to handle an existing remote file: ``add`` (auto-rename on conflict), ``overwrite`` (default), or ``update`` (require previous rev match).'
    }
}

'New-DropboxSharedLink' = @{
    Synopsis = 'Creates a shared link for a Dropbox file or folder.'
    Description = @"
Generates a Dropbox shared link with optional visibility and
expiration. Returns a ``DropboxSharedLink`` object whose ``Url``
property is the link to share.
"@
    Examples = @(
        @{ Code = 'New-DropboxSharedLink -Path /report.pdf'
           Desc = 'Creates a shared link with default (account-policy) visibility and no expiration.' }
        @{ Code = 'New-DropboxSharedLink /draft.docx -Visibility team_only -Expires (Get-Date).AddDays(7)'
           Desc = 'Creates a team-only link that expires in seven days.' }
    )
    Parameters = @{
        Path       = 'Dropbox path of the item to share. Accepts pipeline input by value or by ``FullName`` property.'
        Visibility = 'Link visibility: ``public``, ``team_only``, or ``password``. When omitted, the account default policy applies.'
        Expires    = 'UTC date/time at which the link should expire (Dropbox Professional / Business only).'
    }
}

'Get-DropboxSharedLink' = @{
    Synopsis = 'Lists shared links, or returns metadata for a specific link.'
    Description = @"
Without ``-Url``, lists all shared links in the account, optionally
filtered to those rooted at ``-Path``. With ``-Url``, returns metadata
for that specific shared link.
"@
    Examples = @(
        @{ Code = 'Get-DropboxSharedLink'
           Desc = 'Lists every shared link in the account.' }
        @{ Code = 'Get-DropboxSharedLink -Path /Reports'
           Desc = 'Lists shared links anchored under ``/Reports``.' }
        @{ Code = 'Get-DropboxSharedLink -Url "https://www.dropbox.com/s/abc123/report.pdf"'
           Desc = 'Looks up metadata for a single link by URL.' }
    )
    Parameters = @{
        Path = 'Restrict the listing to shared links anchored at this Dropbox path.'
        Url  = 'Public URL of a specific shared link to look up. Selects the **ByUrl** parameter set.'
    }
}

'Remove-DropboxSharedLink' = @{
    Synopsis = 'Revokes a Dropbox shared link.'
    Description = @"
Revokes the shared link identified by URL. Anyone holding the link
will lose access. Supports ``-WhatIf`` and ``-Confirm``. The
underlying file or folder is not affected.
"@
    Examples = @(
        @{ Code = 'Remove-DropboxSharedLink -Url "https://www.dropbox.com/s/abc123/report.pdf"'
           Desc = 'Revokes the named shared link after prompting for confirmation.' }
        @{ Code = 'Get-DropboxSharedLink -Path /Stale | Remove-DropboxSharedLink -Confirm:$false'
           Desc = 'Bulk-revokes every shared link under ``/Stale``.' }
    )
    Parameters = @{
        Url = 'Public URL of the shared link to revoke.'
    }
}

'Add-DropboxSharedFolder' = @{
    Synopsis = 'Converts a Dropbox folder into a shared folder.'
    Description = @"
Begins sharing the folder at ``-Path`` and returns the resulting
shared-folder ID. Use this ID with ``Add-DropboxMember``,
``Get-DropboxMember``, ``Remove-DropboxMember``, and
``Remove-DropboxSharedFolder``.
"@
    Examples = @(
        @{ Code = 'Add-DropboxSharedFolder -Path /Project'
           Desc = 'Shares ``/Project``; the returned ID is then used to add members.' }
        @{ Code = '$id = Add-DropboxSharedFolder /Project; Add-DropboxMember -SharedFolderId $id -Email alice@contoso.com -AccessLevel editor'
           Desc = 'Shares a folder and immediately invites a member as editor.' }
    )
    Parameters = @{
        Path = 'Dropbox path of the folder to share.'
    }
}

'Remove-DropboxSharedFolder' = @{
    Synopsis = 'Stops sharing a previously shared Dropbox folder.'
    Description = @"
Unshares the folder identified by ``-SharedFolderId``. By default the
content is removed from each member's account; use ``-LeaveACopy`` to
let members keep an unshared copy. Supports ``-WhatIf`` and ``-Confirm``.
"@
    Examples = @(
        @{ Code = 'Remove-DropboxSharedFolder -SharedFolderId "1234567890"'
           Desc = 'Unshares the folder; members lose access to the content.' }
        @{ Code = 'Remove-DropboxSharedFolder -SharedFolderId "1234567890" -LeaveACopy'
           Desc = 'Unshares the folder while leaving each member their own copy.' }
    )
    Parameters = @{
        SharedFolderId = 'Shared-folder ID returned by ``Add-DropboxSharedFolder`` or ``Get-DropboxSharedFolder``.'
        LeaveACopy     = 'Leave the content in each member''s account as an unshared copy.'
    }
}

'Get-DropboxSharedFolder' = @{
    Synopsis = 'Lists shared folders, or returns metadata for one.'
    Description = @"
Without arguments, lists all shared folders the account is a member
of. With ``-SharedFolderId``, returns metadata for that single folder.
"@
    Examples = @(
        @{ Code = 'Get-DropboxSharedFolder'
           Desc = 'Lists every shared folder visible to the account.' }
        @{ Code = 'Get-DropboxSharedFolder -SharedFolderId "1234567890"'
           Desc = 'Returns metadata for one shared folder.' }
    )
    Parameters = @{
        SharedFolderId = 'Optional shared-folder ID. When omitted all shared folders are listed.'
    }
}

'Add-DropboxMember' = @{
    Synopsis = 'Adds a member (by email) to a shared folder or shared file.'
    Description = @"
Invites the user identified by ``-Email`` to a shared folder
(``-SharedFolderId``) or a shared file (``-FilePath``) at the
specified ``-AccessLevel``.
"@
    Examples = @(
        @{ Code = 'Add-DropboxMember -SharedFolderId "1234567890" -Email alice@contoso.com -AccessLevel editor'
           Desc = 'Invites Alice as an editor on a shared folder.' }
        @{ Code = 'Add-DropboxMember -FilePath /Specs/api.md -Email bob@contoso.com -AccessLevel viewer'
           Desc = 'Invites Bob to view a specific file.' }
    )
    Parameters = @{
        SharedFolderId = 'Shared-folder ID. Selects the **Folder** parameter set.'
        FilePath       = 'Dropbox path of a file to share. Selects the **File** parameter set.'
        Email          = 'Email address of the user to invite.'
        AccessLevel    = 'Permission to grant: ``editor``, ``viewer``, or ``viewer_no_comment``. Defaults to ``viewer``.'
    }
}

'Remove-DropboxMember' = @{
    Synopsis = 'Removes a member from a shared folder or shared file.'
    Description = @"
Revokes the membership of ``-Email`` on the shared folder
(``-SharedFolderId``) or shared file (``-FilePath``). Supports
``-WhatIf`` and ``-Confirm``.
"@
    Examples = @(
        @{ Code = 'Remove-DropboxMember -SharedFolderId "1234567890" -Email alice@contoso.com'
           Desc = 'Removes Alice from the shared folder.' }
        @{ Code = 'Remove-DropboxMember -FilePath /Specs/api.md -Email bob@contoso.com -Confirm:$false'
           Desc = 'Removes Bob without confirmation prompt.' }
    )
    Parameters = @{
        SharedFolderId = 'Shared-folder ID. Selects the **Folder** parameter set.'
        FilePath       = 'Dropbox path of a shared file. Selects the **File** parameter set.'
        Email          = 'Email address of the user to remove.'
    }
}

'Get-DropboxMember' = @{
    Synopsis = 'Lists members of a shared folder or shared file.'
    Description = @"
Returns ``DropboxMember`` objects for everyone with access to the
shared folder (``-SharedFolderId``) or shared file (``-FilePath``).
"@
    Examples = @(
        @{ Code = 'Get-DropboxMember -SharedFolderId "1234567890"'
           Desc = 'Lists members of a shared folder.' }
        @{ Code = 'Get-DropboxMember -FilePath /Specs/api.md | Where-Object AccessLevel -eq editor'
           Desc = 'Filters to editors of a shared file.' }
    )
    Parameters = @{
        SharedFolderId = 'Shared-folder ID. Selects the **Folder** parameter set.'
        FilePath       = 'Dropbox path of a shared file. Selects the **File** parameter set.'
    }
}

'Add-DropboxTag' = @{
    Synopsis = 'Adds a user-defined tag to a Dropbox file or folder.'
    Description = @"
Attaches a Dropbox **tag** (a string label) to the item at ``-Path``.
Tags are visible in the Dropbox web UI and queryable via the API. Tag
names follow Dropbox's rules (lower-case letters, digits, and
underscores; max 32 characters).
"@
    Examples = @(
        @{ Code = 'Add-DropboxTag -Path /report.pdf -Tag final'
           Desc = 'Tags ``/report.pdf`` with ``final``.' }
        @{ Code = 'Get-ChildItem Dbx:\Reports\*.pdf | Add-DropboxTag -Tag archived'
           Desc = 'Tags every PDF in ``/Reports`` as ``archived``.' }
    )
    Parameters = @{
        Path = 'Dropbox path to tag. Accepts pipeline input.'
        Tag  = 'Tag name to add. Lower-case letters, digits, underscores; max 32 characters.'
    }
}

'Remove-DropboxTag' = @{
    Synopsis = 'Removes a tag from a Dropbox file or folder.'
    Description = @"
Detaches the named tag from the item at ``-Path``. Other tags on the
item are unaffected. Supports ``-WhatIf`` and ``-Confirm``.
"@
    Examples = @(
        @{ Code = 'Remove-DropboxTag -Path /report.pdf -Tag draft'
           Desc = 'Removes the ``draft`` tag from ``/report.pdf``.' }
    )
    Parameters = @{
        Path = 'Dropbox path of the tagged item.'
        Tag  = 'Name of the tag to remove.'
    }
}

'Get-DropboxTag' = @{
    Synopsis = 'Gets the tags attached to one or more Dropbox items.'
    Description = @"
Returns ``DropboxTag`` objects for the supplied paths. Accepts arrays
or pipeline input so you can query many items in a single API call.
"@
    Examples = @(
        @{ Code = 'Get-DropboxTag -Path /report.pdf'
           Desc = 'Lists tags on a single file.' }
        @{ Code = 'Get-DropboxTag -Path /report.pdf, /summary.docx'
           Desc = 'Batches the lookup for multiple paths in one request.' }
    )
    Parameters = @{
        Path = 'One or more Dropbox paths to query. Accepts arrays and pipeline input.'
    }
}

'Lock-DropboxFile' = @{
    Synopsis = 'Locks one or more Dropbox files to prevent concurrent edits.'
    Description = @"
Acquires a Dropbox file lock on each path. While locked, other users
cannot modify the file via the API or web UI. Returns the updated
``DropboxItem`` for each path.
"@
    Examples = @(
        @{ Code = 'Lock-DropboxFile -Path /report.docx'
           Desc = 'Locks a single file.' }
        @{ Code = '"/a.txt","/b.txt" | Lock-DropboxFile'
           Desc = 'Locks multiple files via pipeline.' }
    )
    Parameters = @{
        Path = 'One or more Dropbox file paths to lock.'
    }
}

'Unlock-DropboxFile' = @{
    Synopsis = 'Releases locks on one or more Dropbox files.'
    Description = @"
Releases a previously-acquired file lock on each path. Returns the
updated ``DropboxItem`` for each.
"@
    Examples = @(
        @{ Code = 'Unlock-DropboxFile -Path /report.docx'
           Desc = 'Unlocks a single file.' }
        @{ Code = 'Get-DropboxFileLock -Path /a.txt,/b.txt | Unlock-DropboxFile -Path { $_.Path }'
           Desc = 'Unlocks every file currently shown as locked.' }
    )
    Parameters = @{
        Path = 'One or more Dropbox file paths to unlock.'
    }
}

'Get-DropboxFileLock' = @{
    Synopsis = 'Returns lock status for one or more Dropbox files.'
    Description = @"
Reports whether each file is currently locked, by whom, and when.
"@
    Examples = @(
        @{ Code = 'Get-DropboxFileLock -Path /report.docx'
           Desc = 'Shows lock state for one file.' }
        @{ Code = 'Get-DropboxFileLock -Path /a.txt,/b.txt | Format-Table Path, IsLocked, LockHolder'
           Desc = 'Tabular view across multiple files.' }
    )
    Parameters = @{
        Path = 'One or more Dropbox file paths to query.'
    }
}

'Get-DropboxAccount' = @{
    Synopsis = 'Returns the current Dropbox account, or another account by ID.'
    Description = @"
With no parameters, returns the account associated with the current
PSDrive. Pass ``-AccountId`` to look up a specific account by ID
(useful when inspecting members of shared resources).
"@
    Examples = @(
        @{ Code = 'Get-DropboxAccount'
           Desc = 'Returns the current account info (display name, email, account type, etc.).' }
        @{ Code = 'Get-DropboxAccount -AccountId "dbid:AAH4f99T0taONIb..."'
           Desc = 'Looks up another account by its Dropbox ID.' }
    )
    Parameters = @{
        AccountId = 'Optional Dropbox account ID. When omitted, returns the connected account.'
    }
}

'Get-DropboxSpaceUsage' = @{
    Synopsis = 'Reports storage quota and usage for the connected Dropbox account.'
    Description = @"
Returns a ``DropboxSpaceUsage`` object with ``UsedBytes``,
``AllocatedBytes``, and the allocation type (individual vs. team).
"@
    Examples = @(
        @{ Code = 'Get-DropboxSpaceUsage'
           Desc = 'Shows current storage usage.' }
        @{ Code = '$u = Get-DropboxSpaceUsage; "{0:N1} GB / {1:N1} GB" -f ($u.UsedBytes/1GB), ($u.AllocatedBytes/1GB)'
           Desc = 'Formats usage as gigabytes.' }
    )
    Parameters = @{}
}

'Get-DropboxTemporaryLink' = @{
    Synopsis = 'Returns a short-lived direct download URL for a Dropbox file.'
    Description = @"
Generates a temporary (typically 4-hour) direct-download URL for the
given Dropbox file. Unlike a shared link, this URL streams the raw
file contents and is intended for programmatic download by systems
that cannot authenticate against the Dropbox API directly.
"@
    Examples = @(
        @{ Code = 'Get-DropboxTemporaryLink -Path /report.pdf'
           Desc = 'Returns a direct-download URL good for ~4 hours.' }
        @{ Code = 'Invoke-WebRequest (Get-DropboxTemporaryLink /report.pdf) -OutFile .\report.pdf'
           Desc = 'Downloads via the temporary link using a generic HTTP client.' }
    )
    Parameters = @{
        Path = 'Dropbox path of the file. Accepts pipeline input by value or by ``FullName`` property.'
    }
}

'Save-DropboxUrl' = @{
    Synopsis = 'Asynchronously saves an external URL into Dropbox.'
    Description = @"
Tells Dropbox to fetch the given ``-Url`` server-side and store it at
``-DropboxPath``. Returns the async job identifier; the actual save
happens on Dropbox's servers and may take time for large URLs.
"@
    Examples = @(
        @{ Code = 'Save-DropboxUrl -DropboxPath /Downloads/spec.pdf -Url "https://example.com/spec.pdf"'
           Desc = 'Queues a server-side fetch of an external URL into Dropbox.' }
    )
    Parameters = @{
        DropboxPath = 'Destination path inside Dropbox.'
        Url         = 'Public HTTPS URL to fetch.'
    }
}

'Get-DropboxPreview' = @{
    Synopsis = 'Returns a PDF preview of a Dropbox file.'
    Description = @"
Downloads a PDF preview rendering for documents Dropbox knows how to
preview (Office files, RTF, plain text, etc.). Without ``-OutFile``
the preview bytes are emitted to the pipeline; with ``-OutFile`` the
bytes are written to disk and a ``FileInfo`` is returned.
"@
    Examples = @(
        @{ Code = 'Get-DropboxPreview -Path /report.docx -OutFile .\preview.pdf'
           Desc = 'Saves a PDF preview of a Word document to disk.' }
        @{ Code = '$bytes = Get-DropboxPreview /report.docx; $bytes.Length'
           Desc = 'Returns preview bytes directly.' }
    )
    Parameters = @{
        Path    = 'Dropbox path of the file to preview.'
        OutFile = 'Optional local path to write the preview to. Without this parameter the bytes are emitted to the pipeline.'
    }
}

'Get-DropboxThumbnail' = @{
    Synopsis = 'Returns an image thumbnail for a Dropbox file.'
    Description = @"
Generates a thumbnail (JPEG or PNG) at one of Dropbox's supported
sizes. Without ``-OutFile`` the thumbnail bytes are emitted; with
``-OutFile`` the bytes are written to disk.
"@
    Examples = @(
        @{ Code = 'Get-DropboxThumbnail -Path /image.jpg -Size w256h256 -OutFile .\thumb.jpg'
           Desc = 'Saves a 256x256 JPEG thumbnail.' }
        @{ Code = 'Get-DropboxThumbnail /image.jpg -Format png -Size w64h64'
           Desc = 'Emits the raw 64x64 PNG bytes to the pipeline.' }
    )
    Parameters = @{
        Path    = 'Dropbox path of the file.'
        Size    = 'Thumbnail size, one of ``w32h32``, ``w64h64``, ``w128h128``, ``w256h256``, ``w480h320``, ``w640h480``, ``w960h640``, ``w1024h768``, ``w2048h1536``. Defaults to ``w64h64``.'
        Format  = 'Image format: ``jpeg`` (default) or ``png``.'
        OutFile = 'Optional local file to write to. Without it the bytes are emitted to the pipeline.'
    }
}

'New-DropboxPaper' = @{
    Synopsis = 'Creates a new Dropbox Paper document.'
    Description = @"
Creates a Paper doc at ``-Path`` populated from ``-Content`` (HTML,
Markdown, or plain text). Returns the URL of the new Paper doc.

> **Note**: Dropbox is winding down Paper for new content; this cmdlet
> exposes the existing Paper API and may stop functioning when Dropbox
> retires the endpoints.
"@
    Examples = @(
        @{ Code = 'New-DropboxPaper -Path /Papers/Notes.paper -Content "# Hello`n`nWorld" -ImportFormat markdown'
           Desc = 'Creates a Paper doc from a Markdown string.' }
        @{ Code = 'Get-Content notes.md -Raw | New-DropboxPaper -Path /Papers/Notes.paper'
           Desc = 'Pipes file contents into a new Paper doc using the default Markdown import format.' }
    )
    Parameters = @{
        Path         = 'Dropbox path for the new Paper doc (typically ending in ``.paper``).'
        Content      = 'Document body. Accepts pipeline input.'
        ImportFormat = 'Source format of ``-Content``: ``html``, ``markdown`` (default), or ``plain_text``.'
    }
}

'Set-DropboxPaper' = @{
    Synopsis = 'Updates an existing Dropbox Paper document.'
    Description = @"
Modifies the Paper doc at ``-Path`` using the supplied ``-Content``
and ``-UpdatePolicy`` (``overwrite``, ``prepend``, or ``append``).
"@
    Examples = @(
        @{ Code = 'Set-DropboxPaper -Path /Papers/Notes.paper -Content "## Update" -UpdatePolicy append'
           Desc = 'Appends a new section to an existing Paper doc.' }
        @{ Code = 'Get-Content fresh.md -Raw | Set-DropboxPaper -Path /Papers/Notes.paper -UpdatePolicy overwrite'
           Desc = 'Overwrites the doc with new Markdown contents.' }
    )
    Parameters = @{
        Path         = 'Dropbox path of the existing Paper doc.'
        Content      = 'New document body. Accepts pipeline input.'
        ImportFormat = 'Source format of ``-Content``: ``html``, ``markdown`` (default), or ``plain_text``.'
        UpdatePolicy = 'How to apply the new content: ``overwrite`` (default), ``prepend``, or ``append``.'
    }
}

'Export-DropboxFile' = @{
    Synopsis = 'Exports a Dropbox file (e.g. Google Docs, Sheets) to a downloadable format.'
    Description = @"
Some Dropbox-stored files (Google Docs, Sheets, Slides, Paper) cannot
be downloaded as-is and must be **exported** to a portable format
(PDF, DOCX, XLSX, etc.). This cmdlet performs that export. Without
``-OutFile`` the bytes are emitted to the pipeline; with ``-OutFile``
the bytes are written and a ``FileInfo`` is returned.
"@
    Examples = @(
        @{ Code = 'Export-DropboxFile -Path /Drafts/Plan.gdoc -OutFile .\plan.docx'
           Desc = 'Exports a Google Doc as a Word document.' }
        @{ Code = '$bytes = Export-DropboxFile /Sheets/Budget.gsheet'
           Desc = 'Captures the exported bytes (XLSX) without writing to disk.' }
    )
    Parameters = @{
        Path    = 'Dropbox path of the file to export.'
        OutFile = 'Optional local file to write to. Without it, raw bytes are emitted.'
    }
}

'Copy-DropboxItemBatch' = @{
    Synopsis = 'Copies many Dropbox items in a single batched API call.'
    Description = @"
Runs Dropbox's batch-copy API. Pass parallel arrays in ``-FromPath``
and ``-ToPath``; each ``FromPath[i]`` is copied to ``ToPath[i]``. The
two arrays must be the same length.

Batching is dramatically faster than calling ``Copy-Item`` per file
when moving many items.
"@
    Examples = @(
        @{ Code = 'Copy-DropboxItemBatch -FromPath /a.txt,/b.txt -ToPath /backup/a.txt,/backup/b.txt'
           Desc = 'Copies two files in one request.' }
        @{ Code = '$src = Get-ChildItem Dbx:\Reports -File | ForEach-Object Path; $dst = $src | ForEach-Object { $_ -replace "/Reports/","/Archive/" }; Copy-DropboxItemBatch -FromPath $src -ToPath $dst'
           Desc = 'Copies an entire folder''s files into ``/Archive`` in one batched call.' }
    )
    Parameters = @{
        FromPath = 'Source Dropbox paths.'
        ToPath   = 'Destination Dropbox paths. Must have the same length as ``-FromPath``.'
    }
}

'Move-DropboxItemBatch' = @{
    Synopsis = 'Moves many Dropbox items in a single batched API call.'
    Description = @"
Runs Dropbox's batch-move API. Pass parallel arrays in ``-FromPath``
and ``-ToPath``; each ``FromPath[i]`` is moved to ``ToPath[i]``. The
two arrays must be the same length.
"@
    Examples = @(
        @{ Code = 'Move-DropboxItemBatch -FromPath /old/a.txt,/old/b.txt -ToPath /new/a.txt,/new/b.txt'
           Desc = 'Moves two files in one batched call.' }
    )
    Parameters = @{
        FromPath = 'Source Dropbox paths.'
        ToPath   = 'Destination Dropbox paths. Must have the same length as ``-FromPath``.'
    }
}

'Remove-DropboxItemBatch' = @{
    Synopsis = 'Deletes many Dropbox items in a single batched API call.'
    Description = @"
Runs Dropbox's batch-delete API on every path in ``-Path``. Supports
``-WhatIf`` and ``-Confirm``. Items go to the Dropbox trash and can be
restored from the web UI within the account's retention window.
"@
    Examples = @(
        @{ Code = 'Remove-DropboxItemBatch -Path /tmp/a.txt,/tmp/b.txt'
           Desc = 'Deletes two files in one batched call after a confirmation prompt.' }
        @{ Code = 'Get-ChildItem Dbx:\Trash -File | Select -Expand Path | Remove-DropboxItemBatch -Confirm:$false'
           Desc = 'Bulk-deletes every file in a folder via pipeline, with no prompt.' }
    )
    Parameters = @{
        Path = 'One or more Dropbox paths to delete. Accepts pipeline input.'
    }
}

}  # end $Content

# ---- Helpers ---------------------------------------------------------------

function Format-ExamplesBlock {
    param([Parameter(Mandatory)] [object[]]$Examples)
    $sb = [System.Text.StringBuilder]::new()
    $i = 1
    foreach ($ex in $Examples) {
        [void]$sb.AppendLine("### Example $i")
        [void]$sb.AppendLine('```powershell')
        [void]$sb.AppendLine("PS> $($ex.Code)")
        [void]$sb.AppendLine('```')
        [void]$sb.AppendLine()
        [void]$sb.AppendLine($ex.Desc)
        [void]$sb.AppendLine()
        $i++
    }
    return $sb.ToString().TrimEnd()
}

function Set-Section {
    param(
        [Parameter(Mandatory)] [string]$Markdown,
        [Parameter(Mandatory)] [string]$HeadingPattern, # e.g. '## SYNOPSIS'
        [Parameter(Mandatory)] [string]$NewBody
    )
    $escaped = [regex]::Escape($HeadingPattern)
    # Match heading line, then everything up to (but not including) the next ## heading.
    $re = [regex]::new("(?ms)(^$escaped\s*\r?\n)(.*?)(?=^##\s|\z)")
    $newBodyCopy = $NewBody
    $evaluator = [System.Text.RegularExpressions.MatchEvaluator]{
        param($m)
        return $m.Groups[1].Value + "`n" + $newBodyCopy + "`n`n"
    }.GetNewClosure()
    return $re.Replace($Markdown, $evaluator, 1)
}

function Set-ParameterDescription {
    param(
        [Parameter(Mandatory)] [string]$Markdown,
        [Parameter(Mandatory)] [string]$ParamName,
        [Parameter(Mandatory)] [string]$NewDescription
    )
    $escaped = [regex]::Escape("### -$ParamName")
    $fence   = [char]0x60 + [char]0x60 + [char]0x60  # three backticks
    $re = [regex]::new('(?ms)(^' + $escaped + '\s*\r?\n)(.*?)(?=^' + [regex]::Escape($fence) + 'yaml)')
    $descCopy = $NewDescription
    $evaluator = [System.Text.RegularExpressions.MatchEvaluator]{
        param($m)
        return $m.Groups[1].Value + "`n" + $descCopy + "`n`n"
    }.GetNewClosure()
    return $re.Replace($Markdown, $evaluator, 1)
}

# ---- Apply -----------------------------------------------------------------

$missing = @()
foreach ($cmdlet in $Content.Keys | Sort-Object) {
    $mdPath = Join-Path $helpRoot "$cmdlet.md"
    if (-not (Test-Path -LiteralPath $mdPath)) {
        $missing += $cmdlet
        continue
    }

    $md = Get-Content -LiteralPath $mdPath -Raw

    $entry = $Content[$cmdlet]

    $md = Set-Section -Markdown $md -HeadingPattern '## SYNOPSIS'    -NewBody $entry.Synopsis
    $md = Set-Section -Markdown $md -HeadingPattern '## DESCRIPTION' -NewBody $entry.Description

    $examplesBody = Format-ExamplesBlock -Examples $entry.Examples
    $md = Set-Section -Markdown $md -HeadingPattern '## EXAMPLES'    -NewBody $examplesBody

    # Per-cmdlet parameters.
    foreach ($p in $entry.Parameters.Keys) {
        $md = Set-ParameterDescription -Markdown $md -ParamName $p -NewDescription $entry.Parameters[$p]
    }

    # Common parameters present on every (or nearly every) cmdlet.
    if ($md -match '(?ms)^### -DriveName\s*\r?\n') {
        $md = Set-ParameterDescription -Markdown $md -ParamName 'DriveName' -NewDescription $DriveNameDesc
    }
    if ($md -match '(?ms)^### -ProgressAction\s*\r?\n') {
        $md = Set-ParameterDescription -Markdown $md -ParamName 'ProgressAction' -NewDescription $ProgressActionDesc
    }

    # Set-Content -Encoding utf8NoBOM (PlatyPS default).
    Set-Content -LiteralPath $mdPath -Value $md -Encoding utf8NoBOM -NoNewline
    Write-Host "  authored: $cmdlet" -ForegroundColor DarkGray
}

if ($missing) {
    Write-Warning "Markdown not found for: $($missing -join ', ')"
}

# ---- Authoring sweep for the module landing page (DbxProvider.md) ----------
$modPagePath = Join-Path $helpRoot 'DbxProvider.md'
if (Test-Path -LiteralPath $modPagePath) {
    $mod = Get-Content -LiteralPath $modPagePath -Raw
    $mod = Set-Section -Markdown $mod -HeadingPattern '## Description' -NewBody @"
DbxProvider exposes the full Dropbox API as a PowerShell **provider**
(so you can ``cd Dbx:\`` and use ``Get-ChildItem``, ``Copy-Item``,
``Set-Content``, etc. against your Dropbox) plus a set of cmdlets for
operations that don't fit a file-system metaphor (sharing, tags,
locks, revisions, batched copy/move/delete, Paper, previews, and
account info).

Start with ``Connect-Dropbox`` to authenticate; thereafter the rest of
the cmdlets operate against the resulting ``Dbx:`` drive.
"@
    Set-Content -LiteralPath $modPagePath -Value $mod -Encoding utf8NoBOM -NoNewline
}

Write-Host "Authoring complete." -ForegroundColor Green
