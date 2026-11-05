using TransactionImport.Domain.Transactions;

namespace TransactionImport.Application.Abstractions;

/// <summary>
/// Business and processing metrics. Implementations must only use bounded label values (outcomes, reason
/// codes, operation names) — never import, request or transaction ids.
/// </summary>
public interface IImportMetrics
{
    void ImportStarted();

    void ImportFinished(string outcome, TimeSpan duration);

    void RecordsCommitted(long accepted, long rejected, long duplicates);

    void RecordRejected(RejectionCode code);

    void BatchStage(string stage, TimeSpan duration);

    void ProcessingFailure(string kind);

    void Retry(string operation);

    void UploadStarted();

    void UploadFinished(string outcome, long bytes);
}
