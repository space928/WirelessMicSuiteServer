using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Net.Sockets;
using System.Net;
using System.Text;

namespace WirelessMicSuiteServer;

public class SennheiserSSCManager : IWirelessMicReceiverManager
{
    private const int UdpPort = 45;
    private const int MaxUDPSize = 0x10000;
    private const int ReceiverDisconnectTimeout = 5000;

    private readonly Socket socket;
    private readonly Task rxTask;
    private readonly Task txTask;
    private readonly Timer discoveryTask;
    private readonly MDNSClient mDNSClient;
    private readonly Decoder decoder;
    private readonly Encoder encoder;
    private readonly byte[] buffer;
    private readonly char[] charBuffer;
    private readonly CancellationToken cancellationToken;
    private readonly CancellationTokenSource cancellationTokenSource;
    private readonly ConcurrentDictionary<uint, SennheiserSSCReceiver> receiversDict;
    private readonly Dictionary<uint, SennheiserSSCWirelessMic> micsDict;
    private readonly ConcurrentDictionary<IPAddress, uint> receiverUIDs;
    private readonly ConcurrentQueue<ByteMessage> txPipe;
    private readonly SemaphoreSlim txAvailableSem;
    private readonly List<uint> toRemove;

    internal Encoder Encoder => encoder;

    public int PollingPeriodMS { get; set; } = 100;
    public ObservableCollection<IWirelessMicReceiver> Receivers { get; init; }

    public SennheiserSSCManager(string? preferredNicAddress)
    {
        Receivers = [];
        receiversDict = [];
        micsDict = [];
        receiverUIDs = [];
        cancellationTokenSource = new();
        cancellationToken = cancellationTokenSource.Token;
        txPipe = new();
        txAvailableSem = new(0);
        toRemove = [];

        Encoding encoding = Encoding.ASCII;
        buffer = new byte[MaxUDPSize];
        charBuffer = new char[encoding.GetMaxCharCount(buffer.Length)];
        decoder = encoding.GetDecoder();
        encoder = encoding.GetEncoder();

        Log($"Starting Sennheiser SSC server...");

        socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        //socket.ReceiveTimeout = 1000;
        //socket.Bind(new IPEndPoint(IPAddress.Any, UdpPort));
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        rxTask = Task.Run(RXTask);
        txTask = Task.Run(TXTask);
        mDNSClient = new MDNSClient(preferredNicAddress);
        mDNSClient.OnMDNSMessage += OnMDNSMessage;
        discoveryTask = new Timer(_ =>
        {
            try
            {
                mDNSClient.SendQuery(new DNSRecordMessage("_ssc._udp.local"));
                DisconnectStaleDevices();
            }
            catch (Exception ex)
            {
                Log(ex.Message, LogSeverity.Warning);
            }
        }, null, 0, 1000);
    }

    private readonly ILogger logger = Program.LoggerFac.CreateLogger<SennheiserSSCManager>();
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

    private static uint ComputeReceiverUID(ulong sennheiserID)
    {
        var typeHash = typeof(SennheiserSSCReceiver).GUID.GetHashCode();
        return CombineHash(unchecked((uint)sennheiserID.GetHashCode()), unchecked((uint)typeHash));
    }

    private void OnMDNSMessage(MDNSMessage obj)
    {
        ulong id = 0;
        IPAddress? address = null;
        string? model = null;
        foreach (var answer in obj.answers)
        {
            if (answer.record == null)
                continue;
            if (answer.type == DNSRecordType.TXT)
            {
                var txt = (DNS_TXT)answer.record;
                foreach (var t in txt.txtData)
                {
                    if (t.StartsWith("id="))
                    {
                        id = ulong.Parse(t.AsSpan(3), System.Globalization.NumberStyles.HexNumber);
                    } else if (t.StartsWith("model="))
                    {
                        model = t[6..];
                    }
                }
            } else if (answer.type == DNSRecordType.A)
            {
                var a = (DNS_A)answer.record;
                address = a.address;
            }
        }

        if (address != null && model != null)
        {
            if (receiverUIDs.TryGetValue(address, out uint uid))
            {
                if (receiversDict.TryGetValue(uid, out var rec))
                    rec.LastPingTime = DateTime.UtcNow;
            }
            else
            {
                uid = ComputeReceiverUID(id);
                receiverUIDs.TryAdd(address, uid);
                var ep = new IPEndPoint(address, UdpPort);
                var receiver = new SennheiserSSCReceiver(this, ep, uid, id, model);
                receiversDict.TryAdd(uid, receiver);
                Receivers.Add(receiver);
                Log($"[Discovery] Found Sennheiser Receiver @ {ep} UID=0x{uid:X}", LogSeverity.Info);
            }
        }
    }

