using System.Threading.Channels;
using ChoboServer.Options;
using Microsoft.Extensions.Options;

namespace ChoboServer.Services;

public interface IBackupRestoreQueues
{
    Channel<Guid> Backups { get; }
    Channel<Guid> Restores { get; }
    ValueTask QueueBackupAsync(Guid id, CancellationToken cancellationToken = default);
    ValueTask QueueRestoreAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class BackupRestoreQueues : IBackupRestoreQueues
{
    public BackupRestoreQueues(IOptions<ChoboBackupRestoreOptions> options)
    {
        var capacity = options.Value.QueueCapacity <= 0 ? 100 : options.Value.QueueCapacity;
        Backups = Channel.CreateBounded<Guid>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
        Restores = Channel.CreateBounded<Guid>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
    }

    public Channel<Guid> Backups { get; }
    public Channel<Guid> Restores { get; }

    public ValueTask QueueBackupAsync(Guid id, CancellationToken cancellationToken = default) =>
        Backups.Writer.WriteAsync(id, cancellationToken);

    public ValueTask QueueRestoreAsync(Guid id, CancellationToken cancellationToken = default) =>
        Restores.Writer.WriteAsync(id, cancellationToken);
}
