using System.Collections;
using System.Collections.ObjectModel;
using System.Net;

namespace WirelessMicSuiteServer;

public class WirelessMicManager : IDisposable
{
    private readonly LocalSynchronizationContext syncContext;
    private readonly List<IWirelessMicReceiverManager> receiverManagers;
    private readonly ObservableCollection<IWirelessMicReceiver> receivers;

    //public IEnumerable<IWirelessMicReceiver> Receivers => receiverManagers.SelectMany(x=>x.Receivers);
    //public ObservableCollection<IWirelessMicReceiver> Receivers { get; init; }
    public ReadOnlyObservableCollection<IWirelessMicReceiver> Receivers { get; init; }
    // TODO: There's a race condition where if receivers get added or removed while this is being enumerated an exception is thrown.
    public IEnumerable<IWirelessMic> WirelessMics => new WirelessMicEnumerator(Receivers, syncContext);

    public WirelessMicManager(IEnumerable<IWirelessMicReceiverManager>? receiverManagers)
    {
        syncContext = new(CancellationToken.None);
        var managerThread = new Thread(syncContext.Run);
        managerThread.IsBackground = true;
        managerThread.Name = "WirelessMicManager";
        managerThread.Start();

        this.receiverManagers = receiverManagers?.ToList() ?? [];
        receivers = [];
        Receivers = new ReadOnlyObservableCollection<IWirelessMicReceiver>(receivers);
        foreach (var rm in this.receiverManagers)
        {
            // Attempt to synchronise the observable collections
            rm.Receivers.CollectionChanged += (o, e) =>
            {
                syncContext.Post(SyncReceivers, e);
            };
        }
        foreach(var r in this.receiverManagers.SelectMany(x => x.Receivers))
            receivers.Add(r);
    }

    private readonly ILogger logger = Program.LoggerFac.CreateLogger<WirelessMicManager>();
    private void Log(string? message, LogSeverity severity = LogSeverity.Info)
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

    private void SyncReceivers(object? x)
    {
        var e = (System.Collections.Specialized.NotifyCollectionChangedEventArgs)x!;
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

    private struct WirelessMicEnumerator : IEnumerable<IWirelessMic>, IEnumerator<IWirelessMic>, IEnumerator
    {
        private readonly List<IWirelessMicReceiver> receivers;
        private int currentMic = 0;
        private int currentRec = 0;

        public readonly IWirelessMic Current => receivers[currentRec]?.WirelessMics[currentMic] ?? throw new InvalidOperationException();

        readonly object IEnumerator.Current => Current;

        public WirelessMicEnumerator(IList<IWirelessMicReceiver> receivers, SynchronizationContext syncContext)
        {
            this.receivers = [];
            // Grab a copy of the receiver list on the main thread.
            syncContext.Send(x =>
            {
                var (src, dst) = ((IList<IWirelessMicReceiver>, List<IWirelessMicReceiver>))x!;
                dst.AddRange(src);
            }, (receivers, this.receivers));
        }

        public void Dispose() { }

        public bool MoveNext()
        {
            if (currentRec >= 0 && currentRec < receivers.Count
                && currentMic < receivers[currentRec].NumberOfChannels - 1)
            {
                currentMic++;
                return true;
            }
            else
            {
                if (currentRec < receivers.Count - 1)
                {
                    currentMic = 0;
                    currentRec++;
                    return true;
                }
                return false;
            }
        }

        public void Reset()
        {
            currentMic = 0;
            currentRec = -1;
        }

        public IEnumerator<IWirelessMic> GetEnumerator() => this;

        IEnumerator IEnumerable.GetEnumerator() => this;
    }
}
