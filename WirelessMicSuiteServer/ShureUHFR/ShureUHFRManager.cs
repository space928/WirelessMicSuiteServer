﻿using Microsoft.Extensions.Logging;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace WirelessMicSuiteServer;

public class ShureUHFRManager : IWirelessMicReceiverManager
{
    private const int UdpPort = 2202;
    private const int UdpPortPrivate = 2201;
    private const int MaxUDPSize = 0x10000;
    internal const uint ManagerSnetID = 0x6A05ADAD;
    private const int ReceiverDisconnectTimeout = 5000;

    private readonly Socket socket;
    private readonly Task rxTask;
    private readonly Task txTask;
    private readonly Task pingTask;
    private readonly Decoder decoder;
    private readonly Encoder encoder;
    private readonly byte[] buffer;
    private readonly char[] charBuffer;
    private readonly CancellationToken cancellationToken;
    private readonly CancellationTokenSource cancellationTokenSource;
    private readonly Dictionary<uint, ShureUHFRReceiver> receiversDict;
    private readonly Dictionary<uint, ShureWirelessMic> micsDict;
    private readonly ConcurrentQueue<ByteMessage> txPipe;
    private readonly SemaphoreSlim txAvailableSem;

    internal Encoder Encoder => encoder;

    public int PollingPeriodMS { get; set; } = 100;
    public ObservableCollection<IWirelessMicReceiver> Receivers { get; init; }

    public ShureUHFRManager()
    {
        Receivers = [];
        receiversDict = [];
        micsDict = [];
        cancellationTokenSource = new();
        cancellationToken = cancellationTokenSource.Token;
        txPipe = new();
        txAvailableSem = new(0);

        Encoding encoding = Encoding.ASCII;
        buffer = new byte[MaxUDPSize];
        charBuffer = new char[encoding.GetMaxCharCount(buffer.Length)];
        decoder = encoding.GetDecoder();
        encoder = encoding.GetEncoder();

        Log($"Starting Shure UHF-R server...");

        socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        //socket.ReceiveTimeout = 1000;
        socket.Bind(new IPEndPoint(IPAddress.Any, UdpPortPrivate));
        rxTask = Task.Run(RXTask);
        txTask = Task.Run(TXTask);
        pingTask = Task.Run(PingTask);
    }

    private readonly ILogger logger = Program.LoggerFac.CreateLogger<ShureUHFRManager>();
    private void Log(string? message, LogSeverity severity = LogSeverity.Info)
    {
        logger.Log(message, severity);
    }

    public IWirelessMicReceiver? TryGetWirelessMicReceiver(uint uid)
    {
        if (receiversDict.TryGetValue(uid, out var receiver))
            return receiver;
        return null;
    }

    public IWirelessMic? TryGetWirelessMic(uint uid)
    {
        if (micsDict.TryGetValue(uid, out var mic))
            return mic;
        return null;
    }

    internal static uint CombineHash(uint a, uint b)
    {
        unchecked
        {
            uint hash = 17;
            hash = hash * 31 + a;
            hash = hash * 31 + b;
            return hash;
        }
    }

    private static uint ComputeReceiverUID(uint snetID)
    {
        var typeHash = typeof(ShureUHFRReceiver).GUID.GetHashCode();
        return CombineHash(snetID, unchecked((uint)typeHash));
    }

