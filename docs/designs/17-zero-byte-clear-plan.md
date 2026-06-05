# Plan: Eliminate zero-byte intermediate revision on Set-Content (Issue #17)

## Problem

PowerShell ``Set-Content``/``Out-File``/``>`` calls ``IContentCmdletProvider.ClearContent(path)``
first, then ``GetContentWriter(path)`` + ``Write`` + ``Close``. ``DropboxProvider.ClearContent``
uploads a zero-byte ``MemoryStream`` as a standalone Dropbox revision; the writer then uploads the
real content as a second revision (``WriteMode.Overwrite``). The zero-byte intermediate is a server
revision a concurrent Dropbox sync client can race into a zero-byte "conflicted copy."

## Design (validated by an empirical provider-lifecycle spike)

A throwaway spike hosted the real provider in-process against the recording fake and logged every
provider call + ``this.DynamicParameters`` type. Findings:

- ``Set-Content``/``Out-File``/redirection: PowerShell resolves ``GetContentWriterDynamicParameters``
  FIRST, and the implicit ``ClearContent`` runs with ``this.DynamicParameters`` being a
  ``DropboxContentWriterDynamicParameters`` instance.
- Standalone ``Clear-Content``: ``ClearContent`` runs with ``this.DynamicParameters == null``
  (``ClearContentDynamicParameters`` returns ``null``).
- ``Add-Content`` (append): no ``ClearContent`` call at all.
- ``ClearContent`` and ``GetContentWriter`` run on DIFFERENT provider instances, and no per-cmdlet
  ``Stop``/end hook fires -- so instance fields and deferred-flush approaches are NOT viable.

Chosen approach (simplest correct, self-contained, no cross-call state):

1. **The fix** -- ``DropboxProvider.ClearContent``: skip the zero-byte upload when
   ``DynamicParameters is DropboxContentWriterDynamicParameters`` (the redundant implicit pre-write
   clear; the writer's overwrite already truncates+replaces). Otherwise (explicit ``Clear-Content``,
   ``DynamicParameters`` null) perform the zero-byte upload to truncate, as today.

### Writer-guard evaluation (rejected by code review)

A secondary "skip the writer upload when nothing was written" guard was prototyped and rejected:
it changed ``Set-Content -Value @()``/``-Value $null`` from "truncate/replace in one revision"
(matching the FileSystem provider) to a no-op, without adding protection against the dangerous
*intermediate* revision (which the ClearContent fix already eliminates). For ``Set-Content -Value ''``
the writer is called normally and uploads in one revision regardless. So the writer is left
unchanged; the ClearContent discriminator alone is the complete fix.

## Test seam

``DropboxServiceClient.UploadAsync`` is made ``virtual`` so the in-memory
``FakeDropboxServiceClient`` can override + record every upload (path + byte length). This is the
only production change outside the two fixes.

## Tasks

- [x] (infra) Make ``UploadAsync`` virtual; add upload recording to ``FakeDropboxServiceClient``.
- [x] (RED) Host test: single ``Set-Content`` => exactly ONE upload, length > 0, no zero-byte
      intermediate. Fails on current code (2 uploads incl. a 0-byte one).
- [x] (GREEN) ``ClearContent`` discriminator skips the implicit clear.
- [x] (guard) Host test: explicit ``Clear-Content`` => exactly ONE zero-byte upload (truncates).
      Guards against a blanket no-op regression.
- [x] (cover) Host tests: ``Set-Content -Value ''`` and ``-Value @()`` => exactly one upload, no
      zero-byte intermediate.
- [x] (review) Evaluate + reject the writer "anything written" guard (see above).
- [x] Refactor, functional test, evidence, code review, PR.

## Acceptance criteria (from issue #17)

- Single ``Set-Content`` => exactly ONE upload, no zero-byte intermediate.
- Explicit ``Clear-Content`` still truncates to zero bytes.
- Behavior-first tests fail (behaviorally) when the fix is reverted.