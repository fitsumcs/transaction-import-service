namespace TransactionImport.Domain.Transactions;

public enum RiskLevel
{
    Low,
    Medium,
    High,
}

public readonly record struct RiskScore
{
    public const int Min = 0;
    public const int Max = 100;
    public const int MediumThreshold = 40;
    public const int HighThreshold = 70;

    public RiskScore(int value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, Min);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, Max);
        Value = value;
    }

    public int Value { get; }

    public RiskLevel Level => Value switch
    {
        >= HighThreshold => RiskLevel.High,
        >= MediumThreshold => RiskLevel.Medium,
        _ => RiskLevel.Low,
    };
}
