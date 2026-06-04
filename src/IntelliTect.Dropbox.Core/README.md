# IntelliTect.Dropbox.Core

Standalone, PowerShell-free core for working with the Dropbox API v2.

- Comprehensive `DropboxServiceClient` wrapper over `Dropbox.Api`.
- Builds a `DropboxClient` directly from an app key/secret/refresh token (or access token).
- Metadata cache, rate-limit retry, credential persistence, and a framework-neutral
  wildcard matcher.

Multi-targets `netstandard2.0` and `net8.0`. Independent of `IntelliTect.Dropbox.Auth`.