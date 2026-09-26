-- What staff did, in the order they did it, and nothing that can be changed.
--
-- Every mutation behind a permission lands here, and so does every refusal of
-- a staff route. It is the answer to "who changed the price at 3am" and to
-- "who tried to", and both answers are worthless if the table can be edited
-- by whoever is being asked about.

CREATE TABLE platform.audit_log (
    seq          bigserial    PRIMARY KEY,
    at           timestamptz  NOT NULL DEFAULT now(),
    -- The token's subject, and a readable name for it as the token had it.
    actor        text         NOT NULL,
    actor_name   text,
    -- 'route' for a request that ran, 'denied' for one that was refused, or a
    -- module's own word for something worth more detail than a route gives.
    kind         text         NOT NULL,
    action       text         NOT NULL,
    permission   text,
    target       text,
    outcome      text         NOT NULL,
    status       integer,
    details      jsonb        NOT NULL DEFAULT '{}'::jsonb,
    correlation  text,
    -- Each row's hash covers the one before it, so removing or editing a row
    -- in a copy of the table, or behind the trigger's back, breaks the chain
    -- from that row on.
    prev_hash    text         NOT NULL,
    hash         text         NOT NULL
);

CREATE INDEX audit_by_actor ON platform.audit_log (actor, seq DESC);
CREATE INDEX audit_by_action ON platform.audit_log (action, seq DESC);

-- Append only. Not a permission the application could be granted by mistake:
-- the table refuses, whoever asks.
CREATE FUNCTION platform.audit_is_append_only() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'platform.audit_log is append-only';
END;
$$;

CREATE TRIGGER audit_no_update BEFORE UPDATE OR DELETE ON platform.audit_log
    FOR EACH ROW EXECUTE FUNCTION platform.audit_is_append_only();
CREATE TRIGGER audit_no_truncate BEFORE TRUNCATE ON platform.audit_log
    FOR EACH STATEMENT EXECUTE FUNCTION platform.audit_is_append_only();
