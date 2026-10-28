// Run with `node --test "deploy/observability/stress/*.test.js"`. The subject
// is each knee rule beside the step that must trip it and the one that must
// not, and the parsing of what Docker prints.
import test from 'node:test';
import assert from 'node:assert/strict';
import {
    ERROR_RATE_LIMIT,
    HOST_CPU_LIMIT,
    clientRow,
    containerStats,
    crossings,
    endedLine,
    ending,
    generatorLimit,
    hostCpu,
    knee,
    liveRow,
    parseCpu,
    parseMemory,
    sqlDelta,
} from './report.js';

// k6's REST API as `GET /v1/metrics` answers it during a run: no trend carries a count.
function rest(step, { requests, p95, rate }) {
    return [
        { id: `http_reqs{step:${step}}`, attributes: { sample: { count: requests, rate: 3 } } },
        { id: `stress_journey_duration{step:${step}}`, attributes: { sample: { med: 10, 'p(95)': p95 } } },
        { id: `stress_failed{step:${step}}`, attributes: { sample: { rate } } },
        { id: 'stress_journey_duration', attributes: { sample: { 'p(95)': 9999 } } },
    ];
}

test('the live row reads the step\'s own submetrics from k6\'s REST API, never the run-wide metric', () => {
    const row = liveRow({ step: 2, vus: 50 }, rest(2, { requests: 900, p95: 120, rate: 0.004 }));

    assert.deepEqual(row, { step: 2, vus: 50, requests: 900, p95: 120, errorRate: 0.004 });
    assert.equal(liveRow({ step: 3, vus: 100 }, rest(2, { requests: 900, p95: 120, rate: 0 })).requests, 0,
        'a step with no submetric yet carries no requests');
});

test('the live stop and the summary find the knee at the same step from the same figures', () => {
    const figures = [
        { requests: 1000, p95: 80, rate: 0 },
        { requests: 2000, p95: 90, rate: 0.001 },
        { requests: 3000, p95: 400, rate: 0.002 },
    ];
    const holds = figures.map((_, i) => ({ step: i + 1, vus: 100 * (i + 1) }));

    let previous = null;
    let live = null;

    for (const [i, hold] of holds.entries()) {
        const row = liveRow(hold, rest(hold.step, figures[i]));
        if (crossings(row, previous).length > 0) {
            live = row.step;
            break;
        }
        previous = row;
    }

    const summary = knee(holds.map((hold, i) => clientRow({
        ...hold,
        journey: { 'p(95)': figures[i].p95, count: figures[i].requests },
        ops: { listing: { count: figures[i].requests } },
        failed: { rate: figures[i].rate },
    }, 120)));

    assert.equal(live, 3);
    assert.equal(summary.step, live);
});

test('a step at which the machine\'s CPU stayed saturated, or k6 ran short of VUs, measured the generator', () => {
    assert.equal(generatorLimit({ vus: 800, vusReached: 800, hostCpu: HOST_CPU_LIMIT - 1 }), null);
    assert.equal(generatorLimit({ vus: 800, vusReached: null, hostCpu: null }), null, 'no reading is no verdict');
    assert.match(generatorLimit({ vus: 800, vusReached: 800, hostCpu: HOST_CPU_LIMIT }), /CPU at 90%/);
    assert.match(generatorLimit({ vus: 800, vusReached: 640, hostCpu: 20 }), /k6 ran 640 of 800 VUs/);
});

test('the machine\'s CPU over a step is the mean of its own samples, and none is no reading', () => {
    const samples = [{ at: 500, host: { cpu: 100 } }, { at: 1000, host: { cpu: 40 } }, { at: 2000, host: { cpu: 60 } }];

    assert.equal(hostCpu(samples, { startMs: 1000, endMs: 2000 }), 50);
    assert.equal(hostCpu(samples, { startMs: 1500, endMs: 2500 }), 60, 'one sample is a reading');
    assert.equal(hostCpu([{ at: 1000, host: { cpu: null } }], { startMs: 1000, endMs: 2000 }), null);
});

test('the run ended on the rule that fired at its last measured step, the generator first', () => {
    const step = (n, extra = {}) => ({ step: n, vus: 100 * n, requests: 500, ...extra });
    const unreached = (n) => ({ step: n, vus: 100 * n, requests: 0 });
    const level = { level: 'low', stopRule: 'knee', capped: true };
    const fixed = { level: null, stopRule: 'errors', capped: false };

    assert.equal(ending([step(1), step(2, { kneeStop: true }), unreached(3)], level).reason, 'knee');
    assert.equal(ending([step(1), step(2, { aborted: true }), unreached(3)], fixed).reason, 'errors');
    assert.equal(ending([step(1), step(2)], level).reason, 'cap');
    assert.equal(ending([step(1), step(2)], fixed).reason, 'held');
    assert.equal(ending([step(1), unreached(2)], level).reason, 'interrupted');
    assert.equal(ending([unreached(1)], level).reason, 'not measured');

    const saturated = ending([step(1), step(2, { kneeStop: true, hostCpu: 97 })], level);
    assert.equal(saturated.reason, 'generator', 'a knee the machine ran out at is the machine\'s');
    assert.equal(saturated.step, 2);
});

test('the Ended line names the reason first, then the step, then the level and the stop rule', () => {
    const level = { level: 'low', stopRule: 'knee' };

    assert.equal(endedLine({ reason: 'knee', step: 4, vus: 800, detail: null }, level),
        'Ended: knee at step 4 (800 VUs); level low, stop rule knee');
    const saturated = { reason: 'generator', step: 5, vus: 1600, detail: 'the machine\'s CPU at 97% over the hold' };
    assert.equal(endedLine(saturated, { level: null, stopRule: 'errors' }),
    'Ended: generator at step 5 (1600 VUs): the machine\'s CPU at 97% over the hold; level none, stop rule errors');
    assert.equal(endedLine({ reason: 'not measured', step: null, vus: null, detail: null }, level),
        'Ended: not measured; level low, stop rule knee');
});

