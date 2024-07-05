using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using WirelessMicSuiteServer;

namespace WirelessMicSuiteServer.Test;

internal class TestMDNSClient
{
    internal static void Test()
    {
        Task.Run(() =>
        {
            MDNSClient client = new();
            client.OnMDNSMessage += (msg) =>
            {
                Log($"Received mDNS response: \n{string.Join('\n', msg.answers.Select(a => $"\t{a.name} [{a.type}] \n{a.record}"))}");
            };

            //client.SendQuery(new DNSRecordMessage("_ssc._udp.local"));
            //client.SendQuery(new MDNSQuery("_ssc-https._tcp.local"), 0x3920);

            while (true)
            {
                client.SendQuery(new DNSRecordMessage("_ssc._udp.local"));

                Thread.Sleep(1000);
            }
        });
    }

    private static void Log(object message, LogSeverity severity = LogSeverity.Info, [CallerMemberName] string? caller = null)
    {
        Program.Log(message, severity, caller, nameof(TestMDNSClient));
    }
}
