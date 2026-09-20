-- The marketplace's schema.
--
-- Same principle as the ledger's: every invariant Postgres can hold is held by
-- Postgres. Here that matters more than usual, because the one rule this
-- module exists to enforce — a buyer's money leaves escrow exactly once — is
-- not something the application can guarantee on its own. Escrow is a platform
-- account and platform accounts are allowed to go negative, so a second payout
-- against the same hold would not bounce. It would quietly create money.

CREATE SCHEMA IF NOT EXISTS marketplace;

-- Something a user has put up for sale.
CREATE TABLE marketplace.listings (
    id            uuid        PRIMARY KEY,
    -- Insertion order, for paging. The primary key is a uuid, which sorts
    -- arbitrarily, so it cannot be the cursor.
    seq           bigserial   NOT NULL UNIQUE,
    seller_id     text        NOT NULL,
    -- Denormalised, and on purpose. A feed of thirty listings would otherwise
    -- be thirty round trips to Keycloak to turn ids into names. It is the name
    -- as it stood when the item was listed, which is also the more honest
    -- thing to show next to a sale that already happened.
    seller_name   text        NOT NULL,
    title         text        NOT NULL,
    description   text        NOT NULL DEFAULT '',
    category      text        NOT NULL,
    condition     text        NOT NULL,
    currency      text        NOT NULL,
    price_minor   bigint      NOT NULL,
    location      text        NOT NULL DEFAULT '',
    photos        text[]      NOT NULL DEFAULT '{}',
    status        text        NOT NULL,
    published_at  timestamptz NOT NULL,
    updated_at    timestamptz NOT NULL,

    CONSTRAINT listings_are_priced_above_zero
        CHECK (price_minor > 0),
    CONSTRAINT listings_have_a_title
        CHECK (length(btrim(title)) > 0),
    CONSTRAINT listings_have_a_known_status
        CHECK (status IN ('active', 'reserved', 'sold', 'withdrawn'))
);

CREATE INDEX listings_by_seller ON marketplace.listings (seller_id, seq DESC);

-- The browse query, and only the rows it can return.
CREATE INDEX listings_on_offer ON marketplace.listings (seq DESC)
    WHERE status = 'active';

CREATE INDEX listings_on_offer_by_category ON marketplace.listings (category, seq DESC)
    WHERE status = 'active';

-- A buyer's claim on a listing, and the money behind it.
--
-- The lifecycle is in OrderStatuses. What is worth repeating here is why there
-- are seven of them rather than four: the ledger owns its own transaction, so
-- moving money and recording that it moved are two commits. `releasing` and
-- `refunding` are the states that exist between them, and they are what tells
-- the sweeper the difference between "the money never moved" and "the money
-- moved and we died before writing it down". Those two need opposite repairs.
CREATE TABLE marketplace.orders (
    id                 uuid        PRIMARY KEY,
    seq                bigserial   NOT NULL UNIQUE,
    listing_id         uuid        NOT NULL REFERENCES marketplace.listings (id),
    buyer_id           text        NOT NULL,
    -- Copied from the listing rather than joined. The seller of record is
    -- whoever it was when the money was locked, and a later edit to the
    -- listing must not change who gets paid.
    seller_id          text        NOT NULL,
    -- Denormalised for the same reason as on the listing: so that showing an
    -- order does not mean asking Keycloak who two people are.
    buyer_name         text        NOT NULL,
    seller_name        text        NOT NULL,
    -- And the title, which is what both parties' statements say they bought
    -- and sold. Frozen with the price: an edit to the listing must not rewrite
    -- what somebody's history says happened.
    listing_title      text        NOT NULL,
    currency           text        NOT NULL,
    -- The price at the moment of the hold, for the same reason: the seller
    -- must not be able to raise it after the buyer has committed.
    amount_minor       bigint      NOT NULL,
    fee_minor          bigint      NOT NULL,
    -- What the buyer shows and the seller scans. Unique across the table, so
    -- redemption is a lookup and two live orders can never share one.
    code               text        NOT NULL UNIQUE,
    status             text        NOT NULL,
    -- 'cancelled' or 'expired', set when the refund starts, so that a sweeper
    -- finishing an interrupted refund knows which of the two it is finishing.
    refund_reason      text,
    created_at         timestamptz NOT NULL,
    expires_at         timestamptz NOT NULL,
    settled_at         timestamptz,
    hold_posting_id    uuid,
    settle_posting_id  uuid,

    CONSTRAINT orders_are_for_a_positive_amount
        CHECK (amount_minor > 0),
    CONSTRAINT orders_fee_fits_inside_the_amount
        CHECK (fee_minor >= 0 AND fee_minor <= amount_minor),
    CONSTRAINT orders_have_a_known_status
        CHECK (status IN (
            'pending', 'held', 'releasing', 'refunding',
            'released', 'cancelled', 'expired')),
    CONSTRAINT orders_refund_reason_is_known
        CHECK (refund_reason IS NULL OR refund_reason IN ('cancelled', 'expired')),
    -- Buying from yourself would move money out and back through escrow and
    -- charge a commission on nothing. Refused in the service, and again here.
    CONSTRAINT orders_buyer_is_not_the_seller
        CHECK (buyer_id <> seller_id)
);

CREATE INDEX orders_by_buyer ON marketplace.orders (buyer_id, seq DESC);
CREATE INDEX orders_by_seller ON marketplace.orders (seller_id, seq DESC);

-- One live claim per listing, enforced by the database.
--
-- The service also sets the listing to 'reserved', which is what a second
-- buyer bounces off in practice. This index is what makes that a guarantee
-- rather than a well-behaved race: two buyers arriving at the same millisecond
-- both read an 'active' listing, and exactly one of them gets to insert.
CREATE UNIQUE INDEX orders_one_live_claim_per_listing
    ON marketplace.orders (listing_id)
    WHERE status IN ('pending', 'held', 'releasing', 'refunding');

-- The sweeper's query: holds that have run out of time, and interrupted
-- movements that need finishing. Partial, because settled orders accumulate
-- for ever and the sweeper must never pay to skip them.
CREATE INDEX orders_needing_attention ON marketplace.orders (expires_at)
    WHERE status IN ('pending', 'held', 'releasing', 'refunding');
