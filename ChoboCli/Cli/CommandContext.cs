using ChoboCli.Infrastructure;

namespace ChoboCli.Cli;

public sealed class CommandContext
{
    private readonly ChoboApiClientFactory _clientFactory;

    public CommandContext(
        ParsedCommand command,
        ChoboApiClientFactory clientFactory,
        ProfileStore profileStore,
        JsonOutputWriter output)
    {
        Command = command;
        _clientFactory = clientFactory;
        Profiles = profileStore;
        Output = output;
    }

    public ParsedCommand Command { get; }
    public ProfileStore Profiles { get; }
    public JsonOutputWriter Output { get; }

    public Task<ChoboApiClient> CreateClientAsync() =>
        _clientFactory.CreateAsync(Command.Options, Profiles);
}
