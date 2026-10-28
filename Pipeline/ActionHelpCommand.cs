using terraform.lib.console;
using terraform.lib.engine;
using terraform.lib.utils;

namespace terraform.Pipeline;

public static class ActionHelpCommand
{
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;

        if (args.Length == 0 || !string.Equals(args[0], "action", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var runner = new ProcessRunner();
        var actions = ActionCatalog.Create(runner);
        var options = args.Skip(1).ToArray();
        var requested = options.Where(argument => argument is not "--help" and not "-h" and not "--list").ToArray();
        var showList = options.Any(argument => string.Equals(argument, "--list", StringComparison.OrdinalIgnoreCase));
        var showHelp = options.Any(argument => argument is "--help" or "-h");

        if (showList)
        {
            if (requested.Length > 0 || showHelp)
            {
                ConsolePlus.Write("[Yellow]Uso: terraform action --list[/]");
                exitCode = 1;
                return true;
            }

            foreach (var action in actions.OrderBy(action => action.ActionType, StringComparer.OrdinalIgnoreCase))
            {
                ConsolePlus.Write($"[Green]{action.ActionType}[/] [DarkGray]>[/] {action.ActionDescription}");
            }

            return true;
        }

        var showAll = requested.Length == 0 && showHelp;

        if (showAll)
        {
            ConsolePlus.WriteBoxHeader("Azioni disponibili", titleColor: ConsoleColor.Cyan);
            foreach (var action in actions.OrderBy(action => action.ActionType, StringComparer.OrdinalIgnoreCase))
            {
                action.Help();
                Console.WriteLine();
                ConsolePlus.WriteHr();
                Console.WriteLine();
            }

            return true;
        }

        if (requested.Length == 1 && args.Skip(1).Any(argument => argument is "--help" or "-h"))
        {
            var actionName = requested[0];

            var action = actions.FirstOrDefault(candidate => string.Equals(candidate.ActionType, actionName, StringComparison.OrdinalIgnoreCase));
            if (action is not null)
            {
                action.Help();
                return true;
            }

            ConsolePlus.Write($"[Red]Azione sconosciuta:[/] {requested[0]}");
            WriteAvailable(actions);
            exitCode = 1;
            return true;
        }

        ConsolePlus.Write("[Yellow]Uso: terraform action --list | terraform action --help | terraform action <ActionType> --help[/]");
        WriteAvailable(actions);
        exitCode = requested.Length == 0 ? 0 : 1;
        return true;
    }

    private static void WriteAvailable(IEnumerable<IAction> actions)
    {
        ConsolePlus.Write("[Cyan]Action type disponibili:[/]");
        foreach (var action in actions.OrderBy(action => action.ActionType, StringComparer.OrdinalIgnoreCase))
        {
            ConsolePlus.Write($"[Green]{action.ActionType}[/] [DarkGray]>[/] {action.ActionDescription}");
        }
    }
}
