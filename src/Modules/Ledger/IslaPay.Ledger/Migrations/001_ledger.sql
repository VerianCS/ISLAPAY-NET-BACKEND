-- The ledger's schema.
--
-- Every invariant that Postgres can hold is held by Postgres, not by the
-- application: the application is one deployment away from being two
-- applications, and the one thing that must never be reachable from a stray
-- UPDATE is the record of who was owed what.

CREATE SCHEMA IF NOT EXISTS ledger;

-- One row per account in the chart of accounts (§8.4). `name` is the
-- structured identifier — user:42:USD — and it is unique, so the same account
-- cannot be opened twice under two ids.
CREATE TABLE ledger.accounts (
    id               bigserial   PRIMARY KEY,
    name             text        NOT NULL UNIQUE,
    owner_type       text        NOT NULL,
    owner            text        NOT NULL,
    currency         text        NOT NULL,
    account_type     text        NOT NULL,
    -- Customer and merchant money may not go negative: that is credit, and
    -- IslaPay does not extend it (§8.3.3). Stored rather than derived so the
    -- constraint below can see it.
    may_go_negative  boolean     NOT NULL,
    opened_at        timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX accounts_by_owner ON ledger.accounts (owner_type, owner);

-- A transaction: a set of legs that balances to zero per currency. The
-- balancing itself is enforced in the domain, where an unbalanced posting
-- cannot be constructed at all.
CREATE TABLE ledger.postings (
    id               uuid        PRIMARY KEY,
    kind             text        NOT NULL,
    posted_at        timestamptz NOT NULL,
    -- A retry of the same user intent must not move money twice. NULL is
    -- allowed and repeats freely; a non-null value may appear once.
    idempotency_key  text        UNIQUE,
    correlation_id   uuid,
    metadata         jsonb       NOT NULL DEFAULT '{}'::jsonb
);

-- The entries themselves. Append-only, enforced below.
CREATE TABLE ledger.entries (
    id           bigserial   PRIMARY KEY,
    posting_id   uuid        NOT NULL REFERENCES ledger.postings (id),
    account_id   bigint      NOT NULL REFERENCES ledger.accounts (id),
    -- Gapless per account: a missing number is a removed entry, which is how
    -- a deletion is detected even if it happened outside this database.
    seq          bigint      NOT NULL,
    currency     text        NOT NULL,
    minor_units  bigint      NOT NULL,
    occurred_at  timestamptz NOT NULL,
    CONSTRAINT entries_seq_is_unique_per_account UNIQUE (account_id, seq),
    CONSTRAINT entries_are_never_zero CHECK (minor_units <> 0)
);

CREATE INDEX entries_by_account ON ledger.entries (account_id, id DESC);
CREATE INDEX entries_by_posting ON ledger.entries (posting_id);

-- The balance, materialised.
--
-- A balance is a fold over the entries and stays the truth of the matter; this
-- table is a cache of that fold, written in the same transaction as the
-- entries it summarises, because reading a year of history on every wallet
-- open is not viable. `entry_count` exists so the reconciliation query can
-- prove the two agree without summing twice.
CREATE TABLE ledger.balances (
    account_id   bigint      PRIMARY KEY REFERENCES ledger.accounts (id),
    minor_units  bigint      NOT NULL,
    entry_count  bigint      NOT NULL DEFAULT 0,
    updated_at   timestamptz NOT NULL DEFAULT now()
);

-- An entry is a historical fact. A mistake is undone by posting its reverse,
-- which leaves both the error and the correction visible; editing one leaves
-- neither.
CREATE OR REPLACE FUNCTION ledger.refuse_mutation() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION
        'ledger.% is append-only. Correct a mistake by posting its reverse.',
        TG_TABLE_NAME
        USING ERRCODE = 'restrict_violation';
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER entries_are_append_only
    BEFORE UPDATE OR DELETE ON ledger.entries
    FOR EACH ROW EXECUTE FUNCTION ledger.refuse_mutation();

CREATE TRIGGER postings_are_append_only
    BEFORE UPDATE OR DELETE ON ledger.postings
    FOR EACH ROW EXECUTE FUNCTION ledger.refuse_mutation();
