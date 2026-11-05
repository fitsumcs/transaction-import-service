namespace TransactionImport.Api.Http;

public sealed record ErrorResponse(ErrorBody Error);

public sealed record ErrorBody(string Code, string Message, string RequestId);
