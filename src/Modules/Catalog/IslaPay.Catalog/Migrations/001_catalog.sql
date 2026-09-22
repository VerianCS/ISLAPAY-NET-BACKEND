-- What money there is, and what chains it moves on.
--
-- This schema exists because `Currency` was a C# enum with four members, and
-- an enum is a closed set decided when the binary was built. Adding the
-- Mexican peso meant a deploy. Adding forty of them meant a deploy and a
-- migration of every account name in the ledger. Switching one off for a
-- jurisdiction was not expressible at all.
--
-- So the set of currencies is data now. Two consequences worth stating,
-- because they shape every table below.
--
-- A currency is listed long before it is switched on. `enabled` starts false
-- and stays false until somebody has actually built the rail behind it — a
-- payment method, a chain scanner, a liquidity source. Listing a currency is a
-- schema change; switching it on is a business decision, and those should not
-- need the same release.
--
-- And a currency does not imply a chain. USDT is not a TRON token: it is a
-- TRON token *and* an Ethereum token and four more, which are different assets
-- that share a name and a price. Sending Ethereum USDT to a TRON address loses
-- the money permanently. Every model where the asset implies the network gets
-- that wrong, so the pair is its own table.

CREATE SCHEMA IF NOT EXISTS catalog;

CREATE TABLE catalog.currencies (
    code              text        PRIMARY KEY,
    name              text        NOT NULL,
    -- Decimal places it is accounted in. The whole reason an amount can be an
    -- integer, and the one fact about a currency that must never be guessed:
    -- reading "1.5" USDT as scale 1 instead of 6 is wrong by a factor of a
    -- hundred thousand.
    scale             int         NOT NULL,
    kind              text        NOT NULL,
    symbol            text        NOT NULL DEFAULT '',
    -- Whether a customer may hold a balance in it. False for a currency the
    -- platform only ever owes through escrow.
    customer_holdable boolean     NOT NULL,
    enabled           boolean     NOT NULL DEFAULT false,
    sort_order        int         NOT NULL DEFAULT 1000,
    updated_at        timestamptz NOT NULL DEFAULT now(),

    -- Eighteen is what an ERC-20 may declare, and also where bigint minor
    -- units stop holding a useful range: one whole unit of an 18-decimal
    -- asset is already 10^18.
    CONSTRAINT currencies_have_a_sane_scale CHECK (scale BETWEEN 0 AND 18),

    CONSTRAINT currencies_have_a_known_kind
        CHECK (kind IN ('fiat', 'stablecoin', 'internal')),

    -- The code is compared as text in the ledger's account names, so its
    -- shape is an invariant and not a convention.
    CONSTRAINT currencies_are_upper_case
        CHECK (code = upper(code) AND code ~ '^[A-Z0-9]{2,12}$')
);

CREATE INDEX currencies_enabled ON catalog.currencies (sort_order, code)
    WHERE enabled;

CREATE TABLE catalog.networks (
    id              text        PRIMARY KEY,
    name            text        NOT NULL,
    -- Blocks after which a transfer is treated as irreversible. A risk
    -- decision rather than a protocol constant, which is why it is a row: the
    -- day a chain misbehaves this gets raised without a release.
    confirmations   int         NOT NULL,
    -- Checked before an address is ever shown to anybody.
    address_pattern text        NOT NULL,
    -- Chains where a deposit needs a memo or tag as well as an address, and
    -- money sent without one is not attributable.
    memo_required   boolean     NOT NULL DEFAULT false,
    enabled         boolean     NOT NULL DEFAULT false,
    updated_at      timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT networks_confirm_at_least_once CHECK (confirmations >= 1)
);

-- One asset on one chain.
CREATE TABLE catalog.currency_networks (
    currency_code          text     NOT NULL REFERENCES catalog.currencies (code),
    network_id             text     NOT NULL REFERENCES catalog.networks (id),
    -- The token's address on that chain; empty for a chain's own coin. This
    -- is what makes "USDT on TRON" a specific thing rather than a phrase.
    contract               text     NOT NULL DEFAULT '',
    token_standard         text     NOT NULL DEFAULT '',
    minimum_withdrawal_minor bigint,
    enabled                boolean  NOT NULL DEFAULT false,
    updated_at             timestamptz NOT NULL DEFAULT now(),

    PRIMARY KEY (currency_code, network_id)
);

