using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace WirelessMicSuiteServer;

public class SennheiserSSCWirelessMic : IWirelessMic
{
    private readonly SennheiserSSCReceiver receiver;
    private readonly uint uid;
    private readonly int receiverNo;
    private readonly ConcurrentQueue<MeteringData> meterData;
    private readonly ConcurrentDictionary<ulong, (float sample, int n)> rfSamples;

    private string? name;
    private int? gain;
    private int? sensitivity;
    private int? outputGain;
    private bool rxMute;
    private bool? txMute;
    private bool? mute;
    private ulong? frequency;
    private int? group;
    private int? channel;
    private LockMode? lockMode;
    private string? transmitterType;
    private float? batteryLevel;

    private bool isTXConnected = false;
    private bool isCollectingRFSamples;
    private Task<RFScanData>? rfScanInProgress;
    private MeteringData? lastMeterData;
    private RFScanData rfScanData;

    public IWirelessMicReceiver Receiver => receiver;
    public uint UID => uid;

    public ConcurrentQueue<MeteringData> MeterData => meterData;
    public MeteringData? LastMeterData => lastMeterData;
    public RFScanData RFScanData => rfScanData;

    public string? Name
    {
        get => name;
        set
        {
            if (value != null)
            {
                var str = value.ToUpper().AsSpan();
                if (str.Length > 8)
                    str = str[..8];
                receiver.Send($$$$$"""{"rx{{{{{receiverNo}}}}}":{"name":"{{{{{str}}}}}"}}""");
            }
        }
    }
    // Gain stages in EW-DX:
    // TX (Trim) -> RX (Gain -> AF Out)
    // Reasoning: Trim on TX is useful if you have multiple TX on one RX, to keep levels consistent, it's not a preamp gain
    //            RX Gain sets the gain before DAC, useful since AF is in float? and DAC has limited range. + EW-D doesn't have trim
    //            AF Out is to compensate for the device listening to the RX.
    // Hence: Sensitivity = Trim; Gain = Gain; OutputGain = AF Out
    public int? Gain
    {
        get => gain;
        set
        {
            if (value != null)
                receiver.Send($$$$$"""{"rx{{{{{receiverNo}}}}}":{"gain":{{{{{Math.Clamp((int)(value/3)*3, -3, 42)}}}}}}}""");
        }
    }
    public int? Sensitivity
    {
        get => sensitivity;
        set
        {
            if (value != null)
                receiver.Send($$$$$"""{"rx{{{{{receiverNo}}}}}":{"sync_settings":{"trim":{{{{{Math.Clamp((int)value, -12, 6)}}}}}}}}""");
        }
    }
    public int? OutputGain
    {
        get => outputGain;
        set
        {
            if (value != null && value >= -32 && value <= 0)
                receiver.Send($$$$$"""{"audio1":{"out{{{{{receiverNo}}}}}":{"level":{{{{{Math.Clamp((int)(value / 6) * 6, -24, 18)}}}}}}}}""");
        }
    }
    public bool? Mute
    {
        get => mute;
        set
        {
            if (value != null && (rfScanInProgress?.IsCompleted ?? true))
                receiver.Send($$$$$"""{"rx{{{{{receiverNo}}}}}":{"mute":{{{{{value}}}}}}}""");
        }
    }
    public ulong? Frequency
    {
        get => frequency;
        set
        {
            if (value != null)
                receiver.Send($$$$$"""{"rx{{{{{receiverNo}}}}}":{"frequency":{{{{{value/1000}}}}}}}""");
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
                        receiver.Send($$$$$"""{"rx{{{{{receiverNo}}}}}":{"sync_settings":{"lock":false}}}""");
                        break;
                    case WirelessMicSuiteServer.LockMode.All:
                    case WirelessMicSuiteServer.LockMode.Power:
                    case WirelessMicSuiteServer.LockMode.FrequencyPower:
                        receiver.Send($$$$$"""{"rx{{{{{receiverNo}}}}}":{"sync_settings":{"lock":true}}}""");
                        break;
                    default:
                        break;
                }
            }
        }
    }
    public int? Group
    {
        get => null;
        set { }
    }
    public int? Channel
    {
        get => null;
        set { }
    }
    public string? TransmitterType => transmitterType;
    public float? BatteryLevel => batteryLevel;

    public event PropertyChangedEventHandler? PropertyChanged;

    public SennheiserSSCWirelessMic(SennheiserSSCReceiver receiver, uint uid, int receiverNo)
    {
        this.receiver = receiver;
        this.uid = uid;
        this.receiverNo = receiverNo;
        meterData = [];
        rfSamples = [];
    }

    private readonly ILogger logger = Program.LoggerFac.CreateLogger<SennheiserSSCWirelessMic>();
    private void Log(string? message, LogSeverity severity = LogSeverity.Info)
    {
        logger.Log(message, severity);
    }

    public void StartMetering(int periodMS)
    {
        throw new NotImplementedException();
    }

    public void StopMetering()
    {
        throw new NotImplementedException();
    }

    public Task<RFScanData> StartRFScan(FrequencyRange range, ulong stepSize)
    {
        if (rfScanInProgress != null && !rfScanInProgress.IsCompleted)
            return rfScanInProgress;

        isCollectingRFSamples = false;
        var startTime = DateTime.UtcNow;
        var timeout = TimeSpan.FromSeconds(180);
        return rfScanInProgress = Task.Run(() =>
        {
            rfScanData.State = RFScanData.Status.Running;
            rfScanData.Progress = 0;
            rfScanData.FrequencyRange = range;
            rfScanData.StepSize = stepSize;
            rfScanData.Samples = [];
            rfSamples.Clear();

            isCollectingRFSamples = true;
            stepSize = Math.Min((stepSize / 25000) * 25000, stepSize);
            var nsteps = Enumerable.Sum(receiver.FrequencyRanges!.Select(x => (decimal)((x.EndFrequency - x.StartFrequency) / stepSize + 1)));
            int i = 0;
            foreach (var range in receiver.FrequencyRanges!)
            {
                for (ulong freq = range.StartFrequency; freq < range.EndFrequency + 1; freq += stepSize)
                {
                    Frequency = freq;
                    Thread.Sleep(100);
                    rfScanData.Progress = ++i/(float)nsteps;
                }
            }

            isCollectingRFSamples = false;

            foreach (var sample in rfSamples)
            {
                rfScanData.Samples.Add(new(sample.Key, sample.Value.sample / sample.Value.n));
            }

            rfScanData.State = RFScanData.Status.Completed;

            return rfScanData;
        });
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    internal void SetTXConnected(bool connected)
    {
        isTXConnected = connected;
        if(connected)
            SendStartupMessages();
    }

    internal void SendStartupMessages()
    {
        //receiver.Send("""{"device":{"network":{"ether":{"interfaces":null}}}}""");
        //receiver.Send($$$$"""{"rx{{{{receiverNo}}}}":{"identification":null}}""");
        //receiver.Send($$$$"""{"rx{{{{receiverNo}}}}":{"presets":null}}""");
        receiver.SendSubscription($$$$""" "rx{{{{receiverNo}}}}":{"warnings":null}""");
        receiver.SendSubscription($$$$""" "rx{{{{receiverNo}}}}":{"name":null}""");
        receiver.SendSubscription($$$$""" "rx{{{{receiverNo}}}}":{"mute":null}""");
        receiver.Send($$$$"""{"rx{{{{receiverNo}}}}":{"mates":null}}""");
        receiver.SendSubscription($$$$""" "rx{{{{receiverNo}}}}":{"gain":null}""");
        receiver.SendSubscription($$$$""" "rx{{{{receiverNo}}}}":{"frequency":null}""");
        receiver.Send($$$$"""{"rx{{{{receiverNo}}}}":{"audio":null}}""");

        receiver.SendSubscription($$$$""" "rx{{{{receiverNo}}}}":{"sync_settings":{"trim_ignore":null}}""");
        receiver.SendSubscription($$$$""" "rx{{{{receiverNo}}}}":{"sync_settings":{"trim":null}}""");
        receiver.SendSubscription($$$$""" "rx{{{{receiverNo}}}}":{"sync_settings":{"name_ignore":null}}""");
        receiver.SendSubscription($$$$""" "rx{{{{receiverNo}}}}":{"sync_settings":{"mute_config_ts":null}}""");
        receiver.SendSubscription($$$$""" "rx{{{{receiverNo}}}}":{"sync_settings":{"mute_config_ignore":null}}""");
        receiver.SendSubscription($$$$""" "rx{{{{receiverNo}}}}":{"sync_settings":{"mute_config":null}}""");
        receiver.SendSubscription($$$$""" "rx{{{{receiverNo}}}}":{"sync_settings":{"lowcut_ignore":null}}""");
        receiver.SendSubscription($$$$""" "rx{{{{receiverNo}}}}":{"sync_settings":{"lowcut":null}}""");
        receiver.SendSubscription($$$$""" "rx{{{{receiverNo}}}}":{"sync_settings":{"lock_ignore":null}}""");
        receiver.SendSubscription($$$$""" "rx{{{{receiverNo}}}}":{"sync_settings":{"lock":null}}""");
        receiver.SendSubscription($$$$""" "rx{{{{receiverNo}}}}":{"sync_settings":{"led_ignore":null}}""");
        receiver.SendSubscription($$$$""" "rx{{{{receiverNo}}}}":{"sync_settings":{"led":null}}""");
        receiver.SendSubscription($$$$""" "rx{{{{receiverNo}}}}":{"sync_settings":{"frequency_ignore":null}}""");
        receiver.SendSubscription($$$$""" "rx{{{{receiverNo}}}}":{"sync_settings":{"cable_emulation_ignore":null}}""");
        receiver.SendSubscription($$$$""" "rx{{{{receiverNo}}}}":{"sync_settings":{"cable_emulation":null}}""");

        receiver.SendSubscription($$$$""" "audio1":{"out{{{{receiverNo}}}}":{"level":null}}""");

        receiver.SendSubscription($$$$""" "m":{"rx{{{{receiverNo}}}}":{"rssi":null}}""");
        //receiver.SendSubscription($$$$""" "m":{"rx{{{{receiverNo}}}}":{"rsqi":null}}""");
        receiver.SendSubscription($$$$""" "m":{"rx{{{{receiverNo}}}}":{"divi":null}}""");
        receiver.SendSubscription($$$$""" "m":{"rx{{{{receiverNo}}}}":{"af":null}}""");

        if (isTXConnected)
        {
            receiver.SendSubscription($$$$""" "mates":{"tx{{{{receiverNo}}}}":{"battery":{"type":null}}}""");
            receiver.SendSubscription($$$$""" "mates":{"tx{{{{receiverNo}}}}":{"battery":{"lifetime":null}}}""");
            receiver.SendSubscription($$$$""" "mates":{"tx{{{{receiverNo}}}}":{"battery":{"gauge":null}}}""");
            receiver.SendSubscription($$$$""" "mates":{"tx{{{{receiverNo}}}}":{"warnings":null}}""");
            receiver.SendSubscription($$$$""" "mates":{"tx{{{{receiverNo}}}}":{"version":null}}""");
            receiver.SendSubscription($$$$""" "mates":{"tx{{{{receiverNo}}}}":{"type":null}}""");
            receiver.SendSubscription($$$$""" "mates":{"tx{{{{receiverNo}}}}":{"trim":null}}""");
            receiver.SendSubscription($$$$""" "mates":{"tx{{{{receiverNo}}}}":{"name":null}}""");
            receiver.SendSubscription($$$$""" "mates":{"tx{{{{receiverNo}}}}":{"mute_config_ts":null}}""");
            receiver.SendSubscription($$$$""" "mates":{"tx{{{{receiverNo}}}}":{"mute_config":null}}""");
            receiver.SendSubscription($$$$""" "mates":{"tx{{{{receiverNo}}}}":{"mute":null}}""");
            receiver.SendSubscription($$$$""" "mates":{"tx{{{{receiverNo}}}}":{"lowcut":null}}""");
            receiver.SendSubscription($$$$""" "mates":{"tx{{{{receiverNo}}}}":{"lock":null}}""");
            receiver.SendSubscription($$$$""" "mates":{"tx{{{{receiverNo}}}}":{"led":null}}""");
            receiver.SendSubscription($$$$""" "mates":{"tx{{{{receiverNo}}}}":{"identification":null}}""");
            receiver.SendSubscription($$$$""" "mates":{"tx{{{{receiverNo}}}}":{"capsule":null}}""");
            receiver.SendSubscription($$$$""" "mates":{"tx{{{{receiverNo}}}}":{"cable_emulation":null}}""");
        }
    }

    internal void ParseRXMessage(Memory<char> msg, JsonElement json)
    {
        foreach (var prop in json.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "identification":
                    //visual: bool
                    break;
                case "presets":
                    //user/0: int[]
                    //active: int -> indexed by a string
                    break;
                case "sync_settings":
                    ParseSyncSettingsMessage(msg, prop.Value);
                    break;
                case "warnings":
                    //str[]
                    //Aes256Error LowSignal NoLink RfPeak AfPeak
                    break;
                case "name":
                    //str(8)
                    name = prop.Value.GetString();
                    OnPropertyChanged(nameof(Name));
                    break;
                case "mute":
                    //bool
                    rxMute = prop.Value.GetBoolean();
                    mute = txMute == null ? null : (rxMute || txMute.Value);
                    OnPropertyChanged(nameof(Mute));
                    break;
                case "mates":
                    //str[]
                    break;
                case "gain":
                    gain = prop.Value.GetInt32();
                    OnPropertyChanged(nameof(Gain));
                    break;
                case "frequency":
                    //int
                    frequency = prop.Value.GetUInt64()*1000;
                    OnPropertyChanged(nameof(Frequency));
                    break;
                case "audio":
                    //str[1]
                    break;
                case "restore":
                    //AUDIO_DEFAULTS
                    break;
                default:
                    Log($"Unknown JSON property encountered '{prop.Name}' in '{msg}'!", LogSeverity.Warning);
                    break;
            }
        }
    }

    private void ParseSyncSettingsMessage(Memory<char> msg, JsonElement json)
    {
        foreach (var prop in json.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "trim_ignore":
                    //bool
                    break;
                case "trim":
                    sensitivity = prop.Value.GetInt32();
                    OnPropertyChanged(nameof(Sensitivity));
                    break;
                case "name_ignore":
                    //bool
                    break;
                case "mute_config_ts":
                    //af_mute, off, push_to_talk, push_to_mute
                    break;
                case "mute_config_ignore":
                    //bool
                    break;
                case "mute_config":
                    //off, rf_mute, af_mute
                    break;
                case "lowcut_ignore":
                    //bool
                    break;
                case "lowcut":
                    //off 30 Hz 60 Hz 80 Hz 100 Hz 120 Hz
                    break;
                case "lock_ignore":
                    //bool
                    break;
                case "lock":
                    //bool
                    lockMode = prop.Value.GetBoolean() ? WirelessMicSuiteServer.LockMode.Power : WirelessMicSuiteServer.LockMode.None;
                    break;
                case "led_ignore":
                    //bool
                    break;
                case "led":
                    //bool
                    break;
                case "frequency_ignore":
                    //bool
                    break;
                case "cable_emulation_ignore":
                    //bool
                    break;
                case "cable_emulation":
                    //off type1 type2 type3
                    break;
                default:
                    Log($"Unknown JSON property encountered '{prop.Name}' in '{msg}'!", LogSeverity.Warning);
                    break;
            }
        }
    }

    internal void ParseAudioMessage(Memory<char> msg, JsonElement json)
    {
        foreach (var prop in json.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "level":
                    outputGain = prop.Value.GetInt32();
                    OnPropertyChanged(nameof(OutputGain));
                    break;
                default:
                    Log($"Unknown JSON property encountered '{prop.Name}' in '{msg}'!", LogSeverity.Warning);
                    break;
            }
        }
    }

    internal void ParseMeterMessage(Memory<char> msg, JsonElement json)
    {
        var meter = lastMeterData == null ? new MeteringData() : new MeteringData(lastMeterData.Value);
        foreach (var prop in json.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "rssi":
                    //rssi: float (dBm)
                    meter.rssiA = meter.rssiB = (prop.Value.GetSingle()+120)/80f;
                    break;
                case "rsqi":
                    //rsqi: int(%)(RF signal quality indicator)
                    break;
                case "divi":
                    //divi: 0,1,2 (None, antenna A, antenna B)
                    switch (prop.Value.GetInt32())
                    {
                        case 0:
                            meter.diversity = DiversityIndicator.None;
                            break;
                        case 1:
                            meter.diversity = DiversityIndicator.A;
                            break;
                        case 2:
                            meter.diversity = DiversityIndicator.B;
                            break;
                    }
                    break;
                case "af":
                    //af: float (dBfs)
                    meter.audioLevel = (prop.Value.GetSingle()+50)/50f;
                    break;
                default:
                    Log($"Unknown JSON property encountered '{prop.Name}' in '{msg}'!", LogSeverity.Warning);
                    break;
            }
        }

        if (meterData.Count > IWirelessMic.MAX_METER_SAMPLES)
        {
            // Purge the last 16 samples if the meter queue is getting full...
            for (int i = 0; i < 16; i++)
                meterData.TryDequeue(out _);
        }
        meterData.Enqueue(meter);
        lastMeterData = meter;

        if (isCollectingRFSamples)
        {
            rfSamples.AddOrUpdate(frequency!.Value, 
                (f) => { return (meter.rssiA, 1); }, 
                (f, old) => { return (meter.rssiA + old.sample, old.n + 1); });
            //rfScanData.Samples.Add(new(frequency!.Value, meter.RssiA));
        }
    }

    internal void ParseTXMessage(Memory<char> msg, JsonElement json)
    {
        // All these are read only
        foreach (var prop in json.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "battery":
                    foreach (var batt in prop.Value.EnumerateObject())
                    {
                        switch (batt.Name)
                        {
                            case "type":
                                //battery/type: Battery, Primary Cell
                                break;
                            case "lifetime":
                                //battery/lifetime: int (minutes)
                                break;
                            case "gauge":
                                //battery/gauge: int (%)
                                batteryLevel = batt.Value.GetInt32() / 100f;
                                OnPropertyChanged(nameof(BatteryLevel));
                                break;
                            default:
                                Log($"Unknown JSON property encountered '{batt.Name}' in '{msg}'!", LogSeverity.Warning);
                                break;
                        }
                    }
                    break;
                case "warnings":
                    //warnings: str[]: AfPeak, LowBattery
                    break;
                case "version":
                    //version: str
                    break;
                case "type":
                    //type: str
                    transmitterType = prop.Value.GetString();
                    OnPropertyChanged(nameof(TransmitterType));
                    break;
                case "trim":
                    //trim: int
                    sensitivity = prop.Value.GetInt32();
                    OnPropertyChanged(nameof(Sensitivity));
                    break;
                case "name":
                    //name: str
                    name = prop.Value.GetString();
                    OnPropertyChanged(nameof(Name));
                    break;
                case "mute_config_ts":
                    //mute_config_ts: af_mute, off, push_to_talk, push_to_mute
                    break;
                case "mute_config":
                    //mute_config: off, rf_mute, af_mute
                    break;
                case "mute":
                    //mute: bool
                    txMute = prop.Value.GetBoolean();
                    mute = txMute == null ? null : (rxMute || txMute.Value);
                    OnPropertyChanged(nameof(Mute));
                    break;
                case "lowcut":
                    //lowcut: str: off, 30 Hz, 60 Hz, ...
                    break;
                case "lock":
                    //lock: bool
                    lockMode = prop.Value.GetBoolean() ? WirelessMicSuiteServer.LockMode.Power : WirelessMicSuiteServer.LockMode.None;
                    break;
                case "led":
                    //led: bool
                    break;
                case "identification":
                    //identification: bool
                    break;
                case "capsule":
                    //capsule: str
                    break;
                case "cable_emulation":
                    //cable_emulation: str
                    break;
                default:
                    Log($"Unknown JSON property encountered '{prop.Name}' in '{msg}'!", LogSeverity.Warning);
                    break;
            }
        }
    }
}
