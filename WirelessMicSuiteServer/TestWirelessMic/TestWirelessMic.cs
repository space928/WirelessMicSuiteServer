using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WirelessMicSuiteServer.TestWirelessMic;

public class TestWirelessMic : IWirelessMic
{
    private float lastRSSIA = .8f;
    private float lastRSSIB = .8f;
    private float lastAudio = .5f;

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

    public IWirelessMicReceiver Receiver { get; init; }
    public ConcurrentQueue<MeteringData>? MeterData { get; init; }
    public MeteringData? LastMeterData => GenerateMeteringData();//{ get; init; }
    public RFScanData RFScanData { get; init; }
    public uint UID { get; init; }

    public string? Name
    {
        get => name;
        set
        {
            name = value;
            OnPropertyChanged();
        }
    }
    public int? Gain
    {
        get => gain;
        set
        {
            gain = value;
            OnPropertyChanged();
        }
    }
    public int? Sensitivity
    {
        get => sensitivity;
        set
        {
            sensitivity = value;
            OnPropertyChanged();
        }
    }
    public int? OutputGain
    {
        get => outputGain;
        set
        {
            outputGain = value;
            OnPropertyChanged();
        }
    }
    public bool? Mute
    {
        get => mute;
        set
        {
            mute = value;
            OnPropertyChanged();
        }
    }
    public ulong? Frequency
    {
        get => frequency;
        set
        {
            frequency = value;
            OnPropertyChanged();
        }
    }
    public LockMode? LockMode
    {
        get => lockMode;
        set
        {
            lockMode = value;
            OnPropertyChanged();
        }
    }
    public int? Group
    {
        get => group;
        set 
        { 
            group = value;
            OnPropertyChanged();
        }
    }
    public int? Channel
    {
        get => channel;
        set 
        { 
            channel = value;
            OnPropertyChanged();
        }
    }

    public string? TransmitterType => "TestTransmitter";

    public float? BatteryLevel => Random.Shared.NextSingle();

    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly ILogger logger = Program.LoggerFac.CreateLogger<TestWirelessMic>();

    public TestWirelessMic(TestWirelessReceiver receiver, uint uid, int receiverNo)
    {
        Receiver = receiver;
        UID = uid;
        MeterData = [];
        //LastMeterData = new MeteringData(1,1,0.5f, DiversityIndicator.A);
        RFScanData = new RFScanData() { State = RFScanData.Status.Completed };
        Name = $"TestMic-{uid}";
        Gain = 10;
        Sensitivity = 2;
        OutputGain = -10;
        Mute = false;
        Frequency = 555_000_000 + (((uid % 4_000_000)/25_000)*25_000);
        Group = 0;
        Channel = receiverNo;
        LockMode = WirelessMicSuiteServer.LockMode.Power;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private MeteringData GenerateMeteringData()
    {
        var rnd = Random.Shared;
        float rssiA = Math.Clamp(lastRSSIA + (rnd.NextSingle() * 2 - 1) * 0.1f, 0, 1);
        float rssiB = Math.Clamp(lastRSSIB + (rnd.NextSingle() * 2 - 1) * 0.1f, 0, 1);
        float audio = Math.Clamp(lastAudio + (rnd.NextSingle() * 2 - 1) * 0.5f, 0, 1);
        var ret = new MeteringData(rssiA, rssiB, audio, (DiversityIndicator)(rnd.Next(4)));
        lastRSSIA = rssiA;
        lastRSSIB = rssiB;
        lastAudio = audio;
        return ret;
    }

    private void Log(string? message, LogSeverity severity = LogSeverity.Info)
    {
        logger.Log(message, severity);
    }

    public void StartMetering(int periodMS)
    {
        Log($"Start metering on Test receiver 0x{UID:X4}");
    }

    public Task<RFScanData> StartRFScan(FrequencyRange range, ulong stepSize)
    {
        Log($"Start RFScan on Test receiver 0x{UID:X4}");
        return Task.FromResult( new RFScanData() { State = RFScanData.Status.Completed } );
    }

    public void StopMetering()
    {
        Log($"Stop metering on Test receiver 0x{UID:X4}");
    }
}
