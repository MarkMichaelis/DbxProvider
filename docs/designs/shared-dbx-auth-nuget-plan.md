# Shared Dropbox-Auth NuGet Package

- Issue: (pending - create before Phase 0/worktree)
- PR:    (pending)
- Slug:  shared-dbx-auth-nuget

# Plan: Extract Dropbox app-registration + OAuth into a reusable NuGet package

## Overview

Extract DbxProvider's Dropbox onboarding logic - default-browser detection, the
Playwright-driven Dropbox App Console registrar, and the loopback OAuth 2 + PKCE token flow -
into a framework-neutral class library in **this** repo (`src/Dbx.Auth`), and publish it to
GitHub Packages as `MarkMichaelis.Dropbox.Auth`. DbxProvider keeps its cmdlets and references the
library via `ProjectReference`; CopyToGooglePhotos consumes the published package instead of
hand-rolling (and deleting) its own inferior copy. The shared code **stays in the DbxProvider
repo** - no new repo.

This plan supersedes the external handoff doc
(`dbxprovider-shared-auth-nuget-plan.md`) and folds in the review corrections from that doc's
assessment.

## Precondition (hard gate)

**Do not start until PR [#5](https://github.com/MarkMichaelis/DbxProvider/pull/5)
(`feat/4-playwright-dbx-registrar`) is merged to `main` AND has had one live smoke test against the
real Dropbox console.** The files to extract (`Services/DefaultBrowser.cs`,
`Services/DropboxAppRegistrar.cs`) and the `RunOAuthFlow` body currently exist **only** on that
branch. Packaging unmerged, end-to-end-unproven code is explicitly out of bounds.

## Key decisions (resolved)

- **D1 - Playwright version:** bump the whole solution to **`Microsoft.Playwright 1.59.0`** to match
  CopyToGooglePhotos and avoid diamond-version conflicts. This is a real upgrade from the current
  `1.49.*` on the PR #5 branch and must be re-smoke-tested after bumping. (If the maintainer prefers
  to hold at `1.49.*` and make CopyToGooglePhotos match instead, swap the pin everywhere in this
  plan.)
- **D2 - Lowest TFM:** library multi-targets `netstandard2.0;net8.0`. `netstandard2.0` is required so
  CopyToGooglePhotos (net10) and DbxProvider (net8) can both consume it. Verify the
  `netstandard2.0` surface in Phase 1 before committing (Dropbox.Api 7 and Playwright 1.59 both ship
  ns2.0 assemblies; the OAuth/PKCE path uses only BCL + `DropboxOAuth2Helper`).
- **D3 - Public surface:** all extracted, consumer-facing types are **`public`**
  (`DropboxAppRegistrar` is currently `internal sealed` - it must be promoted). `InternalsVisibleTo`
  is for same-solution test projects only and does NOT make types usable by CopyToGooglePhotos.

## Deliverable A - new class library `src/Dbx.Auth`

`src/Dbx.Auth/MarkMichaelis.Dropbox.Auth.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>netstandard2.0;net8.0</TargetFrameworks>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>

    <!-- Packaging -->
    <PackageId>MarkMichaelis.Dropbox.Auth</PackageId>
    <Version>0.1.0</Version>
    <Authors>MarkMichaelis</Authors>
    <Description>Reusable Dropbox app registration (Playwright) + loopback OAuth/PKCE token flow.</Description>
    <RepositoryUrl>https://github.com/MarkMichaelis/DbxProvider</RepositoryUrl>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Dropbox.Api" Version="7.0.0" />
    <PackageReference Include="Microsoft.Playwright" Version="1.59.0" />
  </ItemGroup>
</Project>
```

Add the project to `DbxProvider.sln`.

### Code to move (NO `System.Management.Automation`, NO Exe types)

