using System.Buffers.Binary;
using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace WirelessMicSuiteServer;

public class MDNSClient : IDisposable
{
    private const int UdpPort = 5353;
    private const int MaxUDPSize = 0x2000;
    private readonly IPAddress broadcastAddress = IPAddress.Parse("224.0.0.251");

    private readonly Socket socket;
    private readonly Task rxTask;
    private readonly Task txTask;
    private readonly Decoder decoder;
    private readonly Encoder encoder;
    private readonly byte[] buffer;
    private readonly CancellationToken cancellationToken;
    private readonly CancellationTokenSource cancellationTokenSource;
    private readonly ConcurrentQueue<ByteMessage> txPipe;
    private readonly ConcurrentDictionary<ushort, DateTime> activeQueries;
    private readonly SemaphoreSlim txAvailableSem;

    public event Action<MDNSMessage>? OnMDNSMessage;

    public MDNSClient(string? preferredIp = null)
    {
        cancellationTokenSource = new();
        cancellationToken = cancellationTokenSource.Token;
        txPipe = new();
        txAvailableSem = new(0);
        activeQueries = [];

        Encoding encoding = Encoding.UTF8;
        buffer = new byte[MaxUDPSize];
        decoder = encoding.GetDecoder();
        encoder = encoding.GetEncoder();

        Log($"Starting MDNS discovery server...");

        socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        //socket.ReceiveTimeout = 1000;
        Log("List network interfaces: ");
        List<IPAddress> addresses = [];
        IPAddress? addr = null;
        IPAddress? prefferedIpAddr = null;
        if (!string.IsNullOrEmpty(preferredIp))
            if (!IPAddress.TryParse(preferredIp, out prefferedIpAddr))
                Log($"Couldn't parse '{preferredIp}' as an IP address!", LogSeverity.Warning);
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            var ipProps = nic.GetIPProperties();
            var nicAddr = ipProps.UnicastAddresses.Select(x => x.Address);
            addresses.AddRange(nicAddr);
            if (prefferedIpAddr != null && nicAddr.Contains(prefferedIpAddr))
            {
                addr = prefferedIpAddr;
                break;
            }
            if (nicAddr.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork) is IPAddress naddr
                && ipProps.GatewayAddresses.Count > 0)
                addr ??= naddr;
            Log($"\t{nic.Name}: {string.Join(", ", nicAddr)}");
        }
        //socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        socket.Bind(new IPEndPoint(addr ?? IPAddress.Any, 0));
        //socket.SendTo([0, 0, 0, 0], new IPEndPoint(broadcastAddress, UdpPort));
        Log($"mDNS client bound to {socket.LocalEndPoint}.");
        rxTask = Task.Run(RXTask);
        txTask = Task.Run(TXTask);
    }

    private readonly ILogger logger = Program.LoggerFac.CreateLogger<MDNSClient>();
    public void Log(string? message, LogSeverity severity = LogSeverity.Info)
    {
        logger.Log(message, severity);
    }

    public void Dispose()
    {
        cancellationTokenSource.Cancel();
        //Task.WaitAll([rxTask, txTask], 1000);
        rxTask.Dispose();
        txTask.Dispose();
        socket.Dispose();
        txAvailableSem.Dispose();
        cancellationTokenSource.Dispose();
    }

    private void RXTask()
    {
        EndPoint ep = new IPEndPoint(broadcastAddress, UdpPort);
        while (!cancellationToken.IsCancellationRequested)
        {
            ((IPEndPoint)ep).Address = broadcastAddress;
            ((IPEndPoint)ep).Port = UdpPort;
            int read;
            try
            {
                //read = socket.ReceiveFrom(buffer, ref ep);
                read = socket.Receive(buffer);
            }
            catch (Exception ex)
            {
                Log($"Exception while reading from UDP port: {ex}", LogSeverity.Warning);
                continue;
            }

            if (read < MDNSMessageHeader.HEADER_LENGTH)
            {
                Log($"mDNS message was too short, expected at least {MDNSMessageHeader.HEADER_LENGTH} bytes, read {read} bytes!");
                continue;
            }

            var msg = ParseMDNSMessage(read);
            if (msg != null)
            {
                OnMDNSMessage?.Invoke(msg.Value);
            }
        }
    }

    private MDNSMessage? ParseMDNSMessage(int read)
    {
        var buffSpan = buffer.AsSpan(0, read);
        var header = new MDNSMessageHeader(buffSpan);
        int bytesRead = MDNSMessageHeader.HEADER_LENGTH;

        if (!activeQueries.TryRemove(header.transactionID, out _))
            return null;

        DNSRecordMessage[] questions = new DNSRecordMessage[header.questionCount];
        DNSRecordMessage[] answers = new DNSRecordMessage[header.answerCount];
        DNSRecordMessage[] authorities = new DNSRecordMessage[header.authorityCount];
        DNSRecordMessage[] additionalMessages = new DNSRecordMessage[header.additionalCount];
        for (int i = 0; i < header.questionCount; i++)
        {
            var msg = new DNSRecordMessage(buffSpan, bytesRead, decoder);
            questions[i] = msg;
            bytesRead += msg.length;
        }
        for (int i = 0; i < header.answerCount; i++)
        {
            var msg = new DNSRecordMessage(buffSpan, bytesRead, decoder);
            answers[i] = msg;
            bytesRead += msg.length;
        }
        // Parse authority messages
        // Parse additional messages

        return new (header, questions, answers, authorities, additionalMessages);
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
        }
    }

    public void SendQuery(DNSRecordMessage query, ushort? transactionID = null)
    {
        transactionID ??= (ushort)(Random.Shared.Next() & ushort.MaxValue);
        if (!activeQueries.TryAdd(transactionID.Value, DateTime.UtcNow))
        {
            throw new ArgumentException($"Transaction ID {transactionID.Value} is still active and can't be reused.", nameof(transactionID));
        }
        if (activeQueries.Count > 32)
        {
            var now = DateTime.UtcNow;
            var timeout = TimeSpan.FromSeconds(10);
            foreach (var kvp in activeQueries)
                if (now - kvp.Value > timeout)
                    activeQueries.TryRemove(kvp.Key, out _);
        }    

        var header = new MDNSMessageHeader(transactionID.Value, MDNSFlags.None, 1, 0, 0, 0);
        int len = MDNSMessageHeader.HEADER_LENGTH + query.length;
        ByteMessage bmsg = new (new IPEndPoint(broadcastAddress, UdpPort), len);
        Span<byte> buffer = bmsg.Buffer.AsSpan();
        header.Write(buffer);
        buffer = buffer[MDNSMessageHeader.HEADER_LENGTH..];
        query.Write(buffer, encoder);

        txPipe.Enqueue(bmsg);
        txAvailableSem.Release();
    }
}