    private void RXTask()
    {
        EndPoint ep = new IPEndPoint(IPAddress.Any, UdpPortPrivate);
        while (!cancellationToken.IsCancellationRequested)
        {
            ((IPEndPoint)ep).Address = IPAddress.Any;
            ((IPEndPoint)ep).Port = UdpPortPrivate;
            int read;
            try
            {
                read = socket.ReceiveFrom(buffer, ref ep);
            }
            catch (Exception ex)
            {
                Log($"Exception while reading from UDP port: {ex}", LogSeverity.Warning);
                continue;
            }

            if (read < ShureSNetHeader.HEADER_SIZE)
            {
                Log($"Shure sNet message was too short, expected at least {ShureSNetHeader.HEADER_SIZE} bytes, read {read} bytes!");
                continue;
            }

            ShureSNetHeader header = new(buffer);
            uint uid = ComputeReceiverUID(header.snetIDFrom);

            if (header.type == ShureSNetHeader.SnetType.Discovery && header.snetIDTo == 0xffffffff)
            {
                var epIp = (IPEndPoint)ep;
                epIp.Port = UdpPortPrivate;

                lock (receiversDict)
                {
                    if (receiversDict.TryGetValue(uid, out var receiver))
                    {
                        receiver.LastPingTime = DateTime.UtcNow;
                    }
                    else
                    {
                        Log($"[Discovery] Found Shure Receiver @ {epIp} UID=0x{uid:X}", LogSeverity.Info);
                        receiver = new ShureUHFRReceiver(this, epIp, uid, header.snetIDFrom);
                        receiversDict.Add(uid, receiver);
                        Receivers.Add(receiver);
                    }
                }
                continue;
            } else if (header.type == ShureSNetHeader.SnetType.Special)
            {
                // string bmsg = string.Join(" ", buffer[ShureSNetHeader.HEADER_SIZE..read].Select(x => x.ToString("X2")));
                // Log($"Received special message: '{bmsg}'", LogSeverity.Info);
                continue;
            }

            int charsRead = decoder.GetChars(buffer, ShureSNetHeader.HEADER_SIZE, read-ShureSNetHeader.HEADER_SIZE, charBuffer, 0);
            Span<char> str = charBuffer.AsSpan()[..charsRead];
            Log($"Received: '{str}'", LogSeverity.Debug);

            if(receiversDict.TryGetValue(uid, out var rec))
                rec.Receive(str, header);
        }
    }

    private void TXTask()
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ByteMessage msg;
            while (!txPipe.TryDequeue(out msg) && !cancellationToken.IsCancellationRequested)
                txAvailableSem.Wait(1000);
            try
            {
                socket.SendTo(msg.Buffer, msg.endPoint);
                msg.Dispose();
            } catch (Exception ex)
            {
                Log($"Error while sending message: {ex.Message}!", LogSeverity.Warning);
            }
            /*var msg = await txPipe.Reader.ReadAsync(cancellationToken);
            foreach (var part in msg.Buffer)
                await socket.SendAsync(part);*/
        }
    }

    private void PingTask()
    {
        List<uint> toRemove = [];
        while (!cancellationToken.IsCancellationRequested)
        {
            lock (receiversDict)
            {
                foreach (var rec in receiversDict.Values)
                {
                    rec.SendDiscoveryMsg();
                    var dt = DateTime.UtcNow - rec.LastPingTime;
                    if (dt > TimeSpan.FromMilliseconds(ReceiverDisconnectTimeout))
                        toRemove.Add(rec.UID);
                }
                if (toRemove.Count > 0)
                {
                    foreach (var rem in toRemove)
                    {
                        if (receiversDict.TryGetValue(rem, out var rec))
                        {
                            Receivers.Remove(rec);
                            rec.Dispose();
                            Log($"[Discovery] Receiver 0x{rec.UID:X} has not pinged in {ReceiverDisconnectTimeout} ms, marking as disconnected!");
                        }
                        receiversDict.Remove(rem);
                    }
                    toRemove.Clear();
                }
            }
            Thread.Sleep(500);
        }
    }

    internal void SendMessage(ByteMessage message)
    {
        if (message.endPoint.Address == IPAddress.Any)
            throw new ArgumentException($"Specified message endpoint is IPAddress.Any, this is probably not intentional...");
        txPipe.Enqueue(message);
        txAvailableSem.Release();
    }

    internal void RegisterWirelessMic(ShureWirelessMic mic)
    {
        micsDict.Add(mic.UID, mic);
    }

    internal void UnregisterWirelessMic(ShureWirelessMic mic)
    {
        micsDict.Remove(mic.UID);
    }

    public void Dispose()
    {
        cancellationTokenSource.Cancel();
        //Task.WaitAll([txTask, rxTask, pingTask], 1000);
        pingTask.Dispose();
        rxTask.Dispose();
        txTask.Dispose();
        //discoveryTask.Wait(1000);
        //discoveryTask.Dispose();
        socket.Dispose();
        txAvailableSem.Dispose();
        cancellationTokenSource.Dispose();
    }
}

