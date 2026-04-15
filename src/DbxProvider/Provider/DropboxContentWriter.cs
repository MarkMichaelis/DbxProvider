using System;
using System.Collections;
using System.IO;
using System.Management.Automation.Provider;
using System.Text;

namespace DbxProvider.Provider
{
    /// <summary>Writes content to a Dropbox file.</summary>
    public class DropboxContentWriter : IContentWriter
    {
        private readonly Services.DropboxServiceClient _service;
        private readonly string _path;
        private MemoryStream _buffer;
        private StreamWriter? _writer;
        private bool _disposed;
        private readonly bool _raw;

        public DropboxContentWriter(Services.DropboxServiceClient service, string path, bool raw = false)
        {
            _service = service;
            _path = path;
            _raw = raw;
            _buffer = new MemoryStream();
            if (!raw)
            {
                _writer = new StreamWriter(_buffer, Encoding.UTF8, 4096, leaveOpen: true);
            }
        }

        public IList Write(IList content)
        {
            foreach (var item in content)
            {
                if (_raw && item is byte[] bytes)
                {
                    _buffer.Write(bytes, 0, bytes.Length);
                }
                else
                {
                    if (_writer == null)
                    {
                        _writer = new StreamWriter(_buffer, Encoding.UTF8, 4096, leaveOpen: true);
                    }
                    _writer.WriteLine(item?.ToString() ?? "");
                }
            }
            return content;
        }

        public void Seek(long offset, SeekOrigin origin)
        {
            _buffer.Seek(offset, origin);
        }

        public void Close()
        {
            Flush();
            Dispose();
        }

        private void Flush()
        {
            _writer?.Flush();
            _buffer.Position = 0;
            _service.UploadAsync(_path, _buffer).GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _writer?.Dispose();
                _buffer.Dispose();
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }
    }
}