| From (DbxProvider, on/after PR #5) | To (`Dbx.Auth`) | Change required |
|---|---|---|
| `Services/DefaultBrowser.cs` | `DefaultBrowser` (public) | Move as-is; Windows-registry default-browser detection. Promote to `public`. |
| `Services/DropboxAppRegistrar.cs` (241 lines, `internal sealed`) | `DropboxAppRegistrar` (public) | **Refactor, not a move:** promote to `public`; replace `Action<string> _log` with `IConsole`; replace the `string executablePath` ctor with an injected `IBrowserLauncher` so CopyToGooglePhotos can CDP-attach instead of launching from an exe path. |
| `AuthCommands.RunOAuthFlow` (HttpListener loopback, S256 PKCE, `state`, `token_access_type=offline`, `DropboxOAuth2Helper.ProcessCodeFlowAsync`) | `LoopbackOAuthFlow` (public) | Lift the flow out of the cmdlet into a plain class taking `IConsole`; return a `DropboxCredential`. |
| credential DTO(s) | `DropboxCredential` (public record) | New record; document mapping to/from the existing `StoredCredentials`/`StoredAccount` JSON shapes so the on-disk format does NOT break. |

### Abstractions (the injection seam both apps implement)

```csharp
public interface IBrowserLauncher
{
    // Returns a ready-to-drive context. The caller (registrar) MUST NOT dispose it;
    // ownership/lifetime stays with the launcher implementation.
    //  - DbxProvider:        LaunchPersistentContextAsync(userDataDir, ExecutablePath=<detected exe>, Headless=false)
    //  - CopyToGooglePhotos: ConnectOverCDPAsync(<running Chrome>) and reuse an existing context
    Task<IBrowserContext> LaunchAsync(CancellationToken ct);
}

public interface IConsole
{
    void Info(string message);   // DbxProvider: Host.UI adapter;  CopyToGooglePhotos: IConsoleIO adapter
    string Prompt(string message);
}

public interface ICredentialStore
{
    void Save(DropboxCredential cred);          // DbxProvider: CredentialStore; CopyToGooglePhotos: FileAuthStore
    DropboxCredential? Load(string key);
}

public sealed record DropboxCredential(string AppKey, string? AppSecret, string? RefreshToken, string? AccessToken);
```

`DropboxAppRegistrar` and `LoopbackOAuthFlow` take these via constructor injection and contain
**no** PowerShell/Exe types, so the assembly loads cleanly on net8 and net10.

### Keep DbxProvider working

- `src/DbxProvider/DbxProvider.csproj`: add `ProjectReference` to `Dbx.Auth`; drop the moved
  `Services/DefaultBrowser.cs` + `Services/DropboxAppRegistrar.cs`; bump
  `Microsoft.Playwright` to `1.59.0` (D1). `CredentialStore.cs` stays in DbxProvider.
- `AuthCommands.cs`: keep the cmdlet shell; delegate to `LoopbackOAuthFlow`/`DropboxAppRegistrar`,
  passing thin adapters:
  - `IBrowserLauncher` -> launches persistent context from the detected `ExecutablePath` (today's
    behavior).
  - `IConsole` -> `PSCmdlet` `Host.UI` / `WriteVerbose` adapter (preserves current messages).
  - `ICredentialStore` -> wraps existing `CredentialStore`, mapping `DropboxCredential` <->
    `StoredCredentials`/`StoredAccount` (D-G3). No change to the persisted JSON.
- `Connect-Dropbox` behavior is unchanged for DbxProvider users.
- Tests: move `DefaultBrowserTests.cs` and `DropboxAppRegistrarNamingTests.cs` to a library test
  project (or retarget them at the library). Add `InternalsVisibleTo` only if any moved member stays
  `internal`. Decide whether functional `Services/AuthTests.cs` stays (testing the cmdlet adapter) or
  is partly re-expressed against `LoopbackOAuthFlow`.

## Deliverable B - publish to GitHub Packages

`.github/workflows/publish-dbx-auth.yml` (note: adding a workflow is a deliberate, reviewed
exception to the "don't touch `.github/workflows/`" rule):

```yaml
name: publish-dbx-auth
on:
  push:
    tags: [ 'dbx-auth-v*' ]      # e.g. dbx-auth-v0.1.0
permissions:
  packages: write
  contents: read
jobs:
  pack-push:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '10.0.x' }   # align with this repo's CI
      - run: dotnet pack src/Dbx.Auth/MarkMichaelis.Dropbox.Auth.csproj -c Release -o ./artifacts
      - run: >
          dotnet nuget push "./artifacts/*.nupkg"
          --source https://nuget.pkg.github.com/MarkMichaelis/index.json
          --api-key ${{ secrets.GITHUB_TOKEN }}
          --skip-duplicate
```

`GITHUB_TOKEN` can push to the `MarkMichaelis` feed because the repo owner matches the feed owner.
Keep the csproj `<Version>` in sync with the tag (`--skip-duplicate` silently no-ops on a version
collision). Package appears at `https://github.com/MarkMichaelis/DbxProvider/packages`.

## Consumer side (CopyToGooglePhotos) - reference only, out of scope here

1. `nuget.config` source `https://nuget.pkg.github.com/MarkMichaelis/index.json` with a
   `read:packages` PAT (`%GITHUB_PACKAGES_PAT%`).
2. `<PackageReference Include="MarkMichaelis.Dropbox.Auth" Version="0.1.0" />`.
3. Delete `HttpDropboxTokenExchanger`, the displayed-code scraping in `PlaywrightDropboxAuthProvider`,
   and the console scraping in `PlaywrightDropboxAppManager`; wire its CDP `SystemBrowserLauncher`,
   `IConsoleIO`, and `FileAuthStore` into the library's `IBrowserLauncher`/`IConsole`/
   `ICredentialStore`.

**Fallback if no feed:** CopyToGooglePhotos adds DbxProvider as a git submodule + `ProjectReference`
to `src/Dbx.Auth`. No PAT, but no binary versioning. Prefer the feed.

## Acceptance criteria (DbxProvider side)

- [ ] `src/Dbx.Auth/MarkMichaelis.Dropbox.Auth.csproj` exists, multi-targets `netstandard2.0;net8.0`,
      builds with **no** `System.Management.Automation`/Exe references.
- [ ] `DefaultBrowser`, `DropboxAppRegistrar`, `LoopbackOAuthFlow`, `DropboxCredential`, and the three
      interfaces are **`public`** and free of PowerShell coupling.
- [ ] `DropboxAppRegistrar` accepts `IBrowserLauncher` (no `string executablePath` ctor).
- [ ] DbxProvider references the library via `ProjectReference`; `Connect-Dropbox` behavior unchanged;
      on-disk credential JSON unchanged; all existing unit/functional tests green.
- [ ] Whole solution builds against `Microsoft.Playwright 1.59.0`; registrar re-smoke-tested.
- [ ] Moved tests pass against the library.
- [ ] `dotnet pack` produces `MarkMichaelis.Dropbox.Auth.<version>.nupkg`.
- [ ] Tagging `dbx-auth-v0.1.0` publishes to GitHub Packages successfully.
- [ ] (One-time) live smoke: `DropboxAppRegistrar.RegisterAsync` completes against the real console
      via an injected `IBrowserLauncher`.

## TDD / execution order

0. Confirm precondition met (PR #5 merged + smoke). Create GitHub issue; open worktree
   `feat/<issue#>-shared-dbx-auth-nuget`.
1. Create `src/Dbx.Auth` (empty), multi-target `netstandard2.0;net8.0`, add to solution; define the
   three interfaces + `DropboxCredential` (`public`, compile-only). **Verify the ns2.0 surface
   compiles with Dropbox.Api 7 + Playwright 1.59.**
2. Bump `Microsoft.Playwright` to `1.59.0` solution-wide; build green; re-smoke the registrar.
3. Move `DefaultBrowser` (-> `public`) + its tests -> green.
4. Refactor `DropboxAppRegistrar` -> `public`; swap `Action<string>` for `IConsole`; swap
   `executablePath` ctor for `IBrowserLauncher`; move naming tests -> green.
5. Extract `LoopbackOAuthFlow` from `AuthCommands`; unit test that an injected fake redirect/exchange
   yields a `DropboxCredential` -> green.
6. Re-wire `AuthCommands` via adapters (`IBrowserLauncher`/`IConsole`/`ICredentialStore`), including
   the `DropboxCredential` <-> `StoredCredentials` mapping; full DbxProvider suite green; verify
   on-disk JSON unchanged.
7. Add packaging props + `publish-dbx-auth.yml`; `dotnet pack` locally.
8. Live smoke once; tag `dbx-auth-v0.1.0`; publish; verify package visible.

## Risks / notes

- **Playwright bump (1.49 -> 1.59):** browser-protocol behavior can shift; re-smoke after the bump
  (step 2) before trusting the registrar.
- **Public API design:** `IBrowserLauncher` must nail down context lifetime/ownership (persistent vs
  CDP-attach; who disposes). Under-design leaks back into both consumers.
- **Credential format:** the `DropboxCredential` <-> `StoredCredentials`/`StoredAccount` mapping must
  not change the on-disk JSON, or existing DbxProvider users lose stored tokens.
- **Windows-centric registrar:** `DefaultBrowser` reads the Windows registry; the package is still
  referenceable cross-platform but app-registration auto-fill is Windows-only and falls back to the
  manual wizard elsewhere - document this.
- **Consumer PAT:** CopyToGooglePhotos needs a `read:packages` PAT for the GitHub Packages source.