enum ShureCommandType
{
    GET,
    SET,
    METER,
    REPORT,
    SAMPLE,
    NOTE,
    NOTED,
    SCAN,
    RFLEVEL
}

public readonly record struct ShureSNetHeader
{
    public const int HEADER_SIZE = 16;

    public readonly uint snetIDTo;
    public readonly uint snetIDFrom;
    public readonly ushort _padding;
    public readonly SnetType type;
    public readonly ushort length;
    public readonly ushort checksum;

    public ShureSNetHeader(uint snetIDTo, uint snetIDFrom, SnetType type, ushort length)
    {
        this.snetIDTo = snetIDTo;
        this.snetIDFrom = snetIDFrom;
        this.type = type;
        this.length = length;
        checksum = 0;
        Span<byte> tmp = stackalloc byte[HEADER_SIZE];
        WriteToSpan(tmp);
        checksum = ComputeChecksum(tmp, 0, HEADER_SIZE - 2);
    }

    public ShureSNetHeader(uint snetIDTo, uint snetIDFrom, SnetType type, ushort length, ushort checksum)
    {
        this.snetIDTo = snetIDTo;
        this.snetIDFrom = snetIDFrom;
        this.type = type;
        this.length = length;
        this.checksum = checksum;
    }

    public ShureSNetHeader(ReadOnlySpan<byte> bytes)
    {
        snetIDTo = BinaryPrimitives.ReadUInt32BigEndian(bytes[0..4]);
        snetIDFrom = BinaryPrimitives.ReadUInt32BigEndian(bytes[4..8]);
        _padding = BinaryPrimitives.ReadUInt16BigEndian(bytes[8..10]);
        type = (SnetType)BinaryPrimitives.ReadUInt16BigEndian(bytes[10..12]);
        length = BinaryPrimitives.ReadUInt16BigEndian(bytes[12..14]);
        checksum = BinaryPrimitives.ReadUInt16BigEndian(bytes[14..16]);
    }

    /// <summary>
    /// CRC-16-CCITT lookup table
    /// </summary>
    static readonly ushort[] CHECKSUM_LUT = [0x0000, 0xc0c1, 0xc181, 0x0140, 0xc301, 0x03c0, 0x0280, 0xc241, 0xc601, 0x06c0, 0x0780, 0xc741, 0x0500, 0xc5c1, 0xc481, 0x0440, 0xcc01, 0x0cc0, 0x0d80, 0xcd41, 0x0f00, 0xcfc1, 0xce81, 0x0e40, 0x0a00, 0xcac1, 0xcb81, 0x0b40, 0xc901, 0x09c0, 0x0880, 0xc841, 0xd801, 0x18c0, 0x1980, 0xd941, 0x1b00, 0xdbc1, 0xda81, 0x1a40, 0x1e00, 0xdec1, 0xdf81, 0x1f40, 0xdd01, 0x1dc0, 0x1c80, 0xdc41, 0x1400, 0xd4c1, 0xd581, 0x1540, 0xd701, 0x17c0, 0x1680, 0xd641, 0xd201, 0x12c0, 0x1380, 0xd341, 0x1100, 0xd1c1, 0xd081, 0x1040, 0xf001, 0x30c0, 0x3180, 0xf141, 0x3300, 0xf3c1, 0xf281, 0x3240, 0x3600, 0xf6c1, 0xf781, 0x3740, 0xf501, 0x35c0, 0x3480, 0xf441, 0x3c00, 0xfcc1, 0xfd81, 0x3d40, 0xff01, 0x3fc0, 0x3e80, 0xfe41, 0xfa01, 0x3ac0, 0x3b80, 0xfb41, 0x3900, 0xf9c1, 0xf881, 0x3840, 0x2800, 0xe8c1, 0xe981, 0x2940, 0xeb01, 0x2bc0, 0x2a80, 0xea41, 0xee01, 0x2ec0, 0x2f80, 0xef41, 0x2d00, 0xedc1, 0xec81, 0x2c40, 0xe401, 0x24c0, 0x2580, 0xe541, 0x2700, 0xe7c1, 0xe681, 0x2640, 0x2200, 0xe2c1, 0xe381, 0x2340, 0xe101, 0x21c0, 0x2080, 0xe041, 0xa001, 0x60c0, 0x6180, 0xa141, 0x6300, 0xa3c1, 0xa281, 0x6240, 0x6600, 0xa6c1, 0xa781, 0x6740, 0xa501, 0x65c0, 0x6480, 0xa441, 0x6c00, 0xacc1, 0xad81, 0x6d40, 0xaf01, 0x6fc0, 0x6e80, 0xae41, 0xaa01, 0x6ac0, 0x6b80, 0xab41, 0x6900, 0xa9c1, 0xa881, 0x6840, 0x7800, 0xb8c1, 0xb981, 0x7940, 0xbb01, 0x7bc0, 0x7a80, 0xba41, 0xbe01, 0x7ec0, 0x7f80, 0xbf41, 0x7d00, 0xbdc1, 0xbc81, 0x7c40, 0xb401, 0x74c0, 0x7580, 0xb541, 0x7700, 0xb7c1, 0xb681, 0x7640, 0x7200, 0xb2c1, 0xb381, 0x7340, 0xb101, 0x71c0, 0x7080, 0xb041, 0x5000, 0x90c1, 0x9181, 0x5140, 0x9301, 0x53c0, 0x5280, 0x9241, 0x9601, 0x56c0, 0x5780, 0x9741, 0x5500, 0x95c1, 0x9481, 0x5440, 0x9c01, 0x5cc0, 0x5d80, 0x9d41, 0x5f00, 0x9fc1, 0x9e81, 0x5e40, 0x5a00, 0x9ac1, 0x9b81, 0x5b40, 0x9901, 0x59c0, 0x5880, 0x9841, 0x8801, 0x48c0, 0x4980, 0x8941, 0x4b00, 0x8bc1, 0x8a81, 0x4a40, 0x4e00, 0x8ec1, 0x8f81, 0x4f40, 0x8d01, 0x4dc0, 0x4c80, 0x8c41, 0x4400, 0x84c1, 0x8581, 0x4540, 0x8701, 0x47c0, 0x4680, 0x8641, 0x8201, 0x42c0, 0x4380, 0x8341, 0x4100, 0x81c1, 0x8081, 0x4040];
    /// <summary>
    /// CRC-16-CCITT
    /// </summary>
    /// <param name="data">The data to checksum.</param>
    /// <param name="prev">The initial value, should be 0.</param>
    /// <param name="len">How many bytes to checksum.</param>
    /// <returns></returns>
    public static ushort ComputeChecksum(ReadOnlySpan<byte> data, ushort prev, int len)
    {
        if (len == 0)
            return prev;
        ushort sum = (ushort)~prev;

        for (int i = 0; i < len; i++)
        {
            byte d = data[i];
            sum = (ushort)(CHECKSUM_LUT[(byte)((byte)sum ^ d)] ^ (ushort)(sum >> 8));
        }

        return (ushort)~sum;
    }

    public void WriteToSpan(Span<byte> dst)
    {
        if (dst.Length < HEADER_SIZE)
            throw new ArgumentException("Destination span is too short!");

        BinaryPrimitives.WriteUInt32BigEndian(dst[0..4], snetIDTo);
        BinaryPrimitives.WriteUInt32BigEndian(dst[4..8], snetIDFrom);
        BinaryPrimitives.WriteUInt16BigEndian(dst[8..10], _padding);
        BinaryPrimitives.WriteUInt16BigEndian(dst[10..12], (ushort)type);
        BinaryPrimitives.WriteUInt16BigEndian(dst[12..14], length);
        BinaryPrimitives.WriteUInt16BigEndian(dst[14..16], checksum);
    }

    public enum SnetType : ushort
    {
        Discovery = 1,
        Message = 3,
        Special = 4
    }
}
