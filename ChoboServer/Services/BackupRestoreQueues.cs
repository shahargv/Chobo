using System.Threading.Channels;
using Chobo.Contracts;
using ChoboServer.Options;
using Microsoft.Extensions.Options;

namespace ChoboServer.Services;

public interface IBackupRestoreQueues
{
    Channel<BackupRestoreOperationWorkItem> Operations { get; }
    ValueTask QueueBackupAsync(Guid id, BackupContentMode contentMode = BackupContentMode.SchemaAndData, CancellationToken cancellationToken = default);
    ValueTask QueueRestoreAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed record BackupRestoreOperationWorkItem(BackupRestoreQueueKind Kind, Guid OperationId, BackupContentMode? ContentMode, string Reason);

public sealed class BackupRestoreQueues : IBackupRestoreQueues
{
    public BackupRestoreQueues(IOptions<ChoboBackupRestoreOptions> options)
    {
        var capacity = options.Value.QueueCapacity <= 0 ? 100 : options.Value.QueueCapacity;
        Operations = Channel.CreateBounded<BackupRestoreOperationWorkItem>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
    }

    public Channel<BackupRestoreOperationWorkItem> Operations { get; }

    public ValueTask QueueBackupAsync(Guid id, BackupContentMode contentMode = BackupContentMode.SchemaAndData, CancellationToken cancellationToken = default) =>
        Operations.Writer.WriteAsync(new BackupRestoreOperationWorkItem(BackupRestoreQueueKind.Backup, id, contentMode, "backup-queued"), cancellationToken);

    public ValueTask QueueRestoreAsync(Guid id, CancellationToken cancellationToken = default) =>
        Operations.Writer.WriteAsync(new BackupRestoreOperationWorkItem(BackupRestoreQueueKind.Restore, id, null, "restore-queued"), cancellationToken);
}
