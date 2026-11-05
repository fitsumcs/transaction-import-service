using TransactionImport.Application.Errors;
using TransactionImport.Application.Imports;

namespace TransactionImport.UnitTests.Imports;

public sealed class InputRulesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Provider_id_is_required(string? value) =>
        Assert.Equal("PROVIDER_ID_REQUIRED", Assert.Throws<AppException>(() => InputRules.RequireProviderId(value)).Code);

    [Theory]
    [InlineData("bad id")]
    [InlineData("../x")]
    [InlineData("x/y")]
    public void Provider_id_rejects_unsafe_characters(string value) =>
        Assert.Equal("PROVIDER_ID_INVALID", Assert.Throws<AppException>(() => InputRules.RequireProviderId(value)).Code);

    [Fact]
    public void Idempotency_key_is_required() =>
        Assert.Equal("IDEMPOTENCY_KEY_REQUIRED", Assert.Throws<AppException>(() => InputRules.RequireIdempotencyKey(null)).Code);

    [Theory]
    [InlineData("../secret.ndjson", "secret.ndjson")]
    [InlineData("C:\\uploads\\a b.ndjson", "a b.ndjson")]
    [InlineData("../../../etc/passwd", "passwd")]
    [InlineData("", "upload.ndjson")]
    [InlineData(null, "upload.ndjson")]
    [InlineData("....hidden", "hidden")]
    public void File_name_sanitization_strips_paths_and_unsafe_characters(string? input, string expected) =>
        Assert.Equal(expected, InputRules.SanitizeFileName(input));
}
