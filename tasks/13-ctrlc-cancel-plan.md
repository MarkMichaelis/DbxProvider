# Plan: Ctrl-C cancels Connect-Dropbox (Issue #13)

## Design

Ctrl-C in a compiled PSCmdlet invokes `StopProcessing()` on a separate thread. The
fix introduces the canonical cancellation hook and makes the blocking seams honor it:

1. **Auth library (`LoopbackOAuthFlow`)** -- extract the loopback callback wait into a
   testable internal method `WaitForOAuthRedirectAsync(HttpListener, CancellationToken)`
   that registers `ct.Register(() => listener.Stop())` so a pending
   `GetContextAsync()` unblocks immediately on cancel, translating the resulting
   `HttpListenerException` / `ObjectDisposedException` into `OperationCanceledException`.
2. **Cmdlet (`ConnectDropboxCommand`)** -- add a cmdlet-scoped
   `CancellationTokenSource _stopCts`, override `StopProcessing()` to cancel it, and
   thread its token into `RunOAuthFlow` (linked with the Esc monitor) and into
   `PromptForNewAppRegistration -> RegisterAsync` (replacing `CancellationToken.None`).

API availability across TFMs (netstandard2.0 + net8.0): `CancellationToken.Register`,
`HttpListener.Stop`, `CancellationTokenSource.CreateLinkedTokenSource` are all available.

## Tasks

### Task 1 -- RED: failing test for prompt listener cancellation (Auth library)
- File: `test/Dbx.Auth.UnitTests/LoopbackOAuthFlowTests.cs`
- Add `WaitForOAuthRedirectAsync_CancelledToken_ThrowsPromptly`: bind a real
  `HttpListener` on an ephemeral localhost port, start the wait on a background task,
  cancel the token, assert `OperationCanceledException` within ~3s (no hang).
- Expected: FAILS to compile (method does not exist yet) then hangs/throws wrong type.

### Task 2 -- GREEN: extract `WaitForOAuthRedirectAsync` with cancellation wiring
- File: `src/IntelliTect.Dropbox.Auth/LoopbackOAuthFlow.cs`
- Add `internal static async Task<OAuthCallback> WaitForOAuthRedirectAsync(HttpListener listener, CancellationToken ct)`.
- Register `ct.Register(() => listener.Stop())`; wrap `GetContextAsync()` to translate
  cancel-time exceptions into `OperationCanceledException`.
- Refactor `ListenForCallbackAsync` to delegate to the new method.
- Expected: Task 1 test passes.

### Task 3 -- RED/GREEN: `RunAsync` propagates cancellation through listen seam
- File: `test/Dbx.Auth.UnitTests/LoopbackOAuthFlowTests.cs`
- Add `RunAsync_CancelledToken_PropagatesCancellation`: inject a `listen` delegate that
  awaits `Task.Delay(Timeout.Infinite, ct)`; cancel; assert `OperationCanceledException`.
- Regression guard for token plumbing in `RunAsync`.

### Task 4 -- Cmdlet StopProcessing + token threading
- File: `src/DbxProvider/Cmdlets/AuthCommands.cs`
- Add field `CancellationTokenSource? _stopCts`; initialize in `ProcessRecord`, dispose in finally.
- Override `StopProcessing()` to cancel `_stopCts`.
- `RunOAuthFlow`: build `cts` as linked token source over `_stopCts?.Token`.
- `PromptForNewAppRegistration`: pass `_stopCts?.Token ?? CancellationToken.None` to `RegisterAsync`.

### Task 5 -- Verify, format, evidence, review, PR
- `dotnet build` + `dotnet test` (Auth unit tests) green; `dotnet format` clean.
- Capture evidence (bug-fix template) demonstrating prompt cancellation.

## Commits (Conventional Commits)
- `test(auth): add failing prompt-cancellation tests for loopback listener`
- `fix(auth): unblock loopback listener on cancellation via ct.Register`
- `fix(dbxprovider): cancel Connect-Dropbox on Ctrl-C via StopProcessing`
