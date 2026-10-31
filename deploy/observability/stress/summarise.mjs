// Turns one stress run into its per-step table: k6's client figures from
// raw.json, the server's from Prometheus at each step's hold, the resources
// from sample.mjs's samples, the statements from Query Store, and the machine
// they all describe. Writes
// summary.json and summary.md beside them and prints the knee and the first
// service to give up. It reports and never gates, so a reading it cannot make
// is recorded as unavailable.
//
//   node deploy/observability/stress/summarise.mjs artifacts/stress/<run>
//
// PROM_URL defaults to Grafana's datasource proxy on 127.0.0.1, the route
// the SLO run reads Prometheus by.
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
    DEFAULT_PROM_URL,
    OUTBOX_OLDEST,
    QUERY_STORE_DATABASES,
    QUERY_STORE_INTERVAL_MINUTES,
    QUEUED,
    WORK_FILTER,
    WORK_QUEUES,
    clientRow,
    containerStats,
    endedLine,
    ending,
    firstLine,
    firstToGiveUp,
    givingUpStep,
    hostCpu,
    knee,
    parseQueryStore,
    queryStoreLine,
    queryStoreRow,
    queryStoreSql,
    sqlDelta,
    sqlcmdArgs,
} from './report.js';

const out = process.argv[2];

if (!out || !existsSync(join(out, 'raw.json'))) {
    console.error('usage: node deploy/observability/stress/summarise.mjs <the directory stress.js wrote raw.json to>');
    process.exit(2);
}

const PROM_URL = process.env.PROM_URL || DEFAULT_PROM_URL;

const raw = JSON.parse(readFileSync(join(out, 'raw.json'), 'utf8'));
const samplesPath = join(out, 'samples.jsonl');
const samples = existsSync(samplesPath)
    ? readFileSync(samplesPath, 'utf8').split('\n').filter((line) => line.trim()).map((line) => JSON.parse(line))
    : [];

const unavailable = [];

// One instant query evaluated at `atMs`, so a reading made after the run
// still describes the step: Prometheus answers for any time it holds. A
// failure is recorded, and told to `failed` where a caller passes one.
async function query(name, expr, atMs, failed = () => {}) {
    const url = `${PROM_URL}/api/v1/query?query=${encodeURIComponent(expr)}&time=${atMs / 1000}`;
    const fail = (reason) => {
        unavailable.push(`${name}: ${reason}`);
        failed();
        return [];
    };

    try {
        const response = await fetch(url);

        if (!response.ok)
            return fail(`Prometheus answered ${response.status}`);

        return (await response.json()).data.result.map((series) => ({
            labels: series.metric,
            value: Number(series.value[1]),
        })).filter((series) => Number.isFinite(series.value));
    } catch (error) {
        return fail(error.message);
    }
}

async function scalar(name, expr, atMs) {
    const [series] = await query(name, expr, atMs);
    return series ? series.value : null;
}

// Each queue's consumer, from MassTransit's own receive meter, so a queue is
// named by the service that reads it rather than by its spelling.
const CONSUMERS = 'group by (service_name, messaging_masstransit_destination) (messaging_masstransit_receive_ea_total)';
const OUTBOX_BY_SERVICE = 'max by (service_name) (outbox_oldest_age_seconds)';
const QUEUE_DEPTHS = `max by (queue) (${WORK_QUEUES})`;
const ERROR_QUEUE_DEPTHS = 'max by (queue) (rabbitmq_queue_messages{queue=~".+_error"})';

