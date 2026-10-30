-- The stress run's products: every seeded product Ordering holds as
-- available, with its currency, whose price ends in none of the pence the
-- payment simulator scripts. The run takes the first row's currency and every
-- product in it.
SET NOCOUNT ON;

SELECT LOWER(CONVERT(char(36), ProductId)), Currency
FROM ordering.ProductPrices
WHERE IsAvailable = 1
    AND CONVERT(char(36), ProductId) LIKE '5EED0000-%'
    AND CAST(ROUND(Amount * 100, 0) AS bigint) % 100 NOT IN (1, 2, 5, 9)
ORDER BY Currency, ProductId;
