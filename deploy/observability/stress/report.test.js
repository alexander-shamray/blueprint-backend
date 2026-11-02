// Run with `node --test "deploy/observability/stress/*.test.js"`. The subject
// is each knee rule beside the step that must trip it and the one that must
// not, and the parsing of what Docker prints.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import {
    CLOCK_SKEW_LIMIT_MS,
    ERROR_RATE_LIMIT,
    GENERATOR_SERVICE,
    HOST_CPU_LIMIT,
    INBOX_LATCH,
    MINIMUM_RANGE_SECONDS,
    OUTSIDE_SHARE,
    P95_JUMP_FLOOR_MS,
    clientRow,
    clockSkewMs,
    containerOwner,
    containerStats,
    crossings,
    endedLine,
    ending,
    firstLine,
    firstToGiveUp,
    generatorLimit,
    givingUpStep,
    hostCpu,
    inboxLatchDelta,
    knee,
    liveRow,
    p95Where,
    parseCpu,
    parseInboxLatch,
    parseMemory,
    promRange,
    serviceCrossings,
    sqlDelta,
    LOCK_WAITS,
    LOCK_WAITS_READ,
    LOCK_WAITS_TOP,
    lockWaits,
    lockWaitsLine,
    parseLockWaits,
    queuesLine,
    QUERY_STORE_TOP,
    parseQueryStore,
    queryStoreLine,
    queryStoreRow,
    queryStoreSql,
    sqlcmdArgs,
    statementText,
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

    assert.deepEqual(row, {
        step: 2, vus: 50, requests: 900, p95: 120, blockedP95: null, connectingP95: null, waitingP95: null,
        errorRate: 0.004,
    });
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

test('a stop the judge flagged ends the run at its step, though the next step\'s hold sent requests', () => {
    const step = (n, extra = {}) => ({ step: n, vus: 400 * n, requests: 500, ...extra });
    const level = { level: 'custom', stopRule: 'knee', capped: true };
    const begun = { requests: 1208, hostCpu: 97 };

    const knee = ending([step(1), step(2), step(3, { kneeStop: true }), step(4, begun), step(5, { requests: 0 })],
        level);
    assert.deepEqual(knee, { reason: 'knee', step: 3, vus: 1200, detail: null });

    const errors = ending([step(1), step(2, { aborted: true }), step(3, begun)], { ...level, stopRule: 'errors' });
    assert.equal(errors.reason, 'errors');
    assert.equal(errors.step, 2);
});

test('the clock skew is Prometheus\'s time less the midpoint of the round trip, and unread without an answer', () => {
    assert.equal(clockSkewMs(1000.5, 1_000_000, 1_000_200), 400, 'Prometheus answered 400 ms ahead of the midpoint');
    assert.equal(clockSkewMs(999.9, 1_000_000, 1_000_000), -100);
    assert.equal(clockSkewMs(NaN, 1_000_000, 1_000_200), null);
    assert.ok(Math.abs(clockSkewMs(1000.9, 1_000_000, 1_000_000)) < CLOCK_SKEW_LIMIT_MS);
    assert.ok(Math.abs(clockSkewMs(1001.5, 1_000_000, 1_000_000)) > CLOCK_SKEW_LIMIT_MS);
});

