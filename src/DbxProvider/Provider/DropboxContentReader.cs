using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Management.Automation.Provider;

namespace DbxProvider.Provider
{
    /// <summary>Reads content from a Dropbox file as lines or bytes.</summary>
    public class DropboxContentReader : IContentReader
    {
        private readonly Services.DropboxServiceClient _service;
        private readonly string _path;
        private StreamReader? _reader;
        private Stream? _stream;
        private bool _disposed;
        private readonly bool _raw;

        public DropboxContentReader(Services.DropboxServiceClient service, string path, bool raw = false)
        {
            _service = service;
            _path = path;
            _raw = raw;
        }

        public IList Read(long readCount)
        {
            var result = new ArrayList();

            if (_stream == null)
            {
                var (content, _) = _service.DownloadAsync(_path).GetAwaiter().GetResult();
                _stream = content;

                if (_raw)
                {
                    using var ms = new MemoryStream();
                    _stream.CopyTo(ms);
                    result.Add(ms.ToArray());
                    return result;
                }

                _reader = new StreamReader(_stream);
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