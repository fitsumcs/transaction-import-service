using TransactionImport.Application.Imports;
using TransactionImport.Application.Processing;
using TransactionImport.Domain.Imports;
using TransactionImport.Domain.Transactions;

namespace TransactionImport.Api.Http;

public sealed record CreateImportResponse(string Id, string Status, DateTimeOffset CreatedAt);

public sealed record ImportStatusResponse(
    string Id,
    string Status,
    ProgressDto Progress,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? FailureReason);

public sealed record ProgressDto(long Processed, long Accepted, long Rejected, long Duplicates);

public sealed record CancelImportResponse(string Id, string Status);

public sealed record RetryImportResponse(string Id, string Status);

public sealed record SummaryResponse(
    string ImportId,
    TotalsDto Totals,
    IReadOnlyList<CurrencyTotalDto> ByCurrency,
    RiskLevelDto ByRiskLevel,
    IReadOnlyList<GroupTotalDto> TopMerchants,
    IReadOnlyList<GroupTotalDto> TopAccounts,
    bool IsFinal);

public sealed record TotalsDto(long Accepted, long Rejected, long Duplicates);

public sealed record CurrencyTotalDto(string Currency, long TransactionCount, decimal TotalAmount);

public sealed record RiskLevelDto(long Low, long Medium, long High);

public sealed record GroupTotalDto(string Id, string Currency, long TransactionCount, decimal TotalAmount);

public sealed record RejectionItemDto(long LineNumber, string Reason, string Message, object? RawValue);

public sealed record RejectionPageDto(IReadOnlyList<RejectionItemDto> Items, string? NextCursor);

internal static class ImportDtoMapper
{
    public static CreateImportResponse ToCreate(ImportView import) =>
        new(import.Id.ToString("N"), ToStatus(import.Status), import.CreatedAt);

    public static ImportStatusResponse ToStatus(ImportView import) => new(
        import.Id.ToString("N"),
        ToStatus(import.Status),
        new ProgressDto(import.Progress.Processed, import.Progress.Accepted, import.Progress.Rejected, import.Progress.Duplicates),
        import.StartedAt,
        import.CompletedAt,
        import.FailureReason);

    public static CancelImportResponse ToCancel(Guid id, ImportStatus status) =>
        new(id.ToString("N"), ToStatus(status));

    public static RetryImportResponse ToRetry(Guid id, ImportStatus status) =>
        new(id.ToString("N"), ToStatus(status));

    public static SummaryResponse ToSummary(ImportSummary summary) => new(
        summary.ImportId.ToString("N"),
        new TotalsDto(summary.Accepted, summary.Rejected, summary.Duplicates),
        summary.ByCurrency.Select(c => new CurrencyTotalDto(c.Currency, c.TransactionCount, c.TotalAmount)).ToArray(),
        new RiskLevelDto(summary.ByRiskLevel.Low, summary.ByRiskLevel.Medium, summary.ByRiskLevel.High),
        summary.TopMerchants.Select(g => new GroupTotalDto(g.Id, g.Currency, g.TransactionCount, g.TotalAmount)).ToArray(),
        summary.TopAccounts.Select(g => new GroupTotalDto(g.Id, g.Currency, g.TransactionCount, g.TotalAmount)).ToArray(),
        summary.IsFinal);

    public static RejectionPageDto ToRejections(RejectionPage page) => new(
        page.Items.Select(ToRejection).ToArray(),
        page.NextCursor);

    public static RejectionItemDto ToRejection(RecordRejection rejection)
    {
        object? raw = null;
        if (rejection.RawValue is not null)
        {
            try
            {
                raw = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(rejection.RawValue);
            }
            catch (System.Text.Json.JsonException)
            {
                raw = rejection.RawValue;
            }
        }

        return new RejectionItemDto(rejection.LineNumber, rejection.Code.ToApi(), rejection.Message, raw);
    }

    private static string ToStatus(ImportStatus status) => status.ToString().ToLowerInvariant();
}