// What serviceCrossings() reads: each service's own request types, 5xx share,
// outbox and queues, the backlogs at the hold's middle and end as the knee's
// are, and the error queues at its start and end.
async function perService(window, range) {
    const end = window.endMs;
    const middle = (window.startMs + window.endMs) / 2;
    const byLabel = (series, label) => new Map(series.map((s) => [s.labels[label], s.value]));
    // Each failure is named by the field it leaves short, the names serviceCrossings() reads.
    const failed = new Set();
    const read = (field, name, expr, atMs) => query(name, expr, atMs, () => failed.add(field));

    const [requestTypes, serverErrors, consumers, outboxMiddle, outboxEnd, queueMiddle, queueEnd, faultStart,
        faultEnd] = await Promise.all([
        read('requestTypes', 'request.duration p95', 'histogram_quantile(0.95, sum by (le, service_name, request) ' +
            `(rate(request_duration_seconds_bucket[${range}])))`, end),
        read('serverErrors', '5xx share', 'sum by (service_name) (increase(http_server_request_duration_seconds_count' +
            `{http_response_status_code=~"5.."}[${range}])) / sum by (service_name) ` +
            `(increase(http_server_request_duration_seconds_count[${range}]))`, end),
        // Unread, a queue's crossing is still judged, but named by the queue rather than its consumer.
        query('queue consumers', CONSUMERS, end),
        read('outboxes', 'outbox oldest by service, middle', OUTBOX_BY_SERVICE, middle),
        read('outboxes', 'outbox oldest by service, end', OUTBOX_BY_SERVICE, end),
        read('queues', 'queue depth, middle', QUEUE_DEPTHS, middle),
        read('queues', 'queue depth, end', QUEUE_DEPTHS, end),
        read('faultedQueues', 'error queues, start', ERROR_QUEUE_DEPTHS, window.startMs),
        read('faultedQueues', 'error queues, end', ERROR_QUEUE_DEPTHS, end),
    ]);

    const consumer = new Map(consumers.map((s) => [s.labels.messaging_masstransit_destination, s.labels.service_name]));
    const outboxAtMiddle = byLabel(outboxMiddle, 'service_name');
    const queueAtMiddle = byLabel(queueMiddle, 'queue');
    const faultAtStart = byLabel(faultStart, 'queue');

    return {
        requestTypes: requestTypes.map((s) => ({
            service: s.labels.service_name, request: s.labels.request, p95Seconds: s.value,
        })),
        serverErrors: serverErrors.map((s) => ({ service: s.labels.service_name, share: s.value })),
        outboxes: outboxEnd.map((s) => ({
            service: s.labels.service_name, middle: outboxAtMiddle.get(s.labels.service_name) ?? null, end: s.value,
        })),
        queues: queueEnd.map((s) => ({
            queue: s.labels.queue, service: consumer.get(s.labels.queue) ?? null,
            middle: queueAtMiddle.get(s.labels.queue) ?? null, end: s.value,
        })),
        // An error queue exists only once something faulted, so one absent at the start held none.
        faultedQueues: faultEnd.map((s) => ({
            queue: s.labels.queue, service: consumer.get(s.labels.queue.replace(/_error$/, '')) ?? null,
            start: faultAtStart.get(s.labels.queue) ?? 0, end: s.value,
        })),
        // The fields a read left short, so the line never takes a figure it lacks for one that held.
        serviceReadsFailed: [...failed],
    };
}

