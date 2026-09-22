-- Custody: where on-chain money arrives.
--
-- What this module models that no other one does: a fact IslaPay does not
-- control and cannot undo. Every other movement in the system happens because
-- somebody asked for it through an endpoint; a deposit happens because a
-- stranger broadcast a transaction, and the first this application hears of it
-- is a scanner reading a block.
--
-- Two consequences run through the whole schema.
--
-- The chain is the source of truth for whether the money exists, so the
-- ledger is not touched until the chain is certain. A transfer four blocks
-- deep is a transfer that can still be un-happened, and crediting it early
-- means either clawing a balance back from somebody who has already spent it
-- or wearing the loss.
--
-- And the scanner is not trustworthy about *when* it tells us. It re-reads
-- blocks, it restarts mid-range, it is run twice by accident. So the chain's
-- own identifier for a transfer is the primary key of what we know, and seeing
-- the same transfer a hundred times has to be indistinguishable from seeing it
-- once.

CREATE SCHEMA IF NOT EXISTS custody;

-- Where a user's money is sent.
--
-- One per user, network and currency, issued once and kept. See the remarks on
-- `DepositAddressDto` for why it is not rotated per deposit.
CREATE TABLE custody.addresses (
    id            uuid        PRIMARY KEY,
    user_id       text        NOT NULL,
    network       text        NOT NULL,
    currency      text        NOT NULL,
    -- Checked against the network's format before it was written. This is the
    -- one string in the system where being wrong is unrecoverable.
    address       text        NOT NULL,
    -- Whatever the custodian needs to find the key again — a derivation path,
    -- an account index, an id in their system. Opaque here on purpose: this
    -- module knows that addresses come from somewhere and deliberately does
    -- not know where.
    custodian_ref text        NOT NULL DEFAULT '',
    issued_at     timestamptz NOT NULL,

    CONSTRAINT addresses_are_one_per_user_and_asset
        UNIQUE (user_id, network, currency),
    -- Two users sharing an address would make every deposit ambiguous, and the
    -- ambiguity would only surface once money had arrived.
    CONSTRAINT addresses_are_unique_on_their_network
        UNIQUE (network, address)
);

CREATE INDEX addresses_by_address ON custody.addresses (network, address);

-- What the scanner has seen.
CREATE TABLE custody.deposits (
    id                 uuid        PRIMARY KEY,
    user_id            text        NOT NULL,
    address_id         uuid        NOT NULL REFERENCES custody.addresses (id),
    network            text        NOT NULL,
    currency           text        NOT NULL,
    -- The chain's identifier, and the output within it. A transaction can pay
    -- the same address twice, and those are two deposits.
    tx_hash            text        NOT NULL,
    output_index       int         NOT NULL DEFAULT 0,
    amount_minor       bigint      NOT NULL,
    -- How deep it was the last time the scanner looked.
    confirmations      int         NOT NULL DEFAULT 0,
    required_confirmations int     NOT NULL,
    status             text        NOT NULL,
    -- Set when the ledger has been asked to credit, cleared never. Its
    -- presence is what tells the sweeper a posting may exist.
    credit_posting_id  uuid,
    first_seen_at      timestamptz NOT NULL,
    -- When the row last changed. The sweeper reads it to decide what has been
    -- in flight long enough to be worth investigating.
    updated_at         timestamptz NOT NULL,
    credited_at        timestamptz,

    -- The invariant the scanner is built on: seeing the same transfer again is
    -- an update, never a second deposit.
    CONSTRAINT deposits_are_one_per_chain_output
        UNIQUE (network, tx_hash, output_index),

    CONSTRAINT deposits_are_for_a_positive_amount
        CHECK (amount_minor > 0),

    CONSTRAINT deposits_have_a_known_status
        CHECK (status IN ('confirming', 'crediting', 'credited', 'orphaned')),

    -- A credited deposit has both a posting and a time; one without the other
    -- is a half-written truth, and the sweeper would not know which half.
    CONSTRAINT deposits_credited_carry_their_posting
        CHECK (
            (status = 'credited')
            = (credit_posting_id IS NOT NULL AND credited_at IS NOT NULL)
        ),

    -- Nothing is posted before finality. Stated here rather than only in the
    -- service, because this is the rule the whole module exists to keep and a
    -- future caller with a good reason should have to argue with the database.
    CONSTRAINT deposits_are_only_credited_when_final
        CHECK (
            status NOT IN ('crediting', 'credited')
            OR confirmations >= required_confirmations
        )
);

CREATE INDEX deposits_by_user ON custody.deposits (user_id, first_seen_at DESC);

-- The sweeper's query: what is in flight, oldest first.
CREATE INDEX deposits_in_flight
    ON custody.deposits (updated_at)
    WHERE status IN ('confirming', 'crediting');
