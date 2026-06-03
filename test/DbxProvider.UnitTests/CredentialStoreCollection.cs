using Xunit;

namespace DbxProvider.UnitTests;

/// <summary>
/// Host-side definition of the "CredentialStore" collection so credential
/// adapter tests that redirect process-wide <c>LOCALAPPDATA</c> never run in
/// parallel with each other. Mirrors the Dbx.Core.UnitTests definition; xUnit
/// collections are per-assembly.
/// </summary>
[CollectionDefinition("CredentialStore", DisableParallelization = true)]
public sealed class CredentialStoreCollection
{
}