test('with k6 on a second machine the stack\'s busy CPU is the platform\'s, and only a VU shortfall is k6\'s', () => {
    const step = (n, extra = {}) => ({ step: n, vus: 100 * n, requests: 500, ...extra });
    const remote = { level: 'low', stopRule: 'knee', capped: true, generator: 'remote' };

    const busy = ending([step(1), step(2, { kneeStop: true, hostCpu: 97 })], remote);
    assert.equal(busy.reason, 'knee');
    assert.equal(busy.detail, null);
    assert.equal(generatorLimit({ vus: 200, hostCpu: 97 }, 'remote'), null);

    const short = ending([step(1), step(2, { kneeStop: true, hostCpu: 97, vusReached: 150 })], remote);
    assert.equal(short.reason, 'generator');
    assert.equal(short.detail, 'k6 ran 150 of 200 VUs');
    assert.equal(generatorLimit({ vus: 200, hostCpu: 97 }), `the machine's CPU at 97% over the hold`,
        'a row read with no generator named is the local run\'s');
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

test('a container\'s memory is read at the hold\'s middle and end, with its service and cores', () => {
    const at = (ms, memory) =>
        ({ at: ms, containers: [{ name: 'c-1', service: 'ordering-api', cores: 2, cpu: 1, memory }] });
    const [stats] = containerStats([at(1000, 9), at(1400, 5), at(1600, 6), at(2000, 8)],
        { startMs: 1000, endMs: 2000 });

    assert.equal(stats.memoryMiddle, 6, 'the first reading at or after the middle');
    assert.equal(stats.memoryEnd, 8);
    assert.equal(stats.service, 'ordering-api');
    assert.equal(stats.cores, 2);
});

// Every reading made, and none crossing.
const measuredStep = { step: 1, vus: 100, requests: 1000, sql: {}, containers: [{ name: 'idle', service: 'idle' }] };
const signals = (row, previous = null, options = {}) =>
    serviceCrossings({ ...measuredStep, ...row }, previous && { ...measuredStep, ...previous }, options)
        .map(({ service, signal }) => `${service} ${signal}`);

test('a request type whose own p95 doubles past the floor names its service, and a smaller rise does not', () => {
    const before = { requestTypes: [{ service: 'Ordering.Api', request: 'PlaceOrderCommand', p95Seconds: 0.025 }] };
    const at = (p95Seconds) => ({ requestTypes: [{ ...before.requestTypes[0], p95Seconds }] });

    assert.deepEqual(signals(at(0.14), before), ['Ordering.Api p95']);
    assert.match(serviceCrossings(at(0.14), before)[0].detail, /^PlaceOrderCommand p95 25 ms to 140 ms$/);
    assert.deepEqual(signals(at(0.07), before), [], 'past double but within the floor');
    assert.deepEqual(signals(at(0.14)), [], 'no step before to measure it against');
    assert.deepEqual(signals({ requestTypes: [{ ...before.requestTypes[0], request: 'GetOrderQuery', p95Seconds: 1 }] },
        before), [], 'a request type the step before never saw');
});

test('a service whose 5xx share passes one in a hundred is named, and one at the limit is not', () => {
    assert.deepEqual(signals({ serverErrors: [{ service: 'Catalog.Api', share: 0.03 }] }), ['Catalog.Api errors']);
    assert.deepEqual(signals({ serverErrors: [{ service: 'Catalog.Api', share: ERROR_RATE_LIMIT }] }), []);
});

test('a service\'s outbox and the queues it consumes still growing over the hold name it', () => {
    const outbox = (middle, end) => ({ outboxes: [{ service: 'Ordering.Api', middle, end }] });
    const queue = (middle, end) => ({ queues: [{ queue: 'ordering-commands', service: 'Ordering.Api', middle, end }] });

    assert.deepEqual(signals(outbox(0.4, 12)), ['Ordering.Api outbox']);
    assert.deepEqual(signals(outbox(20, 21)), [], 'levelled off');
    assert.deepEqual(signals(queue(0, 900)), ['Ordering.Api queue']);
    assert.deepEqual(signals(queue(10, 60)), [], 'under the floor');
    assert.deepEqual(signals({ queues: [{ queue: 'orphan', service: null, middle: 0, end: 900 }] }), ['orphan queue'],
        'a queue nobody consumes is named itself');
});

test('an error queue that gained messages over the hold names its consumer, and one that held still does not', () => {
    const faulted = (start, end) =>
        ({ faultedQueues: [{ queue: 'inventory-commands_error', service: 'Inventory.Api', start, end }] });

    assert.deepEqual(signals(faulted(0, 3)), ['Inventory.Api faulted']);
    assert.deepEqual(signals(faulted(3, 3)), []);
});

test('a container at nine tenths of the cores it may use, or whose memory climbs, names the service it runs', () => {
    const container = (extra) => ({ containers: [{ name: 'commerce-gateway-1', service: 'gateway', ...extra }],
        serverErrors: [{ service: 'Gateway.Api', share: 0 }] });

    assert.deepEqual(signals(container({ cores: 2, cpuMean: 180 })), ['Gateway.Api cpu']);
    assert.deepEqual(signals(container({ cores: 2, cpuMean: 170 })), [], 'under nine tenths');
    assert.deepEqual(signals(container({ cores: null, cpuMean: 180 }), null, { cores: 8 }), [],
        'no limit of its own is every processor Docker has');
    assert.deepEqual(signals(container({ cores: null, cpuMean: 750 }), null, { cores: 8 }), ['Gateway.Api cpu']);
    assert.deepEqual(signals(container({ cores: null, cpuMean: 750 })), [], 'no cores read is no verdict');

    const mib = 1024 ** 2;
    assert.deepEqual(signals(container({ memoryMiddle: 200 * mib, memoryEnd: 400 * mib })), ['Gateway.Api memory']);
    assert.deepEqual(signals(container({ memoryMiddle: 400 * mib, memoryEnd: 590 * mib })), [],
        'past the floor, but under half again');
    assert.deepEqual(signals(container({ memoryMiddle: 100 * mib, memoryEnd: 160 * mib })), [], 'within the floor');
});

test('the generator\'s container is never a service giving up, and is the stress override\'s own service', () => {
    const k6 = { containers: [{ name: 'commerce-k6-run-1', service: GENERATOR_SERVICE, cores: 1, cpuMean: 100,
        memoryMiddle: 100 * 1024 ** 2, memoryEnd: 900 * 1024 ** 2 }] };

    assert.deepEqual(signals(k6), [], 'its CPU and memory are the generator\'s, never a service\'s');
    assert.deepEqual(signals({ containers: [{ ...k6.containers[0], service: 'gateway' }] }),
        ['gateway cpu', 'gateway memory'], 'the same figures on a service name it');

    const override = readFileSync(new URL('../../compose/docker-compose.stress.yml', import.meta.url), 'utf8');
    assert.match(override, new RegExp(`^  ${GENERATOR_SERVICE}:\\r?\\n(?:    .*\\r?\\n)*?    image: grafana/k6:`, 'm'));
});

test('a container is named by the service whose name Compose spells, alone or with -api', () => {
    const services = ['Gateway.Api', 'Web.Bff', 'Ordering.Api'];

    assert.equal(containerOwner({ name: 'commerce-gateway-1', service: 'gateway' }, services), 'Gateway.Api');
    assert.equal(containerOwner({ name: 'commerce-web-bff-1', service: 'web-bff' }, services), 'Web.Bff');
    assert.equal(containerOwner({ name: 'commerce-sql-1', service: 'sql' }, services), 'sql');
    assert.equal(containerOwner({ name: 'loose', service: null }, services), 'loose', 'no Compose label');
});

test('SQL Server\'s deadlocks, and a wait that grew four times by more than the hold, name it', () => {
    const hold = { holdSeconds: 1 };

    assert.deepEqual(signals({ sql: { deadlocks: 2 } }), ['sql deadlocks']);
    assert.deepEqual(signals({ sql: { deadlocks: 0, lockWaitMs: 5000 } }, { sql: { lockWaitMs: 1000 } }, hold),
        ['sql waits']);
    assert.deepEqual(signals({ sql: { lockWaitMs: 3000 } }, { sql: { lockWaitMs: 1000 } }, hold), [],
        'under four times');
    assert.deepEqual(signals({ sql: { logWaitMs: 900 } }, { sql: { logWaitMs: 100 } }, hold), [],
        'within one connection blocked for the hold');
    // A run that held throughout: its log-flush waits over 180 s holds at steps 1 and 2.
    assert.deepEqual(signals({ sql: { logWaitMs: 20166 } }, { sql: { logWaitMs: 2446 } }, { holdSeconds: 180 }), []);
    assert.deepEqual(signals({ sql: { lockWaitMs: 5000 } }, { sql: { lockWaitMs: 1000 } }), [], 'no hold, no floor');
});

test('the services that crossed are listed furthest past their own threshold first', () => {
    const rows = [
        { ...measuredStep, requestTypes: [{ service: 'Catalog.Api', request: 'GetProductQuery', p95Seconds: 0.02 }] },
        { ...measuredStep, step: 2, requests: 0 },
        {
            ...measuredStep,
            step: 3,
            vus: 400,
            requestTypes: [{ service: 'Catalog.Api', request: 'GetProductQuery', p95Seconds: 0.09 }],
            serverErrors: [{ service: 'Ordering.Api', share: 0.1 }],
            outboxes: [{ service: 'Catalog.Api', middle: 1, end: 9 }],
        },
    ];

    const first = firstToGiveUp(rows, 3);
    assert.equal(first.vus, 400);
    assert.deepEqual(first.services.map((s) => s.service), ['Ordering.Api', 'Catalog.Api'],
        'ten times its threshold before a p95 just past double, measured against the last step measured');
    assert.deepEqual(first.services[1].signals.map((s) => s.signal), ['outbox', 'p95']);
    assert.equal(firstLine(first), 'First to give up: Ordering.Api: 5xx 10.0% | Catalog.Api: oldest outbox row ' +
        '1.0 s to 9.0 s over the hold; GetProductQuery p95 20 ms to 90 ms');
});

test('work refused outright ranks ahead of any multiple of a threshold, and is written as null', () => {
    const row = {
        ...measuredStep,
        serverErrors: [{ service: 'Ordering.Api', share: 0.5 }],
        faultedQueues: [{ queue: 'inventory-commands_error', service: 'Inventory.Api', start: 0, end: 1 }],
        sql: { deadlocks: 1 },
    };

    const first = firstToGiveUp([row], 1);
    assert.deepEqual(first.services.map((s) => s.service), ['Inventory.Api', 'sql', 'Ordering.Api'],
        'one faulted message and one deadlock before a 5xx share fifty times its threshold');
    assert.equal(first.services[0].past, null);
    assert.equal(first.services[2].past, 50);
    assert.equal(JSON.parse(JSON.stringify(first)).services[0].past, null);
});

test('the line says when no service\'s figures moved, and when there was no step to judge', () => {
    assert.equal(firstLine(firstToGiveUp([measuredStep], 1)),
        'First to give up: none named; step 1 crossed only journey-wide signals');
    assert.equal(firstToGiveUp([measuredStep], null), null);
    assert.equal(firstToGiveUp([{ ...measuredStep, requests: 0 }], 1), null, 'a step never reached');
    assert.equal(firstLine(null), 'First to give up: not judged; no step was the knee or past the abort rate');
});

test('a step whose service figures were not all read never says that nothing moved', () => {
    const unread = { ...measuredStep, step: 2, serviceReadsFailed: ['serverErrors'] };
    const unreadAt = (row) => firstToGiveUp([measuredStep, { ...measuredStep, step: 2, ...row }], 2).unread;

    assert.equal(firstLine(firstToGiveUp([measuredStep, unread], 2)),
        'First to give up: not judged; step 2\'s service figures were not all read');
    assert.equal(firstLine(firstToGiveUp([measuredStep, { ...unread, sql: { deadlocks: 1 } }], 2)),
        'First to give up: sql: 1 deadlocks', 'what crossed is still named');
    assert.equal(unreadAt({}), false);
    assert.equal(unreadAt({ sql: null }), true, 'the sampler missed SQL Server');
    assert.equal(unreadAt({ containers: [] }), true, 'the sampler missed the containers');
});

test('from the step before, only the readings the rules compare leave the verdict short', () => {
    const before = (row) => firstToGiveUp([{ ...measuredStep, ...row }, { ...measuredStep, step: 2 }], 2).unread;

    assert.equal(before({ serviceReadsFailed: ['requestTypes'] }), true);
    assert.equal(before({ sql: null }), true);
    assert.equal(before({ serviceReadsFailed: ['serverErrors', 'outboxes', 'queues', 'faultedQueues'] }), false);
    assert.equal(before({ containers: [] }), false);
});

test('the step named is the error stop\'s where it fired, and otherwise the knee\'s', () => {
    const step = (n, extra = {}) => ({ step: n, vus: 100 * n, requests: 500, ...extra });
    const level = { level: 'low', stopRule: 'errors', capped: true };
    const aborted = [step(1), step(2), step(3), step(4), step(5, { aborted: true })];

    assert.equal(givingUpStep(aborted, { step: 3 }), 5);
    const saturated = [...aborted.slice(0, 4), step(5, { aborted: true, hostCpu: 97 })];
    assert.equal(ending(saturated, level).reason, 'generator');
    assert.equal(givingUpStep(saturated, { step: 3 }), 5, 'an abort the machine ran out at is still the abort');
    assert.equal(givingUpStep([step(1), step(2), step(3, { kneeStop: true })], { step: 3 }), 3);
    assert.equal(givingUpStep([step(1), step(2)], { step: 2 }), 2, 'a knee the run held past');
    assert.equal(givingUpStep([step(1), step(2)], null), null);
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

// What sqlcmd prints for one database: two statements, then waits by category, one of them for a third
// statement outside the read's top, which still counts toward the totals.
const ORDERING_STORE = [
    'S|129|543718|73600|285200|(@p7 bigint,@p8 varbinary(8),@p0 datetime2(7))UPDATE [ordering].[InboxState] ' +
        'SET [Consumed] = @p0 WHERE [Id] = @p7 AND [RowVersion] = @p8',
    'S|393|78014|4400|143900|(@p0 uniqueidentifier)SELECT TOP(2) [o].[CorrelationId] FROM [ordering].[Saga] AS [o]',
    'W|129|Buffer Latch|149500',
    'W|129|Latch|54500',
    'W|129|CPU|6600',
    'W|129|Lock|100',
    'W|393|Lock|139100',
    'W|7|Tran Log IO|1800',
].join('\r\n');

test('Query Store is read over a window of UTC instants, its joins in the house form, and nothing else', () => {
    const sql = queryStoreSql('2026-10-30T05:03:17.000Z', '2026-10-30T05:05:17.000Z');

    assert.match(sql, /@from datetimeoffset = '2026-10-30T05:03:17\.000Z', /);
    assert.match(sql, /@to datetimeoffset = '2026-10-30T05:05:17\.000Z'/);
    assert.match(sql, /WHERE i\.start_time >= @from AND i\.end_time <= @to/, 'whole intervals inside the hold');
    assert.match(sql, /PATINDEX\('%\)\[A-Za-z\]%', x\.query_sql_text\)/, 'declarations dropped before sqlcmd cuts');
    assert.match(sql, /AS nvarchar\(200\)\)$/m, 'a bounded type, which sqlcmd prints whole');
    const joins = sql.split('\n').filter((line) => /JOIN/.test(line));
    assert.ok(joins.length > 0 && joins.every((line) => /^INNER JOIN \S+ AS \w+$/.test(line)));
    assert.throws(() => queryStoreSql("2026-10-30'; DROP TABLE x; --", '2026-10-30T05:05:17Z'), /ISO instants/);
    assert.throws(() => sqlcmdArgs('SELECT 1', 'Ordering; DROP'), /not a database name/);
    assert.deepEqual(sqlcmdArgs('SELECT 1', 'Ordering').slice(-4), ['-d', 'Ordering', '-Q', 'SELECT 1']);
    assert.ok(sqlcmdArgs('SELECT 1').includes('-b'), 'a T-SQL error fails the exit');
});

test('a statement loses its declarations, and is cut and made safe for a code span', () => {
    assert.equal(statementText('(@p0 int,@p1 nvarchar(4000))UPDATE [x] SET [a] = @p0'), 'UPDATE [x] SET [a] = @p0');
    assert.equal(statementText('(@MaxAttempts int)WITH claimable AS (SELECT 1)'), 'WITH claimable AS (SELECT 1)');
    assert.equal(statementText('(@p6 nvarchar(4000),@p7 int)SET NOCOUNT ON; INSERT INTO [x]'),
        'SET NOCOUNT ON; INSERT INTO [x]', "EF's batch opens with SET NOCOUNT");
    assert.equal(statementText('SELECT `a`'), "SELECT 'a'");
    assert.ok(statementText(`SELECT ${'x'.repeat(200)}`).endsWith('…'));
});

test('a step names the statements that took longest across databases, and every statement\'s waits', () => {
    const ordering = parseQueryStore('Ordering', ORDERING_STORE);
    const bff = parseQueryStore('Bff', 'S|5|10|20|30000|SELECT 1\nW|5|Buffer Latch|500');
    const row = queryStoreRow([ordering, bff]);

    assert.deepEqual(row.statements.map((s) => `${s.database} ${s.id}`), ['Ordering 129', 'Ordering 393', 'Bff 5']);
    assert.deepEqual(row.statements[0].waits.map((w) => w.category), ['Buffer Latch', 'Latch', 'CPU'],
        'the three largest waits, the fourth dropped');
    assert.deepEqual(row.waits.slice(0, 2),
        [{ category: 'Buffer Latch', ms: 150000 }, { category: 'Lock', ms: 139200 }]);
    assert.ok(row.waits.some((w) => w.category === 'Tran Log IO'), 'a statement outside the top still counts');

    const many = { statements: Array.from({ length: 9 }, (_, i) => ({ id: i, durationMs: i, waits: [] })), waits: [] };
    assert.equal(queryStoreRow([many]).statements.length, QUERY_STORE_TOP);
    assert.equal(queryStoreRow([parseQueryStore('Ordering', ''), parseQueryStore('Bff', null)]), null);
});

test('the statements line reads each statement\'s time, runs, CPU and waits, and says when none was read', () => {
    const row = queryStoreRow([parseQueryStore('Ordering', ORDERING_STORE)]);
    const line = queryStoreLine(3, row);

    assert.match(line, /^- Step 3: Ordering `UPDATE \[ordering\]\.\[InboxState\] SET/);
    assert.match(line, /285\.2 s in 543718 runs, CPU 73\.6 s, Buffer Latch 149\.5 s, Latch 54\.5 s, CPU 6\.6 s;/);
    assert.match(line, /; waits Buffer Latch 149\.5 s, Lock 139\.2 s/);
    assert.equal(queryStoreLine(4, null), '- Step 4: no whole Query Store interval inside the hold');
    assert.equal(queryStoreLine(5, { unread: true }), '- Step 5: Query Store not read; Not measured says why',
        'a failed read is not an empty window');
});

test('the live row reads each phase\'s p95 from the step\'s own submetrics', () => {
    const metrics = [
        ...rest(2, { requests: 900, p95: 1100, rate: 0 }),
        { id: 'stress_journey_blocked{step:2}', attributes: { sample: { 'p(95)': 3 } } },
        { id: 'stress_journey_connecting{step:2}', attributes: { sample: { 'p(95)': 0 } } },
        { id: 'stress_journey_waiting{step:2}', attributes: { sample: { 'p(95)': 1050 } } },
        { id: 'stress_journey_waiting', attributes: { sample: { 'p(95)': 9999 } } },
    ];
    const row = liveRow({ step: 2, vus: 50 }, metrics);

    assert.equal(row.blockedP95, 3);
    assert.equal(row.connectingP95, 0, 'a reused connection opens none, which is a reading');
    assert.equal(row.waitingP95, 1050);
});

test('a step\'s client row reads each phase\'s p95, and a phase nothing was added to is no reading', () => {
    const row = clientRow({
        step: 2,
        vus: 50,
        journey: { 'p(95)': 1063, count: 1700 },
        phases: {
            blocked: { 'p(95)': 2, count: 1700 },
            connecting: { 'p(95)': 0, count: 0 },
            waiting: { 'p(95)': 1040, count: 1700 },
        },
        ops: { listing: { count: 1700 } },
    }, 100);

    assert.equal(row.blockedP95, 2);
    assert.equal(row.connectingP95, null);
    assert.equal(row.waitingP95, 1040);
    assert.equal(clientRow({ step: 1, vus: 10 }, 100).waitingP95, null, 'a raw.json from before the phases');
});

// The step before a jump: a healthy journey, its waiting most of it, the gateway's own a little less.
const steady = { ...healthy, p95: 40, blockedP95: 1, connectingP95: 0, waitingP95: 35, gatewayP95: 30 };

test('a jump k6 waited for and the gateway never saw is placed on the path to it', () => {
    // Run 20261031-193139's step 2: the journey to about 1.1 s, the gateway's own at 49 ms.
    const row = { ...steady, step: 2, p95: 1063, waitingP95: 1050, gatewayP95: 49 };
    const [jump] = crossings(row, steady);

    assert.equal(jump.signal, 'p95');
    assert.equal(jump.where, 'path');
    assert.equal(jump.detail, 'journey p95 40 ms to 1063 ms, outside the gateway, waiting p95 35 ms to 1050 ms ' +
        'against the gateway\'s own p95 30 ms to 49 ms: the path from k6 to the gateway\'s listener, not a service');
});

test('a jump the gateway\'s own p95 explains is placed in the platform, at the share\'s edge too', () => {
    const rise = 1000;
    const edge = { ...steady, step: 2, p95: 1040, waitingP95: 35 + rise, gatewayP95: 30 + rise * OUTSIDE_SHARE };
    const past = { ...edge, gatewayP95: edge.gatewayP95 - 1 };

    assert.equal(p95Where(edge, steady).where, 'platform', 'half of the rise outside is not more than half');
    assert.equal(p95Where(past, steady).where, 'path');
    assert.equal(p95Where(edge, steady).detail, 'in the platform, the gateway\'s own p95 30 ms to 530 ms');
});

test('k6\'s connection setup, outside the journey\'s figure, is noted beside a jump and never placed as it', () => {
    const row = { ...steady, step: 2, p95: 300, blockedP95: 3001, connectingP95: 3000, waitingP95: 290 };
    const where = p95Where(row, steady);

    assert.equal(where.where, 'path', 'the jump is in waiting, which the gateway did not see');
    assert.equal(where.detail, 'outside the gateway, waiting p95 35 ms to 290 ms against the gateway\'s own p95 ' +
        '30 ms to 30 ms: the path from k6 to the gateway\'s listener, not a service; k6\'s connection setup rose ' +
        'too, outside the journey\'s figure, blocked p95 1 ms to 3001 ms, connecting p95 0 ms to 3000 ms');
    assert.match(p95Where({ ...row, blockedP95: null }, steady).detail, /connecting p95 0 ms to 3000 ms$/,
        'connecting alone is a setup rise');
    assert.doesNotMatch(p95Where({ ...row, blockedP95: 1 + P95_JUMP_FLOOR_MS, connectingP95: 0 }, steady).detail,
        /connection setup/, 'a setup rise at the jump\'s floor is not noted');
});

test('a jump with no rise in setup or waiting is in the transfer, and one with no gateway reading says so', () => {
    assert.equal(p95Where({ ...steady, p95: 400, waitingP95: 30 }, steady).where, 'transfer');
    assert.equal(p95Where({ ...steady, p95: 1040, waitingP95: 45, gatewayP95: 30 }, steady).where, 'transfer',
        'a waiting that rose 10 ms under a 1000 ms jump does not carry it');
    assert.equal(p95Where({ ...steady, p95: 1040, waitingP95: 535, gatewayP95: 530 }, steady).where, 'transfer',
        'half of the jump in waiting is not more than half');
    assert.equal(p95Where({ ...steady, p95: 1040, waitingP95: 536, gatewayP95: 531 }, steady).where, 'platform');
    const unread = p95Where({ ...steady, p95: 400, waitingP95: 390, gatewayP95: null }, steady);

    assert.equal(unread.where, 'waiting');
    assert.equal(unread.detail, 'in waiting, waiting p95 35 ms to 390 ms; the gateway\'s own p95 unread');
});

test('a run from before the phases keeps its p95 signal\'s detail as it was', () => {
    const [jump] = crossings({ ...healthy, p95: 200 }, healthy);

    assert.equal(p95Where({ ...healthy, p95: 200 }, healthy), null);
    assert.equal(jump.where, null);
    assert.equal(jump.detail, 'journey p95 80 ms to 200 ms');
});

test('a rate over a hold reaches back to two minutes, and a longer hold is read whole', () => {
    assert.equal(promRange(60), `${MINIMUM_RANGE_SECONDS}s`);
    assert.equal(promRange(180), '180s');
});

test('the inbox latch is read from every database\'s InboxMessages clustered index, and parsed by database', () => {
    assert.match(INBOX_LATCH, /sys\.dm_db_index_operational_stats\(NULL, NULL, NULL, NULL\)/);
    assert.match(INBOX_LATCH, /s\.index_id = 1 AND OBJECT_NAME\(s\.object_id, s\.database_id\) = 'InboxMessages'/);
    assert.deepEqual(parseInboxLatch('Ordering|194\r\nPayments|97\r\n\r\nnoise\r\nInventory|x\r\n'),
        { Ordering: 194, Payments: 97 });
});

test('a step\'s inbox latch is each database\'s change, most first, and a counter that fell is left out', () => {
    const samples = [
        { at: 1000, inboxLatch: { Ordering: 100, Payments: 50, Inventory: 900 } },
        { at: 1500, inboxLatchError: 'sqlcmd failed' },
        { at: 2000, inboxLatch: { Ordering: 294, Payments: 147, Inventory: 10, Shipping: 5 } },
    ];

    assert.deepEqual(inboxLatchDelta(samples, { startMs: 1000, endMs: 2000 }), [
        { database: 'Ordering', ms: 194 },
        { database: 'Payments', ms: 97 },
    ], 'Inventory\'s fell and Shipping has no first reading');
    assert.equal(inboxLatchDelta(samples, { startMs: 1500, endMs: 2500 }), null, 'one reading is no change');
});

const INSERT = '(@p0 nvarchar(256),@p1 uniqueidentifier)INSERT INTO [ordering].[InboxMessages] ([Endpoint])';
const DELETE = 'DELETE TOP (100) FROM [ordering].[InboxState] WHERE CleanupBucket = @Bucket';
const KEYLOCK = 'keylock hobtid=72057594043760640 dbid=7 id=lock1f2 mode=X associatedObjectId=72057594043760640';

test('a lock wait is read from the waiting tasks, with the holder\'s statement whether it runs or idles', () => {
    assert.match(LOCK_WAITS, new RegExp(`^SELECT TOP \\(${LOCK_WAITS_READ}\\)`, 'm'));
    assert.match(LOCK_WAITS, /FROM sys\.dm_os_waiting_tasks AS w/);
    assert.match(LOCK_WAITS, /WHERE w\.wait_type LIKE 'LCK\[_\]%' AND w\.blocking_session_id > 0/);
    assert.match(LOCK_WAITS, /COALESCE\(hr\.sql_handle, hc\.most_recent_sql_handle\)/,
        'an idle holder has no request, so its last batch is read from its connection');
    assert.match(LOCK_WAITS, /hc\.parent_connection_id IS NULL/, 'one connection a session, not each MARS one');
    assert.equal((LOCK_WAITS.match(/AS nvarchar\(200\)\)/g) ?? []).length, 3, 'every text a bounded field');

    const output = [
        `Ordering|61|58|LCK_M_X|27|sleeping|1|NULL|12|${KEYLOCK}|INSERT INTO [ordering].[InboxMessages]|${DELETE}`,
        `Ordering|62|59|LCK_M_U|40|suspended|1|LCK_M_X|NULL|${KEYLOCK}|${DELETE}|${DELETE}`,
        '',
        'Ordering|x|58|LCK_M_X|27|sleeping|1|NULL|12|keylock|a|b',
        'too|few',
    ].join('\r\n');

    assert.deepEqual(parseLockWaits(output), [
        {
            database: 'Ordering', session: 61, holder: 58, waitType: 'LCK_M_X', waitMs: 27,
            holderStatus: 'sleeping', holderTransactions: 1, holderWait: null, holderIdleMs: 12,
            resource: KEYLOCK, statement: 'INSERT INTO [ordering].[InboxMessages]', holderStatement: DELETE,
        },
        {
            database: 'Ordering', session: 62, holder: 59, waitType: 'LCK_M_U', waitMs: 40,
            holderStatus: 'suspended', holderTransactions: 1, holderWait: 'LCK_M_X', holderIdleMs: null,
            resource: KEYLOCK, statement: DELETE, holderStatement: DELETE,
        },
    ]);
});

test('a step\'s lock waits pair each waiting statement with its lock and holder, the most often seen first', () => {
    const wait = (overrides) => ({
        database: 'Ordering', session: 61, holder: 58, waitType: 'LCK_M_X', waitMs: 20,
        holderStatus: 'sleeping', holderTransactions: 1, holderWait: null, holderIdleMs: 5,
        resource: KEYLOCK, statement: INSERT, holderStatement: DELETE, ...overrides,
    });
    const samples = [
        { at: 1000, lockWaits: [wait({}), wait({ waitType: 'LCK_M_U', holderWait: 'LCK_M_X', holderIdleMs: null })] },
        { at: 1500, lockWaitsError: 'sqlcmd failed' },
        { at: 2000, lockWaits: [wait({ waitMs: 90, holderIdleMs: 30 })] },
        { at: 3000, lockWaits: [wait({ waitMs: 500 })] },
    ];

    const row = lockWaits(samples, { startMs: 1000, endMs: 2000 });

    assert.equal(row.samples, 2, 'the failed read is no sample');
    assert.equal(row.seen, 3);
    assert.deepEqual(row.chains[0], {
        database: 'Ordering',
        waitType: 'LCK_M_X',
        resource: 'keylock held X hobt 72057594043760640',
        statement: 'INSERT INTO [ordering].[InboxMessages] ([Endpoint])',
        holder: { statement: DELETE, state: 'idle in its transaction' },
        seen: 2,
        longestMs: 90,
        holderIdleMs: 30,
    });
    assert.equal(row.chains[1].holder.state, 'waiting LCK_M_X',
        'a holder waiting on a lock is a link in a longer chain');

    const table = 'objectlock lockPartition=0 objid=901578250 subresource=FULL dbid=7 id=lock3 mode=IX ' +
        'associatedObjectId=901578250';
    const escalated = lockWaits([{ at: 1, lockWaits: [wait({ resource: table })] }], { startMs: 0, endMs: 2 });
    assert.equal(escalated.chains[0].resource, 'objectlock held IX object 901578250', 'a table lock names no hobt');

    const outside = lockWaits([{ at: 1, lockWaits: [wait({ holderTransactions: 0 })] }], { startMs: 0, endMs: 2 });
    assert.equal(outside.chains[0].holder.state, 'idle', 'a session lock held outside any transaction');

    const gone = lockWaits([{ at: 1, lockWaits: [wait({ holderStatement: null })] }], { startMs: 0, endMs: 2 });
    assert.match(lockWaitsLine(5, gone), /, behind an unread statement idle in its transaction/,
        'no empty code span');
    assert.equal(row.chains[1].holderIdleMs, null);

    assert.equal(lockWaitsLine(2, row).split('; ')[0], '- Step 2: 3 waits in 2 samples');
    assert.equal(lockWaitsLine(2, row).split('; ')[1],
        'Ordering `INSERT INTO [ordering].[InboxMessages] ([Endpoint])` LCK_M_X on keylock held X ' +
        `hobt 72057594043760640, behind \`${DELETE}\` idle in its transaction up to 30 ms, ` +
        'seen 2 times, longest 90 ms');
    assert.equal(lockWaitsLine(3, lockWaits([{ at: 1, lockWaits: [] }], { startMs: 0, endMs: 2 })),
        '- Step 3: no session waited on a lock in any of 1 samples');
    assert.equal(lockWaits(samples, { startMs: 1400, endMs: 1600 }), null, 'no sample read it');
    assert.equal(lockWaitsLine(4, null), '- Step 4: not read');
});

test('a step keeps only the LOCK_WAITS_TOP pairings seen most often', () => {
    const waits = Array.from({ length: LOCK_WAITS_TOP + 2 }, (_, i) => ({
        database: 'Ordering', waitType: 'LCK_M_X', waitMs: i, resource: KEYLOCK,
        statement: `UPDATE t${i}`, holderStatement: DELETE, holderStatus: 'running', holderWait: null,
    }));

    const row = lockWaits([{ at: 1, lockWaits: [...waits, waits[3], waits[3]] }], { startMs: 0, endMs: 2 });

    assert.equal(row.chains.length, LOCK_WAITS_TOP);
    assert.equal(row.chains[0].statement, 'UPDATE t3');
    assert.equal(row.chains[0].holder.state, 'running');
    assert.equal(row.seen, LOCK_WAITS_TOP + 4);
});

test('a step splits each work queue into what it held, what it handled and what was in flight', () => {
    const queues = [
        { queue: 'payments-events', middle: 0, end: 0, handledPerSecond: 0, inFlight: 0 },
        { queue: 'orders-events', middle: 120, end: 13343, handledPerSecond: 41.25, inFlight: 31.6 },
        { queue: 'shipping-events', middle: 2, end: 0, handledPerSecond: 39.9, inFlight: null },
    ];

    assert.equal(queuesLine(4, { queues }),
        '- Step 4: orders-events 120 → 13343 queued, 41.3/s handled, 31.6 in flight; ' +
        'shipping-events 2 → 0 queued, 39.9/s handled, — in flight');
    assert.equal(queuesLine(5, { queues: [queues[0]] }), '- Step 5: no work queue held or handled a message');
    assert.equal(queuesLine(6, { queues: [], serviceReadsFailed: ['queues'] }), '- Step 6: not read',
        'a failed depth read is no reading, not an idle broker');
    assert.equal(queuesLine(7, { queues: [{ ...queues[0], handledPerSecond: null }] }),
        '- Step 7: no work queue held a message; what they handled was not read',
        'an empty handled read says nothing of the handling');
});
