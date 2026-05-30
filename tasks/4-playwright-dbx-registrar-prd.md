# Playwright-driven Dropbox App Registration Wizard

- Issue: https://github.com/MarkMichaelis/DbxProvider/issues/4
- PR:    (pending)
- Slug:  4-playwright-dbx-registrar

# Plan: Auto-fill the Dropbox app-registration form via Playwright

## Overview
Replace today's purely-textual `Connect-Dropbox` registration wizard with one
that detects the user's default browser, drives the Dropbox App Console form
end-to-end via Playwright (pre-fills Create page, then automates redirect URI
+ scopes + App-key extraction after the user clicks Create app), and falls
back gracefully to the existing manual wizard whenever the default browser
is not Chromium-family or Playwright fails. ~10 MB NuGet, zero browser
downloads, no new cmdlet parameters.

## User Story
As a **PowerShell user installing DbxProvider for the first time on a new
machine** (or wiring up a new Dropbox account on an app still in
Development status),
I want **`Connect-Dropbox` to fill in the Dropbox App Console form for me
and pull the App key back automatically**,
so that **first-run setup collapses from "follow 6 numbered steps across two
tabs and copy 7 scope checkboxes" to "sign in once, click Create app, click
Allow"**.

## Approved Design (pending approval)

### Architecture

- **`Services\DefaultBrowser.cs`** (new) -- Windows-only first cut.
  - `static (string? ExecutablePath, string FriendlyName, bool IsChromiumFamily) Detect()`.
  - Reads `HKCU\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice` ProgId; resolves exe via `HKCR\<ProgId>\shell\open\command`.
  - Recognises `MSEdgeHTM`, `ChromeHTML`, `BraveHTML`, `VivaldiHTM`, `OperaStable`, `ArcHTM` as Chromium-family; everything else returns `IsChromiumFamily = false`.
  - Mac/Linux: returns `(null, "unknown", false)` and the cmdlet falls through to manual wizard.

- **`Services\DropboxAppRegistrar.cs`** (new) -- Playwright driver.
  - `Task<RegistrationResult?> RegisterAsync(string suggestedName, string redirectUri, IReadOnlyList<string> scopes, CancellationToken ct)`.
  - Launches via `LaunchPersistentContextAsync(userDataDir, new() { ExecutablePath = <detected exe>, Headless = false })` so we drive the user's already-installed Chromium-family browser without a Chromium download.
  - `userDataDir` = `%LOCALAPPDATA%\DbxProvider\playwright-profile` -- dedicated to this module; we never touch the user's real profile.
  - **Phase A (pre-create, no submit):** navigate to `https://www.dropbox.com/developers/apps/create`. If redirected to login, poll up to 5 min for the URL to return. Pre-fill: select "Scoped access", select "Full Dropbox", set name field to `PSDbxProvider-<8 random alphanumeric>`. Cmdlet writes "Form pre-filled. Review and click 'Create app' in the browser to continue." Wait for URL change to `/developers/apps/<id>`.
  - **Phase B (post-create, automated):** Settings tab -> OAuth 2 -> add redirect URI -> click Add. Permissions tab -> check 7 scope checkboxes -> click Submit. Settings tab -> read App key (and App secret if visible) from DOM.
  - All selectors role/label-based (`GetByRole`, `GetByLabel`).
  - Failure path: save Playwright trace to `%TEMP%\dbxprovider-trace-<utc>.zip`, log warning with path, return null. Caller falls through to manual wizard.

- **`Cmdlets\AuthCommands.cs`** -- in `PromptForNewAppRegistration`, before printing manual instructions:
  1. Call `DefaultBrowser.Detect()`.
  2. If Chromium-family, await `DropboxAppRegistrar.RegisterAsync(...)`. On non-null result, return its `(AppKey, AppSecret)` -- existing OAuth/PKCE flow runs unchanged.
  3. On null result OR non-Chromium default, write a one-line `WriteVerbose` note ("Default browser is X; using manual registration wizard.") and execute the existing manual flow.
  - **No new cmdlet parameters**: the user's click on Create app provides consent, so no `-AutoRegister` switch is needed.