[Flags]
public enum MDNSFlags : ushort
{
    None = 0,
    IsResponse = 1 << 15,
    Op_Query = 0,
    Op_IQuery = 1 << 11,
    Op_Status = 2 << 11,
    Op_Notify = 4 << 11,
    Op_Update = 5 << 11,
    Op_StatefulOp = 6 << 11,
    Authoritative = 1 << 10,
    Truncated = 1 << 9,
    RecursionDesired = 1 << 8,
    RecursionAvailable = 1 << 7,
    Z = 1 << 6,
    AnswerAuthenticated = 1 << 5,
    AcceptNonAuthenticated = 1 << 4,
    ReplyCode_NoError = 0 << 0,
    ReplyCode_FormatError = 1 << 0,
    ReplyCode_ServeFail = 2 << 0,
    ReplyCode_NonExistantDomain = 3 << 0,
    ReplyCode_NotImplemented = 4 << 0,
    ReplyCode_Refused = 5 << 0,
    ReplyCode_YXDomain = 6 << 0,
    ReplyCode_YXRRSet = 7 << 0,
    ReplyCode_NXRRSet = 8 << 0,
    ReplyCode_NotAuth = 9 << 0,
    ReplyCode_NotZone = 10 << 0,
    ReplyCode_DSOTYPENI = 11 << 0,
}

