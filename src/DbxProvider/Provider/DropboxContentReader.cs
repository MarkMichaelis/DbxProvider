using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Management.Automation.Provider;
using IntelliTect.Dropbox;

namespace DbxProvider.Provider
{
    /// <summary>Reads content from a Dropbox file as lines or bytes.</summary>
    public class DropboxContentReader : IContentReader
    {
        private readonly DropboxServiceClient _service;
        private readonly string _path;
        private StreamReader? _reader;
        private Stream? _stream;
        private bool _disposed;
        private readonly bool _raw;

        public DropboxContentReader(DropboxServiceClient service, string path, bool raw = false)
        {
            _service = service;
            _path = path;
            _raw = raw;
        }

        /// <summary>
        /// The maximum number of bytes returned from a single raw-mode
        /// <see cref="Read"/> call. Bounds memory so reading a multi-gigabyte file
        /// with <c>-AsByteStream</c> streams in fixed-size blocks instead of
        /// materializing the whole file as one array (which exhausted memory).
        /// </summary>
        private const int MaxRawBlockSize = 81920;

        /// <summary>Reads up to <paramref name="readCount"/> lines, or a bounded byte block in raw mode.</summary>
        public IList Read(long readCount)
        {
            var result = new ArrayList();
            EnsureOpen();

            if (_raw)
            {
                ReadRawBlock(readCount, result);
                return result;
            }

            if (_reader == null) return result;

            for (long i = 0; i < readCount; i++)
            {
                var line = _reader.ReadLine();
                if (line == null) break;
                result.Add(line);
            }

            return result;
        }

        /// <summary>Downloads and opens the file stream on first read.</summary>
        private void EnsureOpen()
        {
            if (_stream != null) return;
            var (content, _) = _service.DownloadAsync(_path).GetAwaiter().GetResult();
            _stream = content;
            if (!_raw)
            {
                _reader = new StreamReader(_stream);
            }
        }

        /// <summary>Reads a single bounded block of raw bytes from the stream.</summary>
        private void ReadRawBlock(long readCount, ArrayList result)
        {
            if (_stream == null) return;
            int cap = readCount > 0 && readCount < MaxRawBlockSize ? (int)readCount : MaxRawBlockSize;
            var buffer = new byte[cap];
            int read = _stream.Read(buffer, 0, cap);
            if (read <= 0) return;
            if (read == cap)
            {
                result.Add(buffer);
            }
            else
            {
                var trimmed = new byte[read];
                Array.Copy(buffer, trimmed, read);
                result.Add(trimmed);
            }
        }

        public void Seek(long offset, SeekOrigin origin)
        {
            _stream?.Seek(offset, origin);
        }

        public void Close() => Dispose();

        public void Dispose()
        {
            if (!_disposed)
            {
                _reader?.Dispose();
                _stream?.Dispose();
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }
    }
}