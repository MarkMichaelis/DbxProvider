using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace IntelliTect.Dropbox
{
    /// <summary>
    /// Computes Dropbox's <c>content_hash</c> for a file or stream so a local
    /// copy can be proven byte-identical to the Dropbox master before it is
    /// trusted in place of an API download.
    /// </summary>
    /// <remarks>
    /// The algorithm is the one Dropbox documents publicly: split the content
    /// into 4 MiB blocks, take the SHA-256 of each block, concatenate those
    /// block digests in order, take the SHA-256 of the concatenation, and emit
    /// the result as lowercase hexadecimal. An empty file therefore hashes the
    /// SHA-256 of an empty byte sequence.
    /// </remarks>
    public static class DropboxContentHasher
    {
        /// <summary>The Dropbox block size: 4 MiB.</summary>
        public const int BlockSize = 4 * 1024 * 1024;

        /// <summary>
        /// Computes the Dropbox <c>content_hash</c> of the file at
        /// <paramref name="path"/>.
        /// </summary>
        /// <param name="path">Absolute or relative path to the local file.</param>
        /// <returns>The 64-character lowercase hexadecimal content hash.</returns>
        public static string ComputeFileHash(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("Path must be provided.", nameof(path));
            }

            using FileStream stream = File.OpenRead(path);
            return ComputeHash(stream);
        }

        /// <summary>
        /// Computes the Dropbox <c>content_hash</c> of <paramref name="content"/>,
        /// reading sequentially from the current position to the end.
        /// </summary>
        /// <param name="content">The stream to hash.</param>
        /// <returns>The 64-character lowercase hexadecimal content hash.</returns>
        public static string ComputeHash(Stream content)
        {
            if (content is null)
            {
                throw new ArgumentNullException(nameof(content));
            }

            using SHA256 overall = SHA256.Create();
            byte[] buffer = new byte[BlockSize];

            while (true)
            {
                int filled = FillBlock(content, buffer);
                if (filled == 0)
                {
                    break;
                }

                byte[] blockDigest = ComputeBlockDigest(buffer, filled);
                overall.TransformBlock(blockDigest, 0, blockDigest.Length, null, 0);

                if (filled < BlockSize)
                {
                    break;
                }
            }

            overall.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return ToLowerHex(overall.Hash!);
        }

        private static byte[] ComputeBlockDigest(byte[] buffer, int length)
        {
            using SHA256 blockHasher = SHA256.Create();
            return blockHasher.ComputeHash(buffer, 0, length);
        }

        /// <summary>
        /// Fills <paramref name="buffer"/> from <paramref name="stream"/>, looping
        /// because a single <see cref="Stream.Read(byte[], int, int)"/> may return
        /// fewer bytes than requested. Returns the number of bytes read, which is
        /// less than the buffer length only at end of stream.
        /// </summary>
        private static int FillBlock(Stream stream, byte[] buffer)
        {
            int total = 0;
            int read;
            while (total < buffer.Length &&
                   (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
            {
                total += read;
            }

            return total;
        }

        private static string ToLowerHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
            {
                sb.Append(b.ToString("x2"));
            }

            return sb.ToString();
        }
    }
}
