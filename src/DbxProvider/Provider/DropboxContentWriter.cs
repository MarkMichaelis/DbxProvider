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
        private readonly bool _raw;
        private bool _appendRequested;
        private bool _appendLoaded;

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
            EnsureAppendLoaded();
            foreach (var item in content)
            {
                if (_raw)
                {
                    WriteRawItem(item);
                }
                else
                {
                    _writer ??= new StreamWriter(_buffer, Encoding.UTF8, 4096, leaveOpen: true);
                    _writer.WriteLine(item?.ToString() ?? "");
                }
            }
            return content;
        }

        /// <summary>
        /// Writes a single raw-mode pipeline element. PowerShell enumerates a
        /// <c>byte[]</c> value into individual <see cref="byte"/> elements before
        /// calling <see cref="Write"/>, so raw mode must accept scalar bytes (and
        /// byte arrays) and write them verbatim -- not coerce them to UTF-8 text,
        /// which corrupted <c>-AsByteStream</c> output with text digits and a BOM.
        /// </summary>
        private void WriteRawItem(object? item)
        {
            switch (item)
            {
                case null:
                    break;
                case byte b:
                    _buffer.WriteByte(b);
                    break;
                case byte[] bytes:
                    _buffer.Write(bytes, 0, bytes.Length);
                    break;
                case IConvertible convertible:
                    _buffer.WriteByte(Convert.ToByte(convertible));
                    break;
                default:
                    var text = Encoding.UTF8.GetBytes(item.ToString() ?? string.Empty);
                    _buffer.Write(text, 0, text.Length);
                    break;
            }
        }

        /// <summary>
        /// On the first append-mode write, loads the file's existing content into the
        /// buffer so appended bytes are added rather than replacing the file. Dropbox
        /// has no native append, so without this <c>Add-Content</c> silently
        /// overwrote (destroyed) the file via the overwrite upload.
        /// </summary>
        private void EnsureAppendLoaded()
        {
            if (!_appendRequested || _appendLoaded) return;
            _appendLoaded = true;
            try
            {
                _writer?.Flush();
                var (content, _) = _service.DownloadAsync(_path).GetAwaiter().GetResult();
                using (content)
                {
                    content.CopyTo(_buffer);
                }
            }
            catch
            {
                // No existing file (or it could not be read): treat append as a
                // fresh write rather than failing the operation.
            }
        }

        public void Seek(long offset, SeekOrigin origin)
        {
            if (origin == SeekOrigin.End)
            {
                _appendRequested = true;
            }
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