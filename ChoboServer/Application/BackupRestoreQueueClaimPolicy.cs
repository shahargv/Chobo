using Chobo.Contracts;
using ChoboServer.Data;

namespace ChoboServer.Application;

public sealed class BackupRestoreQueueClaimPolicy
{
    public int CandidateWindow => 256;

    public IQueryable<BackupRestoreQueueItemEntity> OrderedQueuedCandidates(
        IQueryable<BackupRestoreQueueItemEntity> items) =>
        items
            .Where(x => x.StartedAt == null && x.CompletedAt == null)
            .OrderByDescending(x => x.IsForced)
            .ThenBy(x => x.Position)
            .ThenBy(x => x.CreatedAt);
}

