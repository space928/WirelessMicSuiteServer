using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;

namespace WirelessMicSuiteServer;

public class ShureUHFRReceiver : IWirelessMicReceiver
{
    private readonly ShureUHFRManager manager;
    private readonly ShureWirelessMic[] mics;
    private readonly uint uid;
    private readonly uint snetID;

    private string? modelName;
    private readonly string manufacturer = "Shure";
    private string? freqBand;
    private FrequencyRange[]? frequencyRanges;
    private string? firmwareVersion;
    private IPv4Address ipAddress;
    private IPv4Address? subnet;
    private IPv4Address? gateway;
    private IPMode? ipMode;
    private MACAddress? macAddress;

    public ShureUHFRManager Manager => manager;
    public IPEndPoint Address { get; init; }
    public DateTime LastPingTime { get; set; }
    public int NumberOfChannels => mics.Length;
    public IWirelessMic[] WirelessMics => mics;
    public uint UID => uid;

    public string? ModelName => modelName;
    public string? Manufacturer => manufacturer;
    public string? FreqBand => freqBand;
    public FrequencyRange[]? FrequencyRanges => frequencyRanges;
    public string? FirmwareVersion => firmwareVersion;
    public IPv4Address IPAddress
    {
        get => ipAddress;
        set
        {
            SetAsync("IP_ADDR", value.ToString());
        }
    }
    public IPv4Address? Subnet
    {
        get => subnet;
        set
        {
            if (value != null)
                SetAsync("SUBNET", value.Value.ToString());
        }
    }
    public IPv4Address? Gateway
    {
        get => gateway;
        set
        {
            if (value != null)
                SetAsync("GATEWAY", value.Value.ToString());
        }
    }
    public IPMode? IPMode
    {
        get => ipMode;
        set
        {
            if (value != null)
            {
                switch (value)
                {
                    case WirelessMicSuiteServer.IPMode.DHCP:
                        SetAsync("IP_MODE", "DHCP");
                        break;
                    case WirelessMicSuiteServer.IPMode.Manual:
                        SetAsync("IP_MODE", "Manual");
                        break;
                }
            }
        }
    }
    public MACAddress? MACAddres => macAddress;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ShureUHFRReceiver(ShureUHFRManager manager, IPEndPoint address, uint uid, uint snetID)
    {
        LastPingTime = DateTime.UtcNow;
        this.manager = manager;
        this.uid = uid;
        this.snetID = snetID;
        Address = new(address.Address, address.Port);
        mics = [
            new ShureWirelessMic(this, ShureUHFRManager.CombineHash(uid, 1u), 1), 
            new ShureWirelessMic(this, ShureUHFRManager.CombineHash(uid, 2u), 2)
        ];
        manager.RegisterWirelessMic(mics[0]);
        manager.RegisterWirelessMic(mics[1]);

        SendDiscoveryMsg();
        SendStartupMessages();
    }

    private readonly ILogger logger = Program.LoggerFac.CreateLogger<ShureUHFRReceiver>();
    public void Log(string? message, LogSeverity severity = LogSeverity.Info)
    {
        if (severity == LogSeverity.Warning || severity == LogSeverity.Error)
        { }
        logger.Log(message, severity);
    }

    public void Dispose() 
    {
        manager.UnregisterWirelessMic(mics[0]);
        manager.UnregisterWirelessMic(mics[1]);
    }

    private void SendStartupMessages()
    {
        Send("* GET MODEL_NAME *");
        Send("* GET FREQ_BAND *");
        Send("* GET BANDLIMITS *");
        Send("* GET SW_VERSION *");
        Send("* GET HARDWARE_ID *");
        Send("* GET MAC_ADDR *");
        Send("* GET IP_MODE *");
        Send("* GET CURRENT_IP_ADDR *");
        Send("* GET CURRENT_SUBNET *");
        Send("* GET CURRENT_GATEWAY *");
        Send("* GET IP_ADDR *");
        Send("* GET SUBNET *");
        Send("* GET GATEWAY *");
        Send("* GET CUSTOM_GROUP_C1 *");
        Send("* GET CUSTOM_GROUP_C2 *");
        Send("* GET CUSTOM_GROUP_C3 *");
        Send("* GET CUSTOM_GROUP_C4 *");
        Send("* GET CUSTOM_GROUP_C5 *");
        Send("* GET CUSTOM_GROUP_C6 *");
    }

