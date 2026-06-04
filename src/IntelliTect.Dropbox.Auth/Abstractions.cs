using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace IntelliTect.Dropbox.Auth;

/// <summary>
/// Launches a Playwright browser context for driving the Dropbox App Console.
/// The launcher owns the lifetime of the returned context: callers
/// <b>must not</b> dispose or close it. Disposing the launcher releases the
/// context and the underlying Playwright instance.
/// </summary>
public interface IBrowserLauncher
{
    /// <summary>Launches (or returns) a browser context. Caller must not dispose it.</summary>
    Task<IBrowserContext> LaunchAsync(CancellationToken ct);
}

/// <summary>
/// Minimal console abstraction so onboarding flows can surface progress and
/// prompts without coupling to a specific host (PowerShell, plain console, GUI).
/// </summary>
public interface IConsole
{
    /// <summary>Writes an informational line to the host.</summary>
    void Info(string message);

    /// <summary>Writes a prompt and reads a single line of user input.</summary>
    string Prompt(string message);
}

/// <summary>
/// Persists and retrieves <see cref="DropboxCredential"/> values. Consumers
/// supply their own storage (DPAPI file, secret manager, in-memory, ...).
/// </summary>
public interface ICredentialStore
{
    /// <summary>Saves the supplied credential.</summary>
    void Save(DropboxCredential cred);

    /// <summary>Loads the credential stored under <paramref name="key"/>, or <c>null</c>.</summary>
    DropboxCredential? Load(string key);
}
