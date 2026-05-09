using ChoboServer.Data;

namespace ChoboServer.Repositories;

public sealed class EfUnitOfWork(ChoboDbContext db) : IUnitOfWork
{
    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        db.SaveChangesAsync(cancellationToken);
}

