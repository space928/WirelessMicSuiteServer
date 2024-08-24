using System.Collections.Concurrent;
using System.Collections.ObjectModel;

namespace WirelessMicSuiteServer.TestWirelessMic;

public class TestWirelessManager : IWirelessMicReceiverManager
{
    public int PollingPeriodMS { get; set; } = 100;
    public ObservableCollection<IWirelessMicReceiver> Receivers { get; init; }

    private readonly ConcurrentDictionary<uint, TestWirelessReceiver> receiversDict = [];
    private readonly Dictionary<uint, TestWirelessMic> micsDict = [];

    public TestWirelessManager(int micsToCreate = 16)
    {
        Receivers = [];
        Log($"Starting Test Wireless Mic server...");
        for (int i = 0; i < (micsToCreate + 1) / 2; i++)
        {
            var uid = ComputeReceiverUID(i);
            var rec = new TestWirelessReceiver(this, uid);
            receiversDict.TryAdd(uid, rec);
            Receivers.Add(rec);
        }
    }

    private readonly ILogger logger = Program.LoggerFac.CreateLogger<TestWirelessManager>();
    private void Log(string? message, LogSeverity severity = LogSeverity.Info)
    {
        logger.Log(message, severity);
    }

    public void Dispose()
    {
        
    }

    internal static uint CombineHash(uint a, uint b)
    {
        unchecked
        {
            uint hash = 17;
            hash = hash * 31 + a;
            hash = hash * 31 + b;
            return hash;
        }
    }

    private static uint ComputeReceiverUID(int index)
    {
        var typeHash = typeof(TestWirelessReceiver).GUID.GetHashCode();
        return CombineHash(unchecked((uint)index), unchecked((uint)typeHash));
    }

    public IWirelessMic? TryGetWirelessMic(uint uid)
    {
        return micsDict.GetValueOrDefault(uid);
    }

    public IWirelessMicReceiver? TryGetWirelessMicReceiver(uint uid)
    {
        return receiversDict.GetValueOrDefault(uid);
    }

    internal void RegisterWirelessMic(TestWirelessMic mic)
    {
        micsDict.Add(mic.UID, mic);
    }

    internal void UnregisterWirelessMic(TestWirelessMic mic)
    {
        micsDict.Remove(mic.UID);
    }
}
