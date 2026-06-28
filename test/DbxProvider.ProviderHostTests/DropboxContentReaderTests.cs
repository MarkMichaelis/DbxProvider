using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DbxProvider.Provider;
using IntelliTect.Dropbox;
using Xunit;

namespace DbxProvider.ProviderHostTests;

/// <summary>
/// Behavior-first tests for <see cref="DropboxContentReader"/> in raw
/// (<c>-AsByteStream</c>) mode. The reader must stream the file in bounded blocks
/// rather than materializing the entire file as one array, so reading a very large
/// file does not exhaust memory.
/// </summary>
public class DropboxContentReaderTests
{
    private static FakeDropboxServiceClient FakeWithFile(string path, byte[] content)
    {
        var fake = new FakeDropboxServiceClient(new List<DropboxItem>
        {
            new() { Name = "f", Path = path, IsFolder = false, Id = "id:" + path, Length = (ulong)content.Length },
        });
        fake.FileBytes[path] = content;
        return fake;
    }

    private static byte[] DrainRaw(DropboxContentReader reader, long readCount, out int maxBlock)
    {
        maxBlock = 0;
        var all = new List<byte>();
        while (true)
        {
            IList block = reader.Read(readCount);
            if (block.Count == 0) break;
            var bytes = (byte[])block[0]!;
            maxBlock = System.Math.Max(maxBlock, bytes.Length);
            all.AddRange(bytes);
        }
        return all.ToArray();
    }

    [Fact]
    public void Read_RawLargeFile_StreamsInBoundedBlocks_AndReconstructsContent()
    {
        // 200 KB exceeds the reader's 80 KB block cap, so a correct streaming reader
        // returns several bounded blocks; the broken whole-file reader returned one
        // 200 KB block.
        var content = Enumerable.Range(0, 200_000).Select(i => (byte)(i % 251)).ToArray();
        var reader = new DropboxContentReader(FakeWithFile("/big.bin", content), "/big.bin", raw: true);

        var result = DrainRaw(reader, readCount: 0, out int maxBlock);

        Assert.Equal(content, result);
        Assert.True(maxBlock <= 81920, $"Expected each raw block to be bounded (<= 81920 bytes) but one was {maxBlock}.");
    }

    [Fact]
    public void Read_RawWithReadCount_LimitsBlockToReadCount()
    {
        var content = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();
        var reader = new DropboxContentReader(FakeWithFile("/c.bin", content), "/c.bin", raw: true);

        IList first = reader.Read(16);

        var bytes = (byte[])first[0]!;
        Assert.Equal(16, bytes.Length);
    }
}
