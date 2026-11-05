using System.Diagnostics.Metrics;
using TransactionImport.Application.Abstractions;
using TransactionImport.Domain.Transactions;

namespace TransactionImport.Infrastructure.Observability;

/// <summary>
/// Business metrics via <see cref="System.Diagnostics.Metrics"/>. Labels are low-cardinality only
/// (outcome, reason code, stage, operation) — never import, request or transaction ids.
/// </summary>
internal sealed class OpenTelemetryImportMetrics : IImportMetrics, IDisposable
{
    public const string MeterName = "TransactionImport";

    private readonly Meter _meter;
    private readonly Counter<long> _importsStarted;
    private readonly Counter<long> _importsFinished;
    private readonly Histogram<double> _importDuration;
    private readonly Counter<long> _recordsAccepted;
    private readonly Counter<long> _recordsRejected;
    private readonly Counter<long> _recordsDuplicates;
    private readonly Counter<long> _rejectionsByCode;
    private readonly Histogram<double> _batchStageDuration;
    private readonly Counter<long> _processingFailures;
    private readonly Counter<long> _retries;
    private readonly Counter<long> _uploadsStarted;
    private readonly Counter<long> _uploadsFinished;
    private readonly Counter<long> _uploadBytes;

    public OpenTelemetryImportMetrics(IMeterFactory meterFactory)
    {
        _meter = meterFactory.Create(MeterName);
        _importsStarted = _meter.CreateCounter<long>("imports_started_total");
        _importsFinished = _meter.CreateCounter<long>("imports_finished_total");
        _importDuration = _meter.CreateHistogram<double>("import_duration_seconds", unit: "s");
        _recordsAccepted = _meter.CreateCounter<long>("records_accepted_total");
        _recordsRejected = _meter.CreateCounter<long>("records_rejected_total");
        _recordsDuplicates = _meter.CreateCounter<long>("records_duplicates_total");
        _rejectionsByCode = _meter.CreateCounter<long>("record_rejections_total");
        _batchStageDuration = _meter.CreateHistogram<double>("batch_stage_duration_seconds", unit: "s");
        _processingFailures = _meter.CreateCounter<long>("processing_failures_total");
        _retries = _meter.CreateCounter<long>("operation_retries_total");
        _uploadsStarted = _meter.CreateCounter<long>("uploads_started_total");
        _uploadsFinished = _meter.CreateCounter<long>("uploads_finished_total");
        _uploadBytes = _meter.CreateCounter<long>("upload_bytes_total", unit: "By");
    }

    public void ImportStarted() => _importsStarted.Add(1);

    public void ImportFinished(string outcome, TimeSpan duration)
    {
        _importsFinished.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        _importDuration.Record(duration.TotalSeconds, new KeyValuePair<string, object?>("outcome", outcome));
    }

    public void RecordsCommitted(long accepted, long rejected, long duplicates)
    {
        if (accepted > 0)
        {
            _recordsAccepted.Add(accepted);
        }

        if (rejected > 0)
        {
            _recordsRejected.Add(rejected);
        }

        if (duplicates > 0)
        {
            _recordsDuplicates.Add(duplicates);
        }
    }

    public void RecordRejected(RejectionCode code) =>
        _rejectionsByCode.Add(1, new KeyValuePair<string, object?>("code", ToMetricCode(code)));

    public void BatchStage(string stage, TimeSpan duration) =>
        _batchStageDuration.Record(duration.TotalSeconds, new KeyValuePair<string, object?>("stage", stage));

    public void ProcessingFailure(string kind) =>
        _processingFailures.Add(1, new KeyValuePair<string, object?>("kind", kind));

    public void Retry(string operation) =>
        _retries.Add(1, new KeyValuePair<string, object?>("operation", operation));

    public void UploadStarted() => _uploadsStarted.Add(1);

    public void UploadFinished(string outcome, long bytes)
    {
        _uploadsFinished.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        if (bytes > 0)
        {
            _uploadBytes.Add(bytes, new KeyValuePair<string, object?>("outcome", outcome));
        }
    }

    public void Dispose() => _meter.Dispose();

    private static string ToMetricCode(RejectionCode code) => code switch
    {
        RejectionCode.InvalidJson => "invalid_json",
        RejectionCode.InvalidEncoding => "invalid_encoding",
        RejectionCode.LineTooLong => "line_too_long",
        RejectionCode.NotAnObject => "not_an_object",
        RejectionCode.MissingField => "missing_field",
        RejectionCode.InvalidFieldType => "invalid_field_type",
        RejectionCode.InvalidTransactionId => "invalid_transaction_id",
        RejectionCode.InvalidAccountId => "invalid_account_id",
        RejectionCode.InvalidMerchantId => "invalid_merchant_id",
        RejectionCode.InvalidAmount => "invalid_amount",
        RejectionCode.InvalidCurrency => "invalid_currency",
        RejectionCode.InvalidTimestamp => "invalid_timestamp",
        RejectionCode.TimestampOutOfRange => "timestamp_out_of_range",
        RejectionCode.DescriptionTooLong => "description_too_long",
        RejectionCode.InvalidCharacters => "invalid_characters",
        RejectionCode.DuplicateTransaction => "duplicate_transaction",
        RejectionCode.DuplicateConflict => "duplicate_conflict",
        _ => "other",
    };
}
