using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Nightcall;

// Preserve the existing authenticated application framing over a direct DTLS channel.
public sealed class DirectStream : Stream
{
    private readonly Channel<byte[]> input = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(512) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly Action<byte[]> send;
    private readonly Action close;
    private byte[] current = Array.Empty<byte>();
    private int offset, disposed;
    public DirectStream(Action<byte[]> send, Action close) { this.send = send; this.close = close; }
    public bool Feed(byte[] packet)
    {
        if (packet.Length is < 1 or > 8192 || !input.Writer.TryWrite(packet)) { Dispose(); return false; }
        return true;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default)
    {
        if (buffer.Length == 0) return 0;
        while (offset == current.Length)
        {
            try { current = await input.Reader.ReadAsync(cancel); offset = 0; }
            catch (ChannelClosedException) { return 0; }
        }
        int count = Math.Min(buffer.Length, current.Length - offset); current.AsMemory(offset, count).CopyTo(buffer); offset += count; return count;
    }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancel = default)
    {
        cancel.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(disposed != 0, this);
        if (buffer.Length is < 1 or > 8192) throw new InvalidDataException("Invalid direct write size.");
        send(buffer.ToArray()); return ValueTask.CompletedTask;
    }
    protected override void Dispose(bool disposing) { if (Interlocked.Exchange(ref disposed, 1) == 0) { input.Writer.TryComplete(); close(); } base.Dispose(disposing); }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();
    public override void Flush() { }
    public override bool CanRead => disposed == 0;
    public override bool CanWrite => disposed == 0;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long length) => throw new NotSupportedException();
}