async function serverRow(window, range) {
    const end = window.endMs;
    const middle = (window.startMs + window.endMs) / 2;

    const [services, outboxes, queues] = await Promise.all([
        perService(window, range),
        query('outbox oldest', `max by (service_name) (max_over_time(outbox_oldest_age_seconds[${range}]))`, end),
        query('queue depth', `topk(5, max by (queue) (max_over_time(${WORK_QUEUES}[${range}])))`, end),
    ]);

    const placed = await scalar('orders.placed', `sum(increase(orders_placed_total[${range}]))`, end);
    const confirmed = await scalar(
        'orders confirmed', `sum(increase(orders_fulfilment_duration_seconds_count[${range}]))`, end);

    return {
        ...services,
        slowestRequests: [...services.requestTypes].sort((a, b) => b.p95Seconds - a.p95Seconds).slice(0, 5),
        outboxOldest: outboxes.map((s) => ({ service: s.labels.service_name, seconds: s.value })),
        deepestQueues: queues.map((s) => ({ queue: s.labels.queue, messages: s.value })),
        outboxMiddle: await scalar('outbox oldest, middle', OUTBOX_OLDEST, middle),
        outboxEnd: await scalar('outbox oldest, end', OUTBOX_OLDEST, end),
        queueMiddle: await scalar('queued, middle', QUEUED, middle),
        queueEnd: await scalar('queued, end', QUEUED, end),
        // An error queue exists only once something faulted, so none is zero rather than no reading.
        errorQueues: await scalar(
            'error queues', 'sum(rabbitmq_queue_messages{queue=~".+_error"}) or vector(0)', end),
        deliveryLagP95: await scalar('messaging.delivery.lag p95',
            `histogram_quantile(0.95, sum by (le) (rate(messaging_delivery_lag_seconds_bucket[${range}])))`, end),
        // The BFF's read model is its consumers' projection, so its staleness is the lag they start at.
        bffLagP95: await scalar('BFF delivery lag p95', 'histogram_quantile(0.95, sum by (le) ' +
            `(rate(messaging_delivery_lag_seconds_bucket{service_name="Web.Bff"}[${range}])))`, end),
        bffUnattributed: await scalar('bff.orders.unattributed',
            `max(max_over_time(bff_orders_unattributed_seconds[${range}]))`, end),
        fulfilmentP95: await scalar('orders.fulfilment.duration p95',
            `histogram_quantile(0.95, sum by (le) (rate(orders_fulfilment_duration_seconds_bucket[${range}])))`, end),
        placed,
        confirmedShare: placed > 0 && confirmed !== null ? confirmed / placed : null,
        shippingOverdue: await scalar('shipping.shipments.overdue',
            `max(max_over_time(shipping_shipments_overdue_seconds[${range}]))`, end),
        notificationsOverdue: await scalar('notifications.overdue',
            `max(max_over_time(notifications_overdue_seconds[${range}]))`, end),
        // The share of time each consumed queue could hand a message straight to a consumer: low is a consumer
        // that cannot keep up. A queue nobody consumes has nothing to say here.
        leastUtilised: (await query('consumer utilisation', 'bottomk(5, min by (queue) ' +
            `(min_over_time(rabbitmq_queue_consumer_utilisation{${WORK_FILTER}}[${range}])) and on (queue) ` +
            `max by (queue) (rabbitmq_queue_consumers{${WORK_FILTER}}) > 0)`, end))
            .map((s) => ({ queue: s.labels.queue, utilisation: s.value })),
        // Common.Web's runtime meter: work queued for a thread, and the time collections paused the service.
        threadPoolQueue: (await query('thread pool queue', 'topk(3, max by (service_name) ' +
            `(max_over_time(dotnet_thread_pool_queue_length_total[${range}])))`, end))
            .map((s) => ({ service: s.labels.service_name, items: s.value })),
        gcPause: (await query('GC pause', 'topk(3, sum by (service_name) ' +
            `(increase(dotnet_gc_pause_time_seconds_total[${range}])))`, end))
            .map((s) => ({ service: s.labels.service_name, seconds: s.value })),
    };
}

function read(command, args, cwd) {
    try {
        return execFileSync(command, args, { cwd, encoding: 'utf8', windowsHide: true }).trim();
    } catch (error) {
        unavailable.push(`${command} ${args[0]}: ${String(error.message).split('\n')[0]}`);
        return null;
    }
}

// A local number means nothing without the machine and Docker's share of it.
function machine() {
    const docker = read('docker', ['info', '--format', '{{json .}}']);
    const info = docker ? JSON.parse(docker) : {};

    return {
        processor: os.cpus()[0]?.model ?? null,
        logicalProcessors: os.cpus().length,
        memoryBytes: os.totalmem(),
        operatingSystem: `${os.type()} ${os.release()}`,
        docker: {
            version: info.ServerVersion ?? null,
            operatingSystem: info.OperatingSystem ?? null,
            cpus: info.NCPU ?? null,
            memoryBytes: info.MemTotal ?? null,
        },
        // This file's checkout, the one stack.ps1 builds the stack from, wherever the caller stands.
        commit: read('git', ['rev-parse', 'HEAD'], fileURLToPath(new URL('.', import.meta.url))),
        k6: k6Image(),
    };
}

