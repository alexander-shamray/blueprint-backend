// Run with `node --test "deploy/observability/stress/*.test.js"`. The subject
// is the staircase's arithmetic, which tags every request and sizes the stock.
import test from 'node:test';
import assert from 'node:assert/strict';
import { DEFAULT_STEPS, maxOrders, parseSeconds, parseSteps, stages, stepAt, windows } from './steps.js';

test('an unset or empty STRESS_STEPS is the default staircase', () => {
    assert.deepEqual(parseSteps(undefined), DEFAULT_STEPS);
    assert.deepEqual(parseSteps(''), DEFAULT_STEPS);
    assert.deepEqual(parseSteps(' 5, 20 ,80 '), [5, 20, 80]);
});

test('a step that does not rise, or is not a positive whole number, is refused', () => {
    assert.throws(() => parseSteps('10,10'), /must rise/);
    assert.throws(() => parseSteps('50,10'), /must rise/);
    assert.throws(() => parseSteps('0,10'), /positive whole/);
    assert.throws(() => parseSteps('1.5,10'), /positive whole/);
    assert.throws(() => parseSteps('ten'), /positive whole/);
    assert.throws(() => parseSeconds('0', 30, 'STRESS_RAMP_SECONDS'), /STRESS_RAMP_SECONDS/);
    assert.equal(parseSeconds(undefined, 30, 'STRESS_RAMP_SECONDS'), 30);
});

test('each step is a ramp then a hold, and its window is the hold alone', () => {
    assert.deepEqual(stages([10, 50], 30, 180), [
        { duration: '30s', target: 10 },
        { duration: '180s', target: 10 },
        { duration: '30s', target: 50 },
        { duration: '180s', target: 50 },
    ]);

    assert.deepEqual(windows([10, 50], 30, 180), [
        { step: 1, vus: 10, start: 30, end: 210 },
        { step: 2, vus: 50, start: 240, end: 420 },
    ]);
});

test('an instant in a ramp belongs to no step, and a hold\'s end belongs to the ramp after it', () => {
    const holds = windows([10, 50], 30, 180);

    assert.equal(stepAt(0, holds), 'ramp');
    assert.equal(stepAt(30, holds), '1');
    assert.equal(stepAt(209.9, holds), '1');
    assert.equal(stepAt(210, holds), 'ramp');
    assert.equal(stepAt(300, holds), '2');
    assert.equal(stepAt(420, holds), 'ramp');
});

test('the order ceiling counts every VU-second at the shortest session', () => {
    // (10 + 50) VUs × 210 s = 12,600 VU-seconds; at 4 s a session that is 3,150 sessions, a quarter ordering.
    assert.equal(maxOrders([10, 50], 30, 180, 4, 0.25), 788);
});
