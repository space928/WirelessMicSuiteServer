using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Timers;
using Timer = System.Timers.Timer;

namespace WirelessMicSuiteServer;

public class WebSocketAPI : IDisposable
{
    private const int MaxReceiveSize = 0x10000;

    private readonly WebSocket socket;
    private readonly Task rxTask;
    private readonly Task txTask;
    private readonly CancellationToken cancellationToken;
    private readonly CancellationTokenSource cancellationTokenSource;
    private readonly Decoder decoder;
    private readonly Encoder encoder;
    private readonly byte[] buffer;
    private readonly char[] charBuffer;
    private readonly ConcurrentQueue<byte[]> txPipe;
    private readonly SemaphoreSlim txAvailableSem;
    private readonly TaskCompletionSource taskCompletion;
    private readonly WebSocketAPIManager manager;

    public WebSocketAPI(WebSocket webSocket, TaskCompletionSource tcs, WebSocketAPIManager manager)
    {
        socket = webSocket;
        cancellationTokenSource = new ();
        cancellationToken = cancellationTokenSource.Token;
        txPipe = new();
        txAvailableSem = new(0);
        taskCompletion = tcs;

        Encoding encoding = Encoding.ASCII;
        buffer = new byte[MaxReceiveSize];
        charBuffer = new char[encoding.GetMaxCharCount(buffer.Length)];
        decoder = encoding.GetDecoder();
        encoder = encoding.GetEncoder();

        Log($"Opened WebSocket API...");

        rxTask = Task.Run(RxTask);
        txTask = Task.Run(TxTask);

        this.manager = manager;
        manager.RegisterWebSocket(this);
    }

    private readonly ILogger logger = Program.LoggerFac.CreateLogger<WebSocketAPI>();
    private void Log(string? message, LogSeverity severity = LogSeverity.Info)
    {
        logger.Log(message, severity);
    }

    public void Dispose()
    {
        Log($"WebSocket API closed...");
        manager.UnregisterWebSocket(this);
        cancellationTokenSource.Cancel();
        Task.WaitAll([rxTask, txTask], 1000);
        rxTask.Dispose();
        txTask.Dispose();
        cancellationTokenSource.Dispose();
        taskCompletion.SetResult();
    }

    private async void RxTask()
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var res = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (!res.EndOfMessage)
                {
                    Log($"Received incomplete WebSocket message! Ignoring...", LogSeverity.Warning);
                    continue;
                }
                if (res.CloseStatus != null)
                {
                    cancellationTokenSource.Cancel();
                    break;
                }
                if (res.MessageType != WebSocketMessageType.Text)
                {
                    Log($"Unpexpected WebSocket message type '{res.MessageType}'! Ignoring...", LogSeverity.Warning);
                    continue;
                }

                int charsRead = decoder.GetChars(buffer, 0, res.Count, charBuffer, 0);
                var str = charBuffer.AsMemory()[..charsRead];
                Log($"Received: '{str}'", LogSeverity.Debug);
            } catch
            {
                cancellationTokenSource.Cancel();
                break;
            }
        }
    }

    private void TxTask() 
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                byte[] msg;
                while (!txPipe.TryDequeue(out msg!))
                    txAvailableSem.Wait(1000);
                socket.SendAsync(msg, WebSocketMessageType.Text, true, cancellationToken)
                    .Wait();
            }
        } catch
        {
            cancellationTokenSource.Cancel();
        }
    }

    internal void SendMessage(byte[] message)
    {
        // I want to be able to send the same byte[] message to multiple clients, so tracking when they've each
        // finished using the byte[] is difficult, hence we'll rely on the GC. Maybe this could be improved in
        // the future, profiling needed.
        txPipe.Enqueue(message);
        txAvailableSem.Release();
    }
}

public class WebSocketAPIManager : IDisposable
{
    private readonly Dictionary<(Type type, string propName), (PropertyInfo prop, string normalizedName)> propCache;
    private readonly WirelessMicManager micManager;
    private readonly List<WebSocketAPI> clients;
    private readonly Timer meteringTimer;
    private bool isSendingMeteringMsg;
    private readonly JsonSerializerOptions jsonSerializerOptions;

    public WebSocketAPIManager(WirelessMicManager micManager, int meterInterval)
    {
        this.micManager = micManager;
        clients = [];
        propCache = [];
        BuildPropCache();

        jsonSerializerOptions = new()
        {
            IncludeFields = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        };

        ((INotifyCollectionChanged)micManager.Receivers).CollectionChanged += (o, e) =>
        {
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    if (e.NewItems != null)
                        foreach (IWirelessMicReceiver obj in e.NewItems)
                            RegisterPropertyChangeHandler(obj);
                    break;
                case NotifyCollectionChangedAction.Remove:
                    if (e.OldItems != null)
                        foreach (IWirelessMicReceiver obj in e.OldItems)
                            UnregisterPropertyChangeHandler(obj);
                    break;
                case NotifyCollectionChangedAction.Replace:
                    if (e.OldItems != null && e.NewItems != null && e.OldItems.Count == e.NewItems.Count)
                        for (int i = 0; i < e.OldItems.Count; i++)
                        {
                            UnregisterPropertyChangeHandler((IWirelessMicReceiver)e.OldItems[i]!);
                            RegisterPropertyChangeHandler((IWirelessMicReceiver)e.NewItems[i]!);
                        }
                    break;
                case NotifyCollectionChangedAction.Move:
                    break;
                case NotifyCollectionChangedAction.Reset:
                    throw new NotSupportedException();
                default:
                    throw new InvalidOperationException();
            }
        };