### Default app-name format

`PSDbxProvider-<8 random alphanumeric>` (e.g. `PSDbxProvider-a3k9p2qm`).

- `PS` prefix signals PowerShell origin in the Dropbox App Console (which has no PS context). The PSGallery module name remains `DbxProvider`.
- Dropbox enforces global app-name uniqueness across all developers (verified). 8 random alphanumeric chars (~2.8 trillion combinations) make collisions essentially impossible -- no retry logic needed.
- Pre-filled into the form; user can edit before clicking Create app.

### Dependency

- Add `<PackageReference Include="Microsoft.Playwright" Version="1.49.*" />` to `src\DbxProvider\DbxProvider.csproj`.
- Driver bundled in the NuGet (~10 MB). **No `playwright install` step** -- we always use a system-installed Chromium-family browser.

### Scope (out)

- Mac/Linux default-browser detection -- defer.
- Stock Firefox / Safari automation -- not supported by Playwright without a separate download; manual fallback runs instead.
- Attempting to drive a non-default Chromium-family browser if the default is Firefox -- no, we never force a browser switch on the user.
- Auto-clicking "Create app" -- no, the user keeps that consent click.

## Evidence Plan

- **Change type:** CLI (interactive cmdlet UX change).
- **Artifact format:** markdown index + recording (asciinema or screen recording) showing a fresh `Connect-Dropbox` first-run that:
  1. Opens the user's default browser.
  2. Pre-fills the Create-app form.
  3. Captures the user clicking Create app.
  4. Shows the redirect URI + scope checkboxes filled automatically.
  5. Shows the App key arriving in the cmdlet output and `Get-ChildItem Dbx:\` succeeding.
- **Capture command:** `pwsh -NoProfile -File .\build\Capture-ConnectDropboxEvidence.ps1` (new helper that records terminal + browser via the `evidence-capture` skill conventions and writes the recording + a scrubbed transcript).
- **Entry-point file:** `.evidence/<phase-id>/evidence.md` (markdown index linking to the recording and the auto-generated wizard transcript).

## Acceptance Criteria

- [ ] On Windows with Edge or Chrome as default, `Connect-Dropbox` (no flags, no saved credentials) opens that browser, pre-fills the Create page, waits for the user to click Create app, then writes the App key and connects.
- [ ] After a successful run, the credentials are persisted under the new `dbid:` and a subsequent `Connect-Dropbox -Account <email>` reconnects without browser interaction.
- [ ] On Windows with Firefox as default (or no default detectable), `Connect-Dropbox` writes a short note and falls through to today's purely-textual wizard. No regression in that path.
- [ ] When Playwright fails (e.g., page selector miss), the cmdlet writes the trace path, falls through to the manual wizard, and still completes successfully.
- [ ] User can edit the pre-filled name field; the cmdlet uses whatever name ends up in the form when the user clicks Create app.
- [ ] On Linux/macOS, `Connect-Dropbox` runs the manual wizard (no Playwright launch) without throwing.
- [ ] No `-AutoRegister` (or any other new) parameter is added to `Connect-Dropbox`.

## Implementation Checklist (TDD-shaped)

- [ ] **Test:** `DefaultBrowserTests.cs` -- registry-mocked unit tests for each ProgId mapping + missing-key + missing-exe paths. (RED first.)
- [ ] **Code:** `Services\DefaultBrowser.cs`. (GREEN.)
- [ ] **Test:** `DropboxAppRegistrarNamingTests.cs` -- name generator emits `PSDbxProvider-<8 alnum>`, no collisions in a 10k sample.
- [ ] **Code:** name generator helper inside `DropboxAppRegistrar`.
- [ ] **Code:** `Services\DropboxAppRegistrar.cs` Phases A + B. (Selectors role/label-based; failure path returns null + saves trace.)
- [ ] **Test:** smoke integration test gated on `DBX_PLAYWRIGHT_TESTS=1` that runs the full registrar against a sandbox Dropbox account; CI does not run it.
- [ ] **Code:** wire the registrar into `AuthCommands.cs::PromptForNewAppRegistration` with the detection + fall-through logic.
- [ ] **Test:** existing `Connect-Dropbox` non-interactive-host test still returns the prior `InvalidOperationException` (regression).
- [ ] **Dep:** add `Microsoft.Playwright` package reference; verify `dotnet build` clean.
- [ ] **Docs:** update `README.md` Multiple Accounts section + `docs\help\en-US\Connect-Dropbox.md` Reuse-mode bullet to describe the new wizard, the persistent profile, and the manual fallback. No new flag to document.
- [ ] **Evidence:** add `build\Capture-ConnectDropboxEvidence.ps1` recording helper; produce `.evidence/<phase-id>/evidence.md` per the Evidence Plan.

## Risks / Mitigations

- **DOM brittleness** -- Dropbox redesigns will break selectors. Mitigation: role/label selectors; trace-on-failure; clean fall-through to manual wizard; `Selectors last verified <date>` comment block; smoke integration test.
- **Dropbox ToS / detection** -- headed, dedicated profile, human-paced clicks is low risk for personal automation; never headless; 200-500 ms `WaitForTimeoutAsync` between clicks; doc note that the user is automating their own account.
- **Sign-in 2FA / SSO redirects** -- poll target URL rather than asserting any login URL; 5-minute timeout with cancellable Esc.
- **Default browser exe missing or moved** -- detection returns null, manual wizard runs.
- **Playwright NuGet size** -- ~10 MB driver only (no browser download). Acceptable.
- **MCP `.playwright-mcp/` confusion** -- that directory is the agent's MCP scratch area per upstream instructions; **this feature's production Playwright dependency is unrelated** and writes its trace to `%TEMP%`. Both should remain in `.gitignore`.

## Alternatives considered

- `-AutoRegister` switch -- rejected: user's click on Create app already provides consent.
- Auto-clicking Create app -- rejected: removes meaningful consent.
- Force `Channel = "msedge"` on every machine -- rejected earlier: forces a browser switch.
- Bundled-Chromium download via `playwright install chromium` -- rejected earlier: avoidable.
- Connect to user's running browser via `--remote-debugging-port` -- rejected: requires user to relaunch their main browser.
- Bookmarklet / userscript -- DOM-dependent and awkward to invoke from PowerShell.
- Internal App-Console API -- undocumented, brittle, ToS-risky.

## Coordination with the new SDLC framework

The IntelliSDLC.ai framework lives in `.worktrees/sdlc-sync` and is not yet
on `main`. Two paths:

- **(A) Wait for `sdlc-sync` to merge**, then run this work through the full
  `@dev-loop` ceremony: feature branch `feat/<issue#>-playwright-dbx-registrar`
  in a worktree under `.worktrees/`, TDD with the `behavior-first-testing`
  skill, evidence captured per the `evidence-capture` skill, code review,
  PR with `--rebase --delete-branch`. (Recommended; one-shot adoption.)
- **(B) Land this feature on `main` now** with conventional-commit messages
  and a feature branch (worktree optional, hook not yet active), and adopt
  the dev-loop ceremony retroactively when `sdlc-sync` merges.

## Decisions still to confirm

1. Approve this as the design, or revise?
2. Coordination path (A) wait-for-sdlc-sync vs. (B) land-on-main-now?
3. Mac/Linux default-browser detection: defer to a follow-up issue, or include in this issue?
4. Evidence recording tool: asciinema (terminal only, lightweight) vs. full-screen recording (captures the browser)?
