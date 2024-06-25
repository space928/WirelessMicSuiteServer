using System.Collections;
using System.Net;

namespace WirelessMicSuiteServer;

public class WirelessMicManager : IDisposable
{
    private readonly List<IWirelessMicReceiverManager> receiverManagers;

    public IEnumerable<IWirelessMicReceiver> Receivers => receiverManagers.SelectMany(x=>x.Receivers);
    // TODO: There's a race condition where if receivers get added or removed while this is being enumerated an exception is thrown.
    public IEnumerable<IWirelessMic> WirelessMics => new WirelessMicEnumerator(Receivers);

    public WirelessMicManager(IEnumerable<IWirelessMicReceiverManager>? receiverManagers)
    {
        this.receiverManagers = receiverManagers?.ToList() ?? [];
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
