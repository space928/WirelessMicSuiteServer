using System;
using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace WirelessMicSuiteServer;

public class SennheiserSSCReceiver : IWirelessMicReceiver
{
    private readonly SennheiserSSCManager manager;
    private readonly SennheiserSSCWirelessMic[] mics;
    private readonly uint uid;
    private readonly ulong sennheiserID;
    private readonly JsonDocumentOptions jsonOptions;
    private int xid = 0;
    private readonly bool isDanteModel;
    private readonly Timer refreshSubscriptionTimer;

    private string? modelName;
    private readonly string manufacturer = "Sennheiser";
    private string? freqBand;
    private FrequencyRange[]? frequencyRanges;
    private string? firmwareVersion;
    private IPv4Address ipAddress;
    private IPv4Address? subnet;
    private IPv4Address? gateway;
    private IPMode? ipMode;
    private MACAddress? macAddress;

    public uint UID => uid;
    public IPEndPoint Address { get; init; }
    public DateTime LastPingTime { get; set; }

    public int NumberOfChannels => mics.Length;
    public IWirelessMic[] WirelessMics => mics;

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
            Send($$$$$"""{"device":{"network":{"ipv4":{"manual_ipaddr":{{{{{value}}}}}}}}}""");
        }
    }
    public IPv4Address? Subnet
    {
        get => subnet;
        set
        {
            if (value != null)
                Send($$$$$"""{"device":{"network":{"ipv4":{"manual_netmask":{{{{{value}}}}}}}}}""");
        }
    }
    public IPv4Address? Gateway
    {
        get => gateway;
        set
        {
            if (value != null)
                Send($$$$$"""{"device":{"network":{"ipv4":{"manual_gateway":{{{{{value}}}}}}}}}""");
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
                        Send("""{"device":{"network":{"ipv4":{"auto":true}}}}""");
                        break;
                    case WirelessMicSuiteServer.IPMode.Manual:
                        Send("""{"device":{"network":{"ipv4":{"auto":false}}}}""");
                        break;
                }
            }
        }
    }
    public MACAddress? MACAddress => macAddress;

    public event PropertyChangedEventHandler? PropertyChanged;

    public SennheiserSSCReceiver(SennheiserSSCManager manager, IPEndPoint address, uint uid, ulong sennheiserID, string model)
    {
        LastPingTime = DateTime.UtcNow;
        this.manager = manager;
        this.uid = uid;
        this.sennheiserID = sennheiserID;
        Address = new(address.Address, address.Port);
        jsonOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 16 };

        // A lazy approach to detect how many channels the receiver supports, should work in practice though.
        int channelCount = 2;
        foreach (char c in model)
            if (c == '4')
                channelCount = 4;
        isDanteModel = model.Contains("DANTE");
        if (channelCount == 2)
        {
            mics = [
                new SennheiserSSCWirelessMic(this, SennheiserSSCManager.CombineHash(uid, 1u), 1),
                new SennheiserSSCWirelessMic(this, SennheiserSSCManager.CombineHash(uid, 2u), 2)
            ];
        } else 
        {
            mics = [
                new SennheiserSSCWirelessMic(this, SennheiserSSCManager.CombineHash(uid, 1u), 1),
                new SennheiserSSCWirelessMic(this, SennheiserSSCManager.CombineHash(uid, 2u), 2),
                new SennheiserSSCWirelessMic(this, SennheiserSSCManager.CombineHash(uid, 3u), 3),
                new SennheiserSSCWirelessMic(this, SennheiserSSCManager.CombineHash(uid, 4u), 4)
            ];
        } 
        
        foreach (var mic in mics)
            manager.RegisterWirelessMic(mic);

        SendStartupMessages();

        refreshSubscriptionTimer = new(_ => SendStartupMessages(), null, Random.Shared.Next() & 0xff, 5000);
    }

    private readonly ILogger logger = Program.LoggerFac.CreateLogger<SennheiserSSCReceiver>();
    private void Log(string? message, LogSeverity severity = LogSeverity.Info)
    {
        logger.Log(message, severity);
    }

    private void SendStartupMessages()
    {
        //SendSubscription(""" "device":{"identification":{"visual":null}}""");
        Send("""{"device":{"identity":{"version":null}}}""");
        Send("""{"device":{"identity":{"vendor":null}}}""");
        Send("""{"device":{"identity":{"serial":null}}}""");
        Send("""{"device":{"identity":{"product":null}}}""");

        //SendSubscription(""" "device":{"network":{"dante":null}}""");
        Send("""{"device":{"network":{"ether":{"macs":null}}}}""");
        Send("""{"device":{"network":{"ether":{"interfaces":null}}}}""");
        SendSubscription(""" "device":{"network":{"ipv4":{"manual_netmask":null}}}""");
        SendSubscription(""" "device":{"network":{"ipv4":{"netmask":null}}}""");
        SendSubscription(""" "device":{"network":{"ipv4":{"manual_ipaddr":null}}}""");
        SendSubscription(""" "device":{"network":{"ipv4":{"ipaddr":null}}}""");
        SendSubscription(""" "device":{"network":{"ipv4":{"manual_gateway":null}}}""");
        SendSubscription(""" "device":{"network":{"ipv4":{"gateway":null}}}""");
        Send("""{"device":{"network":{"ipv4":{"interfaces":null}}}}""");
        SendSubscription(""" "device":{"network":{"ipv4":{"auto":null}}}""");
        SendSubscription(""" "device":{"network":{"mdns":null}}""");

        Send("""{"device":{"preset_spacing":null}}""");
        Send($$$"""{"device":{"time":{{{((DateTimeOffset)DateTime.Now).ToUnixTimeSeconds()}}}}}""");
        //SendSubscription(""" "device":{"restore":null}""");
        //SendSubscription(""" "device":{"restart":null}""");
        SendSubscription(""" "device":{"remote_access":null}""");
        SendSubscription(""" "device":{"name":null}""");
        SendSubscription(""" "device":{"lock":null}""");
        SendSubscription(""" "device":{"location":null}""");
        SendSubscription(""" "device":{"link_density_mode":null}""");
        Send("""{"device":{"language":null}}""");
        Send("""{"device":{"frequency_ranges":null}}""");
        Send("""{"device":{"frequency_code":null}}""");
        SendSubscription(""" "device":{"encryption":null}""");
        SendSubscription(""" "device":{"brightness":null}""");
        if(isDanteModel)
            SendSubscription(""" "device":{"booster":null}""");

        SendSubscription(""" "mates":{"active":null}""");

        //Send("""{"osc":{"error":null}}""");
        //Send("""{"osc":{"state":null}}""");
        //Send("""{"osc":{"feature":null}}""");
        Send("""{"osc":{"version":null}}""");
        //Send("""{"osc":{"xid":null}}""");
        //Send("""{"osc":{"ping":null}}""");
        //Send("""{"osc":{"limits":null}}""");
        //Send("""{"osc":{"schema":null}}""");

        foreach (var mic in mics)
            mic.SendStartupMessages();
    }

    public void Dispose()
    {
        refreshSubscriptionTimer.Dispose();
        foreach (var mic in mics)
        {
            manager.UnregisterWirelessMic(mic);
        }
    }

    public void Identify()
    {
        Send("""{"device":{"identification":{"visual":true}}}""");
    }

    public void Reboot()
    {
        Send("""{"device":{"restart":true}}""");
    }

    public void SendSubscription(ReadOnlySpan<char> prefix, int recNo, ReadOnlySpan<char> property, ReadOnlySpan<char> suffix)
    {
        Send($$$$"""{"osc":{"xid":{{{{xid++}}}},"state":{"subscribe":[{"#":{"min":0,"max":0,"count":1000,"lifetime":10},{{{{prefix}}}}{{{{recNo}}}}":{{{{property}}}}{{{{suffix}}}}}]}}}""");
    }

    public void SendSubscription(ReadOnlySpan<char> property)
    {
        Send($$$$"""{"osc":{"xid":{{{{xid++}}}},"state":{"subscribe":[{"#":{"min":0,"max":0,"count":1000,"lifetime":10},{{{{property}}}}}]}}}""");
    }

    public void Send(ReadOnlySpan<char> data)
    {
        int len = manager.Encoder.GetByteCount(data, true);
        var msg = new ByteMessage(Address, len);
        var buff = msg.Buffer;

        manager.Encoder.GetBytes(data, buff.AsSpan(), true);

        manager.SendMessage(msg);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void Receive(Memory<char> str)
    {
        if (str.Length > 1 && str.Span[^1] == '\n')
            str = str[..^1];
        var json = JsonDocument.Parse(str, jsonOptions);
        foreach(var prop in json.RootElement.EnumerateObject())
        {
            switch(prop.Name)
            {
                case "audio1":
                    foreach (var audio in prop.Value.EnumerateObject())
                    {
                        switch (audio.Name)
                        {
                            case "out1":
                                mics[0].ParseAudioMessage(str, audio.Value); 
                                break;
                            case "out2":
                                mics[1].ParseAudioMessage(str, audio.Value);
                                break;
                            case "out3":
                                mics[2].ParseAudioMessage(str, audio.Value);
                                break;
                            case "out4":
                                mics[3].ParseAudioMessage(str, audio.Value);
                                break;
                            default:
                                Log($"Unknown JSON property encountered '{audio.Name}' in '{str}'!", LogSeverity.Warning);
                                break;
                        }
                    }
                    break;
                case "device":
                    ParseDeviceMessage(str, prop.Value); 
                    break;
                case "interface":
                    //version: str
                case "m":
                    foreach (var meter in prop.Value.EnumerateObject())
                    {
                        switch (meter.Name)
                        {
                            case "rx1":
                                mics[0].ParseMeterMessage(str, meter.Value);
                                break;
                            case "rx2":
                                mics[1].ParseMeterMessage(str, meter.Value);
                                break;
                            case "rx3":
                                mics[2].ParseMeterMessage(str, meter.Value);
                                break;
                            case "rx4":
                                mics[3].ParseMeterMessage(str, meter.Value);
                                break;
                            default:
                                Log($"Unknown JSON property encountered '{meter.Name}' in '{str}'!", LogSeverity.Warning);
                                break;
                        }
                    }
                    break;
                case "mates":
                    foreach (var tx in prop.Value.EnumerateObject())
                    {
                        switch (tx.Name)
                        {
                            case "tx1":
                                mics[0].ParseTXMessage(str, tx.Value);
                                break;
                            case "tx2":
                                mics[1].ParseTXMessage(str, tx.Value);
                                break;
                            case "tx3":
                                mics[2].ParseTXMessage(str, tx.Value);
                                break;
                            case "tx4":
                                mics[3].ParseTXMessage(str, tx.Value);
                                break;
                            case "active":
                                bool a = false, b = false, c = false, d = false;
                                foreach (var mate in tx.Value.EnumerateArray())
                                {
                                    switch (mate.GetString())
                                    {
                                        case "mates/tx1":
                                            a = true;
                                            break;
                                        case "mates/tx2":
                                            b = true;
                                            break;
                                        case "mates/tx3":
                                            c = true;
                                            break;
                                        case "mates/tx4":
                                            d = true;
                                            break;
                                    }
                                }
                                mics[0].SetTXConnected(a);
                                mics[1].SetTXConnected(b);
                                if (mics.Length > 2)
                                {
                                    mics[2].SetTXConnected(c);
                                    mics[3].SetTXConnected(d);
                                }
                                break;
                            default:
                                Log($"Unknown JSON property encountered '{tx.Name}' in '{str}'!", LogSeverity.Warning);
                                break;
                        }
                    }
                    break;
                case "osc":
                    ParseOSCMessage(str, prop.Value);
                    break;
                case "rx1":
                    mics[0].ParseRXMessage(str, prop.Value);
                    break;
                case "rx2":
                    mics[1].ParseRXMessage(str, prop.Value);
                    break;
                case "rx3":
                    mics[2].ParseRXMessage(str, prop.Value);
                    break;
                case "rx4":
                    mics[3].ParseRXMessage(str, prop.Value);
                    break;
                default:
                    Log($"Unknown JSON property encountered '{prop.Name}' in '{str}'!", LogSeverity.Warning);
                    break;
            }
        }
    }

    private void ParseDeviceMessage(Memory<char> msg, JsonElement json)
    {
        foreach (var prop in json.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "identification":
                    //visual: bool
                    break;
                case "identity":
                    foreach (var ident in prop.Value.EnumerateObject())
                    {
                        switch (ident.Name)
                        {
                            case "version": // str
                                firmwareVersion = ident.Value.GetString(); ;
                                OnPropertyChanged(nameof(FirmwareVersion));
                                break;
                            case "vendor": // str
                                break;
                            case "serial": // str
                                break;
                            case "product":
                                modelName = ident.Value.GetString();
                                OnPropertyChanged(nameof(ModelName));
                                break;
                            default:
                                Log($"Unknown JSON property encountered '{ident.Name}' in '{msg}'!", LogSeverity.Warning);
                                break;
                        }
                    }
                    break;
                case "network":
                    foreach (var net in prop.Value.EnumerateObject())
                    {
                        switch (net.Name)
                        {
                            case "dante":
                                //indentity/version: str
                                //ipv4/netmask: str[2]
                                //ipv4/manual_netmask: str[2]
                                //ipv4/manual_ipaddr: str[2]
                                //ipv4/manual_gateway: str[2]
                                //ipv4/ipaddr: str[2]
                                //ipv4/gateway: str[2]
                                //ipv4/auto: bool[2]
                                //macs: str[]
                                //interfaces: str[]
                                //interface_mapping: str enum
                                break;
                            case "ether":
                                foreach (var ether in net.Value.EnumerateObject())
                                {
                                    switch (ether.Name)
                                    {
                                        case "macs":
                                            macAddress = new WirelessMicSuiteServer.MACAddress(ether.Value[0].GetString()!);
                                            break;
                                        case "interfaces": // str[1]
                                            break;
                                        default:
                                            Log($"Unknown JSON property encountered '{ether.Name}' in '{msg}'!", LogSeverity.Warning);
                                            break;
                                    }
                                }
                                break;
                            case "ipv4":
                                foreach (var ipv4 in net.Value.EnumerateObject())
                                {
                                    switch (ipv4.Name)
                                    {
                                        //case "manual_netmask":
                                        case "netmask":
                                            subnet = new IPv4Address(ipv4.Value[0].GetString()!);
                                            OnPropertyChanged(nameof(Subnet));
                                            break;
                                        //case "manual_ipaddr":
                                        case "ipaddr":
                                            ipAddress = new IPv4Address(ipv4.Value[0].GetString()!);
                                            OnPropertyChanged(nameof(IPAddress));
                                            break;
                                        //case "manual_gateway":
                                        case "gateway":
                                            gateway = new IPv4Address(ipv4.Value[0].GetString()!);
                                            OnPropertyChanged(nameof(Gateway));
                                            break;
                                        case "interfaces": // str[1]
                                            break;
                                        case "auto":
                                            ipMode = ipv4.Value.GetBoolean() ? WirelessMicSuiteServer.IPMode.DHCP : WirelessMicSuiteServer.IPMode.Manual;
                                            break;
                                        case "manual_netmask":
                                        case "manual_ipaddr":
                                        case "manual_gateway":
                                            break;
                                        default:
                                            Log($"Unknown JSON property encountered '{ipv4.Name}' in '{msg}'!", LogSeverity.Warning);
                                            break;
                                    }
                                }
                                break;
                            case "mdns": // bool[1]
                                break;
                            default:
                                Log($"Unknown JSON property encountered '{net.Name}' in '{msg}'!", LogSeverity.Warning);
                                break;
                        }
                    }
                    break;
                case "preset_spacing":
                    //int
                    break;
                case "time":
                    //int
                    break;
                case "restore":
                    //str enum
                    break;
                case "restart":
                    //bool
                    break;
                case "remote_access":
                    //str(15)
                    break;
                case "name":
                    //str(18)
                    break;
                case "lock":
                    //bool
                    break;
                case "location":
                    //str(400)
                    break;
                case "link_density_mode":
                    //bool
                    break;
                case "language":
                    //str
                    break;
                case "frequency_ranges":
                    //str[]
                    frequencyRanges = new FrequencyRange[prop.Value.GetArrayLength()];
                    try
                    {
                        for (int i = 0; i < frequencyRanges.Length; i++)
                        {
                            var range = prop.Value[i].GetString();
                            var splits = range!.Split(':');

                            frequencyRanges[i] = new FrequencyRange(ulong.Parse(splits[0]), ulong.Parse(splits[2]));
                        }
                    } catch
                    {
                        Log($"Failed to parse frequency ranges '{prop.Value}' in '{msg}'!", LogSeverity.Warning);
                    }
                    break;
                case "frequency_code":
                    //str
                    freqBand = prop.Value.GetString();
                    OnPropertyChanged(nameof(FreqBand));
                    break;
                case "encryption":
                    //bool
                    break;
                case "brightness":
                    //int 1-5
                    break;
                case "booster":
                    //bool
                    break;
                default:
                    Log($"Unknown JSON property encountered '{prop.Name}' in '{msg}'!", LogSeverity.Warning);
                    break;
            }
        }
    }

    private void ParseOSCMessage(Memory<char> msg, JsonElement json)
    {
        foreach (var prop in json.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "error":
                    //{"osc":{"error":[{"mates":{"tx2":{"warnings":[424,{"desc":"failed dependency"}]}}}]}}
                    for(int i = 0; i < prop.Value.GetArrayLength(); i++)
                    {
                        string error = prop.Value[i].GetRawText();
                        if (error.Contains("mates") && error.Contains("424")) // Silence TX not present error 
                            continue;
                        Log($"Error received from receiver: {error}", LogSeverity.Warning);
                    }
                    break;
                case "state":
                    //auth/access: str[]
                    //~prettyprint
                    //~close
                    //~subscribe
                    break;
                case "feature":
                    //~timetag
                    //~baseaddr
                    //~subscription
                    //~pattern
                    break;
                case "version":
                    break;
                case "xid":
                    break;
                case "ping":
                    break;
                case "limits":
                    break;
                case "schema":
                    break;
                default:
                    Log($"Unknown JSON property encountered '{prop.Name}' in '{msg}'!", LogSeverity.Warning);
                    break;
            }
        }
    }
}
