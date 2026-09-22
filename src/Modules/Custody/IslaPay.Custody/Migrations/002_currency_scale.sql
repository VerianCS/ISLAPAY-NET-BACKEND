-- A stored amount says what a unit is. See marketplace/002 for the reasoning,
-- and for why the scales are listed here rather than read from catalog.
ALTER TABLE custody.addresses ADD COLUMN currency_scale int;
ALTER TABLE custody.deposits  ADD COLUMN currency_scale int;

UPDATE custody.addresses SET currency_scale = v.scale
  FROM (VALUES ('USDT', 6), ('USDC', 6)) AS v(code, scale)
 WHERE v.code = custody.addresses.currency;

UPDATE custody.deposits SET currency_scale = v.scale
  FROM (VALUES ('USDT', 6), ('USDC', 6)) AS v(code, scale)
 WHERE v.code = custody.deposits.currency;

DO $$
DECLARE orphans bigint;
BEGIN
    SELECT count(*) INTO orphans
      FROM (SELECT 1 FROM custody.addresses WHERE currency_scale IS NULL
            UNION ALL SELECT 1 FROM custody.deposits WHERE currency_scale IS NULL) AS x;

    IF orphans > 0 THEN
        RAISE EXCEPTION
            '% custody row(s) are in a currency with no known scale.', orphans;
    END IF;
END $$;

ALTER TABLE custody.addresses ALTER COLUMN currency_scale SET NOT NULL;
ALTER TABLE custody.deposits  ALTER COLUMN currency_scale SET NOT NULL;
