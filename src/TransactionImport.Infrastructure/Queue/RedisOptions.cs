using System.ComponentModel.DataAnnotations;

namespace TransactionImport.Infrastructure.Queue;

public sealed class RedisOptions
{
    public const string SectionName = "Redis";

    [Required]
    public string ConnectionString { get; set; } = "localhost:6379";

    public string StreamKey { get; set; } = "imports:jobs";

    public string DeadLetterStreamKey { get; set; } = "imports:jobs:dlq";

    public string ConsumerGroup { get; set; } = "workers";

    /// <summary>Messages idle longer than this are claimed by another consumer (crashed worker).</summary>
    public TimeSpan ClaimIdleTime { get; set; } = TimeSpan.FromSeconds(90);

    public int MaxDeliveryAttempts { get; set; } = 10;
}
