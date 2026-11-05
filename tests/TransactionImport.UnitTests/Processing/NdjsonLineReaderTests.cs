using System.Text;
using TransactionImport.Application.Processing;

namespace TransactionImport.UnitTests.Processing;

public sealed class NdjsonLineReaderTests
{
    [Fact]
    public async Task Reads_lines_including_a_final_line_without_newline()
    {
        var lines = await ReadAllAsync("a\nbb\n\nccc");

        Assert.Equal(["a", "bb", "", "ccc"], lines.Select(line => line.Text));
        Assert.Equal([1L, 2, 3, 4], lines.Select(line => line.LineNumber));
        Assert.Equal([2L, 5, 6, 9], lines.Select(line => line.EndOffset));
    }

    [Fact]
    public async Task Reports_overlong_line_once_and_continues_with_the_next_line()
    {
        var longLine = new string('x', 5_000);
        var lines = await ReadAllAsync($"ok\n{longLine}\nnext\n", maxLineBytes: 100, bufferSize: 256);

        Assert.Equal(3, lines.Count);
        Assert.True(lines[1].TooLong);
        Assert.Equal(NdjsonLineReader.ExcerptBytes, lines[1].Text.Length);
        Assert.Equal("next", lines[2].Text);
        Assert.Equal(3 + 5_001 + 5, lines[2].EndOffset);
    }

    [Fact]
    public async Task Overlong_final_line_without_newline_is_reported()
    {
        var lines = await ReadAllAsync("ok\n" + new string('y', 300), maxLineBytes: 50, bufferSize: 64);

        Assert.Equal(2, lines.Count);
        Assert.True(lines[1].TooLong);
        Assert.Equal(303, lines[1].EndOffset);
    }

    [Fact]
    public async Task Multibyte_characters_survive_one_byte_reads()
    {
        const string text = "{\"d\":\"café ☕ 日本\"}\n{\"d\":\"😀\"}";
        await using var stream = new TrickleStream(Encoding.UTF8.GetBytes(text));
        using var reader = new NdjsonLineReader(stream, maxLineBytes: 1024, bufferSize: 16);

        var lines = new List<string>();
        while (await reader.ReadNextAsync(TestContext.Current.CancellationToken))
        {
            lines.Add(Encoding.UTF8.GetString(reader.Current));
        }

        Assert.Equal(["{\"d\":\"café ☕ 日本\"}", "{\"d\":\"😀\"}"], lines);
    }

    [Fact]
    public async Task Skips_utf8_byte_order_mark_at_start_of_file()
    {
        var bytes = "\uFEFFfirst\nsecond"u8.ToArray();
        await using var stream = new MemoryStream(bytes);
        using var reader = new NdjsonLineReader(stream, maxLineBytes: 100);

        Assert.True(await reader.ReadNextAsync(TestContext.Current.CancellationToken));
        Assert.Equal("first", Encoding.UTF8.GetString(reader.Current));
    }

    [Fact]
    public async Task Resumes_from_a_checkpoint_offset_with_correct_line_numbers()
    {
        const string text = "l1\nl2\nl3\nl4\n";
        var all = await ReadAllAsync(text);
        var checkpoint = all[1];

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        stream.Seek(checkpoint.EndOffset, SeekOrigin.Begin);
        using var reader = new NdjsonLineReader(stream, 100, checkpoint.EndOffset, checkpoint.LineNumber);

        Assert.True(await reader.ReadNextAsync(TestContext.Current.CancellationToken));
        Assert.Equal("l3", Encoding.UTF8.GetString(reader.Current));
        Assert.Equal(3, reader.LineNumber);
    }

    [Fact]
    public async Task Honors_cancellation()
    {
        await using var stream = new MemoryStream(new byte[10]);
        using var reader = new NdjsonLineReader(stream, 100);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await reader.ReadNextAsync(new CancellationToken(canceled: true)));
    }

    private static async Task<List<ReadLine>> ReadAllAsync(string text, int maxLineBytes = 1024, int bufferSize = 64 * 1024)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        using var reader = new NdjsonLineReader(stream, maxLineBytes, bufferSize: bufferSize);
        var lines = new List<ReadLine>();
        while (await reader.ReadNextAsync(TestContext.Current.CancellationToken))
        {
            lines.Add(new ReadLine(Encoding.UTF8.GetString(reader.Current), reader.LineNumber, reader.EndOffset, reader.IsTooLong));
        }

        return lines;
    }

    private sealed record ReadLine(string Text, long LineNumber, long EndOffset, bool TooLong);

    /// <summary>Returns at most one byte per read to exercise buffer boundaries.</summary>
    private sealed class TrickleStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}
