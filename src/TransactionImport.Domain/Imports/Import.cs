namespace TransactionImport.Domain.Imports;

public sealed class Import
{
    public Import(Guid id, string providerId, DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        Id = id;
        ProviderId = providerId;
        CreatedAt = createdAt;
        Status = ImportStatus.Pending;
        Progress = ImportProgress.Empty;
    }

    public Guid Id { get; }

    public string ProviderId { get; }

    public ImportStatus Status { get; private set; }

    public ImportProgress Progress { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public string? FailureReason { get; private set; }

    public void Start(DateTimeOffset now)
    {
        TransitionTo(ImportStatus.Processing);
        StartedAt ??= now;
    }

    public void RecordBatch(ImportProgress batch)
    {
        if (Status is not (ImportStatus.Processing or ImportStatus.Cancelling))
        {
            throw new InvalidOperationException($"Cannot record progress for import in status '{Status}'.");
        }

        Progress = Progress.Add(batch);
    }

    public void Complete(DateTimeOffset now)
    {
        TransitionTo(ImportStatus.Completed);
        CompletedAt = now;
    }

    public void Fail(string reason, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        TransitionTo(ImportStatus.Failed);
        FailureReason = reason;
        CompletedAt = now;
    }

    /// <summary>Returns <c>true</c> when the request changed state; repeated requests are no-ops.</summary>
    public bool RequestCancellation(DateTimeOffset now)
    {
        switch (Status)
        {
            case ImportStatus.Cancelling or ImportStatus.Cancelled:
                return false;
            case ImportStatus.Pending:
                TransitionTo(ImportStatus.Cancelled);
                CompletedAt = now;
                return true;
            default:
                TransitionTo(ImportStatus.Cancelling);
                return true;
        }
    }

    public void ConfirmCancelled(DateTimeOffset now)
    {
        TransitionTo(ImportStatus.Cancelled);
        CompletedAt = now;
    }

    public void ReleaseForRetry() => TransitionTo(ImportStatus.Pending);

    private void TransitionTo(ImportStatus target)
    {
        if (!ImportStatusTransitions.CanTransition(Status, target))
        {
            throw new InvalidImportStatusTransitionException(Id, Status, target);
        }

        Status = target;
    }
}
