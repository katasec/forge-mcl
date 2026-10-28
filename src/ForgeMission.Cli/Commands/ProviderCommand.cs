using System.CommandLine;
using ForgeMission.Cli.Services;

namespace ForgeMission.Cli.Commands;

// forge provider list/scaffold — surface the known LLM provider catalogue and generate a
// ready-to-paste forge.toml provider block. The catalogue itself lives in
// ForgeMission.ChatClients.ProviderCatalog; this surface owns only the command wiring.
internal static class ProviderCommand
{
    internal static Command BuildProviderCommand()
    {
        var providerCmd = new Command("provider", "Manage LLM provider profiles");
        providerCmd.Add(BuildProviderListCommand());
        providerCmd.Add(BuildProviderScaffoldCommand());
        return providerCmd;
    }

    static Command BuildProviderListCommand()
    {
        var cmd = new Command("list", "List all known LLM providers");
        cmd.SetCancellableAction((_, _) =>
        {
            Console.WriteLine("Known providers:\n");
            Console.WriteLine($"  {"NAME",-12} {"REQUIRED FIELDS",-30} NOTES");
            Console.WriteLine($"  {"----",-12} {"---------------",-30} -----");
            foreach (var p in ForgeMission.ChatClients.ProviderCatalog.Entries)
                Console.WriteLine($"  {p.Name,-12} {p.RequiredFields,-30} {p.Notes}");
            Console.WriteLine("\nRun 'forge provider scaffold <name>' to generate a forge.toml block.");
            return Task.CompletedTask;
        });
        return cmd;
    }

    static Command BuildProviderScaffoldCommand()
    {
        var nameArg  = new Argument<string>("name") { Description = "Provider name (openai, anthropic, azure, ollama)" };
        nameArg.CompletionSources.Add([.. ForgeMission.ChatClients.ProviderCatalog.Entries.Select(e => e.Name)]);
        // Reject an unknown provider at parse time (case-insensitive, matching the action's lookup).
        nameArg.Validators.Add(result =>
        {
            var name = result.GetValueOrDefault<string>();
            if (!string.IsNullOrEmpty(name) && ForgeMission.ChatClients.ProviderCatalog.Find(name.ToLowerInvariant()) is null)
                result.AddError($"Unknown provider '{name}'. Run 'forge provider list' to see known providers.");
        });
        var writeOpt = new Option<bool>("--write") { Description = "Append the block to forge.toml instead of printing it" };

        var cmd = new Command("scaffold", "Generate a ready-to-paste forge.toml provider block");
        cmd.Add(nameArg);
        cmd.Add(writeOpt);

        cmd.SetCancellableAction(async (result, cancellationToken) =>
        {
            var name  = result.GetValue(nameArg)!.ToLowerInvariant();
            var write = result.GetValue(writeOpt);

            var block = ForgeMission.ChatClients.ProviderCatalog.Find(name)?.ScaffoldBlock;

            if (block is null)
            {
                CliDiagnostics.Die($"Unknown provider '{name}'. Run 'forge provider list' to see known providers.");
                return;
            }

            if (write)
            {
                await File.AppendAllTextAsync("forge.toml", $"\n{block}\n", cancellationToken);
                Console.WriteLine("Appended to forge.toml.");
            }
            else
            {
                Console.WriteLine(block);
            }
        });

        return cmd;
    }
}
