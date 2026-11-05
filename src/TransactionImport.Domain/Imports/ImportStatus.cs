namespace TransactionImport.Domain.Imports;

public enum ImportStatus
{
    Pending,
    Processing,
    Completed,
    Failed,
    Cancelling,
    Cancelled,
}
