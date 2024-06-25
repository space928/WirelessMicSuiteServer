using System;
using System.Text;

namespace WirelessMicSuiteServer;

public static class WebAPI
{
    public static void AddWebRoots(WebApplication app, WirelessMicManager micManager)
    {
        app.MapGet("/getWirelessReceivers", (HttpContext ctx) =>
        {
            return micManager.Receivers.Select(x => new WirelessReceiverData(x));
        }).WithName("GetWirelessReceivers")
        .WithOpenApi();

        app.MapGet("/getWirelessMics", (HttpContext ctx) =>
        {
            return micManager.WirelessMics.Select(x => new WirelessMicData(x));
        }).WithName("GetWirelessMics")
        .WithOpenApi();

        app.MapGet("/getWirelessMicReceiver/{uid}", (uint uid, HttpContext ctx) =>
        {
            var rec = micManager.TryGetWirelessMicReceiver(uid);
            if (rec == null)
                return new WirelessReceiverData?();
            return new WirelessReceiverData(rec);
        }).WithName("getWirelessMicReceiver")
        .WithOpenApi();

        app.MapGet("/getWirelessMic/{uid}", (uint uid, HttpContext ctx) =>
        {
            var mic = micManager.TryGetWirelessMic(uid);
            if (mic == null)
                return new WirelessMicData?();
            return new WirelessMicData(mic);
        }).WithName("GetWirelessMic")
        .WithOpenApi();

        app.MapGet("/getMicMeter/{uid}", (uint uid, HttpContext ctx) =>
        {
            var mic = micManager.TryGetWirelessMic(uid);
            if (mic == null || mic.MeterData == null)
                return null;

            var samples = mic.MeterData.ToArray();
            // By not locking the collection, some samples could be lost...
            mic.MeterData.Clear();

            return samples;
        }).WithName("GetMicMeter")
        .WithOpenApi();

        app.MapGet("/getMicMeterAscii/{uid}", (uint uid, HttpContext ctx) =>
        {
            var mic = micManager.TryGetWirelessMic(uid);
            if (mic == null || mic.MeterData == null)
                return "";

            var sample = mic.LastMeterData ?? default;

            int meterHeight = 10;
            StringBuilder sb = new();
            sb.AppendLine("┌──────┐");
            for (int i = meterHeight - 1; i >= 0; i--)
            {
                char a = sample.rssiA * (meterHeight - 0.5) > i ? '║' : ' ';
                char b = sample.rssiB * (meterHeight - 0.5) > i ? '║' : ' ';
                char l = Math.Sqrt(sample.audioLevel) * (meterHeight - 0.5) > i ? '║' : ' ';
                sb.AppendLine($"│ {a}{b} {l} │");
            }
            sb.AppendLine("│ AB L │");
            sb.AppendLine($"│ {uid.ToString()[..4],4} │");
            sb.AppendLine("└──────┘");

            return sb.ToString();
        }).WithName("GetMicMeterAscii")
        .WithOpenApi();
    }
}