const healthy = { step: 1, vus: 10, requests: 1000, errorRate: 0, p95: 80 };

test('a step whose errors pass one in a hundred is the knee, and one at the limit is not', () => {
    assert.deepEqual(crossings({ ...healthy, errorRate: ERROR_RATE_LIMIT }, null), []);
    assert.equal(crossings({ ...healthy, errorRate: 0.02 }, null)[0].signal, 'errors');
});

test('a p95 that doubles past the noise floor is a jump, and a doubling of a few milliseconds is not', () => {
    assert.equal(crossings({ ...healthy, p95: 200 }, healthy)[0].signal, 'p95');
    assert.deepEqual(crossings({ ...healthy, p95: 150 }, healthy), [], 'under double');
    assert.deepEqual(crossings({ ...healthy, p95: 16 }, { ...healthy, p95: 8 }), [], 'eight milliseconds');
});

test('a backlog still growing at the end of the hold crossed, and one that levelled off did not', () => {
    assert.equal(crossings({ ...healthy, outboxMiddle: 4, outboxEnd: 20 }, null)[0].signal, 'outbox');
    assert.deepEqual(crossings({ ...healthy, outboxMiddle: 20, outboxEnd: 21 }, null), []);
    assert.equal(crossings({ ...healthy, queueMiddle: 300, queueEnd: 900 }, null)[0].signal, 'queue');
    assert.deepEqual(crossings({ ...healthy, queueMiddle: 10, queueEnd: 60 }, null), [], 'under the floor');
});

test('the knee is the first step that crossed, naming every signal it crossed', () => {
    const rows = [
        healthy,
        { ...healthy, step: 2, vus: 50, p95: 90 },
        { ...healthy, step: 3, vus: 100, p95: 400, errorRate: 0.05 },
        { ...healthy, step: 4, vus: 200, errorRate: 0.5 },
    ];

    const found = knee(rows);
    assert.equal(found.step, 3);
    assert.deepEqual(found.signals.map((s) => s.signal), ['errors', 'p95']);
});

test('a step the run never reached is skipped, not read as healthy', () => {
    assert.equal(knee([healthy, { ...healthy, step: 2, requests: 0, errorRate: 1 }]), null);
});

test('Docker\'s CPU and memory columns parse, and a blank reading is null', () => {
    assert.equal(parseCpu('12.34%'), 12.34);
    assert.equal(parseCpu('--'), null);
    assert.equal(parseMemory('512MiB / 7.6GiB'), 512 * 1024 * 1024);
    assert.equal(parseMemory('1.5GiB / 7.6GiB'), Math.round(1.5 * 1024 ** 3));
    assert.equal(parseMemory('0B / 0B'), 0);
    assert.equal(parseMemory(''), null);
});

test('a step\'s resources are its own samples, busiest container first', () => {
    const samples = [
        { at: 500, containers: [{ name: 'sql', cpu: 99, memory: 9 }] },
        { at: 1000, containers: [{ name: 'sql', cpu: 10, memory: 5 }, { name: 'gateway', cpu: 40, memory: 2 }] },
        { at: 2000, containers: [{ name: 'sql', cpu: 30, memory: 7 }, { name: 'gateway', cpu: 60, memory: 3 }] },
    ];

    const stats = containerStats(samples, { startMs: 1000, endMs: 2000 });
    assert.deepEqual(stats.map((s) => s.name), ['gateway', 'sql']);
    assert.equal(stats[1].cpuMean, 20);
    assert.equal(stats[1].cpuPeak, 30, 'the sample before the step is not its');
    assert.equal(stats[1].memoryPeak, 7);
});

test('a step\'s client row reads k6\'s own trend keys, and counts every operation\'s requests', () => {
    const row = clientRow({
        step: 2,
        vus: 50,
        journey: { med: 40, 'p(95)': 120, 'p(99)': 300, max: 900, count: 1700 },
        ops: {
            listing: { count: 900, 'p(95)': 30, max: 80 },
            token: { count: 100, 'p(95)': 200, max: 400 },
            order: { count: 0, 'p(95)': 0, max: 0 },
        },
        failures: { 429: 0, '5xx': 3 },
        failed: { rate: 0.003 },
    }, 100);

    assert.equal(row.requests, 1000, 'the token calls are requests too');
    assert.equal(row.rps, 10);
    assert.equal(row.p95, 120);
    assert.equal(row.errorRate, 0.003);
    assert.equal(row.ops.listing.p95, 30);
    assert.equal(row.ops.order.p95, null, 'an operation nobody made is no reading, not a zero');
    assert.equal(clientRow({ step: 3, vus: 100 }, 100).requests, 0, 'a step never reached has no requests');
});

test('SQL Server\'s counters are read as the step\'s change, and one sample is no change', () => {
    const samples = [
        { at: 1000, sql: { deadlocks: 3, lockWaitMs: 100 } },
        { at: 2000, sql: { deadlocks: 5, lockWaitMs: 400 } },
    ];

    assert.deepEqual(sqlDelta(samples, { startMs: 1000, endMs: 2000 }), { deadlocks: 2, lockWaitMs: 300 });
    assert.equal(sqlDelta(samples, { startMs: 1500, endMs: 2500 }), null);
});