-- =============================================================================
-- The seed.
--
-- Everything the specification asks for is listed. Almost none of it is
-- switched on, and that is the honest state: a currency with no payment rail
-- behind it is a currency somebody would publish an offer in and then be
-- unable to settle.
--
-- Scales are ISO 4217 minor units. The zero-decimal ones are not oversights.
-- =============================================================================

-- IslaPay's own unit, and the two stablecoins. The only currencies a customer
-- can hold today, and the only ones switched on.
INSERT INTO catalog.currencies
    (code, name, scale, kind, symbol, customer_holdable, enabled, sort_order)
VALUES
    ('EISLA', 'Moneda IslaPay',  2, 'internal',   'E$', true,  true,  10),
    ('USDT',  'Tether',          6, 'stablecoin', '₮',  true,  true,  20),
    ('USDC',  'USD Coin',        6, 'stablecoin', '$',  true,  true,  30);

-- The Cuban peso: the local leg of a P2P trade. A liability of IslaPay held
-- through escrow, never a wallet anybody opens — hence customer_holdable
-- false, which is the rule that stops a module opening one by accident.
INSERT INTO catalog.currencies
    (code, name, scale, kind, symbol, customer_holdable, enabled, sort_order)
VALUES
    ('CUP', 'Peso cubano', 2, 'fiat', '$', false, true, 40);

-- The Americas.
INSERT INTO catalog.currencies
    (code, name, scale, kind, symbol, customer_holdable, enabled, sort_order)
VALUES
    ('USD', 'Dólar estadounidense',      2, 'fiat', '$',   false, false, 100),
    ('CAD', 'Dólar canadiense',          2, 'fiat', '$',   false, false, 101),
    ('MXN', 'Peso mexicano',             2, 'fiat', '$',   false, false, 102),
    ('BRL', 'Real brasileño',            2, 'fiat', 'R$',  false, false, 103),
    ('ARS', 'Peso argentino',            2, 'fiat', '$',   false, false, 104),
    -- Chile and Paraguay account in whole units: a centavo has not been worth
    -- printing for decades, and a scale of 2 would invent precision.
    ('CLP', 'Peso chileno',              0, 'fiat', '$',   false, false, 105),
    ('COP', 'Peso colombiano',           2, 'fiat', '$',   false, false, 106),
    ('PEN', 'Sol peruano',               2, 'fiat', 'S/',  false, false, 107),
    ('UYU', 'Peso uruguayo',             2, 'fiat', '$',   false, false, 108),
    ('PYG', 'Guaraní paraguayo',         0, 'fiat', '₲',   false, false, 109),
    ('BOB', 'Boliviano',                 2, 'fiat', 'Bs',  false, false, 110),
    ('VES', 'Bolívar venezolano',        2, 'fiat', 'Bs',  false, false, 111),
    ('DOP', 'Peso dominicano',           2, 'fiat', '$',   false, false, 112),
    ('GTQ', 'Quetzal guatemalteco',      2, 'fiat', 'Q',   false, false, 113),
    ('CRC', 'Colón costarricense',       2, 'fiat', '₡',   false, false, 114),
    ('PAB', 'Balboa panameño',           2, 'fiat', 'B/.', false, false, 115),
    ('HNL', 'Lempira hondureño',         2, 'fiat', 'L',   false, false, 116),
    ('NIO', 'Córdoba nicaragüense',      2, 'fiat', 'C$',  false, false, 117),
    ('JMD', 'Dólar jamaicano',           2, 'fiat', '$',   false, false, 118),
    ('TTD', 'Dólar de Trinidad y Tobago',2, 'fiat', '$',   false, false, 119),
    ('BBD', 'Dólar de Barbados',         2, 'fiat', '$',   false, false, 120),
    ('BSD', 'Dólar bahameño',            2, 'fiat', '$',   false, false, 121),
    ('HTG', 'Gourde haitiano',           2, 'fiat', 'G',   false, false, 122);

-- Europe.
INSERT INTO catalog.currencies
    (code, name, scale, kind, symbol, customer_holdable, enabled, sort_order)
