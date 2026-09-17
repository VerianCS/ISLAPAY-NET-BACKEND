using System.Text.Json;
using IslaPay.Contracts;
using IslaPay.Contracts.Exchange;
using IslaPay.Contracts.P2P;
using IslaPay.Contracts.Wallet;
using IslaPay.Platform;

namespace IslaPay.Contracts.Tests;

/// <summary>
/// These lock the wire format. If one fails, the contract changed — which may
/// be intended, but it is never incidental, and a client in the field is
/// affected.
/// </summary>
public class WalletWireTests
{
    private static readonly JsonSerializerOptions Json = IslaPayJson.Options;

    [Fact]
    public void Wallet_response_matches_the_contract_example()
    {
        var response = new WalletResponse(
            Accounts:
            [
                new AccountDto("USD", Money.Parse("1250.00", Currency.Usd), "4587"),
            ],
            Rates: new Dictionary<string, string> { ["USD_USDT"] = "1.0000" },
            Transactions: new CursorPage<TransactionDto>(
                Items:
                [
                    new TransactionDto(
                        Id: "01JQ",
                        Type: LedgerEntryTypes.StorePurchase,
                        Meta: new Dictionary<string, string> { ["merchant"] = "Tienda Solar" },
                        Amount: Money.Parse("-350.00", Currency.Usdt),
                        OccurredAt: new DateTimeOffset(2026, 9, 16, 14, 42, 0, TimeSpan.Zero)),
                ],
                NextCursor: "eyJ"));

        var json = JsonSerializer.Serialize(response, Json);

        Assert.Equal(
            """
            {"accounts":[{"currency":"USD","balance":{"amount":"1250.00","currency":"USD"},"cardLast4":"4587"}],"rates":{"USD_USDT":"1.0000"},"transactions":{"items":[{"id":"01JQ","type":"store_purchase","meta":{"merchant":"Tienda Solar"},"amount":{"amount":"-350.000000","currency":"USDT"},"occurredAt":"2026-09-16T14:42:00.000Z"}],"nextCursor":"eyJ"}}
            """,
            json);
    }

