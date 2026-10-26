// The stress run's decisions, apart from any I/O: what a resource sample
// says, what a step's samples add up to, and which step is the knee. Plain
// functions, so report.test.js can hold each rule against the run that must
// trip it and the one that must not.

// THE KNEE: the first step at which any of the three signals the run exists
// to find crosses. Within one step their order cannot be seen, since each is
// read once per step, so every signal crossing at the knee is named.
//
// Errors: one request in a hundred failing is a platform refusing work.
export const ERROR_RATE_LIMIT = 0.01;

// Latency: the journey's p95 at least doubling on the step before, and by more
// than the floor, so a p95 of a few milliseconds doubling is not a jump.
export const P95_JUMP_RATIO = 2;
export const P95_JUMP_FLOOR_MS = 50;

// A backlog that has not levelled off: still half as large again at the end
// of the hold as at its middle, and by more than a burst leaves behind.
export const GROWTH_RATIO = 1.5;
export const OUTBOX_GROWTH_FLOOR_SECONDS = 5;
export const QUEUE_GROWTH_FLOOR_MESSAGES = 100;

/** Docker's "12.34%" as a number, or null for a value it could not read ("--"). */
export function parseCpu(text) {
    const match = /^\s*([\d.]+)\s*%\s*$/.exec(String(text ?? ''));
    return match ? Number(match[1]) : null;
}

const UNITS = { B: 1, KB: 1e3, MB: 1e6, GB: 1e9, KIB: 1024, MIB: 1024 ** 2, GIB: 1024 ** 3 };

/** The used half of Docker's "123.4MiB / 7.6GiB", in bytes, or null. */
export function parseMemory(text) {
    const match = /^\s*([\d.]+)\s*([KMG]?i?B)\s*\//i.exec(String(text ?? ''));
    return match ? Math.round(Number(match[1]) * UNITS[match[2].toUpperCase()]) : null;
}

function within(samples, window) {
    return samples.filter((sample) => sample.at >= window.startMs && sample.at <= window.endMs);
}

/** Each container's mean and peak CPU and peak memory over a step, busiest first. */
export function containerStats(samples, window) {
    const byName = new Map();

    for (const sample of within(samples, window)) {
        for (const container of sample.containers ?? []) {
            const seen = byName.get(container.name) ?? { name: container.name, cpu: [], memory: [] };
            if (container.cpu !== null) seen.cpu.push(container.cpu);
            if (container.memory !== null) seen.memory.push(container.memory);
            byName.set(container.name, seen);
        }
    }

    return [...byName.values()]
        .map((seen) => ({
            name: seen.name,
            cpuMean: seen.cpu.length ? seen.cpu.reduce((a, b) => a + b, 0) / seen.cpu.length : null,
            cpuPeak: seen.cpu.length ? Math.max(...seen.cpu) : null,
            memoryPeak: seen.memory.length ? Math.max(...seen.memory) : null,
        }))
        .sort((a, b) => (b.cpuMean ?? -1) - (a.cpuMean ?? -1));
}

/**
 * How far each of SQL Server's cumulative counters moved over a step: the
 * last sample inside it less the first. Null for a counter no two samples read.
 */
export function sqlDelta(samples, window) {
    const read = within(samples, window).filter((sample) => sample.sql);

    if (read.length < 2)
        return null;

    const first = read[0].sql;
    const last = read[read.length - 1].sql;

    return Object.fromEntries(Object.keys(last).map((key) =>
        [key, Number.isFinite(first[key]) && Number.isFinite(last[key]) ? last[key] - first[key] : null]));
}

/**
 * A step's client half, from the figures stress.js's handleSummary wrote in
 * k6's own trend keys: wall-clock in milliseconds, rates as fractions.
 */
export function clientRow(step, holdSeconds) {
    const requests = Object.values(step.ops ?? {}).reduce((sum, op) => sum + (op.count ?? 0), 0);

    // k6 reports a trend nothing was added to as zeros, which would read as instant answers.
    const measured = (figures) => (figures?.count > 0 ? figures : {});
    const journey = measured(step.journey);

    return {
        step: step.step,
        vus: step.vus,
        requests,
        rps: requests / holdSeconds,
        p50: journey.med ?? null,
        p95: journey['p(95)'] ?? null,
        p99: journey['p(99)'] ?? null,
        max: journey.max ?? null,
        errorRate: step.failed?.rate ?? 0,
        failures: step.failures ?? {},
        aborted: Boolean(step.aborted),
        ops: Object.fromEntries(Object.entries(step.ops ?? {}).map(([op, figures]) =>
            [op, {
                count: figures.count ?? 0,
                p95: measured(figures)['p(95)'] ?? null,
                max: measured(figures).max ?? null,
            }])),
    };
}

function growing(middle, end, floor) {
    return Number.isFinite(middle) && Number.isFinite(end) && end - middle > floor && end >= middle * GROWTH_RATIO;
}

/** The signals a step crossed, measured against the step before it. */
export function crossings(row, previous) {
    const crossed = [];

    if (row.errorRate > ERROR_RATE_LIMIT)
        crossed.push({ signal: 'errors', detail: `${(row.errorRate * 100).toFixed(1)}% of requests failed` });

    if (previous && Number.isFinite(row.p95) && Number.isFinite(previous.p95) &&
        row.p95 >= previous.p95 * P95_JUMP_RATIO && row.p95 - previous.p95 > P95_JUMP_FLOOR_MS) {
        crossed.push({
            signal: 'p95',
            detail: `journey p95 ${Math.round(previous.p95)} ms to ${Math.round(row.p95)} ms`,
        });
    }

    if (growing(row.outboxMiddle, row.outboxEnd, OUTBOX_GROWTH_FLOOR_SECONDS)) {
        crossed.push({
            signal: 'outbox',
            detail: `oldest outbox row ${row.outboxMiddle.toFixed(1)} s to ${row.outboxEnd.toFixed(1)} s over the hold`,
        });
    }

    if (growing(row.queueMiddle, row.queueEnd, QUEUE_GROWTH_FLOOR_MESSAGES)) {
        crossed.push({
            signal: 'queue',
            detail: `queued messages ${Math.round(row.queueMiddle)} to ${Math.round(row.queueEnd)} over the hold`,
        });
    }

    return crossed;
}

/**
 * The knee, or null when every measured step held. A step the run never
 * reached carries no requests and is skipped rather than read as healthy.
 */
export function knee(rows) {
    let previous = null;

    for (const row of rows) {
        if (!(row.requests > 0))
            continue;

        const crossed = crossings(row, previous);

        if (crossed.length > 0)
            return { step: row.step, vus: row.vus, signals: crossed };

        previous = row;
    }

    return null;
}
