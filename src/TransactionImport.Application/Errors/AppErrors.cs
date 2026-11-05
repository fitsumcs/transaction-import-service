namespace TransactionImport.Application.Errors;

/// <summary>Catalog of application error codes. Codes are part of the public API contract.</summary>
public static class AppErrors
{
    public static AppException ImportNotFound() =>
        new("IMPORT_NOT_FOUND", "The import does not exist.", ErrorKind.NotFound);

    public static AppException UploadNotFound() =>
        new("UPLOAD_NOT_FOUND", "The upload session does not exist or has expired.", ErrorKind.NotFound);

    public static AppException FileTooLarge(long maxBytes) =>
        new("IMPORT_FILE_TOO_LARGE", $"The uploaded file exceeds the allowed size of {maxBytes} bytes.", ErrorKind.PayloadTooLarge);

    public static AppException FileEmpty() =>
        new("IMPORT_FILE_EMPTY", "The uploaded file contains no records.", ErrorKind.RequestValidation);

    public static AppException InvalidFileType() =>
        new("IMPORT_INVALID_FILE_TYPE", "The uploaded file must be UTF-8 NDJSON text.", ErrorKind.UnsupportedMediaType);

    public static AppException CompressedUploadNotSupported() =>
        new("IMPORT_COMPRESSION_NOT_SUPPORTED", "Compressed uploads are not supported.", ErrorKind.UnsupportedMediaType);

    public static AppException IdempotencyKeyMissing() =>
        new("IDEMPOTENCY_KEY_REQUIRED", "The Idempotency-Key header is required.", ErrorKind.RequestValidation);

    public static AppException IdempotencyKeyInvalid() =>
        new("IDEMPOTENCY_KEY_INVALID", "The Idempotency-Key header must be 1-128 characters of letters, digits, '-', '_', ':' or '.'.", ErrorKind.RequestValidation);

    public static AppException IdempotencyKeyInProgress() =>
        new("IDEMPOTENCY_KEY_IN_PROGRESS", "A request with this Idempotency-Key is still being processed.", ErrorKind.Conflict, retryable: true);

    public static AppException ProviderMissing() =>
        new("PROVIDER_ID_REQUIRED", "The X-Provider-Id header is required.", ErrorKind.RequestValidation);

    public static AppException ProviderInvalid() =>
        new("PROVIDER_ID_INVALID", "The provider id must be 1-64 characters of letters, digits, '.', '_', ':' or '-'.", ErrorKind.RequestValidation);

    public static AppException ImportNotCancellable(string status) =>
        new("IMPORT_NOT_CANCELLABLE", $"An import in status '{status}' cannot be cancelled.", ErrorKind.Conflict);

    public static AppException ImportNotRetryable(string status) =>
        new("IMPORT_NOT_RETRYABLE", $"Only failed imports can be retried; current status is '{status}'.", ErrorKind.Conflict);

    public static AppException InvalidCursor() =>
        new("INVALID_CURSOR", "The pagination cursor is invalid.", ErrorKind.RequestValidation);

    public static AppException InvalidLimit(int max) =>
        new("INVALID_LIMIT", $"limit must be between 1 and {max}.", ErrorKind.RequestValidation);

    public static AppException UploadOffsetMismatch(long expected) =>
        new("UPLOAD_OFFSET_MISMATCH", $"Upload-Offset does not match the current offset {expected}.", ErrorKind.Conflict);

    public static AppException UploadNotOpen() =>
        new("UPLOAD_NOT_OPEN", "The upload session is no longer accepting data.", ErrorKind.Conflict);
}