public readonly struct MDNSMessageHeader
{
    public const int HEADER_LENGTH = 12;

    public readonly ushort transactionID;
    public readonly MDNSFlags flags;
    public readonly ushort questionCount;
    public readonly ushort answerCount;
    public readonly ushort authorityCount;
    public readonly ushort additionalCount;

    public readonly byte OpCode => (byte)(((ushort)flags) >> 1);
    public readonly byte ReplyCode => (byte)(((ushort)flags) >> 1);

    public MDNSMessageHeader(ReadOnlySpan<byte> data)
    {
        transactionID = BinaryPrimitives.ReadUInt16BigEndian(data[0..]);
        flags = (MDNSFlags)BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        questionCount = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
        answerCount = BinaryPrimitives.ReadUInt16BigEndian(data[6..]);
        authorityCount = BinaryPrimitives.ReadUInt16BigEndian(data[8..]);
        additionalCount = BinaryPrimitives.ReadUInt16BigEndian(data[10..]);
    }

    public MDNSMessageHeader(ushort transactionID, MDNSFlags flags, ushort questionCount, ushort answerCount, ushort authorityCount, ushort additionalCount)
    {
        this.transactionID = transactionID;
        this.flags = flags;
        this.questionCount = questionCount;
        this.answerCount = answerCount;
        this.authorityCount = authorityCount;
        this.additionalCount = additionalCount;
    }

    public void Write(Span<byte> dst)
    {
        BinaryPrimitives.WriteUInt16BigEndian(dst[0..], transactionID);
        BinaryPrimitives.WriteUInt16BigEndian(dst[2..], (ushort)flags);
        BinaryPrimitives.WriteUInt16BigEndian(dst[4..], questionCount);
        BinaryPrimitives.WriteUInt16BigEndian(dst[6..], answerCount);
        BinaryPrimitives.WriteUInt16BigEndian(dst[8..], authorityCount);
        BinaryPrimitives.WriteUInt16BigEndian(dst[10..], additionalCount);
    }
}

public enum DNSRecordType : ushort
{
    A = 1,
    AAAA = 28,
    AFSDB = 18,
    APL = 42,
    CAA = 257,
    CDNSKEY = 60,
    CDS = 59,
    CERT = 37,
    CNAME = 5,
    CSYNC = 62,
    DHCID = 49,
    DLV = 32769,
    DNAME = 39,
    DNSKEY = 48,
    DS = 43,
    EUI48 = 108,
    EUI64 = 109,
    HINFO = 13,
    HIP = 55,
    HTTPS = 65,
    IPSECKEY = 45,
    KEY = 25,
    KX = 36,
    LOC = 29,
    MX = 15,
    NAPTR = 35,
    NS = 2,
    NSEC = 47,
    NSEC3 = 50,
    NSEC3PARAM = 51,
    OPENPGPKEY = 61,
    PTR = 12,
    RP = 17,
    RRSIG = 46,
    SIG = 24,
    SMIMEA = 53,
    SOA = 6,
    SRV = 33,
    SSHFP = 44,
    SVCB = 64,
    TA = 32768,
    TKEY = 249,
    TLSA = 52,
    TSIG = 250,
    TXT = 16,
    URI = 256,
    ZONEMD = 63
}

public enum DNSClassCode : ushort
{
    Reserved = 0,
    Internet = 1,
    Unassigned = 2,
    Chaos = 3,
    Hesiod = 4,
    QClassNone = 254,
    QClassAny = 255,
}

