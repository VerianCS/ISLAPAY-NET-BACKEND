-- The internal unit is renamed from USD to EISLA.
--
-- This changes no amount, no account and no moment. `platform:fees:USD` and
-- `platform:fees:EISLA` are the same account holding the same balance; only
-- the label it is written under differs. Nothing here converts anything, and
-- there is no rate involved, because there is nothing to convert: the unit was
-- never a dollar. It was an internal liability that borrowed the dollar's
-- three letters, and borrowing them made every screen in the application claim
-- IslaPay issues US dollars.
--
-- On `ledger.entries` being append-only, which it still is.
--
-- The trigger is dropped for the length of this script and put back before it
-- ends. That is worth being uncomfortable about, so here is the distinction it
-- rests on: the trigger exists to stop the *application* from editing history,
-- because an application that can rewrite an entry can rewrite it at three in
-- the morning under load with nobody reading. A migration is the opposite of
-- that — it is reviewed, it runs once, it is checksummed afterwards so it
-- cannot be edited, it holds an advisory lock, and it runs before the process
-- serves a single request. A rename is also not the thing the trigger is
-- protecting against: correcting a mistake by editing an entry hides that the
-- mistake happened, whereas nothing happened here at all.
--
-- If a later script wants to touch `entries` for any reason other than
-- renaming a label, the right answer is a reversing posting and this one is
-- not the precedent for it.

-- The structured name, first: `user:42:USD`, `platform:escrow:USD`.
UPDATE ledger.accounts
   SET name = left(name, length(name) - 3) || 'EISLA'
 WHERE currency = 'USD'
   AND right(name, 4) = ':USD';

UPDATE ledger.accounts
   SET currency = 'EISLA'
 WHERE currency = 'USD';

ALTER TABLE ledger.entries DISABLE TRIGGER entries_are_append_only;

UPDATE ledger.entries
   SET currency = 'EISLA'
 WHERE currency = 'USD';

ALTER TABLE ledger.entries ENABLE TRIGGER entries_are_append_only;

-- Leaving one row behind would be worse than failing: the code is parsed on
-- every statement read, `USD` is no longer a code this application knows, and
-- the first person to notice would be a customer opening their history.
DO $$
DECLARE
    stragglers bigint;
    unnamed    bigint;
BEGIN
    SELECT count(*) INTO stragglers
      FROM (SELECT 1 FROM ledger.accounts WHERE currency = 'USD'
            UNION ALL
            SELECT 1 FROM ledger.entries WHERE currency = 'USD') AS remaining;

    IF stragglers > 0 THEN
        RAISE EXCEPTION 'The USD to EISLA rename left % row(s) behind.', stragglers;
    END IF;

    -- An account whose name and currency disagree is the failure mode this
    -- script could plausibly have: two UPDATEs, one of which matched fewer
    -- rows than the other.
    SELECT count(*) INTO unnamed
      FROM ledger.accounts
     WHERE currency = 'EISLA' AND right(name, 6) <> ':EISLA';

    IF unnamed > 0 THEN
        RAISE EXCEPTION
            '% EISLA account(s) are still named in another currency.', unnamed;
    END IF;
END $$;

-- The trigger is the point of the table. If the re-enable above failed to
-- take, everything after this migration would be able to edit history and
-- nothing would say so.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_trigger
         WHERE tgrelid = 'ledger.entries'::regclass
           AND tgname = 'entries_are_append_only'
           AND tgenabled <> 'D')
    THEN
        RAISE EXCEPTION 'ledger.entries was left without its append-only trigger.';
    END IF;
END $$;
