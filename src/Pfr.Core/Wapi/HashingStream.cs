using System.Security.Cryptography;

namespace Pfr.Core.Wapi;

/// <summary>
/// Read-only pass-through stream that counts the bytes and hashes them (SHA-256) as they go by,
/// so a backup can be parsed straight off the network and still be identified like a file on disk.
/// </summary>
internal sealed class HashingStream(Stream inner) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private bool _finished;

    public long BytesRead { get; private set; }

    /// <summary>The SHA-256 of everything read so far, as lowercase hex. Call once, after the last read.</summary>
    public string FinishHex()
    {
        _finished = true;
        return Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
    }

    public override int Read(Span<byte> buffer)
    {
        var n = inner.Read(buffer);
        if (n > 0 && !_finished)
        {
            _hash.AppendData(buffer[..n]);
            BytesRead += n;
        }
        return n;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
            _hash.Dispose();
        }
        base.Dispose(disposing);
    }
}
