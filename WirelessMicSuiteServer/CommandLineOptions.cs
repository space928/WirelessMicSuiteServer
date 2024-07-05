using System.Reflection;

namespace WirelessMicSuiteServer;

public class CommandLineOptions
{
    private Dictionary<string, CommandLineOption> options;
    private Dictionary<string, object?> parsedArgs;

    public Dictionary<string, object?> ParsedArgs => parsedArgs;

    public CommandLineOptions(CommandLineOption[] options, string[]? args)
    {
        this.parsedArgs = [];
        this.options = new(
            options.Select(x => new KeyValuePair<string, CommandLineOption>(x.key, x))
            .Concat(
                options.Where(x=>x.alias != null)
                .Select(x => new KeyValuePair<string, CommandLineOption>(x.alias!, x))));

        this.options.TryAdd("--help", new("--help", action: _=>PrintHelp()));

        if (args != null ) 
            ParseArgs(args);
    }

    private readonly ILogger logger = Program.LoggerFac.CreateLogger<CommandLineOptions>();
    private void Log(string? message, LogSeverity severity = LogSeverity.Info)
    {
        logger.Log(message, severity);
    }

    public void ParseArgs(string[] args)
    {
        CommandLineOption? option = null;
        List<object> tmpList = [];
        foreach (var arg in args)
        {
            if (options.TryGetValue(arg, out var nOption))
            {
                AddParsedArg(option, tmpList);

                option = nOption;
                continue;
            }

            if (option == null)
            {
                /*Log($"Unrecognised command line argument '{arg}'!", LogSeverity.Error);
                Console.WriteLine();
                PrintHelp();
                parsedArgs.TryAdd("--help", null);
                return;*/
                continue;
            }

            try
            {
                switch (option.Value.argType)
                {
                    case CommandLineArgType.None:
                        break;
                    case CommandLineArgType.String:
                        tmpList.Add(arg);
                        break;
                    case CommandLineArgType.Int:
                        tmpList.Add(int.Parse(arg));
                        break;
                    case CommandLineArgType.Uint:
                        tmpList.Add(uint.Parse(arg));
                        break;
                    case CommandLineArgType.Path:
                        if (!Path.Exists(arg))
                            throw new ArgumentException($"Path '{arg}' does not exist!");
                        tmpList.Add(arg);
                        break;
                }
            }
            catch
            {
                Log($"Couldn't parse '{arg}' as {option.Value.argType}!", LogSeverity.Error);
                Console.WriteLine();
                PrintHelp();
                return;
            }

            if (!option.Value.multipleArguments)
            {
                AddParsedArg(option, tmpList);
                option = null;
            }
        }

        AddParsedArg(option, tmpList);

        foreach (var arg in options.Where(x => x.Key == x.Value.key).Select(x => x.Value))
            if (arg.defaultValue != null && !parsedArgs.ContainsKey(arg.key))
                parsedArgs.Add(arg.key, arg.defaultValue);

        void AddParsedArg(CommandLineOption? option, List<object> tmpList)
        {
            if (option is CommandLineOption opt)
            {
                opt.action?.Invoke(opt.multipleArguments ? tmpList.ToArray() : tmpList.FirstOrDefault());
                parsedArgs.Add(option.Value.key, opt.multipleArguments ? tmpList.ToArray() : tmpList.FirstOrDefault());
                tmpList.Clear();
            }
        }
    }

    public void PrintHelp()
    {
        var assembly = Assembly.GetExecutingAssembly();
        Console.WriteLine($"##### Wireless Mic Suite Server #####");
        Console.WriteLine($"  version: {assembly.GetName().Version}");
        Console.WriteLine($"");
        Console.WriteLine($"Command line options: ");
        foreach (var kvp in options)
        {
            if (kvp.Key != kvp.Value.key)
                continue;
            var opt = kvp.Value;
            Console.WriteLine($"\t{opt.key}{(opt.alias != null?", " + opt.alias:"")} " +
                $"{(opt.argType != CommandLineArgType.None ? "<"+opt.argType+">":"")}{(opt.multipleArguments?", ...":"")}");
            if (opt.defaultValue != null)
                Console.WriteLine($"\t\tDefault: {opt.defaultValue}");
            Console.WriteLine($"\t\t{opt.help}");
            Console.WriteLine();
        }
    }
}

public struct CommandLineOption(string key, string? alias = null, string? help = null, 
    CommandLineArgType argType = CommandLineArgType.None, bool multipleArguments = false,
    object? defaultValue = null, Action<object?>? action = null)
{
    public string key = key;
    public string? alias = alias;
    public string? help = help;
    public CommandLineArgType argType = argType;
    public bool multipleArguments = multipleArguments;
    public object? defaultValue = CastToArg(defaultValue, argType);
    //public bool positional;
    public Action<object?>? action = action;

    private static object? CastToArg(object? arg, CommandLineArgType argType)
    {
        if (arg == null)
            return null;

        return argType switch
        {
            CommandLineArgType.None => null,
            CommandLineArgType.String => (string)arg,
            CommandLineArgType.Int => (int)arg,
            CommandLineArgType.Uint => (uint)arg,
            CommandLineArgType.Path => (string)arg,
            _ => throw new NotImplementedException(),
        };
    }
}

public enum CommandLineArgType
{
    None,
    String,
    Int,
    Uint,
    Path
}
