using ChoboCli.Cli;

namespace Chobo.Tests;

public sealed class CliOptionBagTests
{
    [Fact]
    public void Require_reports_all_missing_options_at_once()
    {
        var options = new OptionBag(new Dictionary<string, string?>
        {
            ["--name"] = "nightly"
        });

        var ex = Assert.Throws<InvalidOperationException>(() => options.Require("--name", "--policy-id", "--cron"));

        Assert.Equal("Missing required options: --policy-id, --cron.", ex.Message);
    }

    [Fact]
    public void Install_alias_maps_to_server_install_command()
    {
        var command = ParsedCommand.Parse(["install", "--server-url", "http://localhost:8080"]);

        Assert.Equal("server", command.Subject);
        Assert.Equal("install", command.Verb);
        Assert.Equal("http://localhost:8080", command.Options.Optional("--server-url"));
    }
}
