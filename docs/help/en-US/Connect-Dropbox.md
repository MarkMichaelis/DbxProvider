---
external help file: DbxProvider.dll-Help.xml
Module Name: DbxProvider
online version:
schema: 2.0.0
---

# Connect-Dropbox

## SYNOPSIS

Authenticates to Dropbox and creates a PSDrive for the account.

## SYNTAX

### OAuth (Default)
```
Connect-Dropbox [-AppKey <String>] [-AppSecret <String>] [-RefreshToken <String>] [-Account <String>]
 [-RedirectPort <Int32>] [-NoSave] [-DriveName <String>] [-ProgressAction <ActionPreference>]
 [<CommonParameters>]
```

### Token
```
Connect-Dropbox [-AccessToken] <String> [-DriveName <String>] [-ProgressAction <ActionPreference>]
 [<CommonParameters>]
```

## DESCRIPTION

Authenticates against the Dropbox API and registers a PowerShell drive
that exposes the account's file tree as a navigable file system.

Three usage modes are supported:

- **Token mode**: `Connect-Dropbox -AccessToken <token>` uses a
  short-lived access token directly. No credentials are persisted.
- **OAuth mode** (default): `Connect-Dropbox -AppKey <key> [-AppSecret <secret>]`
  runs an OAuth 2.0 + PKCE authorization-code flow, opens the browser,
  listens on a local redirect URI, and obtains an offline refresh token.
- **Reuse mode**: `Connect-Dropbox` with no parameters reuses
  credentials previously saved by `Set-DropboxCredential` or by an
  earlier OAuth connect.

Unless `-NoSave` is specified, the AppKey / AppSecret / RefreshToken
are persisted via the platform's credential store
(DPAPI-encrypted JSON on Windows) so subsequent sessions can reconnect
without a browser round-trip.

A global function named `<DriveName>:` is also registered so you can
switch to the drive by typing `Dbx:` (mirroring `C:` for the
filesystem provider).

## EXAMPLES

### Example 1
```powershell
PS> Connect-Dropbox -AccessToken "sl.B-abc123..."
```

Connects using an existing short-lived access token. Useful for quick experiments or automation that supplies the token from a vault.

### Example 2
```powershell
PS> Connect-Dropbox -AppKey "abc123" -AppSecret "xyz789"
```

Runs the full OAuth + PKCE browser flow, requests an offline refresh token, and saves credentials so future sessions reconnect silently.

### Example 3
```powershell
PS> Connect-Dropbox
```

Reuses previously-saved credentials. This is the typical command at the start of a script after the first interactive setup.

### Example 4
```powershell
PS> Connect-Dropbox -AppKey $key -DriveName Work -RedirectPort 53000
```

Connects a second account under a separate drive name and uses a non-default redirect port (which must be registered in the Dropbox App Console).

## PARAMETERS

### -AccessToken

Short-lived Dropbox API access token. Selects the **Token** parameter set; no OAuth flow is run and no credentials are saved.

```yaml
Type: String
Parameter Sets: Token
Aliases:

Required: True
Position: 0
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -Account
Selects which saved account's credentials to load (Dropbox `accountId`, full email, or unambiguous email local-part). When omitted, the default account is used. When the selector matches no saved account and `-AppKey` is supplied, a fresh OAuth flow runs and the resulting credentials are persisted under the newly-discovered `accountId`. When the selector matches no saved account and `-AppKey` is **not** supplied, the cmdlet automatically reuses an `AppKey` from another saved account (preferring the default account's) so adding a new user only requires `Connect-Dropbox -Account <selector>`. Refresh tokens are never shared across accounts.

```yaml
Type: String
Parameter Sets: OAuth
Aliases:

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -AppKey

Dropbox app key (client ID) issued by the Dropbox App Console. Required for the OAuth flow unless one is already saved in the credential store.

```yaml
Type: String
Parameter Sets: OAuth
Aliases:

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -AppSecret

Dropbox app secret. Required for "Full Dropbox" / confidential apps; omit for PKCE-only public apps.

```yaml
Type: String
Parameter Sets: OAuth
Aliases:

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

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

### -NoSave

Do not persist credentials after a successful connect. The refresh token (if any) is printed to the host so you can save it yourself.

```yaml
Type: SwitchParameter
Parameter Sets: OAuth
Aliases:

Required: False
Position: Named
Default value: None
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

### -RedirectPort

Local TCP port the OAuth callback listener binds to. Must match a redirect URI registered in the Dropbox App Console (e.g. ``http://localhost:52475/``). Defaults to 52475.

```yaml
Type: Int32
Parameter Sets: OAuth
Aliases:

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -RefreshToken

Long-lived OAuth refresh token. Provide this to skip the browser flow when you have obtained the token out-of-band.

```yaml
Type: String
Parameter Sets: OAuth
Aliases:

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

### System.Management.Automation.PSDriveInfo

## NOTES

## RELATED LINKS