// k6 runs from the stress override's image, so its tag is the version.
function k6Image() {
    const override = readFileSync(new URL('../../compose/docker-compose.stress.yml', import.meta.url), 'utf8');
    const image = /^ {2}k6:\r?\n(?: {4}.*\r?\n)*? {4}image: (\S+)/m.exec(override);

    if (!image)
        unavailable.push('k6 image: the stress override names none');

    return image ? `k6 ${image[1]}` : null;
}

// The services export their meters once a minute, and a rate needs two
// exports, so a hold shorter than two minutes is read over two minutes that
// reach back into its ramp.
const MINIMUM_RANGE_SECONDS = 120;
const range = `${Math.max(raw.plan.holdSeconds, MINIMUM_RANGE_SECONDS)}s`;

// Every database Query Store records, whose statements each hold is read for.
const queryStoreListing = read('docker', sqlcmdArgs(QUERY_STORE_DATABASES));
const queryStoreDatabases = (queryStoreListing ?? '')
    .split(/\r?\n/).map((line) => line.trim()).filter((name) => /^\w+$/.test(name));

if (queryStoreListing !== null && queryStoreDatabases.length === 0)
    unavailable.push('Query Store: no platform database records it');

// A failed listing, an empty one or every database's read failing is no reading, each recorded under Not
// measured, which the line tells from an empty window.
function statements(window) {
    const sql = queryStoreSql(new Date(window.startMs).toISOString(), new Date(window.endMs).toISOString());
    const reads = queryStoreDatabases
        .map((database) => ({ database, output: read('docker', sqlcmdArgs(sql, database)) }))
        .filter((r) => r.output !== null);

    if (reads.length === 0)
        return { unread: true };

    return queryStoreRow(reads.map((r) => parseQueryStore(r.database, r.output)));
}

const rows = [];

for (const step of raw.steps) {
    const window = { startMs: raw.startedAt + (step.start * 1000), endMs: raw.startedAt + (step.end * 1000) };
    const row = clientRow(step, raw.plan.holdSeconds);

    // A step k6 never reached is left without server readings, which would describe the drain after the run.
    const server = row.requests > 0 ? await serverRow(window, range) : null;

    rows.push({
        ...row,
        window: { start: new Date(window.startMs).toISOString(), end: new Date(window.endMs).toISOString() },
        ...(server ?? {}),
        // Every container, since the first to give up may be any of them; summary.md shows the busiest.
        containers: containerStats(samples, window),
        hostCpu: hostCpu(samples, window),
        sql: sqlDelta(samples, window),
        queryStore: row.requests > 0 ? statements(window) : null,
    });
}

const found = knee(rows);

// A raw.json from before the levels has no level or stop rule, and stopped on errors alone; one from before
// ADR-172 ran its k6 on the stack's machine.
const plan = { level: null, stopRule: 'errors', capped: false, generator: 'local', ...raw.plan };
const ended = ending(rows, plan);

// k6 writes raw.json even when setup() refused, and a run that measured nothing has no knee to be clear of.
const verdict = found
    ? `Knee: step ${found.step} (${found.vus} VUs): ${found.signals.map((s) => `${s.signal}, ${s.detail}`).join('; ')}`
    : rows.some((row) => row.requests > 0)
        ? 'Knee: none; every measured step held.'
        : 'Knee: not measured; no step ran, so the run says nothing about the platform.';

const host = machine();

// A container Compose gave no CPU limit may use every processor Docker has.
const first = firstToGiveUp(rows, givingUpStep(rows, found),
    { cores: host.docker.cpus, holdSeconds: plan.holdSeconds });