/// <summary>
/// Represents a binary encoded DNS record. 
/// RFC1035.
/// </summary>
public readonly struct DNSRecordMessage
{
    public readonly string name;
    public readonly DNSRecordType type;
    public readonly DNSClassCode classCode;
    public readonly bool question;
    public readonly int? timeToLive;
    public readonly ushort? recordSize;
    public readonly IDNSRData? record;

    public readonly int length;

    // 1st name length prefix + name length ('.' become length prefixes) + null term + type + class
    private readonly int ComputeLength => 1 + name.Length + 1 + 2 + 2 + (question ? 0 : (4 + 2 + recordSize!.Value));

    /// <summary>
    /// Constructs a new DNS record message.
    /// </summary>
    /// <param name="name"></param>
    /// <param name="type"></param>
    /// <param name="classCode"></param>
    /// <param name="question"></param>
    public DNSRecordMessage(string name, DNSRecordType type = DNSRecordType.PTR, DNSClassCode classCode = DNSClassCode.Internet, bool question = true)
    {
        this.name = name;
        this.type = type;
        this.classCode = classCode;
        this.question = question;
        this.length = ComputeLength;
    }

    /// <summary>
    /// Parses a DNS record message from a byte span.
    /// </summary>
    /// <param name="data">The span containing the full DNS message including the header.</param>
    /// <param name="offset">The offset into the data span pointing to the start of the record to parse.</param>
    /// <param name="utf8Decoder">An instance of a UTF8 decoder.</param>
    public DNSRecordMessage(ReadOnlySpan<byte> data, int offset, Decoder? utf8Decoder = null)
    {
        utf8Decoder ??= Encoding.UTF8.GetDecoder();

        // Read the segmented name from the mdns message
        // Each segment is prefixed with a byte indicating the length
        // When reading we cast the bytes directly into chars and replace the length prefixes with '.'
        int dataPtr = offset;
        name = ParseDomainName(data, utf8Decoder, ref dataPtr);

        type = (DNSRecordType)BinaryPrimitives.ReadUInt16BigEndian(data[dataPtr..]); dataPtr += 2;
        ushort classTmp = BinaryPrimitives.ReadUInt16BigEndian(data[dataPtr..]); dataPtr += 2;
        classCode = (DNSClassCode)(classTmp & 0x7fff);
        question = (classTmp & 0x8000) != 0;

        length = dataPtr - offset;
        if (question)
            return;

        timeToLive = BinaryPrimitives.ReadInt32BigEndian(data[dataPtr..]); dataPtr += 4;
        recordSize = BinaryPrimitives.ReadUInt16BigEndian(data[dataPtr..]); dataPtr += 2;
        length = dataPtr + recordSize.Value - offset;
        record = IDNSRData.Parse(type, data, dataPtr, dataPtr + recordSize.Value, utf8Decoder);
    }

    /// <summary>
    /// Parse a segmented domain name (including any pointers within it).
    /// </summary>
    /// <param name="data">The span containing the FULL DNS message.</param>
    /// <param name="utf8Decoder">An instance of a UTF8 decoder.</param>
    /// <param name="dataPtr">The pointer to the start of the data within the message.</param>
    /// <returns>A new instance of a string containing the parsed domain name.</returns>
    internal static string ParseDomainName(ReadOnlySpan<byte> data, Decoder utf8Decoder, ref int dataPtr)
    {
        // Read the segmented name from the dns message
        // Each segment is prefixed with a byte indicating the length
        // When reading we cast the bytes directly into chars and replace the length prefixes with '.'
        Span<char> charBuff = stackalloc char[256];
        int charPtr = 0;
        ParseDomainName(data, utf8Decoder, charBuff, ref dataPtr, ref charPtr);
        return new string(charBuff[..(charPtr - 1)]);
    }

    internal static string ParseCharacterString(ReadOnlySpan<byte> data, Decoder utf8Decoder, ref int dataPtr)
    {
        Span<char> charBuff = stackalloc char[256];
        byte len = data[dataPtr++];
        utf8Decoder.Convert(data[dataPtr..(dataPtr + len)], charBuff, true, out int read, out int written, out _);
        dataPtr += len;
        return new string(charBuff[..written]);
    }

    /// <summary>
    /// Parse a segmented domain name (including any pointers within it).
    /// </summary>
    /// <param name="data">The span containing the FULL DNS message.</param>
    /// <param name="utf8Decoder">An instance of a UTF8 decoder.</param>
    /// <param name="charBuff">The char buffer to write the parsed domain name into.</param>
    /// <param name="dataPtr">The pointer to the start of the data within the message.</param>
    /// <param name="charPtr">The pointer into the char buffer.</param>
    private static void ParseDomainName(ReadOnlySpan<byte> data, Decoder utf8Decoder, Span<char> charBuff, ref int dataPtr, ref int charPtr)
    {
        while (true)
        {
            byte len = data[dataPtr++];
            if ((len & 0xc0) == 0xc0)
            {
                // If the top 2 bits of the length are set then parse it as a pointer to another name.
                int ptr = ((len & ~0xc0) << 8) | data[dataPtr++];
                ParseDomainName(data, utf8Decoder, charBuff, ref ptr, ref charPtr);
                break;
            }
            if (len == 0)
                break;
            utf8Decoder.Convert(data[dataPtr..(dataPtr+len)], charBuff[charPtr..], true, out int read, out int written, out _);
            dataPtr += read;
            charPtr += written;
            charBuff[charPtr++] = '.';
        }
    }

    /// <summary>
    /// Writes a DNS record message to a byte span.
    /// </summary>
    /// <param name="dst">The span to write into.</param>
    /// <param name="utf8Encoder">An instance of a UTF8 encoder.</param>
    public void Write(Span<byte> dst, Encoder? utf8Encoder = null)
    {
        utf8Encoder ??= Encoding.UTF8.GetEncoder();

        int writePtr = 0;
        int startPtr = 0;
        for (int i = 0; i < name.Length; i++)
        {
            if (name[i] == '.')
            {
                byte len = (byte)(i - startPtr);
                dst[writePtr++] = len;
                utf8Encoder.Convert(name.AsSpan()[startPtr..i], dst[writePtr..], true, out int _, out int written, out _);
                writePtr += written;
                //for (int c = startPtr; c < len + startPtr; c++)
                //{
                //    dst[writePtr++] = (byte)name[c];
                //}
                //name[startPtr..i].CopyTo(dst[writePtr..])
                startPtr = i + 1;
            }
        }
        byte len1 = (byte)(name.Length - startPtr);
        dst[writePtr++] = len1;
        utf8Encoder.Convert(name.AsSpan()[startPtr..], dst[writePtr..], true, out int _, out int written1, out _);
        writePtr += written1;
        //for (int c = startPtr; c < len1 + startPtr; c++)
        //{
        //    dst[writePtr++] = (byte)name[c];
        //}
        dst[writePtr++] = 0;

        BinaryPrimitives.WriteUInt16BigEndian(dst[writePtr..], (ushort)type); writePtr += 2;
        BinaryPrimitives.WriteUInt16BigEndian(dst[writePtr..], (ushort)((int)classCode | (question ? 0x8000 : 0)));

        if (question)
            return;

        BinaryPrimitives.WriteInt32BigEndian(dst[writePtr..], timeToLive!.Value); writePtr += 4;
        BinaryPrimitives.WriteUInt16BigEndian(dst[writePtr..], recordSize!.Value); writePtr += 2;
    }
}

