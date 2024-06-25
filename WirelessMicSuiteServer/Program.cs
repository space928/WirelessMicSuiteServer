using System;
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

    /*public static void Log(object? message, LogSeverity severity = LogSeverity.Info, [CallerMemberName] string? caller = null, string? className = "Main")
    {
        lock (logLock)
        {
            Console.WriteLine($"[{DateTime.Now:T}] [{severity}] [{className}] [{caller}] {message}");
        }
    }*/
}
