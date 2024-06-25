using System.Buffers;
using System.Net;

namespace WirelessMicSuiteServer;

public readonly struct ByteMessage : IDisposable
{
    public readonly IPEndPoint endPoint;
    public readonly ArraySegment<byte> Buffer => new(buffer, 0, length);
    private readonly byte[] buffer;
    private readonly int length;

    public ByteMessage(IPEndPoint endPoint, int size)
    {
        this.endPoint = endPoint;
        this.length = size;
        this.buffer = ArrayPool<byte>.Shared.Rent(size);
    }

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(buffer);
    }
}
