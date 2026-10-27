using System.Formats.Tar;
using System.IO.Compression;

namespace Pfr.Core.Nios.Backup;

/// <summary>
/// Opens the onedb.xml inside a NIOS grid backup. Accepts:
/// a .bak / .tar.gz (gzip-compressed tar), a plain .tar, or a bare onedb.xml.
/// The backup is streamed in place and never copied or extracted to disk.
/// </summary>
public static class BackupSource
{
    private const string OneDbFileName = "onedb.xml";

    public static Stream OpenOneDb(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
        try
        {
            return OpenOneDb(file);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Returns a stream positioned at the start of onedb.xml. Disposing the
    /// returned stream disposes <paramref name="input"/> too.
    /// </summary>
    public static Stream OpenOneDb(Stream input)
    {
        // Peek at the first bytes, then stitch them back in front of the rest.
        var head = new byte[512];
        var read = 0;
        while (read < head.Length)
        {
            var n = input.Read(head, read, head.Length - read);
            if (n == 0) break;
            read += n;
        }

        var whole = new PrefixedStream(head.AsMemory(0, read), input);

        return Detect(head.AsSpan(0, read)) switch
        {
            BackupKind.Xml => whole,
            BackupKind.GzipTar => FindInTar(new GZipStream(whole, CompressionMode.Decompress), whole),
            BackupKind.Tar => FindInTar(whole, whole),
            _ => Fail(whole),
        };
    }

    private static Stream Fail(Stream whole)
    {
        whole.Dispose();
        throw new InvalidDataException(
            "Unrecognized file. Expected a NIOS grid backup (.bak / .tar.gz) or onedb.xml.");
    }

    private enum BackupKind { Unknown, Xml, GzipTar, Tar }

    private static BackupKind Detect(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 2 && head[0] == 0x1F && head[1] == 0x8B)
        {
            return BackupKind.GzipTar;
        }

        if (head.Length >= 262 && head.Slice(257, 5).SequenceEqual("ustar"u8))
        {
            return BackupKind.Tar;
        }

        // Skip a UTF-8 BOM and whitespace, then expect '<'.
        var i = head.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
        while (i < head.Length && head[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') i++;
        return i < head.Length && head[i] == (byte)'<' ? BackupKind.Xml : BackupKind.Unknown;
    }

    private static Stream FindInTar(Stream tarStream, Stream owner)
    {
        var reader = new TarReader(tarStream, leaveOpen: false);
        try
        {
            TarEntry? entry;
            while ((entry = reader.GetNextEntry()) is not null)
            {
                var name = entry.Name.Replace('\\', '/').TrimEnd('/');
                var fileName = name[(name.LastIndexOf('/') + 1)..];
                if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile
                    && fileName.Equals(OneDbFileName, StringComparison.OrdinalIgnoreCase)
                    && entry.DataStream is not null)
                {
                    return new OwningStream(entry.DataStream, reader, owner);
                }
            }
        }
        catch
        {
            reader.Dispose();
            owner.Dispose();
            throw;
        }

        reader.Dispose();
        owner.Dispose();
        throw new InvalidDataException("The backup does not contain onedb.xml.");
    }

    /// <summary>Read-only stream: a buffered prefix followed by the rest of another stream.</summary>
    private sealed class PrefixedStream(ReadOnlyMemory<byte> prefix, Stream rest) : ReadOnlyStream
    {
        private ReadOnlyMemory<byte> _prefix = prefix;

        public override int Read(Span<byte> buffer)
        {
            if (!_prefix.IsEmpty)
            {
                var n = Math.Min(buffer.Length, _prefix.Length);
                _prefix.Span[..n].CopyTo(buffer);
                _prefix = _prefix[n..];
                return n;
            }
            return rest.Read(buffer);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) rest.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Wraps the onedb.xml entry and disposes the tar reader and source when closed.</summary>
    private sealed class OwningStream(Stream inner, IDisposable reader, Stream owner) : ReadOnlyStream
    {
        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                reader.Dispose();
                owner.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private abstract class ReadOnlyStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public abstract override int Read(Span<byte> buffer);
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
