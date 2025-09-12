using System.IO;
using System.Linq;
using Spectre.Console;
using Mono.Cecil;
using Vibe.Cui;

public class Program
{
    public static async Task Main()
    {
        AnsiConsole.MarkupLine("[bold cyan]Vibe Console Interface[/]");
        using var analyzer = new DllAnalyzer();
        LoadedDll? dll = null;

        while (true)
        {
            if (dll == null)
            {
                var path = AnsiConsole.Ask<string>("Enter path to a DLL (empty to quit):");
                if (string.IsNullOrWhiteSpace(path))
                    return;
                if (!File.Exists(path))
                {
                    AnsiConsole.MarkupLine("[red]File not found.[/]");
                    continue;
                }
                dll = analyzer.Load(path);
                AnsiConsole.WriteLine(analyzer.GetSummary(dll));
            }

            var action = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Choose action")
                    .AddChoices("List exports", "List managed types", "Open another DLL", "Quit"));

            switch (action)
            {
                case "List exports":
                    var exports = await analyzer.GetExportNamesAsync(dll, dll.Cts.Token);
                    if (exports.Count == 0)
                    {
                        AnsiConsole.MarkupLine("[yellow]No exports found.[/]");
                        break;
                    }
                    var export = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("Select export")
                            .PageSize(10)
                            .AddChoices(exports));
                    var code = await analyzer.GetDecompiledExportAsync(dll, export, null, dll.Cts.Token);
                    AnsiConsole.WriteLine(code);
                    break;
                case "List managed types":
                    var types = await analyzer.GetManagedTypesAsync(dll, dll.Cts.Token);
                    if (types.Count == 0)
                    {
                        AnsiConsole.MarkupLine("[yellow]No managed types found.[/]");
                        break;
                    }
                    var typeName = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("Select type")
                            .PageSize(10)
                            .AddChoices(types.Select(t => t.FullName)));
                    var type = types.First(t => t.FullName == typeName);
                    if (!type.Methods.Any())
                    {
                        AnsiConsole.MarkupLine("[yellow]Type has no methods.[/]");
                        break;
                    }
                    var methodName = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("Select method")
                            .PageSize(10)
                            .AddChoices(type.Methods.Select(m => m.FullName)));
                    var method = type.Methods.First(m => m.FullName == methodName);
                    var body = analyzer.GetManagedMethodBody(method);
                    AnsiConsole.WriteLine(body);
                    break;
                case "Open another DLL":
                    dll.Dispose();
                    dll = null;
                    break;
                case "Quit":
                    dll.Dispose();
                    return;
            }
        }
    }
}
