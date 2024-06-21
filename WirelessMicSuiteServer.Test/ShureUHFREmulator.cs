using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.IO.Pipelines;

namespace WirelessMicSuiteServer.Test;

internal class ShureUHFREmulator : IDisposable
{
    private const int MaxUDPSize = 0x10000;

    private readonly Socket socket;
    private readonly Task serverRxTask;
    private readonly Task serverTxTask;
    private readonly Task meterTask1;
    private readonly Task meterTask2;
    private readonly Decoder decoder;
    private readonly Encoder encoder;
    private readonly byte[] buffer;
    private readonly char[] charBuffer;
    private readonly CancellationToken shouldQuit;
    private readonly CancellationTokenSource shouldQuitSource;
    private readonly Pipe txPipe;
    private readonly Random random;

    private UHFRProperties props;

    public ShureUHFREmulator(int port)
    {
        Encoding encoding = Encoding.ASCII;
        buffer = new byte[MaxUDPSize];
        charBuffer = new char[encoding.GetMaxCharCount(buffer.Length)];
        txPipe = new();
        shouldQuitSource = new();
        shouldQuit = shouldQuitSource.Token;
        random = new();

        props = new();

        socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Any, port));

        decoder = encoding.GetDecoder();
        encoder = encoding.GetEncoder();

        Log($"Starting ShureUHFR Emulator on port {port}...");
        serverRxTask = Task.Run(ServerRxTask);
        serverTxTask = Task.Run(ServerTxTask);
        meterTask1 = Task.Run(() => MeterTask(0));
        meterTask2 = Task.Run(() => MeterTask(1));
    }

    private static void Log(object message, LogSeverity severity = LogSeverity.Info, [CallerMemberName] string? caller = null)
    {
        Program.Log(message, severity, caller, nameof(ShureUHFREmulator));
    }

    public void Dispose()
    {
        shouldQuitSource.Cancel();
        socket.Dispose();
        serverRxTask.Wait();
        serverRxTask.Dispose();
        serverTxTask.Wait();
        serverTxTask.Dispose();
    }

    private enum CommandType
    {
        GET,
        SET,
        METER,
        REPORT
    }

    private void ServerRxTask()
    {
        while (!shouldQuit.IsCancellationRequested)
        {
            int read;
            try
            {
                read = socket.Receive(buffer);
            }
            catch (Exception ex)
            {
                Log(ex, LogSeverity.Error);
                break;
            }

            int charsRead = decoder.GetChars(buffer, 0, read, charBuffer, 0);
            Span<char> str = charBuffer.AsSpan()[..charsRead];
            var fullMsg = str;
            Log($"Received: '{fullMsg}'", LogSeverity.Debug);

            // Commands should start with "* " and end with " *"
            if (str.Length < 4 || !str.StartsWith("* ") || !str.EndsWith(" *"))
            {
                Log($"Incoming message did not start and end with the correct header! Received: '{str}'", LogSeverity.Warning);
                continue;
            }
            else
            {
                str = str[2..-2];
            }

            // Commands start with one of "GET", "SET", or "METER"
            CommandType type;
            if (str.StartsWith("GET"))
            {
                type = CommandType.GET;
                str = str[4..];
            }
            else if (str.StartsWith("SET"))
            {
                type = CommandType.SET;
                str = str[4..];
            }
            else if (str.StartsWith("METER"))
            {
                type = CommandType.METER;
                str = str[6..];
            }
            else
            {
                Log($"Unknown command type '{str.ToString().Split(' ')[0]}'", LogSeverity.Warning);
                continue;
            }

            // This is followed by 1 or 2 for the receiver number
            if (str.Length < 2 || !int.TryParse(str[0..1], out int receiver) || receiver < 1 || receiver > 2)
            {
                Log($"Unknown receiver number '{(str.Length > 0 ? str[0..1] : "")}'", LogSeverity.Warning);
                continue;
            }
            else
            {
                str = str[2..];
            }

            // Next is the command itself
            int cmdEnd = str.IndexOf(' ');
            var cmd = cmdEnd == -1 ? str : str[..cmdEnd];
            str = str[cmdEnd..];
            ParseCommand(type, receiver-1, cmd, str, fullMsg);
        }
    }

    private void ParseCommand(CommandType type, int receiver, Span<char> cmd, Span<char> args, Span<char> fullMsg)
    {
        switch (cmd)
        {
            case "CHAN_NAME":
                if (type == CommandType.GET)
                {
                    Report(receiver, cmd, props.channelName[receiver]);
                    //Reply($"* REPORT {receiver} CHAN_NAME {props.channelName[receiver]} *");
                }
                else if (type == CommandType.SET)
                {
                    if (args.Length < 12)
                    {
                        CommandError(fullMsg);
                        break;
                    }

                    var name = args[..13];
                    props.channelName[receiver] = name.ToString().PadLeft(12);
                    Report(receiver, cmd, props.channelName[receiver]);
                    break;
                }
                else
                    CommandError(fullMsg);
                break;
            case "MUTE":
                if (type == CommandType.GET)
                {
                    Report(receiver, cmd, props.mute[receiver] ? "ON" : "OFF");
                }
                else if (type == CommandType.SET)
                {
                    switch (args)
                    {
                        case "ON":
                            props.mute[receiver] = true;
                            break;
                        case "OFF":
                            props.mute[receiver] = false;
                            break;
                        case "TOGGLE":
                            props.mute[receiver] = !props.mute[receiver];
                            break;
                        default:
                            CommandError(fullMsg);
                            break;
                    }
                    Report(receiver, cmd, props.mute[receiver] ? "ON" : "OFF");
                }
                else
                    CommandError(fullMsg);
                break;
            case "AUDIO_GAIN":
                if (type == CommandType.GET)
                {
                    Report(receiver, cmd, props.gain[receiver].ToString("00"));
                }
                else if (type == CommandType.SET)
                {
                    if (args.Length != 2 || int.TryParse(args, out int gain) || gain < 0 || gain > 32)
                    {
                        CommandError(fullMsg);
                        break;
                    }
                    props.gain[receiver] = gain;
                    Report(receiver, cmd, props.gain[receiver].ToString("00"));
                }
                else
                    CommandError(fullMsg);
                break;
            case "GAIN_UP":
                if (type == CommandType.SET)
                {
                    if (args.Length != 1 || int.TryParse(args, out int gain) || gain < 1 || gain > 5)
                    {
                        CommandError(fullMsg);
                        break;
                    }
                    props.gain[receiver] += gain;
                    Report(receiver, cmd, props.gain[receiver].ToString("00"));
                }
                else
                    CommandError(fullMsg);
                break;
            case "GAIN_DOWN":
                if (type == CommandType.SET)
                {
                    if (args.Length != 1 || int.TryParse(args, out int gain) || gain < 1 || gain > 5)
                    {
                        CommandError(fullMsg);
                        break;
                    }
                    props.gain[receiver] -= gain;
                    Report(receiver, cmd, props.gain[receiver].ToString("00"));
                }
                else
                    CommandError(fullMsg);
                break;
            case "GROUP_CHAN":
                if (type == CommandType.GET)
                {
                    int g = props.group[receiver];
                    int c = props.chan[receiver];
                    int f = props.freq[receiver];
                    Report(receiver, cmd, $"{g:00} {c:00}");
                    Report(receiver, "FREQUENCY", $"{f:000000}");
                }
                else if (type == CommandType.SET)
                {
                    if (args.Length != 5 || int.TryParse(args[..2], out int g) || int.TryParse(args[3..], out int c))
                    {
                        CommandError(fullMsg);
                        break;
                    }
                    props.SetGroupChan(receiver, g, c);
                    Report(receiver, cmd, $"{g:00} {c:00}");
                    Report(receiver, "FREQUENCY", $"{props.freq[receiver]:000000}");
                }
                else
                    CommandError(fullMsg);
                break;
            case "GROUP_UP":
                if (type == CommandType.SET)
                {
                    int g = props.group[receiver];
                    int c = props.chan[receiver];
                    props.SetGroupChan(receiver, g+1, c);
                    Report(receiver, cmd, $"{g:00} {c:00}");
                    Report(receiver, "FREQUENCY", $"{props.freq[receiver]:000000}");
                }
                else
                    CommandError(fullMsg);
                break;
            case "GROUP_DOWN":
                if (type == CommandType.SET)
                {
                    int g = props.group[receiver];
                    int c = props.chan[receiver];
                    props.SetGroupChan(receiver, g - 1, c);
                    Report(receiver, cmd, $"{g:00} {c:00}");
                    Report(receiver, "FREQUENCY", $"{props.freq[receiver]:000000}");
                }
                else
                    CommandError(fullMsg);
                break;
            case "CHAN_UP":
                if (type == CommandType.SET)
                {
                    int g = props.group[receiver];
                    int c = props.chan[receiver];
                    props.SetGroupChan(receiver, g, c+1);
                    Report(receiver, cmd, $"{g:00} {c:00}");
                    Report(receiver, "FREQUENCY", $"{props.freq[receiver]:000000}");
                }
                else
                    CommandError(fullMsg);
                break;
            case "CHAN_DOWN":
                if (type == CommandType.SET)
                {
                    int g = props.group[receiver];
                    int c = props.chan[receiver];
                    props.SetGroupChan(receiver, g - 1, c);
                    Report(receiver, cmd, $"{g:00} {c:00}");
                    Report(receiver, "FREQUENCY", $"{props.freq[receiver]:000000}");
                }
                else
                    CommandError(fullMsg);
                break;
            case "TX_BAT":
                if (type == CommandType.GET)
                {
                    int b = props.batt[receiver];
                    Report(receiver, cmd, b < 1 ? "U" : b.ToString());
                }
                else
                    CommandError(fullMsg);
                break;
            case "TX_TYPE":
                if (type == CommandType.GET)
                {
                    Report(receiver, cmd, props.type[receiver]);
                }
                else
                    CommandError(fullMsg);
                break;
            case "ALL":
                if (type == CommandType.METER)
                {
                    if (args.SequenceEqual("STOP"))
                    {
                        props.meter[receiver] = -1;
                        break;
                    }

                    if (args.Length != 3 || int.TryParse(args, out int speed) || speed < 0 || speed > 999)
                    {
                        CommandError(fullMsg);
                        break;
                    }

                    props.meter[receiver] = speed;
                    ReportMeter(receiver);
                } 
                else
                    CommandError(fullMsg);
                break;
        }
    }

    private static void CommandError(ReadOnlySpan<char> str)
    {
        Log($"Unsupported command '{str}'", LogSeverity.Warning);
    }

    private void Report(int channel, ReadOnlySpan<char> cmd, string msg)
    {
        Reply($"* REPORT {channel+1} {cmd} {msg} *");
    }

    private void ReportMeter(int channel)
    {
        Report(channel, "CHAN_NAME", props.channelName[channel]);
        Report(channel, "AUDIO_GAIN", $"{props.gain[channel]:00}");
        Report(channel, "GROUP_CHAN", $"{props.group[channel]:00} {props.chan[channel]:00}");
        Report(channel, "FREQUENCY", $"{props.freq[channel]:000000}");
        Report(channel, "MUTE", props.mute[channel]?"ON":"OFF");
    }

    private void Reply(string message)
    {
        Log($"  Replying with: '{message}'", LogSeverity.Debug);
        var dst = txPipe.Writer.GetSpan(encoder.GetByteCount(message, true));
        int written = encoder.GetBytes(message, dst, true);
        txPipe.Writer.Advance(written);
    }

    private void MeterTask(int channel)
    {
        while (!shouldQuit.IsCancellationRequested)
        {
            int meterTime = props.meter[channel];
            // If metering is disabled or if the metering time > 12 seconds, just wait...
            if (meterTime < 0 || meterTime >= 400)
            {
                Thread.Sleep(100);
                continue;
            }

            int rnd = random.Next();
            string rfLevelA = (random.Next(7)) switch
            {
                0 => "020",
                1 => "070",
                2 => "075",
                3 => "080",
                4 => "085",
                5 => "090",
                6 => "100",
                _ => "100"
            };
            string rfLevelB = (random.Next(7)) switch
            {
                0 => "020",
                1 => "070",
                2 => "075",
                3 => "080",
                4 => "085",
                5 => "090",
                6 => "100",
                _ => "100"
            };
            int b = props.batt[channel];

            Reply($"* SAMPLE {channel + 1} ALL " +
                $"{((rnd&1) != 0 ? 'X' : 'A')}{((rnd & 2) != 0 ? 'X' : 'B')}" +
                $"{rfLevelA} {rfLevelB}" +
                $"{(b < 1 ? 'U' : b)}" +
                $"{(rnd >> 2) & 0xff}" +
                $" *");

            Thread.Sleep(meterTime * 30);
        }
    }

    private async Task ServerTxTask()
    {
        while (!shouldQuit.IsCancellationRequested)
        {
            var msg = await txPipe.Reader.ReadAsync(shouldQuit);
            foreach (var part in msg.Buffer)
                await socket.SendAsync(part);
        }
    }
}

internal struct UHFRProperties
{
    public string[] channelName = ["Chan1       ", "Chan2       "];
    public bool[] mute = [false, false];
    public int[] gain = [0, 0];
    public int[] chan = [0, 0];
    public int[] group = [0, 0];
    public int[] freq = [600000, 600000];
    public int[] batt = [3, 5];
    public string[] type = ["UR1", "UR2"];
    public int[] meter = [-1, -1];

    public UHFRProperties() { }

    public void SetGroupChan(int receiver, int group, int chan)
    {
        this.group[receiver] = Math.Clamp(group, 0, 99);
        this.chan[receiver] = Math.Clamp(chan, 0, 99);
        freq[receiver] = group * 1000 + chan * 100 + 600000;
    }
}
