-- The shop's schema.
--
-- Same principle as the ledger's and the marketplace's: every invariant
-- Postgres can hold is held by Postgres. Two of them matter more than usual
-- here, and neither is something the application can guarantee on its own:
--
--   * A buyer's money leaves escrow exactly once. Escrow is a platform account
--     and platform accounts may go negative, so a second payout against the
--     same order would not bounce — it would quietly create money.
--   * Stock never goes below zero. Two people pressing "buy" on the last unit
--     at the same moment is not a rare case, it is the normal case for
--     anything worth buying.

CREATE SCHEMA IF NOT EXISTS storefront;

-- Who sells.
--
-- A real ledger account owner: the payout posts to `merchant:<id>`, which is a
-- liability that may not go negative. A shop is not a user and not the
-- platform; it is somebody IslaPay owes money to.
CREATE TABLE storefront.merchants (
    id          uuid        PRIMARY KEY,
    name        text        NOT NULL,
    -- Switched off without deleting: orders reference it, and a shop that
    -- closes does not un-happen the sales it made.
    active      boolean     NOT NULL DEFAULT true,
    created_at  timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT merchants_have_a_name CHECK (length(btrim(name)) > 0)
);

CREATE TABLE storefront.categories (
    id          text        PRIMARY KEY,
    name        text        NOT NULL,
    -- A Lucide name. The app maps it to whatever its icon set calls the same
    -- picture; the server has no opinion about Flutter.
    icon        text        NOT NULL DEFAULT 'package',
    sort_order  int         NOT NULL DEFAULT 100,

    CONSTRAINT categories_have_a_name CHECK (length(btrim(name)) > 0)
);

CREATE TABLE storefront.products (
    id            uuid        PRIMARY KEY,
    -- Insertion order, for paging. The primary key is a uuid, which sorts
    -- arbitrarily, so it cannot be the cursor.
    seq           bigserial   NOT NULL UNIQUE,
    merchant_id   uuid        NOT NULL REFERENCES storefront.merchants (id),
    category_id   text        NOT NULL REFERENCES storefront.categories (id),
    name          text        NOT NULL,
    description   text        NOT NULL DEFAULT '',
    icon          text        NOT NULL DEFAULT 'package',
    currency      text        NOT NULL,
    -- The scale travels with the code. Minor units and a code do not say
    -- whether 1500000 is one and a half USDT or a million and a half, and a
    -- row read years from now has to mean what it meant when it was written.
    currency_scale int        NOT NULL,
    price_minor   bigint      NOT NULL,
    stock         int         NOT NULL DEFAULT 0,
    -- Taken off sale by the merchant. Separate from `stock = 0`, because the
    -- two are different answers: one is "come back later".
    listed        boolean     NOT NULL DEFAULT true,
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT products_are_priced_above_zero CHECK (price_minor > 0),
    CONSTRAINT products_have_a_name CHECK (length(btrim(name)) > 0),

    -- The invariant the whole reservation dance exists to keep. Decrementing
    -- below zero raises here rather than overselling, whatever the
    -- application believed it had read a moment earlier.
    CONSTRAINT products_never_oversell CHECK (stock >= 0)
);

CREATE INDEX products_by_category ON storefront.products (category_id, seq)
    WHERE listed;

CREATE INDEX products_by_merchant ON storefront.products (merchant_id, seq);

-- Where a buyer can collect instead of paying for delivery.
CREATE TABLE storefront.pickup_points (
    id       uuid    PRIMARY KEY,
    name     text    NOT NULL,
    address  text    NOT NULL DEFAULT '',
    hours    text    NOT NULL DEFAULT '',
    active   boolean NOT NULL DEFAULT true,

    CONSTRAINT pickup_points_have_a_name CHECK (length(btrim(name)) > 0)
);

