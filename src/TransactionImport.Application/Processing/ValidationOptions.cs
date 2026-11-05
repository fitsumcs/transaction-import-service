namespace TransactionImport.Application.Processing;

public sealed class ValidationOptions
{
    public const string SectionName = "Validation";

    /// <summary>ISO 4217 codes accepted by the platform. Anything else is rejected as INVALID_CURRENCY.</summary>
    public IReadOnlyCollection<string> SupportedCurrencies { get; set; } =
    [
        "USD", "EUR", "GBP", "JPY", "CHF", "CAD", "AUD", "NZD", "SEK", "NOK", "DKK", "PLN", "CZK", "HUF",
        "CNY", "HKD", "SGD", "INR", "KRW", "ZAR", "BRL", "MXN", "TRY", "AED", "SAR", "ILS", "ETB", "KES",
        "NGN", "EGP",
    ];

    public int MaxIdentifierLength { get; set; } = 64;

    public int MaxDescriptionLength { get; set; } = 500;

    public int MaxAmountScale { get; set; } = 4;

    public decimal MaxAmount { get; set; } = 999_999_999_999_999.9999m;

    public DateTimeOffset MinTimestamp { get; set; } = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Tolerated clock skew for timestamps in the future.</summary>
    public TimeSpan MaxFutureSkew { get; set; } = TimeSpan.FromDays(1);
}