const summary = {
    run: { started: new Date(raw.startedAt).toISOString(), ended: new Date(raw.endedAt).toISOString() },
    machine: host,
    plan,
    knee: found,
    verdict,
    firstToGiveUp: { step: null, vus: null, unread: false, services: [], ...first, line: firstLine(first) },
    ended: { ...ended, line: endedLine(ended, plan) },
    steps: rows,
    samplerErrors: [...new Set(samples.flatMap((s) => [s.containersError, s.sqlError]).filter(Boolean))],
    unavailable,
    // Named so that a reader does not take the table's silence for a healthy reading.
    notObservable: [
        'SQL Server plans, and each statement\'s part of an explicit transaction\'s log flush: its COMMIT is ' +
            'its own batch, which Query Store gives to no statement',
    ],
};

writeFileSync(join(out, 'summary.json'), `${JSON.stringify(summary, null, 2)}\n`);
writeFileSync(join(out, 'summary.md'), markdown(summary));

console.log(verdict);
console.log(summary.firstToGiveUp.line);
console.log(summary.ended.line);
console.log(`Summary: ${join(out, 'summary.md')}`);

function ms(value) {
    return value === null || value === undefined ? '—' : `${Math.round(value)}`;
}

function seconds(value) {
    return value === null || value === undefined ? '—' : value.toFixed(1);
}

function percent(value) {
    return value === null || value === undefined ? '—' : `${(value * 100).toFixed(1)}%`;
}

function mib(bytes) {
    return bytes === null || bytes === undefined ? '—' : `${Math.round(bytes / (1024 ** 2))}`;
}

