namespace TransactionImport.Domain;

/// <summary>Base type for business-rule violations. <see cref="Code"/> is stable and safe to expose.</summary>
public abstract class DomainException : Exception
{
    protected DomainException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
