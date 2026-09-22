-- A stored amount says what a unit is.
--
-- `price_minor` is an integer and `currency` is a code; on their own they do
-- not say whether 1500000 is one and a half USDT or a million and a half. That
-- used to be answered by a compile-time enum, which is exactly what the
-- currency catalogue replaced — so the row carries it now, and a row read
-- years from now means what it meant when it was written.
ALTER TABLE marketplace.listings ADD COLUMN currency_scale int;
ALTER TABLE marketplace.orders   ADD COLUMN currency_scale int;

-- Backfilled from a list written here, not by joining catalog.currencies.
--
-- A module's schema does not read another module's tables — that is the same
-- boundary the code keeps, and a migration is not exempt from it; it would
-- also make this script fail to apply wherever Catalog has not been migrated
-- first, which is every one of this module's own tests.
--
-- The list is short because it is historical, not current: these four codes
-- are the only ones any row here can be in, since they are the only ones that
-- existed before this column did. New rows get their scale from the catalogue
-- in the application, where it belongs.
UPDATE marketplace.listings SET currency_scale = v.scale
  FROM (VALUES ('EISLA', 2), ('USDT', 6), ('USDC', 6), ('CUP', 2)) AS v(code, scale)
 WHERE v.code = marketplace.listings.currency;

UPDATE marketplace.orders SET currency_scale = v.scale
  FROM (VALUES ('EISLA', 2), ('USDT', 6), ('USDC', 6), ('CUP', 2)) AS v(code, scale)
 WHERE v.code = marketplace.orders.currency;

-- Anything left is a row in a currency nothing here can interpret, which
-- should be impossible and must not be guessed at.
DO $$
DECLARE orphans bigint;
BEGIN
    SELECT count(*) INTO orphans
      FROM (SELECT 1 FROM marketplace.listings WHERE currency_scale IS NULL
            UNION ALL
            SELECT 1 FROM marketplace.orders WHERE currency_scale IS NULL) AS x;

    IF orphans > 0 THEN
        RAISE EXCEPTION
            '% marketplace row(s) are in a currency with no known scale.', orphans;
    END IF;
END $$;

ALTER TABLE marketplace.listings ALTER COLUMN currency_scale SET NOT NULL;
ALTER TABLE marketplace.orders   ALTER COLUMN currency_scale SET NOT NULL;