-- A purchase, and the money behind it.
--
-- The lifecycle is in StoreOrderStatuses. What is worth repeating here is why
-- `releasing` and `refunding` exist: the ledger owns its own transaction, so
-- moving money and recording that it moved are two commits, and those two
-- states are what sits between them. They are what tells the sweeper the
-- difference between "the money never moved" and "the money moved and we died
-- before writing it down" — which need opposite repairs.
CREATE TABLE storefront.orders (
    id                 uuid        PRIMARY KEY,
    seq                bigserial   NOT NULL UNIQUE,
    -- Short, human, and what a buyer reads out on the phone. Unique so it can
    -- be searched on without ambiguity.
    reference          text        NOT NULL UNIQUE,
    buyer_id           text        NOT NULL,
    buyer_name         text        NOT NULL,
    merchant_id        uuid        NOT NULL REFERENCES storefront.merchants (id),
    -- Denormalised for the same reason the marketplace denormalises the
    -- seller's: a list of thirty orders would otherwise be thirty joins to
    -- show a shop's name, and the name as it stood is the honest thing to
    -- show beside a sale that already happened.
    merchant_name      text        NOT NULL,

    currency           text        NOT NULL,
    currency_scale     int         NOT NULL,
    -- Three figures, not two and a subtraction. What the merchant is owed
    -- must not be something two readers can each round differently.
    goods_minor        bigint      NOT NULL,
    shipping_minor     bigint      NOT NULL DEFAULT 0,
    commission_minor   bigint      NOT NULL,

    delivery           text        NOT NULL,
    address            text        NOT NULL DEFAULT '',
    pickup_point_id    uuid        REFERENCES storefront.pickup_points (id),
    note               text        NOT NULL DEFAULT '',

    -- Only for a pickup, and only ever shown to the buyer. Unique so a scan
    -- addresses exactly one order.
    code               text        UNIQUE,

    status             text        NOT NULL,
    hold_posting_id    uuid,
    settle_posting_id  uuid,
    created_at         timestamptz NOT NULL,
    updated_at         timestamptz NOT NULL,
    expires_at         timestamptz NOT NULL,
    dispatched_at      timestamptz,
    completed_at       timestamptz,

    CONSTRAINT orders_have_a_known_status CHECK (status IN (
        'pending', 'paid', 'dispatched', 'releasing', 'refunding',
        'completed', 'cancelled', 'expired')),

    CONSTRAINT orders_have_a_known_delivery CHECK (delivery IN ('home', 'pickup')),

    CONSTRAINT orders_are_priced_above_zero CHECK (goods_minor > 0),
    CONSTRAINT orders_do_not_charge_negative_shipping CHECK (shipping_minor >= 0),

    -- The commission comes out of the merchant's side, so it cannot exceed
    -- what the goods were worth. A commission larger than the sale would pay
    -- the merchant a negative amount, and their account may not go negative.
    CONSTRAINT orders_commission_fits_the_goods
        CHECK (commission_minor >= 0 AND commission_minor <= goods_minor),

    -- A home delivery needs somewhere to go; a pickup needs a counter. Each
    -- is meaningless without its half, and an order missing one is an order
    -- nobody can fulfil.
    CONSTRAINT orders_know_where_they_are_going CHECK (
        (delivery = 'home'   AND length(btrim(address)) > 0)
     OR (delivery = 'pickup' AND pickup_point_id IS NOT NULL)),

    -- The code is the pickup's release mechanism and has no meaning for a
    -- delivery, where the buyer confirms instead.
    CONSTRAINT orders_only_carry_a_code_for_pickups
        CHECK (delivery = 'pickup' OR code IS NULL),

    -- A completed order has been paid out, so it must name the posting that
    -- paid it. Without this, "we paid them" and "we think we paid them" look
    -- the same in the table.
    CONSTRAINT orders_that_completed_say_when_and_how CHECK (
        status <> 'completed'
     OR (completed_at IS NOT NULL AND settle_posting_id IS NOT NULL)),

    CONSTRAINT orders_that_dispatched_say_when
        CHECK (status NOT IN ('dispatched') OR dispatched_at IS NOT NULL)
);

CREATE INDEX orders_by_buyer ON storefront.orders (buyer_id, seq DESC);
CREATE INDEX orders_by_merchant ON storefront.orders (merchant_id, seq DESC);

-- The sweeper's query, and only the rows it can act on.
CREATE INDEX orders_awaiting_collection ON storefront.orders (expires_at)
    WHERE status IN ('paid', 'dispatched');

CREATE INDEX orders_in_flight ON storefront.orders (updated_at)
    WHERE status IN ('pending', 'releasing', 'refunding');

-- What was bought, as it was bought.
--
-- The name and the unit price are copied rather than joined. A receipt has to
-- keep saying what it said; a merchant repricing a product next week must not
-- rewrite somebody's history, and one deleting it must not empty their order.
CREATE TABLE storefront.order_lines (
    order_id     uuid   NOT NULL REFERENCES storefront.orders (id) ON DELETE CASCADE,
    line_no      int    NOT NULL,
    product_id   uuid   NOT NULL,
    name         text   NOT NULL,
    unit_minor   bigint NOT NULL,
    quantity     int    NOT NULL,

    PRIMARY KEY (order_id, line_no),

    CONSTRAINT order_lines_are_priced_above_zero CHECK (unit_minor > 0),
    CONSTRAINT order_lines_have_a_quantity CHECK (quantity > 0),

    -- One line per product per order. Two lines for the same thing is a cart
    -- the client failed to fold, and it makes the receipt read as if somebody
    -- bought two different items.
    CONSTRAINT order_lines_are_one_per_product UNIQUE (order_id, product_id)
);

-- Append-only, like the ledger's entries.
--
-- An order line is part of a receipt. Editing one after the fact would change
-- what somebody was told they bought, and no legitimate flow needs to: a
-- cancellation is a status change and a refund is a posting, not an edit.
CREATE OR REPLACE FUNCTION storefront.order_lines_are_final()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'storefront.order_lines is append-only (attempted %)', TG_OP;
END $$;

CREATE TRIGGER order_lines_cannot_be_rewritten
    BEFORE UPDATE ON storefront.order_lines
    FOR EACH ROW EXECUTE FUNCTION storefront.order_lines_are_final();
