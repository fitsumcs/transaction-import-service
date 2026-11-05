using System.Buffers;
using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using TransactionImport.Domain.Transactions;

namespace TransactionImport.Application.Processing;

/// <summary>
/// Parses, validates and normalizes one NDJSON line. Never throws for bad input: every problem becomes a
/// <see cref="RecordRejection"/> with a stable code. Normalization rules:
/// <list type="bullet">
/// <item>Identifiers: trimmed; 1..64 chars of <c>[A-Za-z0-9._:-]</c>, starting with a letter or digit.</item>
/// <item>amount: JSON number only, &gt; 0, at most 4 decimal places; trailing zeros are insignificant.</item>
/// <item>currency: trimmed and upper-cased, then checked against the supported list.</item>
/// <item>timestamp: ISO-8601 with an explicit offset or <c>Z</c>; converted to UTC and truncated to milliseconds;
/// must be between 2000-01-01 and now + 1 day.</item>
/// <item>description: NFC-normalized; tab/CR/LF become spaces; other control characters and bidi overrides
/// are rejected; invisible format characters (e.g. zero-width space) are removed; whitespace runs collapse to
/// one space; trimmed; empty becomes null; max 500 characters after normalization.</item>
/// <item>Unknown fields are ignored and never persisted. Duplicate property names are rejected.</item>
/// </list>
/// </summary>
public sealed partial class TransactionLineParser
{
    private const int RawValueMaxFieldLength = 64;

    private static readonly JsonReaderOptions ReaderOptions = new() { MaxDepth = 16, CommentHandling = JsonCommentHandling.Disallow };

    private readonly ValidationOptions _options;
    private readonly FrozenSet<string> _currencies;

    public TransactionLineParser(ValidationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _currencies = options.SupportedCurrencies.Select(code => code.ToUpperInvariant()).ToFrozenSet(StringComparer.Ordinal);
    }

    public static ParsedLine ParseTooLong(long lineNumber, ReadOnlySpan<byte> excerpt) =>
        ParsedLine.Reject(new RecordRejection(lineNumber, RejectionCode.LineTooLong, "Line exceeds the maximum allowed length.", null, ExcerptJson(excerpt)));

    public ParsedLine Parse(ReadOnlySpan<byte> line, long lineNumber, DateTimeOffset now)
    {
        line = line.Trim(" \t\r"u8);
        if (line.IsEmpty)
        {
            return ParsedLine.Skipped;
        }

        if (!Utf8.IsValid(line))
        {
            return Reject(lineNumber, RejectionCode.InvalidEncoding, "Line is not valid UTF-8.", null, ExcerptJson(line));
        }

        RawFields raw;
        try
        {
            var error = ReadFields(line, out raw);
            if (error is { } failure)
            {
                var rawValue = failure.Code is RejectionCode.InvalidJson or RejectionCode.NotAnObject ? ExcerptJson(line) : raw.ToRawJson();
                return Reject(lineNumber, failure.Code, failure.Message, raw.TransactionId, rawValue);
            }
        }
        catch (JsonException)
        {
            return Reject(lineNumber, RejectionCode.InvalidJson, "Line is not valid JSON.", null, ExcerptJson(line));
        }
        catch (InvalidOperationException)
        {
            return Reject(lineNumber, RejectionCode.InvalidJson, "Line contains an invalid JSON string.", null, ExcerptJson(line));
        }

        return Validate(raw, lineNumber, now);
    }

