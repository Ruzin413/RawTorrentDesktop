namespace TorServices.CLI;

public enum CommandAction 
{ 
    Unknown,
    Download, 
    Seed 
}

public class Command
{
    public CommandAction Action { get; set; } = CommandAction.Unknown;
    public string TargetFile { get; set; } = string.Empty;
    public string OutputDirectory { get; set; } = string.Empty;
    public bool Verbose { get; set; }
}

public static class CommandParser
{
    public static Command? Parse(string[] args)
    {
        if (args == null || args.Length == 0)
            return null;

        var command = new Command();

        // Parse Action
        if (Enum.TryParse(args[0], true, out CommandAction action))
        {
            command.Action = action;
        }

        // Parse Target File and manual flags
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "-o" || args[i] == "--output")
            {
                if (i + 1 < args.Length)
                {
                    command.OutputDirectory = args[++i];
                }
            }
            else if (args[i] == "-v" || args[i] == "--verbose")
            {
                command.Verbose = true;
            }
            else if (string.IsNullOrEmpty(command.TargetFile)) // First unrecognized arg is our target
            {
                command.TargetFile = args[i];
            }
        }

        return command;
    }
}