#region DNS RDATA
public interface IDNSRData
{
    public abstract int Length { get; }

    public static IDNSRData Parse(DNSRecordType type, ReadOnlySpan<byte> data, int offset, int end, Decoder? utf8Decoder)
    {
        return type switch
        {
            DNSRecordType.CNAME => new DNS_CNAME(data, offset, utf8Decoder),
            DNSRecordType.HINFO => new DNS_HINFO(data, offset, utf8Decoder),
            DNSRecordType.MX => new DNS_MX(data, offset, utf8Decoder),
            DNSRecordType.NS => new DNS_NS(data, offset, utf8Decoder),
            DNSRecordType.PTR => new DNS_PTR(data, offset, utf8Decoder),
            DNSRecordType.SOA => new DNS_SOA(data, offset, utf8Decoder),
            DNSRecordType.TXT => new DNS_TXT(data, offset, end, utf8Decoder),
            DNSRecordType.A => new DNS_A(data, offset, utf8Decoder),
            DNSRecordType.AAAA => new DNS_AAAA(data, offset, utf8Decoder),
            DNSRecordType.SRV => new DNS_SRV(data, offset, utf8Decoder),
            _ => new DNS_NULL(data, offset, end, utf8Decoder),
        };
    }
}

public readonly struct DNS_CNAME : IDNSRData
{
    public readonly string name;

    public int Length { get; init; }

    public DNS_CNAME(ReadOnlySpan<byte> data, int offset, Decoder? utf8Decoder)
    {
        utf8Decoder ??= Encoding.UTF8.GetDecoder();
        int dataPtr = offset;
        name = DNSRecordMessage.ParseDomainName(data, utf8Decoder, ref dataPtr);
        Length = dataPtr;
    }

    public override string ToString()
    {
        return $"DNS_CNAME {{\n\t{name}\n}}";
    }
}

