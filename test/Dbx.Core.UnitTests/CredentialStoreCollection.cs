using Xunit;

namespace Dbx.Core.UnitTests;

/// <summary>
/// Groups tests that mutate the process-wide <c>LOCALAPPDATA</c> environment
/// variable to redirect the static <see cref="DbxProvider.Services.CredentialStore"/>.
/// Parallelization is disabled so these classes never clobber each other's
/// temp credential file.
/// </summary>
[CollectionDefinition("CredentialStore", DisableParallelization = true)]
public sealed class CredentialStoreCollection
{
}
