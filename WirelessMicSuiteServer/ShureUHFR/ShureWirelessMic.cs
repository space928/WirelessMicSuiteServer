using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WirelessMicSuiteServer;

public class ShureWirelessMic : IWirelessMic
{
    private readonly ShureUHFRReceiver receiver;
    private readonly int receiverNo;
    private readonly ConcurrentQueue<MeteringData> meterData;
    private readonly ConcurrentQueue<(string cmd, string args)> scanCommands;
    private readonly SemaphoreSlim scanCommandsSem;
    private RFScanData rfScanData;
    private Task<RFScanData>? rfScanInProgress;
    private MeteringData? lastMeterData;

    private readonly uint uid;
    private string? name;
    private int? gain;
    private int? sensitivity;
    private int? outputGain;
    private bool? mute;
    private ulong? frequency;
    private int? group;
    private int? channel;
    private LockMode? lockMode;
    private string? transmitterType;
    private float? batteryLevel;

    public IWirelessMicReceiver Receiver => receiver;
    public ConcurrentQueue<MeteringData> MeterData => meterData;
    public MeteringData? LastMeterData => lastMeterData;
    public RFScanData RFScanData => rfScanData;

    public uint UID => uid;
    public string? Name 
    { 
        get => name; 
        set 
        {
            if (value != null && value.Length < 12)
                SetAsync("CHAN_NAME", value.Replace(' ', '_')); 
        }
    }
    public int? Gain 
    { 
        get => gain; 
        set 
        {
            if (value != null && value >= -10 && value <= 20)
                SetAsync("TX_IR_GAIN", Math.Abs(value.Value+10).ToString()); 
        } 
    }
    public int? Sensitivity
    {
        get => sensitivity;
        set
        {
            if (value != null && value >= -10 && value <= 15)
                SetAsync("TX_IR_TRIM", value.Value.ToString());
        }
    }
    public int? OutputGain
    {
        get => outputGain;
        set
        {
            if (value != null && value >= -32 && value <= 0)
                SetAsync("AUDIO_GAIN", Math.Abs(value.Value).ToString());
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
    public LockMode? LockMode
    {
        get => lockMode;
        set
        {
            if (value != null)
            {
                switch (value)
                {
                    case WirelessMicSuiteServer.LockMode.None:
                        SetAsync("TX_IR_LOCK", "UNLOCK");
                        break;
                    case WirelessMicSuiteServer.LockMode.Power:
                        SetAsync("TX_IR_LOCK", "POWER");
                        break;
                    case WirelessMicSuiteServer.LockMode.Frequency:
                        SetAsync("TX_IR_LOCK", "FREQ");
                        break;
                    case WirelessMicSuiteServer.LockMode.FrequencyPower:
                        SetAsync("TX_IR_LOCK", "FREQ_AND_POWER");
                        break;
                    default:
                        break;
                }
            }
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
    private void Log(string? message, LogSeverity severity = LogSeverity.Info)
    {
        logger.Log(message, severity);
    }

    public ShureWirelessMic(ShureUHFRReceiver receiver, uint uid, int receiverNo)
    {
        this.receiver = receiver;
        this.uid = uid;
        this.receiverNo = receiverNo;
        meterData = [];
        rfScanInProgress = null;
        scanCommands = [];
        scanCommandsSem = new(1);

        SendStartupCommands();
    }

    private void SendStartupCommands()
    {
        receiver.Send($"* METER {receiverNo} ALL {receiver.Manager.PollingPeriodMS / 30} *");
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
        receiver.Send($"* GET {receiverNo} TX_GAIN *");
        receiver.Send($"* GET {receiverNo} TX_TRIM *");
        receiver.Send($"* GET {receiverNo} TX_LOCK *");
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
        else if (type == ShureCommandType.SCAN)
        {
            scanCommands.Enqueue((cmd.ToString(), args.ToString()));
            scanCommandsSem.Release();
            return;
        }
        else if(type == ShureCommandType.RFLEVEL)
        {
            ParseRFLevelCommand(cmd, args, fullMsg);
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
                name = args.ToString().Replace('_', ' ');
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
                if (int.TryParse(args, out int ngain) && ngain is >= 0 and <= 30)
                {
                    gain = ngain - 10;
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
            case "TX_IR_TRIM":
            case "TX_TRIM":
                if (int.TryParse(args, out int ntrim) && ntrim is >= -10 and <= 15)
                {
                    sensitivity = ntrim;
                    OnPropertyChanged(nameof(Sensitivity));
                }
                else if (args.SequenceEqual("UNKNOWN"))
                {
                    sensitivity = null;
                    OnPropertyChanged(nameof(Sensitivity));
                }
                else
                    CommandError(fullMsg, "Couldn't parse transmitter gain, or gain was out of the range -10:20.");
                break;
            case "SQUELCH":
                if (int.TryParse(args, out int nsquelch))
                {
                    // In the range 0-20 -> -10-10
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
            case "TX_IR_LOCK":
            case "TX_LOCK":
                switch(args)
                {
                    case "UNLOCK":
                        lockMode = WirelessMicSuiteServer.LockMode.None;
                        OnPropertyChanged(nameof(LockMode));
                        break;
                    case "POWER":
                        lockMode = WirelessMicSuiteServer.LockMode.Power;
                        OnPropertyChanged(nameof(LockMode));
                        break;
                    case "FREQ":
                        lockMode = WirelessMicSuiteServer.LockMode.Frequency;
                        OnPropertyChanged(nameof(LockMode));
                        break;
                    case "FREQ_AND_POWER":
                        lockMode = WirelessMicSuiteServer.LockMode.FrequencyPower;
                        OnPropertyChanged(nameof(LockMode));
                        break;
                    case "NOCHANGE":
                    default:
                        break;
                }
                break;
            case "FRONT_PANEL_LOCK":
            case "TX_IR_POWER":
            case "TX_IR_BAT_TYPE":
            case "TX_IR_CUSTOM_GPS":
            case "AUDIO_INDICATOR":
            case "TX_BAT_MINS":
            case "TX_BAT_TYPE":
            case "TX_POWER":
            case "TX_CHANGE_BAT":
            case "TX_EXT_DC":
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
        DiversityIndicator diversity = DiversityIndicator.None;
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
                case "ANTENNA":
                    switch (args[seps[++i]])
                    {
                        case "NONE":
                            diversity = DiversityIndicator.None;
                            break;
                        case "A":
                            diversity = DiversityIndicator.A;
                            break;
                        case "B":
                            diversity = DiversityIndicator.B;
                            break;
                        case "BOTH":
                            diversity = DiversityIndicator.A | DiversityIndicator.B;
                            break;
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
        var meter = new MeteringData(rssiA, rssiB, audio, diversity);
        meterData.Enqueue(meter);
        lastMeterData = meter;
    }

    private void ParseRFLevelCommand(ReadOnlySpan<char> nargs, ReadOnlySpan<char> args, ReadOnlySpan<char> fullMsg)
    {
        if (rfScanInProgress == null)
            return;

        // "* RFLEVEL n 10 578000 100 578025 100 578050 100 578075 100 578100 100 578125 100 578150 100 578175 100 578200 100 578225 100 *"
        //  * RFLEVEL n numSamples [freq level]... *
        // level: is in - dBm
        Span<Range> splits = stackalloc Range[32];
        int nSplits = args.Split(splits, ' ');
        splits = splits[..nSplits];

        if (!byte.TryParse(nargs, out byte n))
        {
            CommandError(fullMsg, "Couldn't parse number of RF level samples.");
            return;
        }
        if (nSplits != n * 2)
        {
            CommandError(fullMsg, "Unexpected number of RF level samples.");
            return;
        }

        for (int i = 0; i < n*2; i+=2)
        {
            if (!uint.TryParse(args[splits[i]], out uint freq) || !uint.TryParse(args[splits[i+1]], out uint rf))
            {
                CommandError(fullMsg, $"Couldn't parse RF level sample {i/2}.");
                return;
            }
            rfScanData.Samples.Add(new(freq*1000, -(float)rf));
        }

        rfScanData.Progress = rfScanData.Samples.Count / (float)((rfScanData.FrequencyRange.EndFrequency - rfScanData.FrequencyRange.StartFrequency) / rfScanData.StepSize + 1);
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

    public Task<RFScanData> StartRFScan(FrequencyRange range, ulong stepSize)
    {
        if (rfScanInProgress != null && !rfScanInProgress.IsCompleted)
            return rfScanInProgress;

        var startTime = DateTime.UtcNow;
        var timeout = TimeSpan.FromSeconds(180);
        return rfScanInProgress = Task.Run(() =>
        {
            rfScanData.State = RFScanData.Status.Running;
            rfScanData.Progress = 0;
            rfScanData.FrequencyRange = range;
            rfScanData.StepSize = stepSize;
            rfScanData.Samples = [];

            receiver.Send($"* METER {receiverNo} ALL 400 *");
            receiver.Send($"* SCAN RESERVE {receiverNo} xyz *");
            // Wait for "* SCAN RESERVED n xyz *"
            // Wait for "* SCAN RESERVE n ACK xyz *"
            while (DateTime.UtcNow - startTime < timeout)
            {
                (string cmd, string args) cmd;
                while (!scanCommands.TryDequeue(out cmd))
                    scanCommandsSem.Wait(200);
                if (cmd.cmd == "RESERVE" && cmd.args == "ACK xyz")
                    break;
            }
            // Default step size is 25KHz
            receiver.Send($"* SCAN RANGE {receiverNo} {stepSize/1000} {range.StartFrequency/1000} {range.EndFrequency/1000} *");
            // Wait for "* SCAN STARTED n *"
            // Wait for "* RFLEVEL n 10 578000 100 578025 100 578050 100 578075 100 578100 100 578125 100 578150 100 578175 100 578200 100 578225 100 *"
            //           * RFLEVEL n numSamples [freq level]... *
            // level: is in - dBm
            // Wait for "* SCAN DONE n *"
            while (DateTime.UtcNow - startTime < timeout)
            {
                (string cmd, string args) cmd;
                while (!scanCommands.TryDequeue(out cmd))
                    scanCommandsSem.Wait(200);
                if (cmd.cmd == "DONE")
                    break;
            }
            receiver.Send($"* SCAN RELEASE {receiverNo} *");
            
            // Wait for "* SCAN RELEASED n *"
            // Wait for "* SCAN IDLE n *"
            while (DateTime.UtcNow - startTime < timeout)
            {
                (string cmd, string args) cmd;
                while (!scanCommands.TryDequeue(out cmd))
                {
                    receiver.Send($"* SCAN RELEASE {receiverNo} *");
                    scanCommandsSem.Wait(200);
                }
                if (cmd.cmd == "RELEASED")
                    break;
            }
            scanCommands.Clear();
            for (int i = 0; i < scanCommandsSem.CurrentCount; i++)
                scanCommandsSem.Wait(1);
            // Start metering again
            SendStartupCommands();

            rfScanData.State = RFScanData.Status.Completed;

            return rfScanData;
        });
    }
}
