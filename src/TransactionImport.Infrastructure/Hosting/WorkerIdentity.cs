using TransactionImport.Application.Processing;

namespace TransactionImport.Infrastructure.Hosting;

internal sealed class WorkerIdentity : IWorkerIdentity
{
    public WorkerIdentity()
    {
        var id = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
        WorkerId = id.Length <= 64 ? id : id[..64];
    }

    public string WorkerId { get; }
}
