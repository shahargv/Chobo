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
}
