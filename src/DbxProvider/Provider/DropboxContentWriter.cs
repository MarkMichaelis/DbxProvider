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
        private bool _writeFailed;

        public DropboxContentWriter(DropboxServiceClient service, string path, bool raw = false)
        {
            _service = service;
            _path = path;
            _raw = raw;
            _buffer = new MemoryStream();
            // The text writer is created lazily on first write (after any append
            // preload) so that, when appending, the buffer is already positioned past
            // the existing content and StreamWriter suppresses the UTF-8 preamble
            // instead of injecting a BOM into the middle of the file.
        }

        public IList Write(IList content)
        {
            try
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
            }
            catch
            {
                // Buffering failed (e.g. an append could not read the existing file).
                // Mark the write failed so Close()/Flush() does NOT upload -- otherwise
                // an empty/partial buffer would overwrite and destroy the file.
                _writeFailed = true;
                throw;
            }
            return content;
        }

        /// <summary>
        /// Writes a single raw-mode pipeline element. PowerShell enumerates a
        /// <c>byte[]</c> value into individual <see cref="byte"/> elements before
        /// calling <see cref="Write"/>, so raw mode must accept scalar bytes (and
        /// byte arrays) and write them verbatim. The method is total -- it never
        /// throws -- so a stray non-byte value cannot abort mid-write and leave a
        /// partially uploaded (truncated) file.
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
                case string s:
                    WriteRawText(s);
                    break;
                case char c:
                    WriteRawText(c.ToString());
                    break;
                default:
                    if (TryConvertToByte(item, out var value))
                        _buffer.WriteByte(value);
                    else
                        WriteRawText(item.ToString() ?? string.Empty);
                    break;
            }
        }

        private void WriteRawText(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            _buffer.Write(bytes, 0, bytes.Length);
        }

        /// <summary>Converts an integral numeric value in the byte range to a byte;
        /// returns false (rather than throwing) for strings, out-of-range, or
        /// non-integral values so the caller can fall back without aborting the write.</summary>
        private static bool TryConvertToByte(object item, out byte value)
        {
            value = 0;
            switch (item)
            {
                case sbyte or short or ushort or int or uint or long or ulong:
                    try
                    {
                        long n = Convert.ToInt64(item);
                        if (n < 0 || n > 255) return false;
                        value = (byte)n;
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                default:
                    return false;
            }
        }

        /// <summary>
        /// On the first append-mode write, loads the file's existing content into the
        /// buffer so appended bytes are added rather than replacing the file. Dropbox
        /// has no native append, so without this <c>Add-Content</c> silently
        /// overwrote (destroyed) the file via the overwrite upload. A missing file is
        /// treated as a fresh write; any other read failure is allowed to propagate so
        /// a transient error cannot cause an overwrite that loses the existing content.
        /// </summary>
        private void EnsureAppendLoaded()
        {
            if (!_appendRequested || _appendLoaded) return;
            _appendLoaded = true;

            // A genuinely new file has nothing to append to. Probing existence first
            // means a download failure below is a real error (propagated), not silently
            // swallowed into a destructive overwrite.
            if (!_service.ItemExistsAsync(_path).GetAwaiter().GetResult())
                return;

            var (content, _) = _service.DownloadAsync(_path).GetAwaiter().GetResult();
            using (content)
            {
                content.CopyTo(_buffer);
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
            // Do not upload when buffering failed -- uploading the empty/partial buffer
            // would overwrite (and so destroy) the existing file.
            if (_writeFailed) return;
            // Append mode that never wrote anything must not upload either: an empty
            // buffer would truncate the existing file to zero bytes. (Set-Content /
            // overwrite is not append mode, so its intentional zero-byte writes still
            // upload.)
            if (_appendRequested && !_appendLoaded) return;
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