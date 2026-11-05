using TransactionImport.Application.Errors;
using TransactionImport.Application.Imports;

namespace TransactionImport.UnitTests.Imports;

public sealed class UploadInspectionStreamTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Streams_bytes_and_computes_sha256()
    {
        var content = "{\"transactionId\":\"t\"}\n"u8.ToArray();
        await using var stream = new UploadInspectionStream(new MemoryStream(content), maxBytes: 1024);

        var buffer = new byte[content.Length];
        Assert.Equal(content.Length, await ReadExactAsync(stream, buffer));
        Assert.Equal(0, await stream.ReadAsync(buffer, Ct));
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content)), stream.GetSha256Hex());
    }

    [Fact]
    public async Task Rejects_files_that_exceed_the_size_limit_mid_stream()
    {
        await using var stream = new UploadInspectionStream(new MemoryStream("{\"a\":1}\n{\"b\":2}\n"u8.ToArray()), maxBytes: 8);

        var error = await Assert.ThrowsAsync<AppException>(async () =>
        {
            var buffer = new byte[64];
            while (await stream.ReadAsync(buffer, Ct) > 0)
            {
            }
        });

        Assert.Equal("IMPORT_FILE_TOO_LARGE", error.Code);
    }

    [Theory]
    [InlineData(new byte[] { 0x1F, 0x8B, 0x08 })]
    [InlineData(new byte[] { (byte)'P', (byte)'K', 0x03, 0x04 })]
    public async Task Rejects_compressed_uploads_without_decompressing(byte[] header)
    {
        await using var stream = new UploadInspectionStream(new MemoryStream(header), maxBytes: 1024);

        var error = await Assert.ThrowsAsync<AppException>(async () => await stream.ReadAsync(new byte[16], Ct));

        Assert.Equal("IMPORT_COMPRESSION_NOT_SUPPORTED", error.Code);
    }

    [Fact]
    public async Task Rejects_binary_content_with_nul_bytes()
    {
        await using var stream = new UploadInspectionStream(new MemoryStream([(byte)'{', 0x00, (byte)'}']), maxBytes: 1024);

        var error = await Assert.ThrowsAsync<AppException>(async () => await stream.ReadAsync(new byte[16], Ct));

        Assert.Equal("IMPORT_INVALID_FILE_TYPE", error.Code);
    }

    [Fact]
    public async Task Rejects_non_ndjson_first_content_byte()
    {
        await using var stream = new UploadInspectionStream(new MemoryStream("not-json\n"u8.ToArray()), maxBytes: 1024);

        var error = await Assert.ThrowsAsync<AppException>(async () => await stream.ReadAsync(new byte[16], Ct));

        Assert.Equal("IMPORT_INVALID_FILE_TYPE", error.Code);
    }

    [Fact]
    public async Task Allows_bom_and_leading_whitespace_before_the_first_object()
    {
        var content = "\uFEFF  \n{}"u8.ToArray();
        await using var stream = new UploadInspectionStream(new MemoryStream(content), maxBytes: 1024);

        Assert.Equal(content.Length, await ReadExactAsync(stream, new byte[content.Length]));
    }

    [Fact]
    public async Task Empty_file_is_rejected_at_eof()
    {
        await using var stream = new UploadInspectionStream(new MemoryStream([]), maxBytes: 1024);

        var error = await Assert.ThrowsAsync<AppException>(async () => await stream.ReadAsync(new byte[16], Ct));

        Assert.Equal("IMPORT_FILE_EMPTY", error.Code);
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), Ct);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
