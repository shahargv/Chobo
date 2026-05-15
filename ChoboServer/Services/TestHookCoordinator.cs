using ChoboServer.Options;
using Microsoft.Extensions.Options;

namespace ChoboServer.Services;

public interface ITestHookCoordinator
{
    bool Enabled { get; }
    void DelayNextBackupBeforePoll();
    void DelayNextRestoreBeforePoll();
    Task MaybeDelayBackupBeforePollAsync(CancellationToken cancellationToken);
    Task MaybeDelayRestoreBeforePollAsync(CancellationToken cancellationToken);
}

public sealed class TestHookCoordinator(IOptions<ChoboTestHooksOptions> options) : ITestHookCoordinator
{
    private readonly object _lock = new();
    private bool _delayNextBackupBeforePoll;
    private bool _delayNextRestoreBeforePoll;

    public bool Enabled => options.Value.Enabled;

    public void DelayNextBackupBeforePoll()
    {
        if (!Enabled) return;
        lock (_lock) _delayNextBackupBeforePoll = true;
    }

    public void DelayNextRestoreBeforePoll()
    {
        if (!Enabled) return;
        lock (_lock) _delayNextRestoreBeforePoll = true;
    }

    public async Task MaybeDelayBackupBeforePollAsync(CancellationToken cancellationToken)
    {
        if (!Consume(ref _delayNextBackupBeforePoll)) return;
        await Task.Delay(TimeSpan.FromMinutes(10), cancellationToken);
    }

    public async Task MaybeDelayRestoreBeforePollAsync(CancellationToken cancellationToken)
    {
        if (!Consume(ref _delayNextRestoreBeforePoll)) return;
        await Task.Delay(TimeSpan.FromMinutes(10), cancellationToken);
    }

    private bool Consume(ref bool flag)
    {
        if (!Enabled) return false;
        lock (_lock)
        {
            if (!flag) return false;
            flag = false;
            return true;
        }
    }
}
