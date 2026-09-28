using WSGM.Core;

namespace WSGM.Tests.Core;

/// <summary>Reading a third party's body: bounded in size, and in how long it may send nothing.</summary>
public sealed class BoundedHttpTests
{
    [Fact]
    public async Task ABodyThatStopsSendingEndsAsAnIoFailure()
    {
        using var content = new StreamContent(new StalledStream());
        using var output = new MemoryStream();

        var failure = await Assert.ThrowsAsync<IOException>(() => BoundedHttp.CopyAsync(content, output, 1024,
            () => new InvalidOperationException("too large"), CancellationToken.None, TimeSpan.FromMilliseconds(100)));

        Assert.Contains("sent nothing", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABodyLargerThanTheBoundIsRefused()
    {
        using var content = new ByteArrayContent(new byte[16]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => BoundedHttp.ReadAsync(content, 8,
            () => new InvalidOperationException("too large"), CancellationToken.None));
    }

    /// <summary>A body whose first read never completes until it is cancelled.</summary>
    private sealed class StalledStream : Stream
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

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}
