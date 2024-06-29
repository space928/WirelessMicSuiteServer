using System.Collections;
using System.Collections.ObjectModel;
using System.Net;

namespace WirelessMicSuiteServer;

public class WirelessMicManager : IDisposable
{
    private readonly List<IWirelessMicReceiverManager> receiverManagers;
    private readonly ObservableCollection<IWirelessMicReceiver> receivers;

    //public IEnumerable<IWirelessMicReceiver> Receivers => receiverManagers.SelectMany(x=>x.Receivers);
    //public ObservableCollection<IWirelessMicReceiver> Receivers { get; init; }
    public ReadOnlyObservableCollection<IWirelessMicReceiver> Receivers { get; init; }
    // TODO: There's a race condition where if receivers get added or removed while this is being enumerated an exception is thrown.
    public IEnumerable<IWirelessMic> WirelessMics  
    {
        get
        {
            lock (receivers)
            {
                return new WirelessMicEnumerator(Receivers);
            }
        }
    }

    public WirelessMicManager(IEnumerable<IWirelessMicReceiverManager>? receiverManagers)
    {
        this.receiverManagers = receiverManagers?.ToList() ?? [];
        receivers = [];
        Receivers = new ReadOnlyObservableCollection<IWirelessMicReceiver>(receivers);
        foreach (var rm in this.receiverManagers)
        {
            // Attempt to synchronise the observable collections
            rm.Receivers.CollectionChanged += (o, e) =>
            {
                lock (receivers)
                {
                    switch (e.Action)
                    {
                        case System.Collections.Specialized.NotifyCollectionChangedAction.Add:
                            if (e.NewItems != null)
                                foreach (IWirelessMicReceiver obj in e.NewItems)
                                    receivers.Add(obj);
                            break;
                        case System.Collections.Specialized.NotifyCollectionChangedAction.Remove:
                            if (e.OldItems != null)
                                foreach (IWirelessMicReceiver obj in e.OldItems)
                                    receivers.Remove(obj);
                            break;
                        case System.Collections.Specialized.NotifyCollectionChangedAction.Replace:
                            if (e.OldItems != null && e.NewItems != null && e.OldItems.Count == e.NewItems.Count)
                                for (int i = 0; i < e.OldItems.Count; i++)
                                {
                                    receivers.Remove((IWirelessMicReceiver)e.OldItems[i]!);
                                    receivers.Add((IWirelessMicReceiver)e.NewItems[i]!);
                                }
                            break;
                        case System.Collections.Specialized.NotifyCollectionChangedAction.Move:
                            break;
                        case System.Collections.Specialized.NotifyCollectionChangedAction.Reset:
                            throw new NotSupportedException();
                        default:
                            throw new InvalidOperationException();
                    }
                }
            };
        }
        foreach(var r in this.receiverManagers.SelectMany(x => x.Receivers))
            receivers.Add(r);
    }

    private readonly ILogger logger = Program.LoggerFac.CreateLogger<WirelessMicManager>();
    public void Log(string? message, LogSeverity severity = LogSeverity.Info)
    {
        logger.Log(message, severity);
    }

    public void Dispose()
    {
        foreach (var man in receiverManagers)
            man.Dispose();
    }

    public IWirelessMicReceiver? TryGetWirelessMicReceiver(uint uid)
    {
        foreach (var manager in receiverManagers)
        {
            var r = manager.TryGetWirelessMicReceiver(uid);
            if (r != null)
                return r;
        }
        return null;
    }

    public IWirelessMic? TryGetWirelessMic(uint uid)
    {
        foreach (var manager in receiverManagers)
        {
            var r = manager.TryGetWirelessMic(uid);
            if (r != null)
                return r;
        }
        return null;
    }

    private struct WirelessMicEnumerator : IEnumerable<IWirelessMic>, IEnumerator<IWirelessMic>, IEnumerator
    {
        private readonly IEnumerator<IWirelessMicReceiver> receivers;
        private IWirelessMicReceiver? currentReceiver;
        private int currentMic = 0;

        public readonly IWirelessMic Current => currentReceiver?.WirelessMics[currentMic] ?? throw new InvalidOperationException();

        readonly object IEnumerator.Current => throw new NotImplementedException();

        public WirelessMicEnumerator(IEnumerable<IWirelessMicReceiver> receivers)
        {
            this.receivers = receivers.GetEnumerator();
            currentReceiver = null;
        }

        public void Dispose() { }

        public bool MoveNext()
        {
            if (currentReceiver !=  null && currentMic < currentReceiver.NumberOfChannels - 1)
                currentMic++;
            else
            {
                currentMic = 0;
                if (receivers.MoveNext())
                    currentReceiver = receivers.Current;
                else
                {
                    currentReceiver = null;
                    return false;
                }
            }
            return true;
        }

        public void Reset()
        {
            currentMic = 0;
            receivers.Reset();
            currentReceiver = null;//receivers.Current;
        }

        public IEnumerator<IWirelessMic> GetEnumerator() => this;

        IEnumerator IEnumerable.GetEnumerator() => this;
    }
}