public readonly struct DNS_HINFO : IDNSRData
{
    public readonly string cpu;
    public readonly string os;

    public int Length { get; init; }

    public DNS_HINFO(ReadOnlySpan<byte> data, int offset, Decoder? utf8Decoder)
    {
        utf8Decoder ??= Encoding.UTF8.GetDecoder();
        int dataPtr = offset;
        cpu = DNSRecordMessage.ParseCharacterString(data, utf8Decoder, ref dataPtr);
        os = DNSRecordMessage.ParseCharacterString(data, utf8Decoder, ref dataPtr);
        Length = dataPtr;
    }

    public override string ToString()
    {
        return $"DNS_HINFO {{\n\tcpu={cpu}\n\tos={os}\n}}";
    }
}

public readonly struct DNS_MB : IDNSRData
{
    public readonly string madname;

    public int Length { get; init; }

    public DNS_MB(ReadOnlySpan<byte> data, int offset, Decoder? utf8Decoder)
    {
        utf8Decoder ??= Encoding.UTF8.GetDecoder();
        int dataPtr = offset;
        madname = DNSRecordMessage.ParseDomainName(data, utf8Decoder, ref dataPtr);
        Length = dataPtr;
    }
}

public readonly struct DNS_MD : IDNSRData
{
    public readonly string madname;

    public int Length { get; init; }

    public DNS_MD(ReadOnlySpan<byte> data, int offset, Decoder? utf8Decoder)
    {
        utf8Decoder ??= Encoding.UTF8.GetDecoder();
        int dataPtr = 0;
        madname = DNSRecordMessage.ParseDomainName(data, utf8Decoder, ref dataPtr);
        Length = dataPtr;
    }
}

public readonly struct DNS_MF : IDNSRData
{
    public readonly string madname;

    public int Length { get; init; }

    public DNS_MF(ReadOnlySpan<byte> data, int offset, Decoder? utf8Decoder)
    {
        utf8Decoder ??= Encoding.UTF8.GetDecoder();
        int dataPtr = offset;
        madname = DNSRecordMessage.ParseDomainName(data, utf8Decoder, ref dataPtr);
        Length = dataPtr;
    }
}

public readonly struct DNS_MG : IDNSRData
{
    public readonly string mgmname;

    public int Length { get; init; }

    public DNS_MG(ReadOnlySpan<byte> data, int offset, Decoder? utf8Decoder)
    {
        utf8Decoder ??= Encoding.UTF8.GetDecoder();
        int dataPtr = offset;
        mgmname = DNSRecordMessage.ParseDomainName(data, utf8Decoder, ref dataPtr);
        Length = dataPtr;
    }
}

public readonly struct DNS_MINFO : IDNSRData
{
    public readonly string rmailBx;
    public readonly string emailBx;

    public int Length { get; init; }

    public DNS_MINFO(ReadOnlySpan<byte> data, int offset, Decoder? utf8Decoder)
    {
        utf8Decoder ??= Encoding.UTF8.GetDecoder();
        int dataPtr = offset;
        rmailBx = DNSRecordMessage.ParseDomainName(data, utf8Decoder, ref dataPtr);
        emailBx = DNSRecordMessage.ParseDomainName(data, utf8Decoder, ref dataPtr);
        Length = dataPtr;
    }
}

public readonly struct DNS_MR : IDNSRData
{
    public readonly string newname;

    public int Length { get; init; }

    public DNS_MR(ReadOnlySpan<byte> data, int offset, Decoder? utf8Decoder)
    {
        utf8Decoder ??= Encoding.UTF8.GetDecoder();
        int dataPtr = offset;
        newname = DNSRecordMessage.ParseDomainName(data, utf8Decoder, ref dataPtr);
        Length = dataPtr;
    }
}

