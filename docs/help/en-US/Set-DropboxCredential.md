---
external help file: DbxProvider.dll-Help.xml
Module Name: DbxProvider
online version:
schema: 2.0.0
---

# Set-DropboxCredential

## SYNOPSIS

Saves Dropbox credentials to the per-user credential store.

## SYNTAX

```
Set-DropboxCredential [-AppKey <String>] [-AppSecret <String>] [-RefreshToken <String>] [-Account <String>]
 [-SetDefault] [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

## DESCRIPTION

Persists any combination of AppKey, AppSecret, and RefreshToken to the
DPAPI-encrypted credential file used by `Connect-Dropbox` for silent
re-auth. Existing values are preserved when the corresponding parameter
is omitted, allowing you to update one field at a time.

## EXAMPLES

### Example 1
```powershell
PS> Set-DropboxCredential -AppKey "abc123" -AppSecret "xyz789"
```

Stores the app key and secret, leaving any existing refresh token in place.

### Example 2
```powershell
PS> Set-DropboxCredential -RefreshToken $token
```

Updates only the refresh token (e.g. after rotation).

## PARAMETERS

### -Account
Account selector - Dropbox `accountId`, full email, or unambiguous email local-part. When omitted (and `-All` is not specified), the default account is updated. When the selector matches no saved account, an `accountId` (`dbid:...`) or full email creates a new entry.

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

### -AppKey

Dropbox app key (client ID). Pass ``$null`` or omit to leave any existing value untouched.

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

### -AppSecret

Dropbox app secret. Omit to leave the existing value untouched.

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

### -RefreshToken

Long-lived OAuth refresh token. Omit to leave the existing value untouched.

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

### -SetDefault
After saving, mark this account as the default - used by `Connect-Dropbox` and the credential cmdlets when no `-Account` selector is given.

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

### CommonParameters
This cmdlet supports the common parameters: -Debug, -ErrorAction, -ErrorVariable, -InformationAction, -InformationVariable, -OutVariable, -OutBuffer, -PipelineVariable, -Verbose, -WarningAction, and -WarningVariable. For more information, see [about_CommonParameters](http://go.microsoft.com/fwlink/?LinkID=113216).

## INPUTS

### None

## OUTPUTS

### System.Object
## NOTES

## RELATED LINKS
