using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace WirelessMicSuiteServer.Test;

internal class Program
{
    public static readonly object logLock = new();

    static void Main(string[] args)
    {
        Log("Starting WirelessMicSuite Test server...");
        var assembly = Assembly.GetExecutingAssembly();
        Log($"Version: {assembly.GetName().Version}; {FileVersionInfo.GetVersionInfo(assembly.Location).LegalCopyright}");

        ShureUHFREmulator shureEmu = new(2202);

        while (true)
        {
            Thread.Sleep(100);
        }
    }

    public static void Log(object message, LogSeverity severity = LogSeverity.Info, [CallerMemberName] string? caller = null, string? className = "Main")
    {
        Console.WriteLine($"[{DateTime.Now:T}] [{className}] [{caller}] {message}");
    }
}

public enum LogSeverity
{
    Debug,
    Info,
    Warning,
    Error
}
