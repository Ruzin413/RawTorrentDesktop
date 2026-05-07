using TorServices.CLI;
using TorServices.Core;

class Program
{
    static async Task Main(string[] args)
    {
        string? targetFile = null;
        string? outputDir = null;

        // 1. Check if user gave any command
        if (args.Length == 0)
        {
            Console.Clear();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("=========================================================");
            Console.WriteLine("     Hello! This is RawTorrent Engine By Rujin Manandhar");
            Console.WriteLine("=========================================================\n");
            Console.ResetColor();
            
            Console.WriteLine("Which torrent file or magnet link would you like to download?");
            Console.Write("> ");
            
            targetFile = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(targetFile))
            {
                Console.WriteLine("No input provided. Exiting.");
                return;
            }
             
            // Remove accidental quotes if user copy-pasted a quoted string
            targetFile = targetFile.Trim('\"', '\'');
        }
        else
        {
            // 2. Convert raw args into structured command
            var command = CommandParser.Parse(args);
            if (command == null || string.IsNullOrEmpty(command.TargetFile))
            {
                PrintUsage();
                return;
            }

            if (command.Action != CommandAction.Download)
            {
                Console.WriteLine($"Unknown action: {args[0]}");
                PrintUsage();
                return;
            }
            
            targetFile = command.TargetFile;
            outputDir = command.OutputDirectory;
        }

        try
        {
            var controller = new TorrentController();
            
            if (targetFile.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
            {
                await controller.StartMagnetDownload(targetFile, outputDir);
            }
            else
            {
                await controller.StartDownload(targetFile, outputDir);
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[X] CRITICAL ERROR: {ex.Message}");
            if (ex.InnerException != null) Console.WriteLine($"   Inner: {ex.InnerException.Message}");
            Console.WriteLine(ex.StackTrace);
            Console.ResetColor();
        }
    }
    static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  Just run the application without arguments for interactive mode!");
        Console.WriteLine("  OR explicitly use CLI commands:");
        Console.WriteLine("  download <file.torrent>");
        Console.WriteLine("  download \"magnet:?xt=urn:...\"");
        Console.WriteLine("\nOptions:");
        Console.WriteLine("  -o, --output <dir>    Specify output directory");
        Console.WriteLine("  -v, --verbose         Enable verbose logging");
    }
}