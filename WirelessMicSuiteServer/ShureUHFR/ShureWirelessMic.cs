using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WirelessMicSuiteServer;

public class ShureWirelessMic : IWirelessMic
{
    private readonly ShureUHFRReceiver receiver;
    private readonly int receiverNo;
    private readonly ConcurrentQueue<MeteringData> meterData;
    private MeteringData? lastMeterData;

    private readonly uint uid;
    private string? name;
    private int? gain;
    private int? outputGain;
    private bool? mute;
    private ulong? frequency;
    private int? group;
    private int? channel;
    private string? transmitterType;
    private float? batteryLevel;

    public IWirelessMicReceiver Receiver => receiver;
    public ConcurrentQueue<MeteringData> MeterData => meterData;
    public MeteringData? LastMeterData => lastMeterData;

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
    public int? Gain 
    { 
        get => gain; 
        set 
        {
            if (value != null && value >= 0 && value <= 32)
                SetAsync("TX_IR_GAIN", value.Value.ToString()); 
        } 
    }
    public int? OutputGain
    {
        get => outputGain;
        set
        {
            if (value != null && value >= 0 && value <= 32)
                SetAsync("AUDIO_GAIN", value.Value.ToString());
        }
    }
    public bool? Mute
    { 
        get => mute;
        set 
        {
            if (value != null)
                SetAsync("MUTE", value.Value ? "ON" : "OFF");
        }
    }
    public ulong? Frequency 
    { 
        get => frequency; 
        set 
        {
            if (value != null)
                SetAsync("FREQUENCY", (value.Value / 1000).ToString("000000"));
        } 
    }
    public int? Group 
    {
        get => group;
        set
        {
            if (value != null)
                SetAsync("GROUP_CHAN", $"{value:00} {channel:00}");
        }
    }
    public int? Channel 
    { 
        get => channel; 
        set
        {
            if (value != null)
                SetAsync("GROUP_CHAN", $"{group:00} {value:00}");
        }
    }
    public string? TransmitterType => transmitterType;
    public float? BatteryLevel => batteryLevel;

    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly ILogger logger = Program.LoggerFac.CreateLogger<ShureWirelessMic>();
    public void Log(string? message, LogSeverity severity = LogSeverity.Info)
    {
        logger.Log(message, severity);
    }

    public ShureWirelessMic(ShureUHFRReceiver receiver, uint uid, int receiverNo)
    {
        this.receiver = receiver;
        this.uid = uid;
        this.receiverNo = receiverNo;
        meterData = [];

        SendStartupCommands();
    }

    private void SendStartupCommands()
    {
        receiver.Send($"* METER {receiverNo} ALL 1 *");
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
            ParseSampleCommand(args, fullMsg);
            return;
        }
        else if (type != ShureCommandType.REPORT && type != ShureCommandType.NOTE)
        {
            CommandError(fullMsg, $"Unexpected command type '{type}'.");
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
                    CommandError(fullMsg, "Mute value wasn't 'ON' or 'OFF'.");
                break;
            case "AUDIO_GAIN":
                if (int.TryParse(args, out int nOutGain) && nOutGain is >= 0 and <= 32)
                {
                    outputGain = -nOutGain;
                    OnPropertyChanged(nameof(OutputGain));
                }
                else
                    CommandError(fullMsg, "Couldn't parse output gain, or gain was out of the range 0-32.");
                break;
            case "TX_GAIN":
            case "TX_IR_GAIN":
                if (int.TryParse(args, out int ngain) && ngain is >= -10 and <= 20)
                {
                    gain = ngain;
                    OnPropertyChanged(nameof(Gain));
                } 
                else if (args.SequenceEqual("UNKNOWN"))
                {
                    gain = null;
                    OnPropertyChanged(nameof(Gain));
                }
                else
                    CommandError(fullMsg, "Couldn't parse transmitter gain, or gain was out of the range -10:20.");
                break;
            case "SQUELCH":
                if (int.TryParse(args, out int nsquelch))
                {
                    //outputGain = -ngain;
                    //OnPropertyChanged(nameof(OutputGain));
                }
                else
                    CommandError(fullMsg, "Couldn't parse squelch.");
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
                    group = null;
                    channel = null;
                    OnPropertyChanged(nameof(Group));
                    OnPropertyChanged(nameof(Channel));
                }
                else
                    CommandError(fullMsg, "Couldn't parse group or channel arguments.");
                break;
            case "FREQUENCY":
                if (ulong.TryParse(args, out ulong freq))
                {
                    frequency = freq*1000;
                    OnPropertyChanged(nameof(Frequency));
                } else
                    CommandError(fullMsg, "Couldn't parse frequency as a ulong.");
                break;
            case "TX_BAT":
                if (args.Length < 1)
                {
                    CommandError(fullMsg, "Expected argument in TX_BAT command.");
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
            case "FRONT_PANEL_LOCK":
            case "TX_IR_LOCK":
            case "TX_IR_POWER":
            case "TX_IR_TRIM":
            case "TX_IR_BAT_TYPE":
            case "TX_IR_CUSTOM_GPS":
            case "AUDIO_INDICATOR":
            case "TX_BAT_MINS":
            case "TX_BAT_TYPE":
            case "TX_POWER":
            case "TX_CHANGE_BAT":
            case "TX_EXT_DC":
            case "TX_TRIM":
            case "TX_LOCK":
                // Unimplemented for now
                break;
            default:
                CommandError(fullMsg);
                break;
        }
    }

    private void ParseSampleCommand(ReadOnlySpan<char> args, ReadOnlySpan<char> fullMsg)
    {
        Span<Range> seps = stackalloc Range[16];
        int n = args.Split(seps, ' ');
        seps = seps[..n];

        float rssiA = -1;
        float rssiB = -1;
        float audio = -1;
        for (int i = 0; i < seps.Length; i++)
        {
            var word = args[seps[i]];
            switch (word)
            {
                case "RSSI":
                    if (int.TryParse(args[seps[++i]], out int rssiAInt)
                        && int.TryParse(args[seps[++i]], out int rssiBInt))
                    {
                        rssiA = 1 - (rssiAInt - 50) / 50f;
                        rssiB = 1 - (rssiBInt - 50) / 50f;
                    }
                    else
                    {
                        CommandError(fullMsg, "Couldn't parse RF strength.");
                        return;
                    }
                    break;
                case "AUDIO_INDICATOR":
                    if (int.TryParse(args[seps[++i]], out int audioInt))
                    {
                        audio = audioInt / 255f;
                    }
                    else
                    {
                        CommandError(fullMsg, "Couldn't parse audio level.");
                        return;
                    }
                    break;
                default:
                    break;
            }
        }

        if (meterData.Count > IWirelessMic.MAX_METER_SAMPLES)
        {
            // Purge the last 16 samples if the meter queue is getting full...
            for (int i = 0; i < 16; i++)
                meterData.TryDequeue(out _);
        }
        var meter = new MeteringData(rssiA, rssiB, audio);
        meterData.Enqueue(meter);
        lastMeterData = meter;
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