function markdown(s) {
    const m = s.machine;
    const lines = [
        `# Stress run, ${s.run.started}`,
        '',
        'A local run on the Compose stack with ADR-157\'s stress override: it says which part gives out first',
        'and at roughly what load, never what production can carry.',
        '',
        `- Machine: ${m.processor}, ${m.logicalProcessors} logical processors, ${mib(m.memoryBytes)} MiB, ` +
            `${m.operatingSystem}`,
        `- Docker: ${m.docker.version}, ${m.docker.cpus} CPUs and ${mib(m.docker.memoryBytes)} MiB for containers`,
        `- Commit ${m.commit}; ${m.k6}`,
        `- Level ${s.plan.level ?? 'none'}, stop rule ${s.plan.stopRule}` +
            (s.plan.maxUsers ? `, at most ${s.plan.maxUsers} users` : '') +
            `; k6 on ${s.plan.generator === 'remote' ? 'a second machine' : 'this machine'}`,
        `- Staircase ${s.plan.steps.join(', ')} VUs, ${s.plan.rampSeconds} s ramps, ${s.plan.holdSeconds} s holds; ` +
            `${percent(s.plan.orderShare)} of sessions order, over ${s.plan.products} products`,
        '',
        `**${s.verdict}**`,
        '',
        `**${s.firstToGiveUp.line}**`,
        '',
        `**${s.ended.line}**`,
        '',
        '## Client',
        '',
        '| Step | VUs | req/s | p50 ms | p95 ms | p99 ms | errors | 429 | 4xx | 5xx | timeouts | connection |',
        '|---|---|---|---|---|---|---|---|---|---|---|---|',
        ...s.steps.map((r) => `| ${r.step}${r.aborted ? ' (past the abort rate)' : ''}` +
            `${r.kneeStop ? ' (knee stop)' : ''} | ${r.vus} | ` +
            `${r.rps.toFixed(1)} | ` +
            `${ms(r.p50)} | ${ms(r.p95)} | ${ms(r.p99)} | ${percent(r.errorRate)} | ${r.failures['429'] ?? 0} | ` +
            `${r.failures['4xx'] ?? 0} | ${r.failures['5xx'] ?? 0} | ${r.failures.timeout ?? 0} | ` +
            `${r.failures.connection ?? 0} |`),
        '',
        'p95 by operation, ms (`token` is Keycloak, outside the journey\'s figures):',
        '',
        `| Step | ${Object.keys(s.steps[0]?.ops ?? {}).join(' | ')} |`,
        `|---|${Object.keys(s.steps[0]?.ops ?? {}).map(() => '---').join('|')}|`,
        ...s.steps.map((r) => `| ${r.step} | ${Object.values(r.ops).map((op) => ms(op.p95)).join(' | ')} |`),
        '',
        '## Server',
        '',
        '| Step | outbox oldest s, middle → end | queued, middle → end | error queues | delivery lag p95 s | ' +
            'BFF lag p95 s | fulfilment p95 s | confirmed | shipping overdue s | notifications overdue s |',
        '|---|---|---|---|---|---|---|---|---|---|',
        ...s.steps.map((r) => `| ${r.step} | ${seconds(r.outboxMiddle)} → ${seconds(r.outboxEnd)} | ` +
            `${ms(r.queueMiddle)} → ${ms(r.queueEnd)} | ${ms(r.errorQueues)} | ${seconds(r.deliveryLagP95)} | ` +
            `${seconds(r.bffLagP95)} | ${seconds(r.fulfilmentP95)} | ${percent(r.confirmedShare)} | ` +
            `${seconds(r.shippingOverdue)} | ${seconds(r.notificationsOverdue)} |`),
        '',
        'Slowest request types (p95 s), deepest queues, the consumed queues least able to hand a message',
        'straight to a consumer, and the services with the most work waiting for a thread (peak items) and the',
        'most time paused for collections (s), per step:',
        '',
        ...s.steps.flatMap((r) => [
            `- Step ${r.step}: ` + ((r.slowestRequests ?? []).map((q) =>
                `${q.service} ${q.request} ${seconds(q.p95Seconds)}`).join(', ') || '—') +
                '; queues ' + ((r.deepestQueues ?? []).map((q) => `${q.queue} ${ms(q.messages)}`).join(', ') || '—') +
                '; least utilised ' + ((r.leastUtilised ?? []).map((q) =>
                    `${q.queue} ${percent(q.utilisation)}`).join(', ') || '—') +
                '; thread pool ' + ((r.threadPoolQueue ?? []).map((t) => `${t.service} ${ms(t.items)}`).join(', ') ||
                    '—') +
                '; GC ' + ((r.gcPause ?? []).map((g) => `${g.service} ${g.seconds.toFixed(2)}`).join(', ') || '—'),
        ]),
        '',
        '## Resources',
        '',
        '| Step | machine CPU % | busiest containers, CPU % mean / peak, memory MiB peak | SQL deadlocks | ' +
            'lock wait ms | page latch wait ms | log wait ms |',
        '|---|---|---|---|---|---|---|',
        ...s.steps.map((r) => `| ${r.step} | ${ms(r.hostCpu)} | ` +
            (r.containers.slice(0, 4).map((c) =>
                `${c.name} ${ms(c.cpuMean)} / ${ms(c.cpuPeak)}, ${mib(c.memoryPeak)}`).join('; ') || '—') +
            ` | ${ms(r.sql?.deadlocks)} | ${ms(r.sql?.lockWaitMs)} | ${ms(r.sql?.pageLatchWaitMs)} | ` +
            `${ms(r.sql?.logWaitMs)} |`),
        '',
        '## Statements',
        '',
        'Query Store, over the whole intervals inside each hold, which the sampler sets to ' +
            `${QUERY_STORE_INTERVAL_MINUTES} min for the run:`,
        'the statements across every database that took longest, with their CPU and largest waits, then every',
        'statement\'s waits by category. An autocommit statement\'s log flush is its own Tran Log IO; an explicit',
        'transaction\'s COMMIT is its own batch, so its flush is in the log wait column above, not here.',
        '',
        ...s.steps.filter((r) => r.requests > 0).map((r) => queryStoreLine(r.step, r.queryStore)),
        '',
        '## Not measured',
        '',
        ...s.notObservable.map((item) => `- ${item}`),
        ...s.unavailable.map((item) => `- Unavailable: ${item}`),
        ...s.samplerErrors.map((item) => `- Sampler: ${item}`),
        '',
    ];

    return lines.join('\n');
}
