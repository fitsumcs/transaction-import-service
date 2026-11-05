using System.Buffers.Text;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using TransactionImport.Application.Errors;

namespace TransactionImport.Application.Imports;

public sealed class ImportQueries(IImportRepository repository, IOptions<ImportOptions> options)
{
    private const string CursorPrefix = "r1:";
    private readonly ImportOptions _options = options.Value;

    public async Task<ImportView> GetAsync(Guid importId, ProviderScope scope, CancellationToken cancellationToken) =>
        await repository.GetAsync(importId, scope, cancellationToken).ConfigureAwait(false) ?? throw AppErrors.ImportNotFound();

    public async Task<ImportSummary> GetSummaryAsync(Guid importId, ProviderScope scope, CancellationToken cancellationToken) =>
        await repository.GetSummaryAsync(importId, scope, _options.SummaryTopGroups, cancellationToken).ConfigureAwait(false)
        ?? throw AppErrors.ImportNotFound();

    public async Task<RejectionPage> GetRejectionsAsync(Guid importId, ProviderScope scope, int? limit, string? cursor, CancellationToken cancellationToken)
    {
        var pageSize = limit ?? 50;
        if (pageSize < 1 || pageSize > _options.MaxRejectionPageSize)
        {
            throw AppErrors.InvalidLimit(_options.MaxRejectionPageSize);
        }

        var afterLine = DecodeCursor(cursor);
        var rows = await repository.GetRejectionsAsync(importId, scope, afterLine, pageSize, cancellationToken).ConfigureAwait(false)
            ?? throw AppErrors.ImportNotFound();

        if (rows.Count <= pageSize)
        {
            return new RejectionPage(rows, null);
        }

        var page = rows.Take(pageSize).ToList();
        return new RejectionPage(page, EncodeCursor(page[^1].LineNumber));
    }

    /// <summary>Opaque keyset cursor: base64url("r1:{lastLineNumber}").</summary>
    public static string EncodeCursor(long lastLineNumber) =>
        Base64Url.EncodeToString(Encoding.ASCII.GetBytes(CursorPrefix + lastLineNumber.ToString(CultureInfo.InvariantCulture)));

    public static long DecodeCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return 0;
        }

        try
        {
            if (cursor.Length > 64)
            {
                throw AppErrors.InvalidCursor();
            }

            var text = Encoding.ASCII.GetString(Base64Url.DecodeFromChars(cursor));
            if (text.StartsWith(CursorPrefix, StringComparison.Ordinal)
                && long.TryParse(text.AsSpan(CursorPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var line)
                && line >= 0)
            {
                return line;
            }
        }
        catch (FormatException)
        {
        }

        throw AppErrors.InvalidCursor();
    }
}
