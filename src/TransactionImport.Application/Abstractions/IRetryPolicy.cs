namespace TransactionImport.Application.Abstractions;

/// <summary>Executes an operation with bounded retries for transient failures only.</summary>
public interface IRetryPolicy
{
    Task<T> ExecuteAsync<T>(string operation, Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken);
}

/// <summary>Decides whether a failure is transient (safe and useful to retry) or permanent.</summary>
public interface ITransientErrorClassifier
{
    bool IsTransient(Exception exception);
}