    public void SendDiscoveryMsg()
    {
        var msg = new ByteMessage(Address, ShureSNetHeader.HEADER_SIZE + 8);
        var buff = msg.Buffer.AsSpan();

        var header = new ShureSNetHeader(0xffffffff, ShureUHFRManager.ManagerSnetID, ShureSNetHeader.SnetType.Discovery, 8);
        header.WriteToSpan(buff);

        BinaryPrimitives.WriteUInt16BigEndian(buff[16..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(buff[18..], 1);
        BinaryPrimitives.WriteUInt32BigEndian(buff[20..], ShureUHFRManager.ManagerSnetID);

        manager.SendMessage(msg);
    }

    public void Send(ReadOnlySpan<char> data)
    {
        int len = manager.Encoder.GetByteCount(data, true);
        var msg = new ByteMessage(Address, len + ShureSNetHeader.HEADER_SIZE);
        var buff = msg.Buffer;

        var header = new ShureSNetHeader(snetID, ShureUHFRManager.ManagerSnetID, ShureSNetHeader.SnetType.Message, (ushort)len);
        header.WriteToSpan(buff);

        manager.Encoder.GetBytes(data, buff.AsSpan()[ShureSNetHeader.HEADER_SIZE..], true);

        manager.SendMessage(msg);
    }

    public void Receive(ReadOnlySpan<char> msg, ShureSNetHeader header)
    {
        ReadOnlySpan<char> fullMsg = msg;
        // Commands should start with "* " and end with " *"
        if (msg.Length < 4 || !msg.StartsWith("* ") || !msg.EndsWith(" *"))
        {
            Log($"Incoming message did not start and end with the correct header! Received: '{fullMsg}'", LogSeverity.Warning);
            return;
        }
        // Trim
        msg = msg[2..^2];

        // Commands start with one of "REPORT" or "SAMPLE"
        ShureCommandType type;
        if (msg.StartsWith("REPORT"))
        {
            type = ShureCommandType.REPORT;
            msg = msg[6..];
        }
        else if (msg.StartsWith("SAMPLE"))
        {
            type = ShureCommandType.SAMPLE;
            msg = msg[6..];
        }
        else if (msg.StartsWith("NOTE"))
        {
            type = ShureCommandType.NOTE;
            msg = msg[4..];
        }
        else if (msg.StartsWith("UPDATE"))
        {
            // This is just an acknowledgement of the UPDATE command, we can safely ignore it...
            return;
        }
        else
        {
            Log($"Unknown command type '{msg.ToString().Split(' ')[0]}'", LogSeverity.Warning);
            return;
        }

        // This is followed by the note number
        int note = -1;
        if (type == ShureCommandType.NOTE)
        {
            int noteEnd = msg[1..].IndexOf(' ')+1;
            if (!int.TryParse(msg[1..noteEnd], out note))
            {
                Log($"Error parsing note number in '{fullMsg}'", LogSeverity.Warning);
                return;
            }
            msg = msg[noteEnd..];
        }

        // This is followed by 1 or 2 for the receiver number
        int receiver;
        if (msg.Length < 3)
        {
            Log($"Incomplete message, missing receiver number: '{fullMsg}'", LogSeverity.Warning);
            return;
        }
        if (msg[1] == '1')
        {
            receiver = 0;
            msg = msg[2..];
        }
        else if (msg[1] == '2')
        {
            receiver = 1;
            msg = msg[2..];
        }
        else
        {
            receiver = -1;
        }
        msg = msg[1..];

        // Next is the command itself
        int cmdEnd = msg.IndexOf(' ');
        var cmd = cmdEnd == -1 ? msg : msg[..cmdEnd];
        msg = msg[(cmdEnd+1)..];
        if (receiver == -1)
            ParseCommand(type, cmd, msg, fullMsg);
        else
            mics[receiver].ParseCommand(type, cmd, msg, fullMsg);

        if (type == ShureCommandType.NOTE)
        {
            // Reply to the note for some reason
            Send($"* NOTED {note} *");
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void SetAsync(string cmd, string args)
    {
        Send($"* SET {cmd} {args} *");
    }

    private void ParseCommand(ShureCommandType type, ReadOnlySpan<char> cmd, ReadOnlySpan<char> args, ReadOnlySpan<char> fullMsg)
    {
        if (type != ShureCommandType.REPORT && type != ShureCommandType.NOTE)
        {
            CommandError(fullMsg);
            return;
        }
        switch (cmd)
        {
            case "MODEL_NAME":
                modelName = args.ToString();
                OnPropertyChanged(nameof(ModelName));
                break;
            case "FREQ_BAND":
                freqBand = args.ToString();
                OnPropertyChanged(nameof(FreqBand));
                break;
            case "BANDLIMITS":
                {
                    Span<Range> seps = stackalloc Range[4];
                    int words = args.Split(seps, ' ');
                    if (words != 4)
                    {
                        CommandError(fullMsg, "Expected 4 args for band limits!");
                        return;
                    }
                    try
                    {
                        frequencyRanges = [
                            new FrequencyRange(ulong.Parse(args[seps[0]]), ulong.Parse(args[seps[1]])),
                            new FrequencyRange(ulong.Parse(args[seps[2]]), ulong.Parse(args[seps[3]]))
                        ];
                        OnPropertyChanged(nameof(FrequencyRanges));
                    }
                    catch 
                    {
                        CommandError(fullMsg, "Expected 4 uints for band limits!");
                        return;
                    }
                    break;
                }
            case "SW_VERSION":
                firmwareVersion = args.ToString();
                OnPropertyChanged(nameof(FirmwareVersion));
                break;
            case "MAC_ADDR":
                try
                {
                    macAddress = new MACAddress(args);
                    OnPropertyChanged(nameof(MACAddres));
                } catch
                {
                    CommandError(fullMsg, "Expected a MAC address in the form aa:bb:cc:dd:ee:ff!");
                    return;
                }
                break;
            case "IP_MODE":
                if (args.SequenceEqual("DHCP"))
                {
                    ipMode = WirelessMicSuiteServer.IPMode.DHCP;
                    OnPropertyChanged(nameof(IPMode));
                } else if (args.SequenceEqual("Manual"))
                {
                    ipMode = WirelessMicSuiteServer.IPMode.Manual;
                    OnPropertyChanged(nameof(IPMode));
                } else
                {
                    CommandError(fullMsg);
                    return;
                }
                break;
            case "CURRENT_IP_ADDR":
                try
                {
                    ipAddress = new IPv4Address(args);
                    OnPropertyChanged(nameof(IPAddress));
                }
                catch
                {
                    CommandError(fullMsg, "Expected an IP address in the form aaa.bbb.ccc.ddd!");
                    return;
                }
                break;
            case "CURRENT_SUBNET":
                try
                {
                    subnet = new IPv4Address(args);
                    OnPropertyChanged(nameof(Subnet));
                }
                catch
                {
                    CommandError(fullMsg, "Expected an IP address in the form aaa.bbb.ccc.ddd!");
                    return;
                }
                break;
            case "CURRENT_GATEWAY":
                try
                {
                    gateway = new IPv4Address(args);
                    OnPropertyChanged(nameof(Gateway));
                }
                catch
                {
                    CommandError(fullMsg, "Expected an IP address in the form aaa.bbb.ccc.ddd!");
                    return;
                }
                break;
            case "HARDWARE_ID":
            case "IP_ADDR":
            case "SUBNET":
            case "GATEWAY":
            case "CUSTOM_GROUP_C1":
            case "CUSTOM_GROUP_C2":
            case "CUSTOM_GROUP_C3":
            case "CUSTOM_GROUP_C4":
            case "CUSTOM_GROUP_C5":
            case "CUSTOM_GROUP_C6":
                break;
            default:
                CommandError(fullMsg);
                break;
        }
    }

    private void CommandError(ReadOnlySpan<char> str, string? details = null)
    {
        if (details != null)
        {
            Log($"Error while parsing command '{str}'. {details}", LogSeverity.Warning);
        }
        else
        {
            Log($"Error while parsing command '{str}'", LogSeverity.Warning);
        }
    }
}
