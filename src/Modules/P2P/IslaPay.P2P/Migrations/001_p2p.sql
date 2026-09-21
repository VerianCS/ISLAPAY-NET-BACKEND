-- The instant-exchange schema.
--
-- What this module models that the others do not: a leg that happens outside
-- IslaPay. Money in CUP moves through Transfermóvil, sent or received by a
-- person, and no amount of correctness in here makes that instant. The tables
-- below are mostly about the wait — who is owed what, by when, and what an
-- operator has already done about it.

CREATE SCHEMA IF NOT EXISTS p2p;

-- A rail the market trades over.
CREATE TABLE p2p.methods (
    id                text        PRIMARY KEY,
    name              text        NOT NULL,
    -- The currency the off-platform leg settles in. 'CUP' today.
    local_currency    text        NOT NULL,
    -- The wallet currency this rail trades against.
    wallet_currency   text        NOT NULL,
    -- A rail that is switched off stays in the list, greyed. One that vanishes
    -- without explanation reads as a bug to the person looking at it.
    available         boolean     NOT NULL DEFAULT true,
    -- Where a buyer sends their local money. Free text because it is read by a
    -- human and differs per rail: a phone number here, a card number there.
    instructions      text        NOT NULL DEFAULT '',
    minimum_minor     bigint      NOT NULL,
    maximum_minor     bigint      NOT NULL,
    updated_at        timestamptz NOT NULL,

    CONSTRAINT methods_have_a_sane_range
        CHECK (minimum_minor > 0 AND maximum_minor >= minimum_minor)
);

-- Published rates, appended rather than updated.
--
-- A trade settled last Tuesday has to be explicable at the rate it was priced
-- at, which an UPDATE would have destroyed. Rows are never edited; a new rate
-- is a new row and the current one is simply the latest.
CREATE TABLE p2p.rates (
    id                bigserial   PRIMARY KEY,
    method_id         text        NOT NULL REFERENCES p2p.methods (id),
    side              text        NOT NULL,
    wallet_currency   text        NOT NULL,
    -- Units of local currency per one wallet unit. Numeric, not floating
    -- point: this multiplies money.
    rate              numeric(24, 8) NOT NULL,
    effective_from    timestamptz NOT NULL,
    set_by            text        NOT NULL,

    CONSTRAINT rates_have_a_known_side CHECK (side IN ('sell', 'buy')),
    CONSTRAINT rates_are_positive CHECK (rate > 0)
);

-- The lookup every quote makes: newest row for a rail, side and currency.
CREATE INDEX rates_current
    ON p2p.rates (method_id, side, wallet_currency, effective_from DESC);

-- One user's trade.
--
-- The status lifecycle is in P2PTradeStatuses. As in the marketplace,
-- `settling` exists because the ledger owns its own transaction: moving money
-- and recording that it moved are two commits, and a process that dies between
-- them leaves this status behind for the sweeper to resolve. `settle_intent`
-- says which of the three movements was in flight, because unlike the
-- marketplace there are three rather than two.
CREATE TABLE p2p.trades (
    id                  uuid        PRIMARY KEY,
    seq                 bigserial   NOT NULL UNIQUE,
    user_id             text        NOT NULL,
    -- Denormalised so that showing an operator their queue is not one round
    -- trip to Keycloak per row.
    user_name           text        NOT NULL,
    side                text        NOT NULL,
    method_id           text        NOT NULL REFERENCES p2p.methods (id),
    -- Frozen with everything else: a rail renamed next month must not rewrite
    -- what somebody's history says they used.
    method_name         text        NOT NULL,

    wallet_currency     text        NOT NULL,
    -- Before the fee, in the wallet currency.
    amount_minor        bigint      NOT NULL,
    fee_minor           bigint      NOT NULL,

    local_currency      text        NOT NULL,
    -- What the user receives (sell) or must send (buy). Computed once, from
    -- the rate below, and never recomputed: the arithmetic is the agreement.
    local_minor         bigint      NOT NULL,
    rate                numeric(24, 8) NOT NULL,

    -- Short and unique. The operator types it into a banking app; a buyer
    -- writes it on their transfer so it can be matched.
    reference           text        NOT NULL UNIQUE,

    status              text        NOT NULL,
    -- 'payout', 'credit' or 'refund' while `settling`, so an interrupted
    -- movement can be finished as the one it was.
    settle_intent       text,
    -- In the operator's words, and shown to the user.
    failure_reason      text,

    created_at          timestamptz NOT NULL,
    expires_at          timestamptz NOT NULL,
    settled_at          timestamptz,

    commit_posting_id   uuid,
    settle_posting_id   uuid,

    -- Who settled it and what the bank called it. Without the second, a
    -- dispute six weeks later has nothing to check against.
    operator_id         text,
    operator_reference  text,

    CONSTRAINT trades_have_a_known_side
        CHECK (side IN ('sell', 'buy')),
    CONSTRAINT trades_are_for_a_positive_amount
        CHECK (amount_minor > 0 AND local_minor > 0),
    CONSTRAINT trades_fee_fits_inside_the_amount
        CHECK (fee_minor >= 0 AND fee_minor <= amount_minor),
    CONSTRAINT trades_have_a_known_status
        CHECK (status IN (
            'pending', 'awaiting_payout', 'awaiting_payment',
            'settling', 'completed', 'refunded', 'expired', 'cancelled')),
    CONSTRAINT trades_settle_intent_is_known
        CHECK (settle_intent IS NULL OR settle_intent IN ('payout', 'credit', 'refund')),
    -- A sell never waits for payment and a buy never waits for a payout.
    -- Cheap to state here, and it makes a whole class of wrong transition
    -- impossible rather than merely untested.
    CONSTRAINT trades_wait_on_the_right_leg
        CHECK (
            (status <> 'awaiting_payout' OR side = 'sell')
            AND (status <> 'awaiting_payment' OR side = 'buy')
            AND (status <> 'pending' OR side = 'sell')
            AND (status <> 'refunded' OR side = 'sell')
            AND (status <> 'expired' OR side = 'buy')
        ),
    CONSTRAINT trades_settled_carry_a_time
        CHECK ((status IN ('completed', 'refunded')) = (settled_at IS NOT NULL))
);

CREATE INDEX trades_by_user ON p2p.trades (user_id, seq DESC);

-- The operator's queue: oldest first, because the person who has waited
-- longest is the one to serve next.
CREATE INDEX trades_awaiting_an_operator ON p2p.trades (created_at)
    WHERE status IN ('awaiting_payout', 'awaiting_payment');

-- The sweeper's query: movements interrupted mid-flight, and buys that ran
-- out of time. Partial, because settled trades accumulate for ever.
CREATE INDEX trades_needing_attention ON p2p.trades (expires_at)
    WHERE status IN ('pending', 'settling', 'awaiting_payment');

-- The one rail this build ships with.
--
-- Seeded here rather than by a start-up routine: a rail is configuration that
-- other rows reference by foreign key, and a fresh database with no methods is
-- a system that cannot quote.
INSERT INTO p2p.methods
    (id, name, local_currency, wallet_currency, available, instructions,
     minimum_minor, maximum_minor, updated_at)
VALUES
    ('cup_transfermovil', 'CUP Transfermóvil', 'CUP', 'USD', false,
     '', 500, 50000, now());

-- Deliberately `available = false` and with no rate. A rail that started life
-- switched on would begin quoting the moment it was deployed, at whatever rate
-- somebody happened to insert first. An operator turns it on.