    private ParsedLine Validate(RawFields raw, long lineNumber, DateTimeOffset now)
    {
        var rawJson = raw.ToRawJson();
        ParsedLine Fail(RejectionCode code, string message) => Reject(lineNumber, code, message, Truncate(raw.TransactionId), rawJson);

        foreach (var (name, value) in raw.Required())
        {
            if (value is null)
            {
                return Fail(RejectionCode.MissingField, $"Missing required field '{name}'.");
            }
        }

        var transactionId = raw.TransactionId!.Trim();
        if (!IsValidIdentifier(transactionId))
        {
            return Fail(RejectionCode.InvalidTransactionId, "transactionId must be 1-64 characters of letters, digits, '.', '_', ':' or '-'.");
        }

        var accountId = raw.AccountId!.Trim();
        if (!IsValidIdentifier(accountId))
        {
            return Fail(RejectionCode.InvalidAccountId, "accountId must be 1-64 characters of letters, digits, '.', '_', ':' or '-'.");
        }

        var merchantId = raw.MerchantId!.Trim();
        if (!IsValidIdentifier(merchantId))
        {
            return Fail(RejectionCode.InvalidMerchantId, "merchantId must be 1-64 characters of letters, digits, '.', '_', ':' or '-'.");
        }

        var amount = raw.Amount!.Value;
        if (amount <= 0m || amount > _options.MaxAmount)
        {
            return Fail(RejectionCode.InvalidAmount, "Amount must be greater than zero and within the supported range.");
        }

        if (amount.Scale > _options.MaxAmountScale && decimal.Round(amount, _options.MaxAmountScale) != amount)
        {
            return Fail(RejectionCode.InvalidAmount, $"Amount supports at most {_options.MaxAmountScale} decimal places.");
        }

        var currency = raw.Currency!.Trim().ToUpperInvariant();
        if (!CurrencyShape().IsMatch(currency))
        {
            return Fail(RejectionCode.InvalidCurrency, "Currency must be a three-letter code.");
        }

        if (!_currencies.Contains(currency))
        {
            return Fail(RejectionCode.InvalidCurrency, "Currency must be a supported three-letter code.");
        }

        var timestampText = raw.Timestamp!.Trim();
        if (!TimestampShape().IsMatch(timestampText)
            || !DateTimeOffset.TryParse(timestampText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
        {
            return Fail(RejectionCode.InvalidTimestamp, "Timestamp must be an ISO-8601 date-time with a UTC offset.");
        }

        if (timestamp < _options.MinTimestamp || timestamp > now + _options.MaxFutureSkew)
        {
            return Fail(RejectionCode.TimestampOutOfRange, "Timestamp is outside the accepted range.");
        }

        var utc = timestamp.UtcDateTime;
        utc = new DateTime(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);

        string? description = null;
        if (raw.Description is not null)
        {
            var result = NormalizeDescription(raw.Description, out description);
            if (result is { } failure)
            {
                return Fail(failure.Code, failure.Message);
            }
        }

        return ParsedLine.Accept(new NormalizedTransaction(transactionId, accountId, merchantId, amount, currency, utc, description));
    }

    private Failure? NormalizeDescription(string value, out string? normalized)
    {
        normalized = null;
        string composed;
        try
        {
            composed = value.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return new Failure(RejectionCode.InvalidCharacters, "Description contains invalid Unicode.");
        }

        var builder = new StringBuilder(composed.Length);
        var pendingSpace = false;
        for (var i = 0; i < composed.Length; i++)
        {
            var c = composed[i];
            if (char.IsHighSurrogate(c) && i + 1 < composed.Length && char.IsLowSurrogate(composed[i + 1]))
            {
                AppendPendingSpace(builder, ref pendingSpace);
                builder.Append(c).Append(composed[++i]);
                continue;
            }

            if (char.IsSurrogate(c))
            {
                return new Failure(RejectionCode.InvalidCharacters, "Description contains invalid Unicode.");
            }

            if (c is '\t' or '\r' or '\n' || char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (char.IsControl(c) || IsBidiControl(c))
            {
                return new Failure(RejectionCode.InvalidCharacters, "Description contains control characters.");
            }

            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format)
            {
                continue;
            }

            AppendPendingSpace(builder, ref pendingSpace);
            builder.Append(c);
        }

        if (builder.Length > _options.MaxDescriptionLength)
        {
            return new Failure(RejectionCode.DescriptionTooLong, $"Description must be at most {_options.MaxDescriptionLength} characters.");
        }

        normalized = builder.Length == 0 ? null : builder.ToString();
        return null;
    }

    private static void AppendPendingSpace(StringBuilder builder, ref bool pendingSpace)
    {
        if (pendingSpace)
        {
            builder.Append(' ');
            pendingSpace = false;
        }
    }

    private static bool IsBidiControl(char c) => c is (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069');

    private bool IsValidIdentifier(string value) =>
        value.Length >= 1 && value.Length <= _options.MaxIdentifierLength && IdentifierShape().IsMatch(value);

    private Failure? ReadFields(ReadOnlySpan<byte> line, out RawFields raw)
    {
        raw = new RawFields();
        var reader = new Utf8JsonReader(line, ReaderOptions);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return new Failure(RejectionCode.NotAnObject, "Each line must be a JSON object.");
        }

        Failure? typeError = null;
        var seen = 0;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var field = IdentifyField(ref reader);
            reader.Read();

            if (field == Field.Unknown)
            {
                reader.Skip();
                continue;
            }

            if ((seen & (int)field) != 0)
            {
                return new Failure(RejectionCode.InvalidJson, "Line contains a duplicate property.");
            }

            seen |= (int)field;
            var error = field switch
            {
                Field.Amount => ReadAmount(ref reader, ref raw),
                Field.Description => ReadString(ref reader, "description", nullable: true, out raw.Description, _options.MaxDescriptionLength * 6),
                Field.TransactionId => ReadString(ref reader, "transactionId", nullable: false, out raw.TransactionId),
                Field.AccountId => ReadString(ref reader, "accountId", nullable: false, out raw.AccountId),
                Field.MerchantId => ReadString(ref reader, "merchantId", nullable: false, out raw.MerchantId),
                Field.Currency => ReadString(ref reader, "currency", nullable: false, out raw.Currency),
                Field.Timestamp => ReadString(ref reader, "timestamp", nullable: false, out raw.Timestamp),
                _ => null,
            };
            typeError ??= error;
        }

        if (reader.TokenType != JsonTokenType.EndObject || reader.Read())
        {
            return new Failure(RejectionCode.InvalidJson, "Line must contain exactly one JSON object.");
        }

        return typeError;
    }

    private static Field IdentifyField(ref Utf8JsonReader reader)
    {
        if (reader.ValueTextEquals("transactionId"u8))
        {
            return Field.TransactionId;
        }

        if (reader.ValueTextEquals("accountId"u8))
        {
            return Field.AccountId;
        }

        if (reader.ValueTextEquals("merchantId"u8))
        {
            return Field.MerchantId;
        }

        if (reader.ValueTextEquals("amount"u8))
        {
            return Field.Amount;
        }

        if (reader.ValueTextEquals("currency"u8))
        {
            return Field.Currency;
        }

        if (reader.ValueTextEquals("timestamp"u8))
        {
            return Field.Timestamp;
        }

        return reader.ValueTextEquals("description"u8) ? Field.Description : Field.Unknown;
    }

    private static Failure? ReadString(ref Utf8JsonReader reader, string name, bool nullable, out string? value, int maxRawBytes = 1024)
    {
        value = null;
        if (reader.TokenType == JsonTokenType.Null && nullable)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            reader.Skip();
            return new Failure(RejectionCode.InvalidFieldType, $"Field '{name}' must be a string.");
        }

        var rawLength = reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length;
        if (rawLength > maxRawBytes)
        {
            var code = name == "description" ? RejectionCode.DescriptionTooLong : InvalidCodeFor(name);
            return new Failure(code, $"Field '{name}' is too long.");
        }

        value = reader.GetString();
        return null;
    }