public readonly struct DNS_MX : IDNSRData
{
    public readonly short preference;
    public readonly string exchange;

    public int Length { get; init; }

    public DNS_MX(ReadOnlySpan<byte> data, int offset, Decoder? utf8Decoder)
    {
        utf8Decoder ??= Encoding.UTF8.GetDecoder();
        int dataPtr = offset;
        preference = BinaryPrimitives.ReadInt16BigEndian(data[dataPtr..]); dataPtr += 2;
        exchange = DNSRecordMessage.ParseDomainName(data, utf8Decoder, ref dataPtr);
        Length = dataPtr;
    }

    public override string ToString()
    {
        return $"DNS_MX {{\n\tpreference={preference}\n\texchange={exchange}\n}}";
    }
}

public readonly struct DNS_NULL : IDNSRData
{
    public readonly byte[] data;

    public int Length { get; init; }

    public DNS_NULL(ReadOnlySpan<byte> data, int offset, int end, Decoder? utf8Decoder)
    {
        //utf8Decoder ??= Encoding.UTF8.GetDecoder();
        this.data = new byte[end - offset];
        data[offset..end].CopyTo(this.data);
        Length = this.data.Length;
    }

    public override string ToString()
    {
        return $"DNS_NULL {{\n\t{Convert.ToHexString(data)}\n}}";
    }
}

public readonly struct DNS_NS : IDNSRData
{
    public readonly string nsDName;

    public int Length { get; init; }

    public DNS_NS(ReadOnlySpan<byte> data, int offset, Decoder? utf8Decoder)
    {
        utf8Decoder ??= Encoding.UTF8.GetDecoder();
        int dataPtr = offset;
        nsDName = DNSRecordMessage.ParseDomainName(data, utf8Decoder, ref dataPtr);
        Length = dataPtr;
    }

    public override string ToString()
    {
        return $"DNS_NS {{\n\tnsDName={nsDName}\n}}";
    }
}

public readonly struct DNS_PTR : IDNSRData
{
    public readonly string ptrDName;

    public int Length { get; init; }

    public DNS_PTR(ReadOnlySpan<byte> data, int offset, Decoder? utf8Decoder)
    {
        utf8Decoder ??= Encoding.UTF8.GetDecoder();
        int dataPtr = offset;
        ptrDName = DNSRecordMessage.ParseDomainName(data, utf8Decoder, ref dataPtr);
        Length = dataPtr;
    }

    public override string ToString()
    {
        return $"DNS_PTR {{\n\tptrDName={ptrDName}\n}}";
    }
}

public readonly struct DNS_SOA : IDNSRData
{
    public readonly string mName;
    public readonly string rName;
    public readonly uint serial;
    public readonly int referesh;
    public readonly int retry;
    public readonly int expire;
    public readonly uint minimum;

    public int Length { get; init; }

    public DNS_SOA(ReadOnlySpan<byte> data, int offset, Decoder? utf8Decoder)
    {
        utf8Decoder ??= Encoding.UTF8.GetDecoder();
        int dataPtr = offset;
        mName = DNSRecordMessage.ParseDomainName(data, utf8Decoder, ref dataPtr);
        rName = DNSRecordMessage.ParseDomainName(data, utf8Decoder, ref dataPtr);
        serial = BinaryPrimitives.ReadUInt32BigEndian(data[dataPtr..]); dataPtr += 4;
        referesh = BinaryPrimitives.ReadInt32BigEndian(data[dataPtr..]); dataPtr += 4;
        retry = BinaryPrimitives.ReadInt32BigEndian(data[dataPtr..]); dataPtr += 4;
        expire = BinaryPrimitives.ReadInt32BigEndian(data[dataPtr..]); dataPtr += 4;
        minimum = BinaryPrimitives.ReadUInt32BigEndian(data[dataPtr..]); dataPtr += 4;
        Length = dataPtr;
    }
}

public readonly struct DNS_TXT : IDNSRData
{
    public readonly string[] txtData;

    public int Length { get; init; }

    public DNS_TXT(ReadOnlySpan<byte> data, int offset, int end, Decoder? utf8Decoder)
    {
        utf8Decoder ??= Encoding.UTF8.GetDecoder();
        int dataPtr = offset;
        List<string> strings = [];
        while (dataPtr < end)
        {
            strings.Add(DNSRecordMessage.ParseCharacterString(data, utf8Decoder, ref dataPtr));
        }
        txtData = [.. strings];
        Length = end - offset;
    }

    public override string ToString()
    {
        return $"DNS_TXT {{\n\t{string.Join("\n\t", txtData)}\n}}";
    }
}

