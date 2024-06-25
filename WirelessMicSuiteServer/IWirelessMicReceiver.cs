using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace WirelessMicSuiteServer;

public interface IWirelessMicReceiverManager : IDisposable
{
    public abstract int PollingPeriodMS { get; set; }
    public abstract ObservableCollection<IWirelessMicReceiver> Receivers { get; init; }

    //public void DiscoverDevices();
    // public IEnumerable<IPEndPoint> EnumerateDevices();
}

public interface IWirelessMicReceiver : IDisposable
{
    //public static abstract IEnumerable<IPEndPoint> EnumerateDevices();
    
    public abstract uint UID { get; }
    public abstract IPEndPoint Address { get; }
    public abstract int NumberOfChannels { get; }
    public abstract IWirelessMic[] WirelessMics { get; }
}

public interface IWirelessMic : INotifyPropertyChanged
{
    public abstract IWirelessMicReceiver Receiver { get; }

    /// <summary>
    /// A unnique identifier for this wireless transmitter. Note that this is associated with the 
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
    public abstract int Gain { get; set; }
    /// <summary>
    /// Transmitter mute.
    /// </summary>
    public abstract bool Mute { get; set; }
    /// <summary>
    /// Transmitter RF frequency in Hz.
    /// </summary>
    public abstract ulong Frequency { get; set; }
    /// <summary>
    /// Transmitter frequency group.
    /// </summary>
    public abstract int Group { get; set; }
    /// <summary>
    /// Transmitter frequency channel, within the group.
    /// </summary>
    public abstract int Channel { get; set; }
    /// <summary>
    /// Transmitter model type identifier, ie: UR1, UR1H, UR2.
    /// </summary>
    public abstract string? TransmitterType { get; }
    /// <summary>
    /// Battery percentage.
    /// </summary>
    public abstract float BatteryLevel { get; }

    //public void Subscribe();
    //public void Unsubscribe();
    public void StartMetering(int periodMS);
    public void StopMetering();
}

public struct WirelessMicData : IWirelessMic
{
    [JsonIgnore] public IWirelessMicReceiver Receiver { get; init; }

    [JsonInclude] public readonly uint ReceiverID => Receiver.UID;
    [JsonInclude] public uint UID { get; init; }
    [JsonInclude] public string? Name { get; set; }
    [JsonInclude] public int Gain { get; set; }
    [JsonInclude] public bool Mute { get; set; }
    [JsonInclude] public ulong Frequency { get; set; }
    [JsonInclude] public int Group { get; set; }
    [JsonInclude] public int Channel { get; set; }
    [JsonInclude] public string? TransmitterType { get; init; }
    [JsonInclude] public float BatteryLevel { get; init; }


    public event PropertyChangedEventHandler? PropertyChanged;

    public WirelessMicData(IWirelessMic other)
    {
        Receiver = other.Receiver;
        UID = other.UID;
        Name = other.Name;
        Gain = other.Gain;
        Mute = other.Mute;
        Frequency = other.Frequency;
        Group = other.Group;
        Channel = other.Channel;
        TransmitterType = other.TransmitterType;
        BatteryLevel = other.BatteryLevel;
    }

    public void StartMetering(int periodMS)
    {
        throw new NotImplementedException();
    }

    public void StopMetering()
    {
        throw new NotImplementedException();
    }
}
