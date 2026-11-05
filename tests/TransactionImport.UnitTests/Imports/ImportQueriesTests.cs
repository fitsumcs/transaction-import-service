using TransactionImport.Application.Errors;
using TransactionImport.Application.Imports;

namespace TransactionImport.UnitTests.Imports;

public sealed class ImportQueriesTests
{
    [Fact]
    public void Cursor_round_trips()
    {
        var encoded = ImportQueries.EncodeCursor(12_345);
        Assert.Equal(12_345, ImportQueries.DecodeCursor(encoded));
        Assert.Equal(0, ImportQueries.DecodeCursor(null));
    }

    [Theory]
    [InlineData("not-base64!")]
    [InlineData("dGVzdA")] // "test"
    [InlineData("")]
    public void Invalid_cursors_are_rejected(string cursor)
    {
        if (cursor.Length == 0)
        {
            Assert.Equal(0, ImportQueries.DecodeCursor(cursor));
            return;
        }

        Assert.Equal("INVALID_CURSOR", Assert.Throws<AppException>(() => ImportQueries.DecodeCursor(cursor)).Code);
    }
}
