using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace WirelessMicSuiteServer;

public interface IWirelessMicReceiverManager : IDisposable
{
    public abstract int PollingPeriodMS { get; set; }
    public abstract ObservableCollection<IWirelessMicReceiver> Receivers { get; init; }

    /// <summary>
    /// Tries to get a <see cref="IWirelessMicReceiver"/> by it's UID (in O(1) time).
    /// </summary>
    /// <param name="uid">The UID of the receiver to find.</param>
    /// <returns>The receiver if it exists or <c>null</c>.</returns>
    public IWirelessMicReceiver? TryGetWirelessMicReceiver(uint uid);
    public IWirelessMic? TryGetWirelessMic(uint uid);
}

public interface IWirelessMicReceiver : IDisposable, INotifyPropertyChanged
{
    /// <summary>
    /// A unique identifier for this wireless receiver unit. Note that this is associated with the 
    /// physical receiver unit, and is persistant between reboots/ip address changes.
    /// </summary>
    public abstract uint UID { get; }
    /// <summary>
    /// The IP address of the wireless receiver; note that on DHCP networks this may not be constant.
    /// </summary>
    public abstract IPEndPoint Address { get; }
    /// <summary>
    /// The number of receiver channels this receiver supports. Usually either 1, 2, or 4.
    /// </summary>
    public abstract int NumberOfChannels { get; }
    /// <summary>
    /// The array of wireless mics connected to this receiver, the length here is equal to <see cref="NumberOfChannels"/>.
    /// </summary>
    public abstract IWirelessMic[] WirelessMics { get; }

    /// <summary>
    /// The model name of the wireless receiver.
    /// </summary>
    public abstract string? ModelName { get; }
    /// <summary>
    /// The name of the manufacturer of the wireless receiver.
    /// </summary>
    public abstract string? Manufacturer { get; }
    /// <summary>
    /// The name of the frequency band the receiver operates on (manufacturer specific).
    /// </summary>
    public abstract string? FreqBand { get; }
    /// <summary>
    /// The range of frequencies the receiver operates on in Hz.
    /// </summary>
    public abstract FrequencyRange[]? FrequencyRanges { get; }
    /// <summary>
    /// The firmware version of the wireless receiver.
    /// </summary>
    public abstract string? FirmwareVersion { get; }

    /// <summary>
    /// The current IP address of the wireless receiver.
    /// </summary>
    public abstract IPv4Address IPAddress { get; set; }
    /// <summary>
    /// The subnet mask of the wireless receiver.
    /// </summary>
    public abstract IPv4Address? Subnet { get; set; }
    /// <summary>
    /// The network gateway of the wireless receiver.
    /// </summary>
    public abstract IPv4Address? Gateway { get; set; }
    /// <summary>
    /// The network mode of the wireless receiver, either manual network configuration or DHCP.
    /// </summary>
    public abstract IPMode? IPMode { get; set; }
    /// <summary>
    /// The MAC address of the wireless receiver.
    /// </summary>
    public abstract MACAddress? MACAddress { get; }

    /// <summary>
    /// Flashses the LEDs on the device to identify it.
    /// </summary>
    public void Identify();
    /// <summary>
    /// Sends a reboot command to the wireless receiver.
    /// </summary>
    public void Reboot();
}

public interface IWirelessMic : INotifyPropertyChanged
{
    public const int MAX_METER_SAMPLES = 1024;

    public abstract IWirelessMicReceiver Receiver { get; }

    /// <summary>
    /// A concurrrent queue containing the metering data.
    /// </summary>
    public abstract ConcurrentQueue<MeteringData>? MeterData { get; }
    public abstract MeteringData? LastMeterData { get; }
    public abstract RFScanData RFScanData { get; }

