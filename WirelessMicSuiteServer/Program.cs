﻿using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace WirelessMicSuiteServer;

public class Program
{
    //public static readonly object logLock = new();
    public static ILoggerFactory LoggerFac { get; private set; } = LoggerFactory.Create(builder => builder.AddWebLoggerFormatter(options => { }));
    private readonly static ILogger logger = LoggerFac.CreateLogger(nameof(Program));

    public static void Main(string[] args)
    {
        Log("Starting Wireless Mic Suite server...");
        var assembly = Assembly.GetExecutingAssembly();
        string? copyright = string.IsNullOrEmpty(assembly.Location) ? "Copyright Thomas Mathieson 2024" : FileVersionInfo.GetVersionInfo(assembly.Location).LegalCopyright;
        Log($"Version: {assembly.GetName().Version}; {copyright}");

        WirelessMicManager micManager = new([new ShureUHFRManager()]);
        if (args.Contains("-m"))
            Task.Run(() => MeterTask(micManager));
        StartWebServer(args, micManager);
    }

    private static void StartWebServer(string[] args, WirelessMicManager micManager)
    {
        var builder = WebApplication.CreateBuilder(args);
        //builder.Logging.ClearProviders();
        builder.Logging.AddWebLoggerFormatter(x => { });

        // Add services to the container.
        builder.Services.AddAuthorization();

        // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        var app = builder.Build();

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseHttpsRedirection();

        app.UseAuthorization();

        WebAPI.AddWebRoots(app, micManager);

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
