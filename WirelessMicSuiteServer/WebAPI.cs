using Microsoft.OpenApi.Models;
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;

namespace WirelessMicSuiteServer;

public static class WebAPI
{
    public static void AddSwaggerGen(IServiceCollection services)
    {
        services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo
            {
                Version = "v1",
                Title = "Wireless Mic Suite API",
                Description = "An API for interfacing with wireless microphone receivers.",
                Contact = new OpenApiContact
                {
                    Name = "Thomas Mathieson",
                    Email = "thomas@mathieson.dev",
                    Url = new Uri("https://github.com/space928/WirelessMicSuiteServer/issues")
                },
                License = new OpenApiLicense
                {
                    Name = "GPLv3",
                    Url = new Uri("https://github.com/space928/WirelessMicSuiteServer/blob/main/LICENSE.txt")
                },
            });

            // using System.Reflection;
            var xmlFilename = Path.Combine(AppContext.BaseDirectory, $"{Assembly.GetExecutingAssembly().GetName().Name}.xml");
            if (File.Exists(xmlFilename))
                c.IncludeXmlComments(xmlFilename);

            c.MapType<IPv4Address>(() => new OpenApiSchema { Type = "string" });
            c.MapType<MACAddress>(() => new OpenApiSchema { Type = "string" });
            //c.MapType<PropertyChangeNotification>(() => new OpenApiSchema {  });
        });
    }

    public static void AddWebRoots(WebApplication app, WirelessMicManager micManager, WebSocketAPIManager wsAPIManager, CommandLineOptions cli)
    {
        #region Getters
        app.MapGet("/getWirelessReceivers", (HttpContext ctx) =>
        {
            SetAPIHeaderOptions(ctx);
            return micManager.Receivers.Select(x => new WirelessReceiverData(x));
        }).WithName("GetWirelessReceivers")
        .WithDescription("Gets the configuration and status information of all wireless mic receivers.")
        .WithTags("Getters")
        .WithOpenApi();

        app.MapGet("/getWirelessMics", (HttpContext ctx) =>
        {
            SetAPIHeaderOptions(ctx);
            return micManager.WirelessMics.Select(x => new WirelessMicData(x));
        }).WithName("GetWirelessMics")
        .WithDescription("Gets the configuration and status information of all wireless mic receiver channels.")
        .WithTags("Getters")
        .WithOpenApi();

        app.MapGet("/getWirelessMicReceiver/{uid}", (uint uid, HttpContext ctx) =>
        {
            SetAPIHeaderOptions(ctx);
            var rec = micManager.TryGetWirelessMicReceiver(uid);
            if (rec == null)
                return new WirelessReceiverData?();
            return new WirelessReceiverData(rec);
        }).WithName("GetWirelessMicReceiver")
        .WithDescription("Gets the configuration and status information of a given wireless mic receiver.")
        .WithTags("Getters")
        .WithOpenApi();

        app.MapGet("/getWirelessMic/{uid}", (uint uid, HttpContext ctx) =>
        {
            SetAPIHeaderOptions(ctx);
            var mic = micManager.TryGetWirelessMic(uid);
            if (mic == null)
                return new WirelessMicData?();
            return new WirelessMicData(mic);
        }).WithName("GetWirelessMic")
        .WithDescription("Gets the configuration and status information of a given wireless mic receiver channel.")
        .WithTags("Getters")
        .WithOpenApi();

        app.MapGet("/getMicMeter/{uid}", (uint uid, HttpContext ctx) =>
        {
            SetAPIHeaderOptions(ctx);
            var mic = micManager.TryGetWirelessMic(uid);
            if (mic == null || mic.MeterData == null)
                return null;

            var samples = mic.MeterData.ToArray();
            // By not locking the collection, some samples could be lost...
            mic.MeterData.Clear();

            return samples;
        }).WithName("GetMicMeter")
        .WithDescription("Gets an array of RF/Audio meter samples. Returns up to the last 1024 unread meter samples.")
        .WithTags("Getters")
        .WithOpenApi();

        app.MapGet("/getMicMeterAscii/{uid}", (uint uid, HttpContext ctx) =>
        {
            SetAPIHeaderOptions(ctx);
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
        .WithDescription("Gets an ASCII-art representation of the RF/Audio meters for the given mic.")
        .WithTags("Getters")
        .WithOpenApi();

        app.MapGet("/rfScan/{uid}", (HttpContext ctx, uint uid, ulong? minFreq, ulong? maxFreq, ulong? stepSize = 25000) =>
        {
            SetAPIHeaderOptions(ctx);
            var mic = micManager.TryGetWirelessMic(uid);
            if (mic == null)
                return (RFScanData?)null;

            var scan = mic.RFScanData;
            if (scan.State == RFScanData.Status.Running)
            {
                var scanCpy = scan;
                scanCpy.Samples = [];
                return scanCpy;
            }
            if (minFreq != null && maxFreq != null && stepSize != null)
            {
                mic.StartRFScan(new FrequencyRange(minFreq.Value, maxFreq.Value), stepSize.Value);
                scan.FrequencyRange = new FrequencyRange(minFreq.Value, maxFreq.Value);
                scan.StepSize = stepSize.Value;
                scan.Samples = [];
                scan.Progress = 0;
                scan.State = RFScanData.Status.Started;
            }

            return scan;
        }).WithName("RFScan")
        .WithDescription("Starts an RF spectrum scan using the specified receiver. If the min/max freq fields are left " +
        "unset then this method returns the result/progress of the last RF scan from this receiver. Audio output is " +
        "automatically muted while the RF scan is ongoing.")
        .WithTags("Getters")
        .WithOpenApi();
        #endregion

        #region Setters
        var receiverProps = typeof(IWirelessMicReceiver).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.SetProperty);
        Dictionary<string, PropertyInfo> receiverSetters = new(receiverProps.Select(x => new KeyValuePair<string, PropertyInfo>(x.Name.ToLowerInvariant(), x)));
        app.MapGet("/setWirelessMicReceiver/{uid}/{param}/{value}", (uint uid, string param, string value, HttpContext ctx) =>
        {
            SetAPIHeaderOptions(ctx);
            var mic = micManager.TryGetWirelessMicReceiver(uid);
            if (mic == null)
                return new APIResult(false, $"Couldn't find wireless mic with UID 0x{uid:X}!");

            if (!receiverSetters.TryGetValue(param.ToLowerInvariant(), out var prop))
                return new APIResult(false, $"Property '{param}' does not exist!");

            try
            {
                var val = DeserializeSimpleType(value, prop.PropertyType);
                prop.SetValue(mic, val);
            }
            catch (Exception ex)
            {
                return new APIResult(false, ex.Message);
            }

            return new APIResult(true);
        }).WithName("SetWirelessMicReceiver")
        .WithDescription("Sets a named property to the given value on an IWirelessMicReceiver. See IWirelessMicReceiver for the full list of supported properties.")
        .WithTags("Setters")
        .WithOpenApi();

        var micProps = typeof(IWirelessMic).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.SetProperty);
        Dictionary<string, PropertyInfo> micSetters = new(micProps.Select(x => new KeyValuePair<string, PropertyInfo>(x.Name.ToLowerInvariant(), x)));
        app.MapGet("/setWirelessMic/{uid}/{param}/{value}", (uint uid, string param, string value, HttpContext ctx) =>
        {
            SetAPIHeaderOptions(ctx);
            var mic = micManager.TryGetWirelessMic(uid);
            if (mic == null)
                return new APIResult(false, $"Couldn't find wireless mic with UID 0x{uid:X}!");

            if (!micSetters.TryGetValue(param.ToLowerInvariant(), out var prop))
                return new APIResult(false, $"Property '{param}' does not exist!");

            try
            {
                var val = DeserializeSimpleType(value, prop.PropertyType);
                prop.SetValue(mic, val);
            } catch (Exception ex)
            {
                return new APIResult(false, ex.Message);
            }

            return new APIResult(true);
        }).WithName("SetWirelessMic")
        .WithDescription("Sets a named property to the given value on an IWirelessMic. See IWirelessMic for the full list of supported properties.")
        .WithTags("Setters")
        .WithOpenApi();

        app.MapGet("/identifyWirelessReceiver/{uid}", (uint uid, HttpContext ctx) =>
        {
            SetAPIHeaderOptions(ctx);
            var rec = micManager.TryGetWirelessMicReceiver(uid);
            if (rec == null)
                return new APIResult(false, $"Couldn't find wireless receiver with UID 0x{uid:X}!");

            rec.Identify();

            return new APIResult(true);
        }).WithName("IdentifyWirelessReceiver")
        .WithDescription("Blinks the LEDs on the given wireless mic receiver.")
        .WithTags("Setters")
        .WithOpenApi();

        app.MapGet("/rebootWirelessReceiver/{uid}", (uint uid, HttpContext ctx) =>
        {
            SetAPIHeaderOptions(ctx);
            var rec = micManager.TryGetWirelessMicReceiver(uid);
            if (rec == null)
                return new APIResult(false, $"Couldn't find wireless receiver with UID 0x{uid:X}!");

            rec.Reboot();

            return new APIResult(true);
        }).WithName("RebootWirelessReceiver")
        .WithDescription("Reboots the given wireless mic receiver.")
        .WithTags("Setters")
        .WithOpenApi();
        #endregion

        app.Map("/ws", async (HttpContext ctx) => 
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var socketFinishedTcs = new TaskCompletionSource();

            using WebSocketAPI wsAPI = new(ws, socketFinishedTcs, wsAPIManager);

            await socketFinishedTcs.Task;
        }).WithName("OpenWebSocket")
        .WithTags("WebSockets")
        .WithDescription("Opens a new WebSocket for receiving property change notifications and metering data.")
        .WithOpenApi();

        #region File Storage
        {
            string dir = (string)cli.ParsedArgs["--save-dir"]!;
            try
            {
                dir = Path.GetFullPath(dir);
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                    Log($"Save directory '{dir}' created!");
                } else
                {
                    Log($"Save directory is '{dir}'");
                }
            }
            catch (Exception ex)
            {
                Log($"Save directory '{dir}' does not exist and couldn't be created! Exception: {ex.Message}", LogSeverity.Warning);
            }
        }

        app.MapGet("/getSaveFiles/{query}", (HttpContext ctx, string ? query = null) =>
        {
            SetAPIHeaderOptions(ctx);

            string dir = (string)cli.ParsedArgs["--save-dir"]!;
            string q = (string.IsNullOrEmpty(query) || query == ",") ? "*.json" : query + ".json";

            try
            {
                return Directory.EnumerateFiles(dir, q, SearchOption.TopDirectoryOnly).Select(x => Path.GetFileNameWithoutExtension(x));
            } catch (Exception ex) 
            {
                Log($"Failed to enumerate save files '{dir}' query='{q}'. Exception: {ex.Message}", LogSeverity.Warning);
                ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
                return [];
            }
        }).WithName("GetSaveFiles")
        .WithDescription("Enumerates all JSON file matching a given query (supports wildcards) on the web server.")
        .WithTags("Files")
        .Produces(200, contentType: "application/json")
        .Produces(404, contentType: "application/json")
        .WithOpenApi()
        .RequireRateLimiting("files");

        app.MapGet("/getSaveFile/{name}", async (string name, HttpContext ctx) =>
        {
            SetAPIHeaderOptions(ctx);

            string dir = (string)cli.ParsedArgs["--save-dir"]!;
            string path = Path.Combine(dir, name + ".json");
            try
            {
                string data = await File.ReadAllTextAsync(path);
                ctx.Response.ContentType = "application/json";
                return data;
            } catch (Exception ex)
            {
                Log($"Failed to get save file '{path}'. Exception: {ex.Message}", LogSeverity.Warning);
                ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
                return "";
            }
        }).WithName("GetSaveFile")
        .WithDescription("Gets a saved JSON file with a given name from the web server.")
        .WithTags("Files")
        .Produces(200, contentType: "application/json")
        .Produces(404, contentType: "application/json")
        .RequireRateLimiting("files");

        app.MapGet("/getUniqueSaveFileName", (HttpContext ctx) =>
        {
            SetAPIHeaderOptions(ctx);

            string dir = (string)cli.ParsedArgs["--save-dir"]!;
            if (!Directory.Exists(dir))
                return "";

            for (int i = 0; i < 100; i++)
            {
                string fname = $"WMS_{DateTime.Now:s}_{i}";
                string path = Path.Combine(dir, fname + ".json");
                if (!File.Exists(path))
                    return fname;
            }

            return "";
        }).WithName("GetUniqueSaveFileName")
        .WithDescription("Gets a unique file name to be used to save a file to the web server.")
        .WithTags("Files")
        .WithOpenApi()
        .RequireRateLimiting("files");

        app.MapPut("/putSaveFile/{name}/{overwrite}", async (HttpContext ctx, string name, bool overwrite = false) =>
        {
            SetAPIHeaderOptions(ctx);

            string dir = (string)cli.ParsedArgs["--save-dir"]!;
            string path = Path.Combine(dir, name + ".json");
            try
            {
                if (ctx.Request.ContentType != "application/json")
                    throw new InvalidOperationException("Request ContentType was not 'application/json'!");

                var writeStream = new FileStream(path, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write);
                await ctx.Request.Body.CopyToAsync(writeStream);

                return new APIResult(true);
            }
            catch (Exception ex)
            {
                string msg = $"Failed to get save file '{path}'. Exception: {ex.Message}";
                Log(msg, LogSeverity.Warning);
                ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
                return new APIResult(false, msg);
            }
        }).WithName("PutSaveFile")
        .WithDescription("Saves an arbitrary JSON file with a given name to the web server.")
        .WithTags("Files")
        .Produces(200, contentType: "application/json")
        .Produces(404, contentType: "application/json")
        .WithOpenApi()
        .RequireRateLimiting("files");
        #endregion
    }

    private static readonly ILogger logger = Program.LoggerFac.CreateLogger(typeof(WebAPI).FullName!);
    private static void Log(string? message, LogSeverity severity = LogSeverity.Info)
    {
        logger.Log(message, severity);
    }

    private static void SetAPIHeaderOptions(HttpContext ctx)
    {
        ctx.Response.Headers.AccessControlAllowOrigin = "*";
        ctx.Response.Headers.CacheControl = "no-store";
    }

    private static bool IsSimple(Type type)
    {
        return type.IsPrimitive
          || type.IsEnum
          || type.Equals(typeof(string))
          || type.Equals(typeof(decimal));
    }

    private static readonly ConcurrentDictionary<Type, ConstructorInfo> stringConstructors = [];
    public static object? DeserializeSimpleType(string valueStr, Type targetType)
    {
        if (Nullable.GetUnderlyingType(targetType) is Type nullableType)
        {
            if (valueStr == "null")
                return null;
            else
                targetType = nullableType;
        }

        if (!IsSimple(targetType))
        {
            if (!stringConstructors.TryGetValue(targetType, out var strConstructor))
            {
                var str = targetType.GetConstructor([typeof(string)]);
                if (str != null)
                    strConstructor = str;
                else
                    throw new ArgumentException($"Couldn't find a suitable constructor for {targetType.FullName} which takes a single string as a parameter!");
                stringConstructors.TryAdd(targetType, strConstructor);
            }

            return strConstructor.Invoke([valueStr]);
        }

        object? value;
        var simpleType = targetType;
        if (targetType.IsEnum)
        {
            if (Enum.TryParse(targetType, valueStr, out value))
                return value;
            simpleType = targetType.GetEnumUnderlyingType();
        }

        try
        {
            if (simpleType == typeof(bool))
                value = int.Parse(valueStr) != 0;
            else if (simpleType == typeof(byte))
                value = byte.Parse(valueStr);
            else if (simpleType == typeof(sbyte))
                value = sbyte.Parse(valueStr);
            else if (simpleType == typeof(char))
                value = valueStr[0];//char.Parse(line);
            else if (simpleType == typeof(decimal))
                value = decimal.Parse(valueStr);
            else if (simpleType == typeof(double))
                value = double.Parse(valueStr);
            else if (simpleType == typeof(float))
                value = float.Parse(valueStr);
            else if (simpleType == typeof(int))
                value = int.Parse(valueStr);
            else if (simpleType == typeof(uint))
                value = uint.Parse(valueStr);
            else if (simpleType == typeof(nint))
                value = nint.Parse(valueStr);
            else if (simpleType == typeof(long))
                value = long.Parse(valueStr);
            else if (simpleType == typeof(ulong))
                value = ulong.Parse(valueStr);
            else if (simpleType == typeof(short))
                value = short.Parse(valueStr);
            else if (simpleType == typeof(ushort))
                value = ushort.Parse(valueStr);
            else if (simpleType == typeof(string))
                value = new string(valueStr);
            else
                throw new ArgumentException($"Fields of type {targetType.Name} are not supported!");
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            throw new ArgumentException($"Value of '{valueStr}' couldn't be parsed as a {targetType.Name}!");
        }

        if (targetType.IsEnum)
            value = Enum.ToObject(targetType, value);

        return value;
    }

    /// <summary>
    /// Represents the result of an API operation.
    /// </summary>
    /// <param name="success">Whether the operation succeeded.</param>
    /// <param name="message">Optionally, an error message if it failed.</param>
    [Serializable]
    public readonly struct APIResult(bool success, string? message = null)
    {
        public readonly bool Success { get; init; } = success;
        public readonly string? Message { get; init; } = message;
    }
}