public readonly struct DNS_A : IDNSRData
{
    public readonly IPAddress address;

    public int Length { get; init; }

    public DNS_A(ReadOnlySpan<byte> data, int offset, Decoder? utf8Decoder)
    {
        //utf8Decoder ??= Encoding.UTF8.GetDecoder();
        int dataPtr = offset;
        address = new IPAddress(data[dataPtr..(dataPtr+4)]);dataPtr += 4;
        Length = dataPtr;
    }

    public override string ToString()
    {
        return $"DNS_A {{\n\t{address}\n}}";
    }
}

public readonly struct DNS_AAAA : IDNSRData
{
    public readonly IPAddress address;

    public int Length { get; init; }

    public DNS_AAAA(ReadOnlySpan<byte> data, int offset, Decoder? utf8Decoder)
    {
        //utf8Decoder ??= Encoding.UTF8.GetDecoder();
        int dataPtr = offset;
        address = new IPAddress(data[dataPtr..(dataPtr+16)]); dataPtr += 16;
        Length = dataPtr;
    }

    public override string ToString()
    {
        return $"DNS_AAAA {{\n\t{address}\n}}";
    }
}

public readonly struct DNS_WKS : IDNSRData
{
    public readonly IPAddress address;
    public readonly byte protocol;
    public readonly BitArray bitMap;

    public int Length { get; init; }

    public DNS_WKS(ReadOnlySpan<byte> data, int offset, int end, Decoder? utf8Decoder)
    {
        //utf8Decoder ??= Encoding.UTF8.GetDecoder();
        int dataPtr = offset;
        address = new IPAddress(data[dataPtr..(dataPtr+4)]); dataPtr += 4;
        protocol = data[dataPtr++];
        var bytes = new byte[end - dataPtr];
        data[dataPtr..end].CopyTo(bytes);
        bitMap = new BitArray(bytes);
        Length = dataPtr;
    }
}

public readonly struct DNS_SRV : IDNSRData
{
    public readonly ushort priority;
    public readonly ushort weight;
    public readonly ushort port;
    public readonly string target;

    public int Length { get; init; }

    public DNS_SRV(ReadOnlySpan<byte> data, int offset, Decoder? utf8Decoder)
    {
        utf8Decoder ??= Encoding.UTF8.GetDecoder();
        int dataPtr = offset;
        priority = BinaryPrimitives.ReadUInt16BigEndian(data[dataPtr..]); dataPtr += 2;
        weight = BinaryPrimitives.ReadUInt16BigEndian(data[dataPtr..]); dataPtr += 2;
        port = BinaryPrimitives.ReadUInt16BigEndian(data[dataPtr..]); dataPtr += 2;
        target = DNSRecordMessage.ParseDomainName(data, utf8Decoder, ref dataPtr);
        Length = dataPtr;
    }

    public override string ToString()
    {
        return $"DNS_SRV {{\n\tpriority={priority}\n\tweight={weight}\n\tport={port}\n\ttarget={target}\n}}";
    }
}
#endregion

/// <summary>
/// Represents a full mDNS message.
/// </summary>
/// <param name="header"></param>
/// <param name="questions"></param>
/// <param name="answers"></param>
/// <param name="authorities"></param>
/// <param name="additionalRecords"></param>
public struct MDNSMessage(MDNSMessageHeader header, 
    DNSRecordMessage[] questions, DNSRecordMessage[] answers, 
    DNSRecordMessage[] authorities, DNSRecordMessage[] additionalRecords)
{
    public MDNSMessageHeader header = header;
    public readonly DNSRecordMessage[] questions = questions;
    public readonly DNSRecordMessage[] answers = answers;
    public readonly DNSRecordMessage[] authorities = authorities;
    public readonly DNSRecordMessage[] additionalRecords = additionalRecords;
}
