// Turns one stress run into its per-step table: k6's client figures from
// raw.json, the server's from Prometheus at each step's hold, the resources
// from sample.mjs's samples, and the machine they all describe. Writes
// summary.json and summary.md beside them and prints the knee. It reports
// and never gates, so a reading it cannot make is recorded as unavailable.
//
//   node deploy/observability/stress/summarise.mjs artifacts/stress/<run>
//
// PROM_URL defaults to Grafana's datasource proxy on 127.0.0.1, the route
// the SLO run reads Prometheus by.
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import { join } from 'node:path';
import { clientRow, containerStats, knee, sqlDelta } from './report.js';

const out = process.argv[2];

if (!out || !existsSync(join(out, 'raw.json'))) {
    console.error('usage: node deploy/observability/stress/summarise.mjs <the directory stress.js wrote raw.json to>');
    process.exit(2);
}

const PROM_URL = process.env.PROM_URL || 'http://127.0.0.1:3000/api/datasources/proxy/uid/prometheus';

const raw = JSON.parse(readFileSync(join(out, 'raw.json'), 'utf8'));
const samplesPath = join(out, 'samples.jsonl');
const samples = existsSync(samplesPath)
    ? readFileSync(samplesPath, 'utf8').split('\n').filter((line) => line.trim()).map((line) => JSON.parse(line))
    : [];

const unavailable = [];

// One instant query evaluated at `atMs`, so a reading made after the run
// still describes the step: Prometheus answers for any time it holds.
async function query(name, expr, atMs) {
    const url = `${PROM_URL}/api/v1/query?query=${encodeURIComponent(expr)}&time=${atMs / 1000}`;

    try {
        const response = await fetch(url);

        if (!response.ok) {
            unavailable.push(`${name}: Prometheus answered ${response.status}`);
            return [];
        }

        return (await response.json()).data.result.map((series) => ({
            labels: series.metric,
            value: Number(series.value[1]),
        })).filter((series) => Number.isFinite(series.value));
    } catch (error) {
        unavailable.push(`${name}: ${error.message}`);
        return [];
    }
}

async function scalar(name, expr, atMs) {
    const [series] = await query(name, expr, atMs);
    return series ? series.value : null;
}

const WORK_QUEUES = 'rabbitmq_queue_messages{queue!~".+_(error|skipped)"}';

async function serverRow(window, range) {
    const end = window.endMs;
    const middle = (window.startMs + window.endMs) / 2;

    const [requests, outboxes, queues] = await Promise.all([
        query('request.duration p95', `topk(5, histogram_quantile(0.95, sum by (le, service_name, request) ` +
            `(rate(request_duration_seconds_bucket[${range}]))))`, end),
        query('outbox oldest', `max by (service_name) (max_over_time(outbox_oldest_age_seconds[${range}]))`, end),
        query('queue depth', `topk(5, max by (queue) (max_over_time(${WORK_QUEUES}[${range}])))`, end),
    ]);

    const placed = await scalar('orders.placed', `sum(increase(orders_placed_total[${range}]))`, end);
    const confirmed = await scalar(
        'orders confirmed', `sum(increase(orders_fulfilment_duration_seconds_count[${range}]))`, end);

    return {
        slowestRequests: requests.map((s) => ({
            service: s.labels.service_name, request: s.labels.request, p95Seconds: s.value,
        })),
        outboxOldest: outboxes.map((s) => ({ service: s.labels.service_name, seconds: s.value })),
        deepestQueues: queues.map((s) => ({ queue: s.labels.queue, messages: s.value })),
        outboxMiddle: await scalar('outbox oldest, middle', 'max(outbox_oldest_age_seconds)', middle),
        outboxEnd: await scalar('outbox oldest, end', 'max(outbox_oldest_age_seconds)', end),
        queueMiddle: await scalar('queued, middle', `sum(${WORK_QUEUES})`, middle),
        queueEnd: await scalar('queued, end', `sum(${WORK_QUEUES})`, end),
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
        // Common.Web's runtime meter: work queued for a thread, and the time collections paused the service.
        threadPoolQueue: (await query('thread pool queue', 'topk(3, max by (service_name) ' +
            `(max_over_time(dotnet_thread_pool_queue_length_total[${range}])))`, end))
            .map((s) => ({ service: s.labels.service_name, items: s.value })),
        gcPause: (await query('GC pause', 'topk(3, sum by (service_name) ' +
            `(increase(dotnet_gc_pause_time_seconds_total[${range}])))`, end))
            .map((s) => ({ service: s.labels.service_name, seconds: s.value })),
    };
}

