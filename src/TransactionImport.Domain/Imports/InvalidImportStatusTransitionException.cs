namespace TransactionImport.Domain.Imports;

public sealed class InvalidImportStatusTransitionException : DomainException
{
    public InvalidImportStatusTransitionException(Guid importId, ImportStatus from, ImportStatus to)
        : base("IMPORT_INVALID_STATUS_TRANSITION", $"Import cannot move from '{from}' to '{to}'.")
    {
        ImportId = importId;
        From = from;
        To = to;
    }

    public Guid ImportId { get; }

    public ImportStatus From { get; }

    public ImportStatus To { get; }
}
