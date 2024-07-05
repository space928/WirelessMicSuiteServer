using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using System;

namespace WirelessMicSuiteServer;

public enum LogSeverity
{
    Debug,
    Info,
    Warning,
    Error
}

public static class LoggerExtensions
{
    public static void Log(this ILogger logger, string? message, LogSeverity severity = LogSeverity.Info, params object?[] args)
    {
        LogLevel level = severity switch
        {
            LogSeverity.Debug => LogLevel.Debug,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Error => LogLevel.Error,
            _ => LogLevel.None
        };

        logger.Log(level, 0, null, message, args);
    }

    public static ILoggingBuilder AddWebLoggerFormatter(
        this ILoggingBuilder builder,
    Action<WebLoggerOptions> configure) =>
        builder.AddConsole(options => options.FormatterName = nameof(WebLoggerFormatter))
            .AddConsoleFormatter<WebLoggerFormatter, WebLoggerOptions>(configure);
}

public sealed class WebLoggerOptions : ConsoleFormatterOptions
{
    //public string? CustomPrefix { get; set; }
}

public sealed class WebLoggerFormatter : ConsoleFormatter, IDisposable
{
    private readonly IDisposable? _optionsReloadToken;
    private WebLoggerOptions _formatterOptions;

    public WebLoggerFormatter(IOptionsMonitor<WebLoggerOptions> options) : base(nameof(WebLoggerFormatter))
    {
        _optionsReloadToken = options.OnChange(ReloadLoggerOptions);
        _formatterOptions = options.CurrentValue;
    }

    private void ReloadLoggerOptions(WebLoggerOptions options) => _formatterOptions = options;

    static ConsoleColor LogLevelColour(LogLevel level) => level switch
    {
        LogLevel.Trace => ConsoleColor.Gray,
        LogLevel.Debug => ConsoleColor.Gray,
        LogLevel.Information => ConsoleColor.White,
        LogLevel.Warning => ConsoleColor.Yellow,
        LogLevel.Error => ConsoleColor.Red,
        LogLevel.Critical => ConsoleColor.DarkRed,
        LogLevel.None => ConsoleColor.White,
        _ => ConsoleColor.White
    };

    static string LogLevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "Trace",
        LogLevel.Debug => "Debug",
        LogLevel.Information => "Info ",
        LogLevel.Warning => "Warn ",
        LogLevel.Error => "Error",
        LogLevel.Critical => "Crit ",
        LogLevel.None => "None ",
        _ => "     "
    };

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        string? message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);

        if (message is null)
        {
            return;
        }

        ReadOnlySpan<char> cat = logEntry.Category.AsSpan();
        int sep = cat.LastIndexOf('.');
        if (sep >= 0)
            cat = cat[(sep+1)..];

        textWriter.WriteColor(foreground: ConsoleColor.Gray);
        textWriter.Write($"[{DateTime.Now:T}] [{cat,-22}] [{LogLevelName(logEntry.LogLevel)}] ");
        textWriter.WriteColor(foreground: LogLevelColour(logEntry.LogLevel));
        textWriter.Write(message);
        textWriter.WriteDefaultColor();
        textWriter.WriteLine();
    }

    public void Dispose() => _optionsReloadToken?.Dispose();
}

// Derived from: https://learn.microsoft.com/en-us/dotnet/core/extensions/console-log-formatter
public static class TextWriterExtensions
{
    const string DefaultForegroundColor = "\x1B[39m\x1B[22m";
    const string DefaultBackgroundColor = "\x1B[49m";

    public static void WriteDefaultColor(this TextWriter textWriter)
    {
        textWriter.Write(DefaultForegroundColor);
        textWriter.Write(DefaultBackgroundColor);
    }

    public static void WriteColor(this TextWriter textWriter, ConsoleColor? background = null, ConsoleColor? foreground = null)
    {
        if (background.HasValue)
            textWriter.Write(GetBackgroundColorEscapeCode(background.Value));
        if (foreground.HasValue)
            textWriter.Write(GetForegroundColorEscapeCode(foreground.Value));
    }

    public static void WriteWithColor(
        this TextWriter textWriter,
        string message,
        ConsoleColor? background,
        ConsoleColor? foreground)
    {
        // Order:
        //   1. background color
        //   2. foreground color
        //   3. message
        //   4. reset foreground color
        //   5. reset background color

        var backgroundColor = background.HasValue ? GetBackgroundColorEscapeCode(background.Value) : null;
        var foregroundColor = foreground.HasValue ? GetForegroundColorEscapeCode(foreground.Value) : null;

        if (backgroundColor != null)
        {
            textWriter.Write(backgroundColor);
        }
        if (foregroundColor != null)
        {
            textWriter.Write(foregroundColor);
        }

        textWriter.WriteLine(message);

        if (foregroundColor != null)
        {
            textWriter.Write(DefaultForegroundColor);
        }
        if (backgroundColor != null)
        {
            textWriter.Write(DefaultBackgroundColor);
        }
    }

    static string GetForegroundColorEscapeCode(ConsoleColor color) =>
        color switch
        {
            ConsoleColor.Black => "\x1B[30m",
            ConsoleColor.DarkRed => "\x1B[31m",
            ConsoleColor.DarkGreen => "\x1B[32m",
            ConsoleColor.DarkYellow => "\x1B[33m",
            ConsoleColor.DarkBlue => "\x1B[34m",
            ConsoleColor.DarkMagenta => "\x1B[35m",
            ConsoleColor.DarkCyan => "\x1B[36m",
            ConsoleColor.Gray => "\x1B[37m",
            ConsoleColor.Red => "\x1B[1m\x1B[31m",
            ConsoleColor.Green => "\x1B[1m\x1B[32m",
            ConsoleColor.Yellow => "\x1B[1m\x1B[33m",
            ConsoleColor.Blue => "\x1B[1m\x1B[34m",
            ConsoleColor.Magenta => "\x1B[1m\x1B[35m",
            ConsoleColor.Cyan => "\x1B[1m\x1B[36m",
            ConsoleColor.White => "\x1B[1m\x1B[37m",

            _ => DefaultForegroundColor
        };

    static string GetBackgroundColorEscapeCode(ConsoleColor color) =>
        color switch
        {
            ConsoleColor.Black => "\x1B[40m",
            ConsoleColor.DarkRed => "\x1B[41m",
            ConsoleColor.DarkGreen => "\x1B[42m",
            ConsoleColor.DarkYellow => "\x1B[43m",
            ConsoleColor.DarkBlue => "\x1B[44m",
            ConsoleColor.DarkMagenta => "\x1B[45m",
            ConsoleColor.DarkCyan => "\x1B[46m",
            ConsoleColor.Gray => "\x1B[47m",

            _ => DefaultBackgroundColor
        };
}