    /// <summary>
    /// A unique identifier for this wireless transmitter. Note that this is associated with the 
    /// physical receiver channel of the receiver unit, not the physical transmitter.
    /// </summary>
    public abstract uint UID { get; }
    /// <summary>
    /// Transmitter name.
    /// </summary>
    public abstract string? Name { get; set; }
    /// <summary>
    /// Transmitter gain/trim in dB.
    /// </summary>
    public abstract int? Gain { get; set; }
    /// <summary>
    /// Transmitter sensitivity in dB.
    /// </summary>
    public abstract int? Sensitivity { get; set; }
    /// <summary>
    /// Receiver output gain in dB.
    /// </summary>
    public abstract int? OutputGain { get; set; }
    /// <summary>
    /// Transmitter mute.
    /// </summary>
    public abstract bool? Mute { get; set; }
    /// <summary>
    /// Transmitter RF frequency in Hz.
    /// </summary>
    public abstract ulong? Frequency { get; set; }
    /// <summary>
    /// Transmitter frequency group.
    /// </summary>
    public abstract int? Group { get; set; }
    /// <summary>
    /// Transmitter frequency channel, within the group.
    /// </summary>
    public abstract int? Channel { get; set; }
    /// <summary>
    /// The type of UI lock that is enabled on the wireless transmitter.
    /// </summary>
    public abstract LockMode? LockMode { get; set; }
    /// <summary>
    /// Transmitter model type identifier, ie: UR1, UR1H, UR2.
    /// </summary>
    public abstract string? TransmitterType { get; }
    /// <summary>
    /// Battery percentage.
    /// </summary>
    public abstract float? BatteryLevel { get; }

    //public void Subscribe();
    //public void Unsubscribe();
    public void StartMetering(int periodMS);
    public void StopMetering();

    /// <summary>
    /// Starts scanning the RF spectrum using this receiver. Audio is muted while this task is executing.
    /// </summary>
    /// <param name="range">The frequency range to scan, must be within the frequency range supported by the receiver.</param>
    /// <param name="stepSize">If supported, the step size in Hz between each RF sample.</param>
    /// <returns>A data structure containing the results of the scan.</returns>
    public Task<RFScanData> StartRFScan(FrequencyRange range, ulong stepSize);
}

/// <summary>
/// Represents which antenna(e) is currently active.
/// </summary>
[Flags]
public enum DiversityIndicator
{
    None = 0,
    A = 1 << 0,
    B = 1 << 1,
    C = 1 << 2,
    D = 1 << 3,
}

/// <summary>
/// Represents what type of UI lock is enabled on a wireless transmitter.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<LockMode>))]
//[Flags]
public enum LockMode
{
    None,
    //Mute = 1,
    Power = 2,
    Frequency = 3,
    FrequencyPower = 4,
    All = 5
}

/// <summary>
/// A data structure representing a single sample of metering data.
/// </summary>
/// <param name="rssiA"></param>
/// <param name="rssiB"></param>
/// <param name="audioLevel"></param>
/// <param name="diversity"></param>
public struct MeteringData(float rssiA, float rssiB, float audioLevel, DiversityIndicator diversity)
{
    [JsonIgnore] public float rssiA = rssiA;
    [JsonIgnore] public float rssiB = rssiB;
    [JsonIgnore] public float audioLevel = audioLevel;
    [JsonIgnore] public DiversityIndicator diversity = diversity;

    public readonly float RssiA => rssiA;
    public readonly float RssiB => rssiB;
    public readonly float AudioLevel => audioLevel;
    public readonly DiversityIndicator Diversity => diversity;

    public MeteringData(MeteringData other) :
        this(other.rssiA, other.rssiB, other.audioLevel, other.diversity)
    { }
}

/// <summary>
/// Implements the <see cref="IWirelessMicReceiver"/> interface in a struct that's easy to serialize as JSON.
/// </summary>
[Serializable]
public struct WirelessReceiverData(IWirelessMicReceiver other) : IWirelessMicReceiver
{
    [JsonIgnore] public IPEndPoint Address { get; init; } = other.Address;
    [JsonIgnore] public IWirelessMic[] WirelessMics { get; init; } = other.WirelessMics;

    /// <inheritdoc />
    public uint UID { get; init; } = other.UID;

    /// <inheritdoc />
    public int NumberOfChannels { get; init; } = other.NumberOfChannels;
    /// <inheritdoc />
    public readonly IEnumerable<uint> WirelessMicIDs => WirelessMics.Select(x => x.UID);
    /// <inheritdoc />
    public string? ModelName { get; init; } = other.ModelName;
    /// <inheritdoc />
    public string? Manufacturer { get; init; } = other.Manufacturer;
    /// <inheritdoc />
    public string? FreqBand { get; init; } = other.FreqBand;
    /// <inheritdoc />
    public FrequencyRange[]? FrequencyRanges { get; init; } = other.FrequencyRanges;
    /// <inheritdoc />
    public string? FirmwareVersion { get; init; } = other.FirmwareVersion;
    /// <inheritdoc />
    public IPv4Address IPAddress { get; set; } = other.IPAddress;
    /// <inheritdoc />
    public IPv4Address? Subnet { get; set; } = other.Subnet;
    /// <inheritdoc />
    public IPv4Address? Gateway { get; set; } = other.Gateway;
    /// <inheritdoc />
    public IPMode? IPMode { get; set; } = other.IPMode;
    /// <inheritdoc />
    public MACAddress? MACAddress { get; init; } = other.MACAddress;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Dispose() { }

