using System.Net.Sockets;
using Npgsql;
using StackExchange.Redis;
using TransactionImport.Application.Abstractions;
using TransactionImport.Application.Errors;
using TransactionImport.Infrastructure.Queue;

namespace TransactionImport.Infrastructure.Resilience;

/// <summary>
/// Classifies failures as transient (safe to retry) or permanent. Validation failures, duplicates,
/// permanent constraint violations, cancellations and programming errors are never retried.
/// </summary>
internal sealed class TransientErrorClassifier : ITransientErrorClassifier
{
    public bool IsTransient(Exception exception) => exception switch
    {
        OperationCanceledException => false,
        AppException { Retryable: true } => true,
        AppException => false,
        TimeoutException => true,
        SocketException => true,
        IOException => true,
        NpgsqlException npgsql => IsTransientNpgsql(npgsql),
        RedisException redis => RedisImportJobQueue.IsTransient(redis),
        _ => false,
    };

    private static bool IsTransientNpgsql(NpgsqlException exception)
    {
        if (exception.IsTransient)
        {
            return true;
        }

        // Serialization failure / deadlock — safe to retry the whole transaction.
        return exception.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected;
    }
}
