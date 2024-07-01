using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
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

        Log("List network interfaces: ");
        List<IPAddress> addresses = [];
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            var nicAddr = nic.GetIPProperties().UnicastAddresses.Select(x => x.Address);
            addresses.AddRange(nicAddr);
            Log($"\t{nic.Name}: {string.Join(", ", nicAddr)}");
        }

        ShureUHFREmulator shureEmu = new(addresses, 2202);

        TestMDNSClient.Test();

        while (true)
        {
            Thread.Sleep(100);
        }
    }

    public static void Log(object message, LogSeverity severity = LogSeverity.Info, [CallerMemberName] string? caller = null, string? className = "Main")
    {
        lock (logLock)
        {
            Console.WriteLine($"[{DateTime.Now:T}] [{severity}] [{className}] [{caller}] {message}");
        }
    }
}

public enum LogSeverity
{
    Debug,
    Info,
    Warning,
    Error
}
