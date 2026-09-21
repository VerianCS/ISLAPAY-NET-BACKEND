-- The wallet side of a rail is E-ISLA, not USD.
--
-- See `ledger/002_eisla.sql` for what the rename is and is not. Here it is
-- narrower: a rail's `wallet_currency` says which of the customer's balances
-- the local leg trades against, and the answer was always the internal unit.
--
-- `p2p.rates` is append-only by convention rather than by trigger — a rate is
-- never updated, a new one is inserted with a later `effective_from` — and the
-- same reasoning applies: a rate of 120 pesos to the unit is still 120 pesos
-- to the unit. Rewriting the code it is quoted in does not reprice a trade
-- that settled last Tuesday.

UPDATE p2p.methods SET wallet_currency = 'EISLA' WHERE wallet_currency = 'USD';
UPDATE p2p.rates   SET wallet_currency = 'EISLA' WHERE wallet_currency = 'USD';
UPDATE p2p.trades  SET wallet_currency = 'EISLA' WHERE wallet_currency = 'USD';

DO $$
DECLARE
    stragglers bigint;
BEGIN
    SELECT count(*) INTO stragglers
      FROM (SELECT 1 FROM p2p.methods WHERE wallet_currency = 'USD'
            UNION ALL
            SELECT 1 FROM p2p.rates   WHERE wallet_currency = 'USD'
            UNION ALL
            SELECT 1 FROM p2p.trades  WHERE wallet_currency = 'USD') AS remaining;

    IF stragglers > 0 THEN
        RAISE EXCEPTION 'The USD to EISLA rename left % P2P row(s) behind.', stragglers;
    END IF;
END $$;