    /// <inheritdoc />
    public void Identify() { }
    /// <inheritdoc />
    public void Reboot() { }
}

/// <summary>
/// Implements the <see cref="IWirelessMic"/> interface in a struct that's easy to serialize as JSON.
/// </summary>
[Serializable]
public struct WirelessMicData(IWirelessMic other) : IWirelessMic
{
    [JsonIgnore] public IWirelessMicReceiver Receiver { get; init; } = other.Receiver;
    [JsonIgnore] public ConcurrentQueue<MeteringData>? MeterData { get; init; } = other.MeterData;
    [JsonIgnore] public RFScanData RFScanData { get; init; } = other.RFScanData;
    [JsonInclude] public MeteringData? LastMeterData { get; init; } = other.LastMeterData;

    /// <summary>
    /// The UID of the receiver this mic is connected too.
    /// </summary>
    [JsonInclude] public readonly uint ReceiverID => Receiver.UID;
    /// <inheritdoc />
    [JsonInclude] public uint UID { get; init; } = other.UID;
    /// <inheritdoc />
    [JsonInclude] public string? Name { get; set; } = other.Name;
    /// <inheritdoc />
    [JsonInclude] public int? Gain { get; set; } = other.Gain;
    /// <inheritdoc />
    [JsonInclude] public int? Sensitivity { get; set; } = other.Sensitivity;
    /// <inheritdoc />
    [JsonInclude] public int? OutputGain { get; set; } = other.OutputGain;
    /// <inheritdoc />
    [JsonInclude] public bool? Mute { get; set; } = other.Mute;
    /// <inheritdoc />
    [JsonInclude] public ulong? Frequency { get; set; } = other.Frequency;
    /// <inheritdoc />
    [JsonInclude] public int? Group { get; set; } = other.Group;
    /// <inheritdoc />
    [JsonInclude] public int? Channel { get; set; } = other.Channel;
    /// <inheritdoc />
    [JsonInclude] public LockMode? LockMode { get; set; } = other.LockMode;
    /// <inheritdoc />
    [JsonInclude] public string? TransmitterType { get; init; } = other.TransmitterType;
    /// <inheritdoc />
    [JsonInclude] public float? BatteryLevel { get; init; } = other.BatteryLevel;

    public event PropertyChangedEventHandler? PropertyChanged;

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

/// <summary>
/// The data resulting from an RF spectrum scan.
/// </summary>
public struct RFScanData
{
    /// <summary>
    /// The recorded RF level samples at each sampled frequency.
    /// </summary>
    public List<Sample> Samples { get; internal set; }
    /// <summary>
    /// The frequency range covered by the scan.
    /// </summary>
    public FrequencyRange FrequencyRange { get; internal set; }
    /// <summary>
    /// The distance between RF samples in Hz.
    /// </summary>
    public ulong StepSize { get; internal set; }
    /// <summary>
    /// The percentage completion of the RF scan operation.
    /// </summary>
    public float Progress { get; internal set; }
    /// <summary>
    /// The current state of the RF scan operation.
    /// </summary>
    public Status State { get; internal set; }
    /// <summary>
    /// The error message related to the state of the RF scan operation.
    /// </summary>
    public string? StatusMessage { get; internal set; }

    /// <summary>
    /// An RF spectrum sample.
    /// </summary>
    /// <param name="freq">The frequency of the sample in Hz.</param>
    /// <param name="strength">The strength of the sample in dBm.</param>
    public readonly struct Sample(ulong freq, float strength)
    {
        /// <summary>
        /// The frequency of the sample in Hz.
        /// </summary>
        public readonly ulong Frequency { get; init; } = freq;
        /// <summary>
        /// The strength of the sample in dBm.
        /// </summary>
        public readonly float Strength { get; init; } = strength;
    }

