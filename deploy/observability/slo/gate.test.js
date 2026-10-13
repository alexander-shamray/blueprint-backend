// Run with `node --test "deploy/observability/slo/*.test.js"`. The subject is
// the floor and the stock precondition, not a passing run: every case
// here is a run that must fail, beside the one that must not.
import test from 'node:test';
import assert from 'node:assert/strict';
import { CONFIRMED_FLOOR, confirmedBreach, stockShortfall } from './gate.js';

test('a run whose every order was cancelled breaches the floor', () => {
    // 540 of 540 cancelled out_of_stock: no confirmed series exists at all.
    assert.match(confirmedBreach(540, null), /0\.0% of 540 placed/);
});

test('a run below the floor breaches it, and one at the floor does not', () => {
    assert.ok(confirmedBreach(100, 89));
    assert.equal(confirmedBreach(100, 100 * CONFIRMED_FLOOR), null);
    assert.equal(confirmedBreach(540, 540), null);
});

test('no placed series, or none placed, is NO DATA rather than a pass', () => {
    assert.match(confirmedBreach(null, 5), /NO DATA/);
    assert.match(confirmedBreach(0, 0), /NO DATA/);
});

test('stock short of the run\'s orders is a shortfall, exact stock is not', () => {
    assert.match(stockShortfall(539, 540), /539 available.*540 orders/);
    assert.equal(stockShortfall(540, 540), null);
    assert.ok(stockShortfall(Number.NaN, 540));
    assert.ok(stockShortfall(undefined, 540));
});
