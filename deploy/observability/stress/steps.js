// The stress run's staircase and its arithmetic: the stages k6 runs, the
// window each step is measured over, and which step an instant falls in.
// Plain functions with no k6 import, so stress.js and steps.test.js share one
// copy, as slo/gate.js is shared.

// The default staircase. Each step holds long enough for the outbox, the
// queues and the saga to settle at that load, and the ramps are short, so a
// run of the five takes about seventeen minutes.
export const DEFAULT_STEPS = [10, 50, 100, 200, 400];
export const DEFAULT_RAMP_SECONDS = 30;
export const DEFAULT_HOLD_SECONDS = 180;

// The named levels STRESS_PROFILE chooses, each starting where the one below
// ends, so a level that held says which to run next. Each keeps doubling past
// its last step up to the cap, so none can end on every step held below it.
export const LEVELS = {
    low: [100, 200, 400, 800],
    medium: [800, 1600, 3200],
    high: [3200, 6400, 12800],
    xhigh: [12800],
};
export const LEVEL_RAMP_SECONDS = 30;
export const LEVEL_HOLD_SECONDS = 120;

// k6 starts every VU of the staircase before setup(), about a quarter of a
// MiB each (710 MiB for 3,000 on 2026-10-28), and setup() makes one Keycloak
// user per VU. High's top is the most a workstation is asked for by default.
export const DEFAULT_MAX_USERS = 12800;

export const STOP_RULES = ['knee', 'errors'];

// Where k6 runs: on the stack's machine, or on a second one, where that
// machine's CPU is the platform's alone (ADR-172).
export const GENERATORS = ['local', 'remote'];

function unset(text) {
    return text === undefined || text === null || String(text).trim() === '';
}

/**
 * The step targets from STRESS_STEPS, such as "10,50,100", or the default for
 * an empty value. Refuses anything but rising positive whole numbers, since a
 * falling step would measure the drain of the one before it.
 */
export function parseSteps(text) {
    if (unset(text))
        return [...DEFAULT_STEPS];

    const parts = String(text).split(',').map((part) => part.trim());
    const steps = parts.map(Number);

    if (steps.some((step, i) => !/^\d+$/.test(parts[i]) || step < 1))
        throw new Error(`STRESS_STEPS must be positive whole numbers, not "${text}"`);

    if (steps.some((step, i) => i > 0 && step <= steps[i - 1]))
        throw new Error(`STRESS_STEPS must rise, not "${text}"`);

    return steps;
}

function parseWhole(text, fallback, name, unit) {
    if (unset(text))
        return fallback;

    if (!/^\d+$/.test(String(text).trim()) || Number(text) < 1)
        throw new Error(`${name} must be a positive whole number of ${unit}, not "${text}"`);

    return Number(text);
}

/** A whole number of seconds from the environment, or the fallback when unset. */
export function parseSeconds(text, fallback, name) {
    return parseWhole(text, fallback, name, 'seconds');
}

/** STRESS_STOP: 'knee' when unset, or one of STOP_RULES. */
export function parseStop(text) {
    if (unset(text))
        return 'knee';

    const rule = String(text).trim().toLowerCase();

    if (!STOP_RULES.includes(rule))
        throw new Error(`STRESS_STOP must be one of ${STOP_RULES.join(', ')}, not "${text}"`);

    return rule;
}

/** STRESS_GENERATOR: 'local' when unset, or one of GENERATORS. */
export function parseGenerator(text) {
    if (unset(text))
        return 'local';

    const generator = String(text).trim().toLowerCase();

    if (!GENERATORS.includes(generator))
        throw new Error(`STRESS_GENERATOR must be one of ${GENERATORS.join(', ')}, not "${text}"`);

    return generator;
}

/**
 * The run's staircase from the environment's variables, by name. STRESS_STEPS
 * is taken as written, and the run's level is then `custom`, since it is no
 * level's staircase; a level doubles past its last step while the next stays
 * within the cap, so `capped` says its top is the cap's. Unset, both are
 * DEFAULT_STEPS. A step above the cap is refused, except a level's own, which
 * is dropped; a level left with one step has no step before it to compare,
 * and is refused.
 */
export function plan(env) {
    const named = unset(env.STRESS_PROFILE) ? null : String(env.STRESS_PROFILE).trim().toLowerCase();

    if (named !== null && !Object.hasOwn(LEVELS, named))
        throw new Error(`STRESS_PROFILE must be one of ${Object.keys(LEVELS).join(', ')}, not "${env.STRESS_PROFILE}"`);

    const maxUsers = parseWhole(env.STRESS_MAX_USERS, DEFAULT_MAX_USERS, 'STRESS_MAX_USERS', 'users');
    let level = named;
    let steps;
    let capped = false;

    if (!unset(env.STRESS_STEPS)) {
        steps = parseSteps(env.STRESS_STEPS);
        level = 'custom';
    } else if (named !== null) {
        steps = LEVELS[named].filter((vus) => vus <= maxUsers);

        while (steps.length > 0 && steps[steps.length - 1] * 2 <= maxUsers)
            steps.push(steps[steps.length - 1] * 2);

        if (steps.length < 2)
            throw new Error(`STRESS_PROFILE ${named} needs STRESS_MAX_USERS of at least ${LEVELS[named][0] * 2} ` +
                `for two steps, not ${maxUsers}: raise it, or choose a lower level`);

        capped = true;
    } else {
        steps = [...DEFAULT_STEPS];
    }

    if (steps[steps.length - 1] > maxUsers)
        throw new Error(`the staircase reaches ${steps[steps.length - 1]} users, above STRESS_MAX_USERS ${maxUsers}`);

    return {
        level,
        steps,
        maxUsers,
        capped,
        rampSeconds: parseSeconds(env.STRESS_RAMP_SECONDS,
            named === null ? DEFAULT_RAMP_SECONDS : LEVEL_RAMP_SECONDS, 'STRESS_RAMP_SECONDS'),
        holdSeconds: parseSeconds(env.STRESS_HOLD_SECONDS,
            named === null ? DEFAULT_HOLD_SECONDS : LEVEL_HOLD_SECONDS, 'STRESS_HOLD_SECONDS'),
    };
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