    private static Failure? ReadAmount(ref Utf8JsonReader reader, ref RawFields raw)
    {
        if (reader.TokenType != JsonTokenType.Number)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                raw.AmountText = reader.GetString();
            }

            reader.Skip();
            return new Failure(RejectionCode.InvalidFieldType, "Field 'amount' must be a number.");
        }

        raw.AmountText = Encoding.UTF8.GetString(reader.ValueSpan);
        if (!reader.TryGetDecimal(out var amount))
        {
            return new Failure(RejectionCode.InvalidAmount, "Amount is outside the supported range.");
        }

        raw.Amount = amount;
        return null;
    }

    private static RejectionCode InvalidCodeFor(string name) => name switch
    {
        "transactionId" => RejectionCode.InvalidTransactionId,
        "accountId" => RejectionCode.InvalidAccountId,
        "merchantId" => RejectionCode.InvalidMerchantId,
        "currency" => RejectionCode.InvalidCurrency,
        "timestamp" => RejectionCode.InvalidTimestamp,
        _ => RejectionCode.InvalidFieldType,
    };

    private static ParsedLine Reject(long lineNumber, RejectionCode code, string message, string? transactionId, string? rawValue) =>
        ParsedLine.Reject(new RecordRejection(lineNumber, code, message, Truncate(transactionId), rawValue));

    private static string? Truncate(string? value) =>
        value is null ? null : value.Length <= RawValueMaxFieldLength ? value : value[..RawValueMaxFieldLength];

    /// <summary>Builds <c>{"excerpt":"..."}</c> from the first bytes of an unparseable line, with control characters removed.</summary>
    internal static string ExcerptJson(ReadOnlySpan<byte> line)
    {
        var slice = line[..Math.Min(line.Length, NdjsonLineReader.ExcerptBytes)];
        var text = Encoding.UTF8.GetString(slice);
        var cleaned = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!char.IsControl(c) && !IsBidiControl(c))
            {
                cleaned.Append(c);
            }
        }

        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("excerpt", cleaned.ToString());
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:\-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierShape();

    [GeneratedRegex("^[A-Z]{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex CurrencyShape();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampShape();

    [Flags]
    private enum Field
    {
        Unknown = 0,
        TransactionId = 1,
        AccountId = 2,
        MerchantId = 4,
        Amount = 8,
        Currency = 16,
        Timestamp = 32,
        Description = 64,
    }

    private readonly record struct Failure(RejectionCode Code, string Message);

    /// <summary>Field values as found in the line, before validation. Description is never included in raw output.</summary>
    private struct RawFields
    {
        public string? TransactionId;
        public string? AccountId;
        public string? MerchantId;
        public decimal? Amount;
        public string? AmountText;
        public string? Currency;
        public string? Timestamp;
        public string? Description;

        public readonly IEnumerable<(string Name, object? Value)> Required()
        {
            yield return ("transactionId", TransactionId);
            yield return ("accountId", AccountId);
            yield return ("merchantId", MerchantId);
            yield return ("amount", Amount is null && AmountText is null ? null : (object?)Amount ?? AmountText);
            yield return ("currency", Currency);
            yield return ("timestamp", Timestamp);
        }

        public readonly string? ToRawJson()
        {
            var buffer = new ArrayBufferWriter<byte>(256);
            var any = false;
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                any |= Write(writer, "transactionId", TransactionId);
                any |= Write(writer, "accountId", AccountId);
                any |= Write(writer, "merchantId", MerchantId);
                any |= Write(writer, "amount", AmountText);
                any |= Write(writer, "currency", Currency);
                any |= Write(writer, "timestamp", Timestamp);
                writer.WriteEndObject();
            }

            return any ? Encoding.UTF8.GetString(buffer.WrittenSpan) : null;
        }

        private static bool Write(Utf8JsonWriter writer, string name, string? value)
        {
            if (value is null)
            {
                return false;
            }

            writer.WriteString(name, Truncate(value));
            return true;
        }
    }
}
