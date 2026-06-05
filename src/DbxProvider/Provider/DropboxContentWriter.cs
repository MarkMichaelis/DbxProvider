using System;
using System.Collections;
using System.IO;
using System.Management.Automation.Provider;
using IntelliTect.Dropbox;
using System.Text;

namespace DbxProvider.Provider
{
    /// <summary>Writes content to a Dropbox file.</summary>
    public class DropboxContentWriter : IContentWriter
    {
        private readonly DropboxServiceClient _service;
        private readonly string _path;
        private MemoryStream _buffer;
        private StreamWriter? _writer;
        private bool _disposed;
        private bool _hasWritten;
        private readonly bool _raw;

        public DropboxContentWriter(DropboxServiceClient service, string path, bool raw = false)
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
                _hasWritten = true;
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
            // A writer that was opened but never received any content (no Write call)
            // must not create a spurious zero-byte server revision. Writing an empty
            // value (Set-Content -Value '') does call Write, so that path still uploads
            // and truncates the file to empty.
            if (!_hasWritten)
            {
                return;
            }
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