VALUES
    ('EUR', 'Euro',              2, 'fiat', '€',  false, false, 200),
    ('GBP', 'Libra esterlina',   2, 'fiat', '£',  false, false, 201),
    ('CHF', 'Franco suizo',      2, 'fiat', 'Fr', false, false, 202),
    ('SEK', 'Corona sueca',      2, 'fiat', 'kr', false, false, 203),
    ('NOK', 'Corona noruega',    2, 'fiat', 'kr', false, false, 204),
    ('DKK', 'Corona danesa',     2, 'fiat', 'kr', false, false, 205),
    ('PLN', 'Zloty polaco',      2, 'fiat', 'zł', false, false, 206),
    ('CZK', 'Corona checa',      2, 'fiat', 'Kč', false, false, 207),
    ('HUF', 'Forinto húngaro',   2, 'fiat', 'Ft', false, false, 208),
    ('RON', 'Leu rumano',        2, 'fiat', 'lei',false, false, 209),
    ('BGN', 'Lev búlgaro',       2, 'fiat', 'лв', false, false, 210);

-- Asia. One today; the rest are a row each when there is a rail behind them.
INSERT INTO catalog.currencies
    (code, name, scale, kind, symbol, customer_holdable, enabled, sort_order)
VALUES
    ('CNY', 'Yuan chino', 2, 'fiat', '¥', false, false, 300);

-- The chains.
--
-- Confirmations are where each chain's own consensus stops being reversible,
-- not a round number. Address patterns are checked before an address is shown,
-- because money sent to a wrong-but-valid address is gone.
INSERT INTO catalog.networks
    (id, name, confirmations, address_pattern, memo_required, enabled)
VALUES
    -- Two thirds of 27 super representatives having built on the block.
    ('tron',     'TRON',             19, '^T[1-9A-HJ-NP-Za-km-z]{33}$', false, true),
    ('ethereum', 'Ethereum',         12, '^0x[0-9a-fA-F]{40}$',         false, false),
    ('bsc',      'BNB Smart Chain',  15, '^0x[0-9a-fA-F]{40}$',         false, false),
    ('polygon',  'Polygon',         128, '^0x[0-9a-fA-F]{40}$',         false, false),
    ('base',     'Base',             12, '^0x[0-9a-fA-F]{40}$',         false, false),
    ('solana',   'Solana',           32, '^[1-9A-HJ-NP-Za-km-z]{32,44}$', false, false);

-- Which asset is on which chain, with the contract that identifies it there.
INSERT INTO catalog.currency_networks
    (currency_code, network_id, contract, token_standard, enabled)
VALUES
    ('USDT', 'tron',     'TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t',          'TRC-20', true),
    ('USDT', 'ethereum', '0xdAC17F958D2ee523a2206206994597C13D831ec7',  'ERC-20', false),
    ('USDT', 'bsc',      '0x55d398326f99059fF775485246999027B3197955',  'BEP-20', false),
    ('USDT', 'polygon',  '0xc2132D05D31c914a87C6611C10748AEb04B58e8F',  'ERC-20', false),
    ('USDC', 'ethereum', '0xA0b86991c6218b36c1d19D4a2e9Eb0cE3606eB48',  'ERC-20', false),
    ('USDC', 'polygon',  '0x3c499c542cEF5E3811e1192ce70d8cC03d5c3359',  'ERC-20', false),
    ('USDC', 'base',     '0x833589fCD6eDb6E08f4c7C32D4f71b54bdA02913',  'ERC-20', false),
    ('USDC', 'solana',   'EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v', 'SPL',   false);

-- A currency's scale is immutable once anything has been counted in it.
--
-- Every stored amount is an integer of minor units, and the scale is what says
-- what a unit is. Changing USDT from 6 to 2 would not reprice anything — it
-- would silently restate every balance, every posting and every order by a
-- factor of ten thousand, in tables nobody would think to look at. There is no
-- migration that fixes that afterwards, because the old and new readings are
-- both arithmetically valid.
--
-- So it is refused here rather than discouraged in a comment. A currency that
-- genuinely needs a different scale is a different currency.
CREATE OR REPLACE FUNCTION catalog.refuse_scale_change() RETURNS trigger AS $$
BEGIN
    IF NEW.scale <> OLD.scale THEN
        RAISE EXCEPTION
            'catalog.currencies.scale is immutable: % is counted in 10^-% units and '
            'every amount already stored says so.',
            OLD.code, OLD.scale
            USING ERRCODE = 'restrict_violation';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER currencies_keep_their_scale
    BEFORE UPDATE ON catalog.currencies
    FOR EACH ROW EXECUTE FUNCTION catalog.refuse_scale_change();
