# Dbx.Core Extraction -- Implementation Plan

Issue: #11
Branch: refactor/11-dbx-core-extraction (stacked on feat/4-playwright-dbx-registrar, PR #5)

## Design / invariants (approved)

- Dbx.Auth and Dbx.Core are INDEPENDENT (neither references the other).
- Dbx.Core multi-targets netstandard2.0;net8.0; deps: Dropbox.Api 7.0.0 (+ framework
  polyfill packages: System.Text.Json + System.Security.Cryptography.ProtectedData as
  required). No System.Management.Automation, no Playwright, no Dbx.Auth.
- Wildcard search moves into Core as a framework-neutral matcher preserving exact
  WildcardPattern semantics (characterization tests first).
- DropboxItem stays a POCO with FileSystem-parity aliases.

## Namespace decision

Moved types keep their existing namespaces (DbxProvider.Models / DbxProvider.Services).
Rationale: guarantees byte-for-byte behavior parity and zero consumer churn; namespaces
spanning assemblies is idiomatic .NET. Package independence + PowerShell-freeness are
governed by assembly references, not namespaces. (Deviation from "rename namespaces";
documented in PR. Trivial follow-up if a rename is later desired.)

## Steps (build + full unit suite green after each)

1. Create src/Dbx.Core csproj (netstandard2.0;net8.0, PackageId=IntelliTect.Dropbox.Core,
   Authors=IntelliTect, Dropbox.Api 7.0.0 + polyfill packages, InternalsVisibleTo for
   Dbx.Core.UnitTests + DbxProvider.* test assemblies). Add to solution.
2. Characterization tests (Dbx.Core.UnitTests) for wildcard semantics: *, ?, [set],
   backtick-escape, case-insensitivity -- mirroring WildcardPattern.
3. Add Core WildcardMatcher (regex translation); characterization tests green.
4. Move DropboxItem + CacheOptions into Core; build+tests green.
5. Move RateLimitRetry (+ RateLimitRetryTests) into Core; green.
6. Move MetadataCache into Core; green.
7. Move CredentialStore (+ CredentialStoreMultiAccountTests + non-parallel collection)
   into Core; keep persistence in Core; glue (DbxCredentialStore) stays in host; green.
8. Move DropboxServiceClient; swap WildcardPattern -> Core matcher; add a test proving
   construction from app key/secret/refresh token with no Auth; green.
9. Repoint DbxProvider (Provider/* + Cmdlets/*) to Dbx.Core via ProjectReference; remove
   moved files from host; full solution + Pester green.
10. Rename Auth PackageId -> IntelliTect.Dropbox.Auth (csproj + publish-dbx-auth.yml).
    Add publish-dbx-core.yml (tags dbx-core-v*). dotnet pack sanity for BOTH packages.

## Notes

- net8.0 runtime absent locally; tests run with DOTNET_ROLL_FORWARD=LatestMajor (10.0.8).
- Credential tests mutate process-wide LOCALAPPDATA -> keep the non-parallel xUnit
  collection isolation when moved.
- Live Dropbox smoke test is a manual follow-up.