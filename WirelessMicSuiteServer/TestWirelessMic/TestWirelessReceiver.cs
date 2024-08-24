using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;

namespace WirelessMicSuiteServer.TestWirelessMic;

public class TestWirelessReceiver : IWirelessMicReceiver
{
    private readonly TestWirelessManager manager;
    private IPMode? ipMode = WirelessMicSuiteServer.IPMode.DHCP;
    private IPv4Address ipAddress = new("192.168.0.255");
    private IPv4Address? gatewayAddress = new("192.168.0.1");
    private IPv4Address? subnet = new("255.255.255.0");

    public uint UID { get; init; }
    public IPEndPoint Address { get; init; }
    public int NumberOfChannels => 2;
    public IWirelessMic[] WirelessMics { get; init;}
    public string? ModelName => "TestReciever";
    public string? Manufacturer => "Mathieson.Dev";
    public string? FreqBand => "TestBand";
    public FrequencyRange[]? FrequencyRanges { get; init; }
    public string? FirmwareVersion => "0.0.1";

    public IPv4Address IPAddress
    {
        get => ipAddress; set
        {
            ipAddress = value;
            OnPropertyChanged();
        }
    }
    public IPv4Address? Subnet
    {
        get => subnet; set
        {
            subnet = value;
            OnPropertyChanged();
        }
    }
    public IPv4Address? Gateway
    {
        get => gatewayAddress; set
        {
            gatewayAddress = value;
            OnPropertyChanged();
        }
    }
    public IPMode? IPMode
    {
        get => ipMode; set
        {
            ipMode = value;
            OnPropertyChanged();
        }
    }
    public MACAddress? MACAddress => new MACAddress(0xff00ff00ff00ff00);

    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly ILogger logger = Program.LoggerFac.CreateLogger<TestWirelessReceiver>();

    public TestWirelessReceiver(TestWirelessManager manager, uint uid)
    {
        UID = uid;
        this.manager = manager;
        Address = new IPEndPoint(ipAddress._data, 65525);
        WirelessMics = [
            new TestWirelessMic(this, TestWirelessManager.CombineHash(uid, 1u), 1),
            new TestWirelessMic(this, TestWirelessManager.CombineHash(uid, 2u), 2),
        ];
        foreach (var m in WirelessMics)
            manager.RegisterWirelessMic((TestWirelessMic)m);
    }

    private void Log(string? message, LogSeverity severity = LogSeverity.Info)
    {
        logger.Log(message, severity);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void Dispose()
    {
        foreach (var m in WirelessMics)
            manager.UnregisterWirelessMic((TestWirelessMic)m);
        Log($"Destroyed Test receiver 0x{UID:X4}");
    }

    public void Identify()
    {
        Log($"Identified Test receiver 0x{UID:X4}");
    }

    public void Reboot()
    {
        Log($"Rebooted Test receiver 0x{UID:X4}");
    }
}
