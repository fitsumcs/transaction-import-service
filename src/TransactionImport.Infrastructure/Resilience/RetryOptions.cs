namespace TransactionImport.Infrastructure.Resilience;

public sealed class RetryOptions
{
    public const string SectionName = "Retry";

    public int MaxAttempts { get; set; } = 5;

    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(100);

    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(5);
}
