// Run with `node --test "deploy/observability/stress/*.test.js"`. The subject
// is the staircase's arithmetic, which tags every request and sizes the stock.
import test from 'node:test';
import assert from 'node:assert/strict';
import {
    DEFAULT_HOLD_SECONDS,
    DEFAULT_MAX_USERS,
    DEFAULT_STEPS,
    LEVEL_HOLD_SECONDS,
    maxOrders,
    parseSeconds,
    parseSteps,
    parseStop,
    plan,
    stages,
    stepAt,
    windows,
} from './steps.js';

test('with no level the staircase is today\'s default, its holds and ramps unchanged, and never capped', () => {
    const run = plan({});

    assert.deepEqual(run.steps, DEFAULT_STEPS);
    assert.equal(run.holdSeconds, DEFAULT_HOLD_SECONDS);
    assert.equal(run.level, null);
    assert.equal(run.capped, false);
    assert.equal(run.maxUsers, DEFAULT_MAX_USERS);
});

test('a level starts at its own steps and doubles past them while the next stays within the cap', () => {
    assert.deepEqual(plan({ STRESS_PROFILE: 'low', STRESS_MAX_USERS: '3000' }).steps, [100, 200, 400, 800, 1600]);
    assert.deepEqual(plan({ STRESS_PROFILE: 'medium' }).steps, [800, 1600, 3200, 6400, 12800]);
    assert.deepEqual(plan({ STRESS_PROFILE: 'High' }).steps, [3200, 6400, 12800], 'case does not matter');
    assert.deepEqual(plan({ STRESS_PROFILE: 'xhigh', STRESS_MAX_USERS: '60000' }).steps, [12800, 25600, 51200]);

    const low = plan({ STRESS_PROFILE: 'low' });
    assert.equal(low.capped, true);
    assert.equal(low.holdSeconds, LEVEL_HOLD_SECONDS);
    assert.equal(low.rampSeconds, 30);
});

test('a cap below a level\'s own steps drops them, and one below its first refuses the level', () => {
    assert.deepEqual(plan({ STRESS_PROFILE: 'high', STRESS_MAX_USERS: '7000' }).steps, [3200, 6400]);
    assert.deepEqual(plan({ STRESS_PROFILE: 'xhigh' }).steps, [12800], 'the default cap is its first step');
    assert.throws(() => plan({ STRESS_PROFILE: 'xhigh', STRESS_MAX_USERS: '10000' }),
        /xhigh starts at 12800 users, above STRESS_MAX_USERS 10000: raise it/);
    assert.throws(() => plan({ STRESS_PROFILE: 'medium', STRESS_MAX_USERS: '500' }), /STRESS_MAX_USERS 500/);
});

test('STRESS_STEPS is taken as written, never doubled, and refused above the cap', () => {
    const run = plan({ STRESS_PROFILE: 'low', STRESS_STEPS: '10,20', STRESS_HOLD_SECONDS: '60' });

    assert.deepEqual(run.steps, [10, 20]);
    assert.equal(run.capped, false);
    assert.equal(run.holdSeconds, 60);
    assert.throws(() => plan({ STRESS_STEPS: '10,600', STRESS_MAX_USERS: '500' }), /reaches 600 users/);
});

test('an unknown level, a bad cap and an unknown stop rule are refused by name', () => {
    assert.throws(() => plan({ STRESS_PROFILE: 'extreme' }), /STRESS_PROFILE must be one of low, medium, high, xhigh/);
    assert.throws(() => plan({ STRESS_MAX_USERS: '0' }), /STRESS_MAX_USERS must be a positive whole number of users/);
    assert.throws(() => plan({ STRESS_MAX_USERS: '1,2' }), /STRESS_MAX_USERS/);
    assert.equal(parseStop(undefined), 'knee');
    assert.equal(parseStop(' Errors '), 'errors');
    assert.throws(() => parseStop('never'), /STRESS_STOP must be one of knee, errors/);
});

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
