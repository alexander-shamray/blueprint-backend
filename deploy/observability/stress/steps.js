// The stress run's staircase and its arithmetic: the stages k6 runs, the
// window each step is measured over, and which step an instant falls in.
// Plain functions with no k6 import, so stress.js, summarise.mjs and
// steps.test.js share one copy, as slo/gate.js is shared.

// The default staircase. Each step holds long enough for the outbox, the
// queues and the saga to settle at that load, and the ramps are short, so a
// run of the five takes about seventeen minutes.
export const DEFAULT_STEPS = [10, 50, 100, 200, 400];
export const DEFAULT_RAMP_SECONDS = 30;
export const DEFAULT_HOLD_SECONDS = 180;

/**
 * The step targets from STRESS_STEPS, such as "10,50,100", or the default for
 * an empty value. Refuses anything but rising positive whole numbers, since a
 * falling step would measure the drain of the one before it.
 */
export function parseSteps(text) {
    if (text === undefined || text === null || String(text).trim() === '')
        return [...DEFAULT_STEPS];

    const parts = String(text).split(',').map((part) => part.trim());
    const steps = parts.map(Number);

    if (steps.some((step, i) => !/^\d+$/.test(parts[i]) || step < 1))
        throw new Error(`STRESS_STEPS must be positive whole numbers, not "${text}"`);

    if (steps.some((step, i) => i > 0 && step <= steps[i - 1]))
        throw new Error(`STRESS_STEPS must rise, not "${text}"`);

    return steps;
}

/** A whole number of seconds from the environment, or the fallback when unset. */
export function parseSeconds(text, fallback, name) {
    if (text === undefined || text === null || String(text).trim() === '')
        return fallback;

    if (!/^\d+$/.test(String(text).trim()) || Number(text) < 1)
        throw new Error(`${name} must be a positive whole number of seconds, not "${text}"`);

    return Number(text);
}

/** The staircase as ramping-vus stages: a ramp to each step's VUs, then a hold at them. */
export function stages(steps, rampSeconds, holdSeconds) {
    return steps.flatMap((target) => [
        { duration: `${rampSeconds}s`, target },
        { duration: `${holdSeconds}s`, target },
    ]);
}

/**
 * Each step's hold, in seconds from the run's start: the span in which all of
 * its VUs run. A ramp belongs to no step, because it mixes two loads.
 */
export function windows(steps, rampSeconds, holdSeconds) {
    return steps.map((vus, i) => {
        const start = (i * (rampSeconds + holdSeconds)) + rampSeconds;
        return { step: i + 1, vus, start, end: start + holdSeconds };
    });
}

/** The step whose hold holds `elapsedSeconds`, as the tag value, or 'ramp'. */
export function stepAt(elapsedSeconds, holds) {
    const hold = holds.find((w) => elapsedSeconds >= w.start && elapsedSeconds < w.end);
    return hold ? String(hold.step) : 'ramp';
}

/**
 * The most orders the staircase can place: every VU of every stage running
 * sessions of the shortest length end to end, the ordering share of them
 * ending in an order. Ramps count at their target, so this is an upper bound.
 */
export function maxOrders(steps, rampSeconds, holdSeconds, shortestSessionSeconds, orderShare) {
    const vuSeconds = steps.reduce((sum, vus) => sum + (vus * (rampSeconds + holdSeconds)), 0);
    return Math.ceil((vuSeconds / shortestSessionSeconds) * orderShare);
}
