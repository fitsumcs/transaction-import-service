using System.ComponentModel.DataAnnotations;

namespace TransactionImport.Infrastructure.Persistence;

public sealed class PostgresOptions
{
    public const string SectionName = "Postgres";

    [Required]
    public string ConnectionString { get; set; } = string.Empty;
}
