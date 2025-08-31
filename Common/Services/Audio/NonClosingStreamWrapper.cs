using System;
using System.IO;

namespace Common.Services.Audio;

internal sealed class NonClosingStreamWrapper : Stream
{
    private readonly Stream _innerStream;

    public NonClosingStreamWrapper(Stream innerStream)
    {
        _innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
    }

    public override bool CanRead => _innerStream.CanRead;
    public override bool CanSeek => _innerStream.CanSeek;
    public override bool CanWrite => _innerStream.CanWrite;
    public override long Length => _innerStream.Length;

    public override long Position
    {
        get => _innerStream.Position;
        set => _innerStream.Position = value;
    }

    public override void Flush()
    {
        _innerStream.Flush();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return _innerStream.Read(buffer, offset, count);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        return _innerStream.Seek(offset, origin);
    }

    public override void SetLength(long value)
    {
        _innerStream.SetLength(value);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _innerStream.Write(buffer, offset, count);
    }

    protected override void Dispose(bool disposing)
    {
        // 不关闭底层流，仅尽量刷新
        if (disposing)
        {
            try
            {
                _innerStream.Flush();
            }
            catch
            {
                // 忽略刷新异常
            }
        }
        base.Dispose(false);
    }

    public override void Close()
    {
        // 忽略关闭，保持底层流可用
        try
        {
            _innerStream.Flush();
        }
        catch
        {
            // 忽略刷新异常
        }
    }
}


