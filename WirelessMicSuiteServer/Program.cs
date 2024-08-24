using Microsoft.AspNetCore.RateLimiting;
using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using WirelessMicSuiteServer.TestWirelessMic;

namespace WirelessMicSuiteServer;

public class Program
{
    //public static readonly object logLock = new();
    public static ILoggerFactory LoggerFac { get; private set; } = LoggerFactory.Create(builder => builder.AddWebLoggerFormatter(options => { }));
    private readonly static ILogger logger = LoggerFac.CreateLogger(nameof(Program));

    public static void Main(string[] args)
    {
        var cli = CreateCommandLineArgs(args);
        if (cli.ParsedArgs.ContainsKey("--help"))
            return;

        Log("Starting Wireless Mic Suite server...");
        var assembly = Assembly.GetExecutingAssembly();
        string? copyright = string.IsNullOrEmpty(assembly.Location) ? "Copyright Thomas Mathieson 2024" : FileVersionInfo.GetVersionInfo(assembly.Location).LegalCopyright;
        Log($"Version: {assembly.GetName().Version}; {copyright}");

        int meterInterval = 50;
        if (cli.ParsedArgs.TryGetValue("--meter-interval", out object? meterIntervalArg))
            meterInterval = (int)(uint)meterIntervalArg!;
        Log($"List options: ");
        foreach (var a in cli.ParsedArgs)
            Log($"\t{a.Key} = {a.Value}");

        WirelessMicManager micManager = new([
            new ShureUHFRManager() { PollingPeriodMS = meterInterval },
            new SennheiserSSCManager((string?)cli.ParsedArgs.GetValueOrDefault("--nic-address")) { PollingPeriodMS = meterInterval },
            new TestWirelessManager((int)(cli.ParsedArgs["--test-mic-count"] as uint? ?? 0)) { PollingPeriodMS = meterInterval },
        ]);
        WebSocketAPIManager wsAPIManager = new(micManager, meterInterval);
        if (cli.ParsedArgs.ContainsKey("--meters"))
            Task.Run(() => MeterTask(micManager));
        StartWebServer(args, micManager, wsAPIManager, cli);
    }

    private static CommandLineOptions CreateCommandLineArgs(string[] args)
    {
        return new CommandLineOptions([
            new("--meters", "-m", help: "Displays ASCII-art meters in the terminal for the connected wireless mics. Don't use this in production."),
            new("--meter-interval", "-i", argType: CommandLineArgType.Uint, defaultValue: 50u, help:"Sets the interval at which metering information should be polled from the wireless receivers. This is specified in milli-seconds."),
            new("--save-dir", "-s", argType: CommandLineArgType.String, defaultValue: "save", help:"Specifies a directory to save."),
            new("--nic-address", "-a", argType: CommandLineArgType.String, defaultValue: null, help:"Specifies the IP address of the network adapter to use."),
            new("--test-mic-count", "-t", argType: CommandLineArgType.Uint, defaultValue: 0u, help:"Specifies how many demo wireless mics to create.")
        ], args);
    }

    private static void StartWebServer(string[] args, WirelessMicManager micManager, WebSocketAPIManager wsAPIManager, CommandLineOptions cli)
    {
        var builder = WebApplication.CreateBuilder(args);
        //builder.Logging.ClearProviders();
        builder.Logging.AddWebLoggerFormatter(x => { });

        // Add services to the container.
        builder.Services.AddAuthorization();

        builder.Services.AddRateLimiter(x =>
        {
            x.AddFixedWindowLimiter("files", options =>
            {
                options.PermitLimit = 4;
                options.QueueLimit = 8;
                options.QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst;
                options.Window = TimeSpan.FromSeconds(1);
            });
        });

        // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
        builder.Services.AddEndpointsApiExplorer();
        WebAPI.AddSwaggerGen(builder.Services);

        var app = builder.Build();

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        //app.UseHttpsRedirection();

        app.UseAuthorization();

        var webSocketOptions = new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromMinutes(2)
        };

        app.UseWebSockets(webSocketOptions);

        //app.Environment.WebRootPath = "static";
        app.UseStaticFiles(new StaticFileOptions() { });
        app.UseDefaultFiles();

        app.UseRateLimiter();

        WebAPI.AddWebRoots(app, micManager, wsAPIManager, cli);

        app.Run();
    }

    public static void Log(string? message, LogSeverity severity = LogSeverity.Info)
    {
        logger.Log(message, severity);
    }

    private static void MeterTask(WirelessMicManager manager)
    {
        while (true)
        {
            // This code is very racey, no good way to lock the console logger though...
            int i = 0;
            int leftPos = Console.BufferWidth - 8 - 1;
            int sl = Console.CursorLeft;
            int st = Console.CursorTop;
            try
            {
                foreach (var mic in manager.WirelessMics)
                {
                    MeteringData data = mic.LastMeterData ?? default;
                    DrawMeter(leftPos, 0, i, data);

                    i++;
                    leftPos -= 8;
                }
            }
            catch { }
            Console.CursorLeft = sl;
            Console.CursorTop = st;

            Thread.Sleep(20);
        }
    }

    /*
     * ┌──────┐
     * │ ║║ ║ │
     * │ ║║ ║ │
     * │ ║║ ║ │
     * │ ║║ ║ │
     * │ ║║ ║ │
     * │ AB L │
     * │ 02_S │
     * └──────┘
     */
    private static void DrawMeter(int left, int top, int index, MeteringData sample)
    {
        //int width = 8;
        //int height = 8;

        Console.CursorLeft = left;
        Console.CursorTop = top;
        Console.Write("┌──────┐");
        Console.CursorTop++;
        Console.CursorLeft = left;
        int meterHeight = 10;
        for (int i = meterHeight-1; i >= 0; i--)
        {
            char a = sample.rssiA * (meterHeight - 0.5) > i ? '║' : ' ';
            char b = sample.rssiB * (meterHeight - 0.5) > i ? '║' : ' ';
            char l = Math.Sqrt(sample.audioLevel) * (meterHeight - 0.5) > i ? '║' : ' ';
            Console.Write($"│ {a}{b} {l} │");
            Console.CursorTop++;
            Console.CursorLeft = left;
        }
        Console.Write("│ AB L │");
        Console.CursorTop++;
        Console.CursorLeft = left;
        Console.Write($"│ {index,4} │");
        Console.CursorTop++;
        Console.CursorLeft = left;
        Console.Write("└──────┘");
    }

    /*public static void Log(object? message, LogSeverity severity = LogSeverity.Info, [CallerMemberName] string? caller = null, string? className = "Main")
    {
        lock (logLock)
        {
            Console.WriteLine($"[{DateTime.Now:T}] [{severity}] [{className}] [{caller}] {message}");
        }
    }*/
}
