using System.IO;
using System.Text;
using IntelliTect.Dropbox;
using FluentAssertions;
using Xunit;

namespace Dbx.Core.UnitTests;

/// <summary>
/// Pins <see cref="DropboxContentHasher"/> to Dropbox's published
/// <c>content_hash</c> algorithm (4 MiB blocks, SHA-256 per block, SHA-256 of
/// the concatenated block digests, lowercase hex). Expected values are produced
/// by an independent PowerShell implementation, not by the production code, so
/// reverting the block-splitting logic fails the multi-block case for a
/// behavioral reason.
/// </summary>
public class DropboxContentHasherTests
{
    private const string EmptyHash =
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private const string HelloHash =
        "9595c9df90075148eb06860365df33584b75bff782a510c6cd4883a419833d50";
    private const string FiveMiBOfLowercaseA =
        "31eebee77ad19453ca9817312ea1a938a001f5507fda7c721601319ad84aa857";

    [Fact]
    public void ComputeHash_EmptyStream_HashesEmptyDigestConcatenation()
    {
        using var stream = new MemoryStream();

        DropboxContentHasher.ComputeHash(stream).Should().Be(EmptyHash);
    }

    [Fact]
    public void ComputeHash_SingleBlock_DoubleHashesTheBlock()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("hello"));

        DropboxContentHasher.ComputeHash(stream).Should().Be(HelloHash);
    }

    [Fact]
    public void ComputeHash_SpansMultipleBlocks_ConcatenatesBlockDigests()
    {
        var data = new byte[5 * 1024 * 1024];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = 0x61;
        }
        using var stream = new MemoryStream(data);

        DropboxContentHasher.ComputeHash(stream).Should().Be(FiveMiBOfLowercaseA);
    }
}