    [Fact]
    public void Money_never_appears_as_a_bare_number()
    {
        var json = JsonSerializer.Serialize(
            new RechargeRequest(Money.Parse("50.00", Currency.Usd), "zelle"), Json);

        Assert.Contains("\"amount\":\"50.00\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"amount\":50", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Timestamps_end_in_Z_not_an_offset()
    {
        var entry = new TransactionDto(
            "1", LedgerEntryTypes.Recharge,
            new Dictionary<string, string> { ["method"] = "zelle" },
            Money.Parse("10.00", Currency.Usd),
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(-5)));

        var json = JsonSerializer.Serialize(entry, Json);

        // Same instant, expressed in UTC.
        Assert.Contains("\"occurredAt\":\"2026-01-02T08:04:05.000Z\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("+00:00", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_last_page_omits_the_cursor_entirely()
    {
        var json = JsonSerializer.Serialize(new CursorPage<string>(["a"]), Json);
        Assert.Equal("""{"items":["a"]}""", json);
    }

    [Fact]
    public void Deposit_address_omits_an_absent_memo()
    {
        var json = JsonSerializer.Serialize(
            new DepositAddressResponse("USDT", "TRON (TRC-20)", "TXyz"), Json);

        Assert.Equal(
            """{"currency":"USDT","network":"TRON (TRC-20)","address":"TXyz"}""",
            json);
    }
}

public class ForwardCompatibilityTests
{
    private static readonly JsonSerializerOptions Json = IslaPayJson.Options;

    [Fact]
    public void A_property_the_reader_does_not_know_is_ignored()
    {
        // The server is ahead of this build, mid rolling deploy.
        var dto = JsonSerializer.Deserialize<AccountDto>(
            """
            {"currency":"USD","balance":{"amount":"1.00","currency":"USD"},
             "cardLast4":"0001","frozen":true,"openedAt":"2026-01-01T00:00:00.000Z"}
            """, Json);

        Assert.NotNull(dto);
        Assert.Equal("0001", dto.CardLast4);
    }

    [Fact]
    public void An_unknown_movement_type_still_deserialises()
    {
        // This is why Type is a string and not an enum: a new kind of movement
        // must not stop the whole history from loading on an older client.
        var dto = JsonSerializer.Deserialize<TransactionDto>(
            """
            {"id":"1","type":"payroll_credit","meta":{"employer":"ACME"},
             "amount":{"amount":"500.00","currency":"USD"},
             "occurredAt":"2026-09-16T14:42:00.000Z"}
            """, Json);

        Assert.NotNull(dto);
        Assert.Equal("payroll_credit", dto.Type);
        Assert.DoesNotContain(dto.Type, LedgerEntryTypes.All);
    }

    [Fact]
    public void An_unknown_error_code_still_deserialises()
    {
        var problem = JsonSerializer.Deserialize<ApiProblem>(
            """{"code":"card_frozen","title":"Card frozen","status":409}""", Json);

        Assert.NotNull(problem);
        Assert.Equal("card_frozen", problem.Code);
    }
}

public class ErrorWireTests
{
    private static readonly JsonSerializerOptions Json = IslaPayJson.Options;

    [Fact]
    public void Problem_carries_the_meta_the_clients_type_needs()
    {
        var problem = new ApiProblem(
            Code: ErrorCodes.InsufficientFunds,
            Title: "Insufficient funds",
            Status: 422,
            Detail: "Balance 45.00 USDT is below the requested 100.00 USDT.",
            Meta: new Dictionary<string, object>
            {
                ["currency"] = "USDT",
                ["available"] = "45.00",
                ["requested"] = "100.00",
            });

        var json = JsonSerializer.Serialize(problem, Json);

        Assert.Contains("\"code\":\"insufficient_funds\"", json, StringComparison.Ordinal);
        Assert.Contains("\"currency\":\"USDT\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Codes_that_the_client_cannot_construct_without_a_currency_are_listed()
    {
        // InsufficientFunds(currency), ExchangeFundUnavailable(currency) and
        // DuplicateCard(currency, kind) all take one in the Flutter client, so
        // a response missing it cannot be mapped to a typed failure.
        Assert.Contains(ErrorCodes.InsufficientFunds, ErrorCodes.RequireCurrencyMeta);
        Assert.Contains(ErrorCodes.FundUnavailable, ErrorCodes.RequireCurrencyMeta);
        Assert.Contains(ErrorCodes.DuplicateCard, ErrorCodes.RequireCurrencyMeta);
        Assert.DoesNotContain(ErrorCodes.InvalidAmount, ErrorCodes.RequireCurrencyMeta);
    }

    [Fact]
    public void Optional_fields_are_omitted_rather_than_sent_as_null()
    {
        var json = JsonSerializer.Serialize(
            new ApiProblem(ErrorCodes.InvalidAmount, "Invalid amount", 422), Json);

        Assert.Equal(
            """{"code":"invalid_amount","title":"Invalid amount","status":422}""",
            json);
    }
}

public class ExchangeWireTests
{
    private static readonly JsonSerializerOptions Json = IslaPayJson.Options;

    [Fact]
    public void An_unexecutable_quote_is_a_normal_response_with_a_reason()
    {
        var quote = new QuoteResponse(
            QuoteId: "01JQ",
            From: "USD",
            To: "USDT",
            Amount: Money.Parse("100.00", Currency.Usd),
            Fee: Money.Parse("1.00", Currency.Usd),
            Received: Money.Parse("99.00", Currency.Usdt),
            Rate: "1.0000",
            ExpiresAt: new DateTimeOffset(2026, 9, 17, 10, 0, 30, TimeSpan.Zero),
            Executable: false,
            Reason: ErrorCodes.FundUnavailable);

        var json = JsonSerializer.Serialize(quote, Json);

        Assert.Contains("\"executable\":false", json, StringComparison.Ordinal);
        Assert.Contains("\"reason\":\"fund_unavailable\"", json, StringComparison.Ordinal);
        // The fee is in the source currency, what lands is in the target.
        Assert.Contains("\"fee\":{\"amount\":\"1.00\",\"currency\":\"USD\"}", json, StringComparison.Ordinal);
        Assert.Contains("\"received\":{\"amount\":\"99.000000\",\"currency\":\"USDT\"}", json, StringComparison.Ordinal);
    }

    [Fact]
    public void An_executable_quote_omits_the_reason()
    {
        var quote = new QuoteResponse(
            "01JQ", "USD", "USDT",
            Money.Parse("100.00", Currency.Usd),
            Money.Parse("1.00", Currency.Usd),
            Money.Parse("99.00", Currency.Usdt),
            "1.0000",
            new DateTimeOffset(2026, 9, 17, 10, 0, 30, TimeSpan.Zero),
            Executable: true);

        Assert.DoesNotContain("reason", JsonSerializer.Serialize(quote, Json), StringComparison.Ordinal);
    }

    [Fact]
    public void Executing_sends_only_the_quote_id()
    {
        // No amounts: the client cannot execute at figures the user never saw.
        Assert.Equal(
            """{"quoteId":"01JQ"}""",
            JsonSerializer.Serialize(new ConversionRequest("01JQ"), Json));
    }
}

public class P2PWireTests
{
    private static readonly JsonSerializerOptions Json = IslaPayJson.Options;

    [Fact]
    public void Side_travels_as_a_name_not_an_ordinal()
    {
        var json = JsonSerializer.Serialize(
            new P2PTradeRequest(P2PSide.Sell, Money.Parse("100.00", Currency.Usdt), "cup_tm"),
            Json);

        Assert.Equal(
            """{"side":"sell","amount":{"amount":"100.000000","currency":"USDT"},"methodId":"cup_tm"}""",
            json);
    }

    [Theory]
    [InlineData("sell", P2PSide.Sell)]
    [InlineData("buy", P2PSide.Buy)]
    public void Side_round_trips(string wire, P2PSide expected)
    {
        var request = JsonSerializer.Deserialize<P2PTradeRequest>(
            $$"""{"side":"{{wire}}","amount":{"amount":"1.000000","currency":"USDT"},"methodId":"x"}""",
            Json);

        Assert.NotNull(request);
        Assert.Equal(expected, request.Side);
    }

    [Fact]
    public void A_method_defaults_to_available()
    {
        var method = new P2PMethodDto("cup_tm", "CUP Transfermóvil", "CUP", "380");
        Assert.True(method.Available);
        Assert.Contains("\"available\":true", JsonSerializer.Serialize(method, Json), StringComparison.Ordinal);
    }
}
