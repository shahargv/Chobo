using ChoboCli.Infrastructure;

namespace ChoboCli.Cli;

public sealed class CliApplication(
    CommandRegistry registry,
    ChoboApiClientFactory clientFactory,
    ProfileStore profileStore,
    JsonOutputWriter output)
{
    public async Task<int> RunAsync(string[] args)
    {
        try
        {
            var command = ParsedCommand.Parse(args);
            if (command.IsHelp)
            {
                output.WriteText(registry.GetHelp());
                return 0;
            }

            var handler = registry.Resolve(command.Subject, command.Verb);
            var context = new CommandContext(command, clientFactory, profileStore, output);
            var result = await handler.HandleAsync(context);
            output.Write(result);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}

