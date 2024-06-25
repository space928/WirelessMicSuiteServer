namespace WirelessMicSuiteServer;

public static class WebAPI
{
    public static void AddWebRoots(WebApplication app, WirelessMicManager micManager)
    {
        /*app.MapGet("/wirelessReceivers", (HttpContext ctx) =>
        {
            return micManager.Receivers;
        }).WithName("GetWirelessReceivers")
        .WithOpenApi();*/

        app.MapGet("/wirelessMics", (HttpContext ctx) =>
        {
            return micManager.WirelessMics.Select(x => new WirelessMicData(x));
        }).WithName("GetWirelessMics")
        .WithOpenApi();

        //app.MapGet("/wirelessMic")
    }
}