    private void RXTask()
    {
        EndPoint ep = new IPEndPoint(IPAddress.Any, UdpPort);
        while (!cancellationToken.IsCancellationRequested)
        {
            ((IPEndPoint)ep).Address = IPAddress.Any;
            ((IPEndPoint)ep).Port = UdpPort;
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

            if (!receiverUIDs.TryGetValue(((IPEndPoint)ep).Address, out uint uid))
            {
                Log($"Received message from receiver which hasn't been discovered yet: {((IPEndPoint)ep).Address}", LogSeverity.Warning);
                continue;
            }

            int charsRead = decoder.GetChars(buffer, 0, read, charBuffer, 0);
            Memory<char> str = charBuffer.AsMemory()[..charsRead];
            Log($"Received: '{str}'", LogSeverity.Debug);

            if (receiversDict.TryGetValue(uid, out var rec))
            {
                try
                {
                    rec.Receive(str);
                }
                catch (Exception ex)
                {
                    Log($"Exception while reading message '{str}'", LogSeverity.Warning);
                    Log(ex.ToString(), LogSeverity.Warning);
                }
            }
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
            }
            catch (Exception ex)
            {
                Log($"Error while sending message: {ex.Message}!", LogSeverity.Warning);
            }
            /*var msg = await txPipe.Reader.ReadAsync(cancellationToken);
            foreach (var part in msg.Buffer)
                await socket.SendAsync(part);*/
        }
    }

    private void DisconnectStaleDevices()
    {
        foreach (var rec in receiversDict.Values)
        {
            var dt = DateTime.UtcNow - rec.LastPingTime;
            if (dt > TimeSpan.FromMilliseconds(ReceiverDisconnectTimeout))
                toRemove.Add(rec.UID);
        }
        if (toRemove.Count > 0)
        {
            foreach (var rem in toRemove)
            {
                if (receiversDict.TryRemove(rem, out var rec))
                {
                    receiverUIDs.TryRemove(rec.Address.Address, out _);
                    Receivers.Remove(rec);
                    rec.Dispose();
                    Log($"[Discovery] Receiver 0x{rec.UID:X} has not pinged in {ReceiverDisconnectTimeout} ms, marking as disconnected!");
                }
                //receiversDict.TryRemove(rem, out _);
            }
            toRemove.Clear();
        }
    }

    internal void SendMessage(ByteMessage message)
    {
        if (message.endPoint.Address == IPAddress.Any)
            throw new ArgumentException($"Specified message endpoint is IPAddress.Any, this is probably not intentional...");
        txPipe.Enqueue(message);
        txAvailableSem.Release();
    }

    internal void RegisterWirelessMic(SennheiserSSCWirelessMic mic)
    {
        micsDict.Add(mic.UID, mic);
    }

    internal void UnregisterWirelessMic(SennheiserSSCWirelessMic mic)
    {
        micsDict.Remove(mic.UID);
    }

    public void Dispose()
    {
        cancellationTokenSource.Cancel();
        //Task.WaitAll([txTask, rxTask], 1000);
        mDNSClient.Dispose();
        rxTask.Dispose();
        txTask.Dispose();
        //discoveryTask.Wait(1000);
        //discoveryTask.Dispose();
        socket.Dispose();
        txAvailableSem.Dispose();
        cancellationTokenSource.Dispose();
    }
}
