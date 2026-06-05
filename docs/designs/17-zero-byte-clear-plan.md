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

1. **Primary fix** -- ``DropboxProvider.ClearContent``: skip the zero-byte upload when
   ``DynamicParameters is DropboxContentWriterDynamicParameters`` (the redundant implicit pre-write
   clear; the writer's overwrite already truncates+replaces). Otherwise (explicit ``Clear-Content``,
   ``DynamicParameters`` null) perform the zero-byte upload to truncate, as today.
2. **Defensive secondary fix** -- ``DropboxContentWriter``: track whether any ``Write`` happened;
   on ``Close`` skip the upload only when ``Write`` was NEVER called. Preserve the upload when
   ``Write`` was called with empty content (``Set-Content -Value ''`` still truncates to empty).

## Test seam

``DropboxServiceClient.UploadAsync`` is made ``virtual`` so the in-memory
``FakeDropboxServiceClient`` can override + record every upload (path + byte length). This is the
only production change outside the two fixes.

## Tasks

- [ ] (infra) Make ``UploadAsync`` virtual; add upload recording to ``FakeDropboxServiceClient``.
- [ ] (RED) Host test: single ``Set-Content`` => exactly ONE upload, length > 0, no zero-byte
      intermediate. Fails on current code (2 uploads incl. a 0-byte one).
- [ ] (GREEN) ``ClearContent`` discriminator skips the implicit clear.
- [ ] (guard) Host test: explicit ``Clear-Content`` => exactly ONE zero-byte upload (truncates).
      Guards against a blanket no-op regression.
- [ ] (RED) Unit test: ``DropboxContentWriter.Close`` without any ``Write`` => no upload;
      with ``Write('')`` => exactly one upload.
- [ ] (GREEN) Writer guard tracks whether ``Write`` was called.
- [ ] Refactor, functional test, evidence, code review, PR.

## Acceptance criteria (from issue #17)

- Single ``Set-Content`` => exactly ONE upload, no zero-byte intermediate.
- Explicit ``Clear-Content`` still truncates to zero bytes.
- Behavior-first tests fail (behaviorally) when the fix is reverted.