using TransactionImport.Application.Abstractions;

namespace TransactionImport.Infrastructure.Identity;

/// <summary>Time-ordered UUIDv7 keeps B-tree index inserts append-mostly.</summary>
internal sealed class GuidV7IdGenerator(TimeProvider timeProvider) : IIdGenerator
{
    public Guid NewId() => Guid.CreateVersion7(timeProvider.GetUtcNow());
}
