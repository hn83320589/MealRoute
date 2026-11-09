namespace MealRoute.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("MealRoute CLI");
            Console.WriteLine("Commands are added in later milestones (generate, solomon, perf).");
            return 0;
        }

        Console.Error.WriteLine($"Unknown command: {args[0]}");
        return 1;
    }
}
