using System.Buffers.Binary;
using System.ComponentModel;
using System.Data;
using System.Net;
using System.Runtime.CompilerServices;

namespace WirelessMicSuiteServer;

public class ShureUHFRReceiver : IWirelessMicReceiver
{
    private readonly ShureUHFRManager manager;
    private readonly ShureWirelessMic[] mics;
    private readonly uint uid;
    private readonly uint snetID;

    public IPEndPoint Address { get; init; }
    public DateTime LastPingTime { get; set; }
    public int NumberOfChannels => mics.Length;
    public IWirelessMic[] WirelessMics => mics;
    public uint UID => uid;

    public ShureUHFRReceiver(ShureUHFRManager manager, IPEndPoint address, uint uid, uint snetID)
    {
        LastPingTime = DateTime.UtcNow;
        this.manager = manager;
        this.uid = uid;
        this.snetID = snetID;
        Address = new(address.Address, address.Port);
        mics = [
            new ShureWirelessMic(this, unchecked((uint)HashCode.Combine(uid, 1u)), 1), 
            new ShureWirelessMic(this, unchecked((uint)HashCode.Combine(uid, 2u)), 2)
        ];

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

    public void Dispose() { }

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
        else
        {
            Log($"Unknown command type '{msg.ToString().Split(' ')[0]}'", LogSeverity.Warning);
            return;
        }

        // This is followed by the note number
        int note = -1;
        if (type == ShureCommandType.NOTE)
        {
            int noteEnd = msg[1..].IndexOf(' ');
            if (!int.TryParse(msg[1..noteEnd], out note))
            {
                Log($"Error parsing note numebr in '{fullMsg}'", LogSeverity.Warning);
                return;
            }
            msg = msg[(noteEnd + 1)..];
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

    private void ParseCommand(ShureCommandType type, ReadOnlySpan<char> cmd, ReadOnlySpan<char> args, ReadOnlySpan<char> fullMsg)
    {

    }

    private void CommandError(ReadOnlySpan<char> str)
    {
        Log($"Unsupported command '{str}'", LogSeverity.Warning);
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
    NOTED
}

public class ShureWirelessMic : IWirelessMic
{
    private readonly ShureUHFRReceiver receiver;
    private readonly int receiverNo;

    private uint uid;
    private string? name;
    private int gain;
    private bool mute;
    private ulong frequency;
    private int group;
    private int channel;
    private string? transmitterType;
    private float batteryLevel;

    public IWirelessMicReceiver Receiver => receiver;

    public uint UID => uid;
    public string? Name 
    { 
        get => name; 
        set 
        {
            if (value != null && value.Length < 12)
                SetAsync("CHAN_NAME", value); 
        } 
    }
    public int Gain 
    { 
        get => gain; 
        set 
        {
            if (value >= 0 && value <= 32)
                SetAsync("GAIN", value.ToString("00")); 
        } 
    }
    public bool Mute
    { 
        get => mute;
        set 
        {
            SetAsync("MUTE", value ? "ON" : "OFF");
        }
    }
    public ulong Frequency 
    { 
        get => frequency; 
        set 
        {
            SetAsync("FREQUENCY", (value / 1000).ToString("000000"));
        } 
    }
    public int Group 
    {
        get => group;
        set
        {
            SetAsync("GROUP_CHAN", $"{value:00} {channel:00}");
        }
    }
    public int Channel 
    { 
        get => channel; 
        set
        {
            SetAsync("GROUP_CHAN", $"{group:00} {value:00}");
        }
    }
    public string? TransmitterType => transmitterType;
    public float BatteryLevel => batteryLevel;

    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly ILogger logger = Program.LoggerFac.CreateLogger<ShureUHFRReceiver>();
    public void Log(string? message, LogSeverity severity = LogSeverity.Info)
    {
        logger.Log(message, severity);
    }

    public ShureWirelessMic(ShureUHFRReceiver receiver, uint uid, int receiverNo)
    {
        this.receiver = receiver;
        this.uid = uid;
        this.receiverNo = receiverNo;

        SendStartupCommands();
    }

    private void SendStartupCommands()
    {
        receiver.Send($"* METER {receiverNo} ALL 500 *");
        receiver.Send($"* UPDATE {receiverNo} ADD *");

        receiver.Send($"* GET {receiverNo} CHAN_NAME *");
        receiver.Send($"* GET {receiverNo} FRONT_PANEL_LOCK *");
        receiver.Send($"* GET {receiverNo} AUDIO_GAIN *");
        receiver.Send($"* GET {receiverNo} MUTE *");
        receiver.Send($"* GET {receiverNo} SQUELCH *");
        receiver.Send($"* GET {receiverNo} GROUP_CHAN *");
        receiver.Send($"* GET {receiverNo} FREQUENCY *");
        receiver.Send($"* GET {receiverNo} TX_IR_LOCK *");
        receiver.Send($"* GET {receiverNo} TX_IR_GAIN *");
        receiver.Send($"* GET {receiverNo} TX_IR_POWER *");
        receiver.Send($"* GET {receiverNo} TX_IR_TRIM *");
        receiver.Send($"* GET {receiverNo} TX_IR_BAT_TYPE *");
        receiver.Send($"* GET {receiverNo} TX_IR_CUSTOM_GPS *");
        receiver.Send($"* GET {receiverNo} AUDIO_INDICATOR *");
        receiver.Send($"* GET {receiverNo} TX_TYPE *");
        receiver.Send($"* GET {receiverNo} TX_BAT *");
        receiver.Send($"* GET {receiverNo} TX_BAT_MINS *");
        receiver.Send($"* GET {receiverNo} TX_POWER *");
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void SetAsync(string cmd, string args)
    {
        receiver.Send($"* SET {receiverNo} {cmd} {args} *");
    }

    public void StartMetering(int periodMS)
    {
        throw new NotImplementedException();
    }

    public void StopMetering()
    {
        throw new NotImplementedException();
    }

    internal void ParseCommand(ShureCommandType type, ReadOnlySpan<char> cmd, ReadOnlySpan<char> args, ReadOnlySpan<char> fullMsg)
    {
        if (type == ShureCommandType.SAMPLE)
        {
            CommandError(fullMsg);
        }
        else if (type != ShureCommandType.REPORT && type != ShureCommandType.NOTE)
        {
            CommandError(fullMsg);
            return;
        }
        switch (cmd)
        {
            case "CHAN_NAME":
                name = args.ToString();
                OnPropertyChanged(nameof(Name));
                break;
            case "MUTE":
                if (args.SequenceEqual("ON"))
                {
                    mute = true;
                    OnPropertyChanged(nameof(Mute));
                }
                else if (args.SequenceEqual("OFF"))
                {
                    mute = false;
                    OnPropertyChanged(nameof(Mute));
                }
                else
                    CommandError(fullMsg);
                break;
            case "AUDIO_GAIN":
                if (int.TryParse(args, out int ngain) && ngain is >= 0 and <= 32)
                {
                    gain = ngain;
                    OnPropertyChanged(nameof(Gain));
                }
                else
                    CommandError(fullMsg);
                break;
            case "GROUP_CHAN":
                int sep = args.IndexOf(' ');
                if (sep != -1 && int.TryParse(args[..sep], out int g) && int.TryParse(args[(sep + 1)..], out int c))
                {
                    group = g;
                    channel = c;
                    OnPropertyChanged(nameof(Group));
                    OnPropertyChanged(nameof(Channel));
                } else if (args.SequenceEqual("-- --"))
                {
                    group = -1;
                    channel = -1;
                    OnPropertyChanged(nameof(Group));
                    OnPropertyChanged(nameof(Channel));
                }
                else
                    CommandError(fullMsg);
                break;
            case "FREQUENCY":
                if (ulong.TryParse(args, out ulong freq))
                {
                    frequency = freq*1000;
                    OnPropertyChanged(nameof(Frequency));
                } else
                    CommandError(fullMsg);
                break;
            case "TX_BAT":
                if (args.Length < 1)
                {
                    CommandError(fullMsg);
                    break;
                }
                if (args[0] == 'U')
                {
                    batteryLevel = 0;
                    OnPropertyChanged(nameof(BatteryLevel));
                }
                else
                {
                    int level = (byte)args[0] - (byte)'0';
                    if (level is >= 1 and <= 5)
                    {
                        batteryLevel = level/5f;
                        OnPropertyChanged(nameof(BatteryLevel));
                    } else
                    {
                        CommandError(fullMsg, "Battery level out of range 1-5.");
                    }
                }
                break;
            case "TX_TYPE":
                transmitterType = args.ToString();
                OnPropertyChanged(nameof(TransmitterType));
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