function read(command, args) {
    try {
        return execFileSync(command, args, { encoding: 'utf8', windowsHide: true }).trim();
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
        commit: read('git', ['rev-parse', 'HEAD']),
        k6: read('k6', ['version']),
    };
}

// The services export their meters once a minute, and a rate needs two
// exports, so a hold shorter than two minutes is read over two minutes that
// reach back into its ramp. The default hold is three.
const MINIMUM_RANGE_SECONDS = 120;
const range = `${Math.max(raw.plan.holdSeconds, MINIMUM_RANGE_SECONDS)}s`;

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
        containers: containerStats(samples, window).slice(0, 8),
        sql: sqlDelta(samples, window),
    });
}

const found = knee(rows);

// k6 writes raw.json even when setup() refused, and a run that measured nothing has no knee to be clear of.
const verdict = found
    ? `Knee: step ${found.step} (${found.vus} VUs): ${found.signals.map((s) => `${s.signal}, ${s.detail}`).join('; ')}`
    : rows.some((row) => row.requests > 0)
        ? 'Knee: none; every measured step held.'
        : 'Knee: not measured; no step ran, so the run says nothing about the platform.';

const summary = {
    run: { started: new Date(raw.startedAt).toISOString(), ended: new Date(raw.endedAt).toISOString() },
    machine: machine(),
    plan: raw.plan,
    knee: found,
    verdict,
    steps: rows,
    samplerErrors: [...new Set(samples.flatMap((s) => [s.containersError, s.sqlError]).filter(Boolean))],
    unavailable,
    // Named so that a reader does not take the table's silence for a healthy reading.
    notObservable: [
        'SQL Server per-query waits and plans: only the instance-wide counters sample.mjs reads',
        'RabbitMQ consumer utilisation: the broker publishes depths, not how busy each consumer is',
    ],
};

writeFileSync(join(out, 'summary.json'), `${JSON.stringify(summary, null, 2)}\n`);
writeFileSync(join(out, 'summary.md'), markdown(summary));

console.log(verdict);
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
        `- Staircase ${s.plan.steps.join(', ')} VUs, ${s.plan.rampSeconds} s ramps, ${s.plan.holdSeconds} s holds; ` +
            `${percent(s.plan.orderShare)} of sessions order, over ${s.plan.products} products`,
        '',
        `**${s.verdict}**`,
        '',
        '## Client',
        '',
        '| Step | VUs | req/s | p50 ms | p95 ms | p99 ms | errors | 429 | 4xx | 5xx | timeouts | connection |',
        '|---|---|---|---|---|---|---|---|---|---|---|---|',
        ...s.steps.map((r) => `| ${r.step}${r.aborted ? ' (past the abort rate)' : ''} | ${r.vus} | ` +
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
        'Slowest request types (p95 s), deepest queues, and the services with the most work waiting for a',
        'thread (peak items) and the most time paused for collections (s), per step:',
        '',
        ...s.steps.flatMap((r) => [
            `- Step ${r.step}: ` + ((r.slowestRequests ?? []).map((q) =>
                `${q.service} ${q.request} ${seconds(q.p95Seconds)}`).join(', ') || '—') +
                '; queues ' + ((r.deepestQueues ?? []).map((q) => `${q.queue} ${ms(q.messages)}`).join(', ') || '—') +
                '; thread pool ' + ((r.threadPoolQueue ?? []).map((t) => `${t.service} ${ms(t.items)}`).join(', ') ||
                    '—') +
                '; GC ' + ((r.gcPause ?? []).map((g) => `${g.service} ${g.seconds.toFixed(2)}`).join(', ') || '—'),
        ]),
        '',
        '## Resources',
        '',
        '| Step | busiest containers, CPU % mean / peak, memory MiB peak | SQL deadlocks | lock wait ms | ' +
            'page latch wait ms | log wait ms |',
        '|---|---|---|---|---|---|',
        ...s.steps.map((r) => `| ${r.step} | ` +
            (r.containers.slice(0, 4).map((c) =>
                `${c.name} ${ms(c.cpuMean)} / ${ms(c.cpuPeak)}, ${mib(c.memoryPeak)}`).join('; ') || '—') +
            ` | ${ms(r.sql?.deadlocks)} | ${ms(r.sql?.lockWaitMs)} | ${ms(r.sql?.pageLatchWaitMs)} | ` +
            `${ms(r.sql?.logWaitMs)} |`),
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
