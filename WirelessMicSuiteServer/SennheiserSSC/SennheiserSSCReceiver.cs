using System.ComponentModel;
using System.Net;

namespace WirelessMicSuiteServer;

public class SennheiserSSCReceiver : IWirelessMicReceiver
{
    private readonly SennheiserSSCManager manager;
    private readonly SennheiserSSCWirelessMic[] mics;
    private readonly uint uid;
    private readonly ulong sennheiserID;

    public uint UID => uid;
    public IPEndPoint Address { get; init; }
    public DateTime LastPingTime { get; set; }

    public int NumberOfChannels => mics.Length;
    public IWirelessMic[] WirelessMics => mics;

    public string? ModelName => throw new NotImplementedException();
    public string? Manufacturer => throw new NotImplementedException();
    public string? FreqBand => throw new NotImplementedException();
    public FrequencyRange[]? FrequencyRanges => throw new NotImplementedException();
    public string? FirmwareVersion => throw new NotImplementedException();
    public IPv4Address IPAddress { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public IPv4Address? Subnet { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public IPv4Address? Gateway { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public IPMode? IPMode { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public MACAddress? MACAddress => throw new NotImplementedException();

    public event PropertyChangedEventHandler? PropertyChanged;

    public SennheiserSSCReceiver(SennheiserSSCManager manager, IPEndPoint address, uint uid, ulong sennheiserID, string model)
    {
        LastPingTime = DateTime.UtcNow;
        this.manager = manager;
        this.uid = uid;
        this.sennheiserID = sennheiserID;
        Address = new(address.Address, address.Port);

        mics = [
            new SennheiserSSCWirelessMic(this, SennheiserSSCManager.CombineHash(uid, 1u), 1),
            new SennheiserSSCWirelessMic(this, SennheiserSSCManager.CombineHash(uid, 2u), 2)
        ];
        manager.RegisterWirelessMic(mics[0]);
        manager.RegisterWirelessMic(mics[1]);

        SendStartupMessages();
    }

    private readonly ILogger logger = Program.LoggerFac.CreateLogger<SennheiserSSCReceiver>();
    private void Log(string? message, LogSeverity severity = LogSeverity.Info)
    {
        logger.Log(message, severity);
    }

    private void SendStartupMessages()
    {

    }

    public void Dispose()
    {
        foreach(var mic in mics)
        {
            manager.UnregisterWirelessMic(mic);
        }
    }

    public void Identify()
    {
        throw new NotImplementedException();
    }

    public void Reboot()
    {
        throw new NotImplementedException();
    }

    public void Receive(Span<char> str)
    {
        throw new NotImplementedException();
    }
}
