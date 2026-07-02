namespace ChoboServer.Application;

public sealed class BackupRestoreOperationGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async ValueTask<IAsyncDisposable> EnterAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        return new Releaser(_semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            semaphore.Release();
            return ValueTask.CompletedTask;
        }
    }
}