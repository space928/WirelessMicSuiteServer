using System.Collections.Concurrent;
using System.ComponentModel;

namespace WirelessMicSuiteServer;

public class SennheiserSSCWirelessMic : IWirelessMic
{
    private SennheiserSSCReceiver receiver;
    private uint uid;
    private int receiverNo;

    public IWirelessMicReceiver Receiver => receiver;
    public uint UID => uid;

    public ConcurrentQueue<MeteringData>? MeterData => throw new NotImplementedException();
    public MeteringData? LastMeterData => throw new NotImplementedException();
    public RFScanData RFScanData => throw new NotImplementedException();

    public string? Name { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public int? Gain { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public int? Sensitivity { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public int? OutputGain { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public bool? Mute { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public ulong? Frequency { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public int? Group { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public int? Channel { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public LockMode? LockMode { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public string? TransmitterType => throw new NotImplementedException();
    public float? BatteryLevel => throw new NotImplementedException();

    public event PropertyChangedEventHandler? PropertyChanged;

    public SennheiserSSCWirelessMic(SennheiserSSCReceiver receiver, uint uid, int receiverNo)
    {
        this.receiver = receiver;
        this.uid = uid;
        this.receiverNo = receiverNo;
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

    public Task<RFScanData> StartRFScan(FrequencyRange range, ulong stepSize)
    {
        throw new NotImplementedException();
    }

    public void StopMetering()
    {
        throw new NotImplementedException();
    }
}
