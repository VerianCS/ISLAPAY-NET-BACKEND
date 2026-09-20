using System.Text.Json;
using IslaPay.Platform;
using IslaPay.Platform.Api;
using IslaPay.Platform.Serialization;
using IslaPay.Wallet.Contracts;

namespace IslaPay.Wallet.Tests;

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
    public void Deposit_address_omits_an_absent_memo()
    {
        var json = JsonSerializer.Serialize(
            new DepositAddressResponse("USDT", "TRON (TRC-20)", "TXyz"), Json);

        Assert.Equal(
            """{"currency":"USDT","network":"TRON (TRC-20)","address":"TXyz"}""",
            json);
    }
}

public class WalletForwardCompatibilityTests
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
        Assert.Equal("0001", dto!.CardLast4);
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
        Assert.Equal("payroll_credit", dto!.Type);
        Assert.DoesNotContain(dto.Type, LedgerEntryTypes.All);
    }
}

public class WalletErrorTests
{
    private static readonly JsonSerializerOptions Json = IslaPayJson.Options;

    [Fact]
    public void Problem_carries_the_meta_the_clients_type_needs()
    {
        var problem = new ApiProblem(
            Code: WalletErrors.InsufficientFunds,
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
    public void The_codes_that_need_a_currency_are_declared_by_this_module()
    {
        // InsufficientFunds(currency) and DuplicateCard(currency, kind) both
        // take one in the Flutter client, so a response missing it cannot be
        // mapped to a typed failure. Exchange declares its own separately;
        // there is deliberately no global list to forget to update.
        Assert.Contains(WalletErrors.InsufficientFunds, WalletErrors.RequireCurrencyMeta);
        Assert.Contains(WalletErrors.DuplicateCard, WalletErrors.RequireCurrencyMeta);
        Assert.DoesNotContain(WalletErrors.InvalidAmount, WalletErrors.RequireCurrencyMeta);
    }
}
