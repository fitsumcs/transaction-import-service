using TransactionImport.Domain.Imports;

namespace TransactionImport.UnitTests.Domain;

public sealed class ImportTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 24, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void New_import_is_pending_with_empty_progress()
    {
        var import = NewImport();

        Assert.Equal(ImportStatus.Pending, import.Status);
        Assert.Equal(ImportProgress.Empty, import.Progress);
        Assert.Null(import.StartedAt);
    }

    [Fact]
    public void Happy_path_records_progress_and_timestamps()
    {
        var import = NewImport();

        import.Start(T0.AddMinutes(1));
        import.RecordBatch(ImportProgress.ForBatch(accepted: 90, rejected: 10, duplicates: 4));
        import.RecordBatch(ImportProgress.ForBatch(accepted: 50, rejected: 0, duplicates: 0));
        import.Complete(T0.AddMinutes(5));

        Assert.Equal(ImportStatus.Completed, import.Status);
        Assert.Equal(new ImportProgress(150, 140, 10, 4), import.Progress);
        Assert.Equal(T0.AddMinutes(1), import.StartedAt);
        Assert.Equal(T0.AddMinutes(5), import.CompletedAt);
    }

    [Fact]
    public void Restart_after_lease_release_keeps_original_start_time()
    {
        var import = NewImport();
        import.Start(T0);
        import.ReleaseForRetry();

        import.Start(T0.AddMinutes(10));

        Assert.Equal(T0, import.StartedAt);
    }

    [Fact]
    public void Cancelling_a_pending_import_cancels_it_immediately()
    {
        var import = NewImport();

        var changed = import.RequestCancellation(T0);

        Assert.True(changed);
        Assert.Equal(ImportStatus.Cancelled, import.Status);
    }

    [Fact]
    public void Cancelling_a_processing_import_waits_for_worker_confirmation()
    {
        var import = NewImport();
        import.Start(T0);

        import.RequestCancellation(T0.AddSeconds(5));
        Assert.Equal(ImportStatus.Cancelling, import.Status);

        import.ConfirmCancelled(T0.AddSeconds(6));
        Assert.Equal(ImportStatus.Cancelled, import.Status);
    }

    [Fact]
    public void Repeated_cancellation_is_idempotent()
    {
        var import = NewImport();
        import.Start(T0);
        import.RequestCancellation(T0);

        Assert.False(import.RequestCancellation(T0));
        Assert.Equal(ImportStatus.Cancelling, import.Status);
    }

    [Fact]
    public void Cancelling_a_completed_import_is_rejected()
    {
        var import = NewImport();
        import.Start(T0);
        import.Complete(T0);

        var error = Assert.Throws<InvalidImportStatusTransitionException>(() => import.RequestCancellation(T0));
        Assert.Equal("IMPORT_INVALID_STATUS_TRANSITION", error.Code);
    }

    [Fact]
    public void Recording_progress_outside_processing_is_a_programming_error()
    {
        var import = NewImport();

        Assert.Throws<InvalidOperationException>(() => import.RecordBatch(ImportProgress.ForBatch(1, 0, 0)));
    }

    [Fact]
    public void Batch_cannot_report_more_duplicates_than_rejections() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ImportProgress.ForBatch(accepted: 5, rejected: 1, duplicates: 2));

    private static Import NewImport() => new(Guid.CreateVersion7(), "provider-a", T0);
}
