namespace TransactionImport.Application.Errors;

/// <summary>Classifies failures so hosts can map them consistently (HTTP status, retry decisions, logging level).</summary>
public enum ErrorKind
{
    /// <summary>The request itself is malformed (headers, parameters, multipart structure).</summary>
    RequestValidation,

    NotFound,

    /// <summary>The request conflicts with current state (business rule), e.g. cancelling a completed import.</summary>
    Conflict,

    PayloadTooLarge,
    UnsupportedMediaType,
    RateLimited,

    /// <summary>A dependency is unavailable; the operation may succeed later.</summary>
    Unavailable,
}

/// <summary>
/// Structured application error: a stable machine-readable <see cref="Code"/>, a message that is safe to show
/// to clients, a <see cref="Kind"/> for transport mapping and a retryability flag. Internal diagnostic context
/// travels in <see cref="Exception.InnerException"/> and <see cref="Exception.Data"/> and is only logged.
/// </summary>
public sealed class AppException : Exception
{
    public AppException(string code, string safeMessage, ErrorKind kind, bool retryable = false, Exception? innerException = null)
        : base(safeMessage, innerException)
    {
        Code = code;
        Kind = kind;
        Retryable = retryable;
    }

    public string Code { get; }

    public ErrorKind Kind { get; }

    public bool Retryable { get; }
}