        isSendingMeteringMsg = false;
        meteringTimer = new(TimeSpan.FromMilliseconds(meterInterval))
        {
            AutoReset = true,
            Enabled = true,
        };
        meteringTimer.Elapsed += MeteringTimer_Elapsed;
        meteringTimer.Start();
    }

    private void MeteringTimer_Elapsed(object? sender, ElapsedEventArgs e)
    {
        if (isSendingMeteringMsg)
            return;
        isSendingMeteringMsg = true;
        try
        {
            var meterData = micManager.WirelessMics.Where(x => x.LastMeterData != null)
                .Select(x => new MeterInfoNotification(x.UID, x.LastMeterData!.Value));
            var json = JsonSerializer.SerializeToUtf8Bytes(meterData);

            foreach (var client in clients)
                client.SendMessage(json);
        }
        catch { }
        finally
        {
            isSendingMeteringMsg = false;
        }
    }

    private void RegisterPropertyChangeHandler(IWirelessMicReceiver receiver)
    {
        receiver.PropertyChanged += OnPropertyChanged;
        foreach (var mic in receiver.WirelessMics)
            mic.PropertyChanged += OnPropertyChanged;
    }

    private void UnregisterPropertyChangeHandler(IWirelessMicReceiver receiver)
    {
        receiver.PropertyChanged -= OnPropertyChanged;
        foreach (var mic in receiver.WirelessMics)
            mic.PropertyChanged -= OnPropertyChanged;
    }

    private void OnPropertyChanged(object? target, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == null || target == null)
            return;

        uint uid = 0;
        (PropertyInfo prop, string name) cached;
        if (target is IWirelessMic mic)
        {
            if (!propCache.TryGetValue((typeof(IWirelessMic), args.PropertyName), out cached!))
                return;
            uid = mic.UID;

        }
        else if (target is IWirelessMicReceiver rec)
        {
            if (!propCache.TryGetValue((typeof(IWirelessMicReceiver), args.PropertyName), out cached!))
                return;
            uid = rec.UID;
        }
        else
            return;

        object? val = cached.prop.GetValue(target);

        var propNotif = new PropertyChangeNotification(cached.name, val, uid);
        var json = JsonSerializer.SerializeToUtf8Bytes(propNotif, jsonSerializerOptions);

        foreach (var client in clients)
            client.SendMessage(json);
    }

    private void BuildPropCache()
    {
        var recProps = typeof(IWirelessMicReceiver).GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.GetProperty);
        var micProps = typeof(IWirelessMic).GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.GetProperty);

        foreach (var prop in recProps)
        {
            if (!CheckProp(prop, out var normalizedName))
                continue;

            propCache.Add((typeof(IWirelessMicReceiver), prop.Name), (prop, normalizedName));
        }

        foreach (var prop in micProps)
        {
            if (!CheckProp(prop, out var normalizedName))
                continue;

            propCache.Add((typeof(IWirelessMic), prop.Name), (prop, normalizedName));
        }

        static bool CheckProp(PropertyInfo prop, [NotNullWhen(true)] out string? normalizedName)
        {
            normalizedName = null;
            if (prop.GetCustomAttribute<JsonIgnoreAttribute>() != null)
                return false;
            if (prop.GetCustomAttribute<NotificationIgnoreAttribute>() != null)
                return false;
            normalizedName = JsonNamingPolicy.CamelCase.ConvertName(prop.Name);
            return true;
        }
    }

    public void RegisterWebSocket(WebSocketAPI webSocket)
    {
        lock (clients)
        {
            clients.Add(webSocket);
        }
    }

    public void UnregisterWebSocket(WebSocketAPI webSocket)
    {
        lock (clients)
        {
            clients.Remove(webSocket);
        }
    }

    public void Dispose()
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Marks the annotated field to NOT send property change notifications to the WebSocketAPI.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class NotificationIgnoreAttribute : Attribute { }

[Serializable]
public readonly struct PropertyChangeNotification(string propName, object? value, uint uid)
{
    /// <summary>
    /// The UID of the object (microphone or receiver) which was updated.
    /// </summary>
    public readonly uint UID { get; init; } = uid;
    /// <summary>
    /// The name of the property that was updated.
    /// </summary>
    public readonly string PropertyName { get; init; } = propName;
    /// <summary>
    /// The new value of the property.
    /// </summary>
    public readonly object? Value { get; init; } = value;
}

[Serializable]
public readonly struct MeterInfoNotification(uint uid, MeteringData metering)
{
    /// <summary>
    /// The UID of the microphone this data belongs too.
    /// </summary>
    public readonly uint UID { get; init; } = uid;
    /// <summary>
    /// The metered values.
    /// </summary>
    public readonly MeteringData MeteringData { get; init; } = metering;
}
