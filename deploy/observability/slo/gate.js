// The two decisions of slo.js that must not rest on a passing run: whether the
// run has the stock it consumes, and whether enough of its orders were
// confirmed. They are plain functions of numbers, with no k6 import, so
// gate.test.js can run them under Node.

// The share of placed orders that must end confirmed for the run to count.
// Not 1: orders placed in the last seconds of the window are still in the
// saga when the first read is made, and teardown() waits for them only so long.
export const CONFIRMED_FLOOR = 0.9;

/**
 * The reason a product cannot carry `orders` single-unit orders, or null.
 * `available` is StockDto.Available: Inventory's own count net of reservations.
 */
export function stockShortfall(available, orders) {
    if (!Number.isFinite(available))
        return 'the stock read returned no number';

    return available >= orders
        ? null
        : `the product has ${available} available and the run places ${orders} orders`;
}

/**
 * The breach for a run whose placed and confirmed order counts are given, or
 * null. A null `placed` or a zero one is NO DATA, never a pass: a run that
 * placed nothing the server counted measured nothing. A null `confirmed` is
 * zero, because a counter nothing ever incremented has no series, and that is
 * the very case this exists for.
 */
export function confirmedBreach(placed, confirmed, floor = CONFIRMED_FLOOR) {
    if (placed === null || !(placed > 0))
        return 'Orders confirmed: NO DATA (no orders.placed series for the window)';

    const share = (confirmed ?? 0) / placed;

    return share >= floor
        ? null
        : `Orders confirmed: ${(share * 100).toFixed(1)}% of ${Math.round(placed)} placed, floor ${floor * 100}%`;
}
