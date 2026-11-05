using System.Text;
using System.Text.RegularExpressions;
using TransactionImport.Application.Errors;

namespace TransactionImport.Application.Imports;

public static partial class InputRules
{
    public static string RequireProviderId(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            throw AppErrors.ProviderMissing();
        }

        var trimmed = providerId.Trim();
        return trimmed.Length <= 64 && Identifier().IsMatch(trimmed) ? trimmed : throw AppErrors.ProviderInvalid();
    }

    public static string RequireIdempotencyKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw AppErrors.IdempotencyKeyMissing();
        }

        return key.Length <= 128 && IdempotencyKey().IsMatch(key) ? key : throw AppErrors.IdempotencyKeyInvalid();
    }

    /// <summary>
    /// Produces a display-only file name: last path segment only (no traversal), control and unusual characters
    /// replaced, length-limited. It is never used to build storage paths.
    /// </summary>
    public static string SanitizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "upload.ndjson";
        }

        var lastSegment = fileName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
        var builder = new StringBuilder(Math.Min(lastSegment.Length, 255));
        foreach (var c in lastSegment)
        {
            if (builder.Length == 255)
            {
                break;
            }

            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ' ' ? c : '_');
        }

        var sanitized = builder.ToString().Trim().TrimStart('.');
        return sanitized.Length == 0 ? "upload.ndjson" : sanitized;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:\-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();

    [GeneratedRegex(@"^[A-Za-z0-9._:\-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex IdempotencyKey();
}