    [JsonConverter(typeof(JsonStringEnumConverter<Status>))]
    public enum Status
    {
        Started,
        Running,
        Completed,
        Failure
    }
}

/// <summary>
/// A frequency range in Hz.
/// </summary>
/// <param name="startFreq">The lower bound of the tunable frequency range in Hz.</param>
/// <param name="endFreq">The upper bound of the tunable frequency range in Hz.</param>
public struct FrequencyRange(ulong startFreq, ulong endFreq)
{
    /// <summary>
    /// The lower bound of the tunable frequency range in Hz.
    /// </summary>
    public ulong StartFrequency { get; set; } = startFreq;
    /// <summary>
    /// The upper bound of the tunable frequency range in Hz.
    /// </summary>
    public ulong EndFrequency { get; set; } = endFreq;
}

[JsonConverter(typeof(JsonStringEnumConverter<IPMode>))]
public enum IPMode
{
    DHCP,
    Manual
}

/// <summary>
/// Represents an IPv4 address.
/// </summary>
[StructLayout(LayoutKind.Explicit)]
[JsonConverter(typeof(JsonStringConverter<IPv4Address>))]
public struct IPv4Address
{
    [FieldOffset(0)] public byte a;
    [FieldOffset(1)] public byte b;
    [FieldOffset(2)] public byte c;
    [FieldOffset(3)] public byte d;

    [FieldOffset(0)] public uint _data;

    public IPv4Address(uint data)
    {
        _data = data;
    }

    public IPv4Address(byte a, byte b, byte c, byte d, byte e, byte f)
    {
        this.a = a;
        this.b = b;
        this.c = c;
        this.d = d;
    }

    public IPv4Address(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ArgumentException("Expected an IPv4 address!");

        var dst = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref _data, 1));
        if (!address.TryWriteBytes(dst, out int _))
            throw new ArgumentException($"Failed to convert IP address '{address}' to IPv4Address!");
    }

    /// <inheritdoc cref="IPv4Address(ReadOnlySpan{char})"/>
    [JsonConstructor]
    public IPv4Address(string str) : this(str.AsSpan()) { }

    /// <summary>
    /// Parse an IPv4 address in the form 'aaa.bbb.ccc.ddd'.
    /// </summary>
    /// <param name="str"></param>
    /// <exception cref="ArgumentException"></exception>
    [JsonConstructor]
    public IPv4Address(ReadOnlySpan<char> str)
    {
        Span<Range> seps = stackalloc Range[4];
        int written = str.Split(seps, '.');
        if (written != 4)
            throw new ArgumentException($"Input string '{str}' is too short to parse as an IP address!");
        a = byte.Parse(str[seps[0]]);
        b = byte.Parse(str[seps[1]]);
        c = byte.Parse(str[seps[2]]);
        d = byte.Parse(str[seps[3]]);
        return;
    }

    public override readonly string ToString()
    {
        return $"{a}.{b}.{c}.{d}";
    }
}

/// <summary>
/// Represents a MAC address.
/// </summary>
[StructLayout(LayoutKind.Explicit)]
[JsonConverter(typeof(JsonStringConverter<MACAddress>))]
public struct MACAddress
{
    [FieldOffset(0)] public byte a;
    [FieldOffset(1)] public byte b;
    [FieldOffset(2)] public byte c;
    [FieldOffset(3)] public byte d;
    [FieldOffset(4)] public byte e;
    [FieldOffset(5)] public byte f;

    [FieldOffset(0)] public ulong _data;

    public MACAddress(ulong data)
    {
        _data = data;
    }

    public MACAddress(byte a, byte b, byte c, byte d, byte e, byte f)
    {
        this.a = a;
        this.b = b;
        this.c = c;
        this.d = d;
        this.e = e;
        this.f = f;
    }

    /// <inheritdoc cref="MACAddress(ReadOnlySpan{char})"/>
    [JsonConstructor]
    public MACAddress(string str) : this(str.AsSpan()) { }

    /// <summary>
    /// Parse a MAC address in the form 'aa:bb:cc:dd:ee:ff'.
    /// </summary>
    /// <param name="str"></param>
    /// <exception cref="ArgumentException"></exception>
    public MACAddress(ReadOnlySpan<char> str)
    {
        // TODO: Handle MAC addresses which don't have 2 chars per byte (including 1 and 0 chars per byte) ie: ff:a::22:1:1
        if (str.Length < 6 * 2 + 5)
            throw new ArgumentException($"Input string '{str}' is too short to parse as a MAC address!");

        var hex = System.Globalization.NumberStyles.HexNumber;
        a = byte.Parse(str[0..2], hex);
        b = byte.Parse(str[3..5], hex);
        c = byte.Parse(str[6..8], hex);
        d = byte.Parse(str[9..11], hex);
        e = byte.Parse(str[12..14], hex);
        f = byte.Parse(str[15..17], hex);
    }

    public override readonly string ToString()
    {
        return $"{a:X2}:{b:X2}:{c:X2}:{d:X2}:{e:X2}:{f:X2}";
    }
}
