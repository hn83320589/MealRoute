using System.Globalization;

namespace MealRoute.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintHelp();
            return 0;
        }

        var options = CliArgs.Parse(args.Skip(1));
        try
        {
            return args[0] switch
            {
                "solomon" => SolomonCommand.Run(options),
                "generate" => SampleCommands.Generate(options),
                "solve" => SampleCommands.Solve(options),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IOException or Core.Projects.ProjectFileException)
        {
            Console.Error.WriteLine($"錯誤：{ex.Message}");
            return 2;
        }
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"未知的指令：{command}");
        PrintHelp();
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            MealRoute CLI

              generate  Generate a synthetic city + roster project
                        --size small|medium|large   --out <dir>   --seed <n>
              solve     Import a project, build the travel model, optimise and print metrics
                        --project <file>  --seconds <n> (default 60)  --iterations <n>  --runs <n>  --save
              solomon   Run Solomon VRPTW benchmarks (static mode) and compare with best known solutions
                        --dir <path>        instance folder (default benchmarks/solomon)
                        --only <prefix>     e.g. c1, r1, rc1, r101 (default: all *.txt)
                        --seconds <n>       time limit per instance (default 60)
                        --iterations <n>    ALNS iterations per run (default 1000000, time limit usually ends first)
                        --runs <n>          independent runs (default 4)
                        --seed <n>          random seed (default 1)
                        --out <file>        write a Markdown results table
            """);
    }
}

/// <summary>Minimal --key value parser.</summary>
internal sealed class CliArgs
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public static CliArgs Parse(IEnumerable<string> args)
    {
        var result = new CliArgs();
        string? key = null;
        foreach (var a in args)
        {
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                if (key is not null) result._values[key] = "true";
                key = a[2..];
            }
            else if (key is not null)
            {
                result._values[key] = a;
                key = null;
            }
            else throw new ArgumentException($"無法解析的參數：{a}");
        }
        if (key is not null) result._values[key] = "true";
        return result;
    }

    public string Get(string key, string fallback) => _values.TryGetValue(key, out var v) ? v : fallback;
    public string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;

    public int GetInt(string key, int fallback) =>
        _values.TryGetValue(key, out var v)
            ? int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : throw new ArgumentException($"--{key} 必須是整數：{v}")
            : fallback;

    public double GetDouble(string key, double fallback) =>
        _values.TryGetValue(key, out var v)
            ? double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : throw new ArgumentException($"--{key} 必須是數值：{v}")
            : fallback;
}
