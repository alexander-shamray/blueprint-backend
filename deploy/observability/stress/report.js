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

// The backlog readings the knee compares, as Prometheus expressions, so the
// live stop in stress.js and the summary in summarise.mjs read the same ones.
export const DEFAULT_PROM_URL = 'http://127.0.0.1:3000/api/datasources/proxy/uid/prometheus';
export const WORK_FILTER = 'queue!~".+_(error|skipped)"';
export const WORK_QUEUES = `rabbitmq_queue_messages{${WORK_FILTER}}`;
export const OUTBOX_OLDEST = 'max(outbox_oldest_age_seconds)';
export const QUEUED = `sum(${WORK_QUEUES})`;

// THE FIRST TO GIVE UP: at the step that stopped the run, each service's own
// figures, held to the journey's ratios and floors. A container is saturated
// at this share of the cores it may use. A doubled load doubles SQL Server's
// waits, so only four times is contention, and by more than the hold's length,
// one connection blocked throughout: a run that held passed a lower floor.
export const CONTAINER_CPU_LIMIT = 0.9;
export const MEMORY_GROWTH_FLOOR_BYTES = 100 * 1024 ** 2;
export const WAIT_JUMP_RATIO = 4;

// THE GENERATOR: a step at which k6 ran fewer VUs than it asked for, or, with
// k6 on the stack's machine, the machine's CPU stayed this busy, measured the
// workstation rather than the platform. With k6 on a second machine that CPU
// is the platform's alone, so only the VUs are read (ADR-172).
export const HOST_CPU_LIMIT = 90;

// THE CLOCK: the knee stop reads Prometheus at k6's own instants, so a k6
// whose clock is off reads the wrong part of each hold. On a second machine
// it can be (ADR-172); a second is a small share of the shortest hold.
export const CLOCK_SKEW_LIMIT_MS = 1000;

/** How far Prometheus's clock runs ahead of k6's, in ms, from a time() answered between two instants, or null. */
export function clockSkewMs(serverSeconds, sentMs, receivedMs) {
    return Number.isFinite(serverSeconds) ? serverSeconds * 1000 - (sentMs + receivedMs) / 2 : null;
}

// The stress override's own k6 container: the generator, whose CPU counts
// toward the machine's reading above and whose CPU and memory are never a
// service giving up.
export const GENERATOR_SERVICE = 'k6';

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

/**
 * Each container's mean and peak CPU, peak memory, and memory at the hold's
 * middle and end, over a step, busiest first, with its Compose service and
 * the cores it may use, both null where sample.mjs could not read them.
 */
export function containerStats(samples, window) {
    const byName = new Map();
    const middle = (window.startMs + window.endMs) / 2;

    for (const sample of within(samples, window)) {
        for (const container of sample.containers ?? []) {
            const seen = byName.get(container.name) ??
                { name: container.name, service: null, cores: null, cpu: [], memory: [] };
            seen.service = container.service ?? seen.service;
            seen.cores = container.cores ?? seen.cores;
            if (container.cpu !== null) seen.cpu.push(container.cpu);
            if (container.memory !== null) seen.memory.push({ at: sample.at, bytes: container.memory });
            byName.set(container.name, seen);
        }
    }

    return [...byName.values()]
        .map((seen) => ({
            name: seen.name,
            service: seen.service,
            cores: seen.cores,
            cpuMean: seen.cpu.length ? seen.cpu.reduce((a, b) => a + b, 0) / seen.cpu.length : null,
            cpuPeak: seen.cpu.length ? Math.max(...seen.cpu) : null,
            memoryPeak: seen.memory.length ? Math.max(...seen.memory.map((m) => m.bytes)) : null,
            memoryMiddle: seen.memory.find((m) => m.at >= middle)?.bytes ?? null,
            memoryEnd: seen.memory.length ? seen.memory[seen.memory.length - 1].bytes : null,
        }))
        .sort((a, b) => (b.cpuMean ?? -1) - (a.cpuMean ?? -1));
}

/** The machine's mean CPU, as a percentage of all its processors, over a step, or null. */
export function hostCpu(samples, window) {
    const read = within(samples, window).map((sample) => sample.host?.cpu).filter(Number.isFinite);
    return read.length ? read.reduce((a, b) => a + b, 0) / read.length : null;
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
        kneeStop: Boolean(step.kneeStop),
        vusReached: step.vusReached ?? null,
        ops: Object.fromEntries(Object.entries(step.ops ?? {}).map(([op, figures]) =>
            [op, {
                count: figures.count ?? 0,
                p95: measured(figures)['p(95)'] ?? null,
                max: measured(figures).max ?? null,
            }])),
    };
}

/**
 * A step's client half while the run is still going, from k6's REST API
 * (`GET /v1/metrics`'s `data`), which reports no trend's count, so the
 * requests are `http_reqs`'. It carries the fields crossings() reads.
 */
export function liveRow(hold, metrics) {
    const sample = (name) =>
        (metrics ?? []).find((m) => m.id === `${name}{step:${hold.step}}`)?.attributes?.sample ?? {};
    const requests = sample('http_reqs').count ?? 0;

    return {
        step: hold.step,
        vus: hold.vus,
        requests,
        p95: requests > 0 ? sample('stress_journey_duration')['p(95)'] ?? null : null,
        errorRate: sample('stress_failed').rate ?? 0,
    };
}

/**
 * How far a reading went past growing by `ratio` and by more than `floor`,
 * as the lesser of the two multiples, or null when it did not.
 */
function beyond(before, after, ratio, floor) {
    if (!Number.isFinite(before) || !Number.isFinite(after) || !(after - before > floor) || !(after >= before * ratio))
        return null;

    return Math.min(before > 0 ? after / before / ratio : Infinity, floor > 0 ? (after - before) / floor : Infinity);
}

function growing(middle, end, floor) {
    return beyond(middle, end, GROWTH_RATIO, floor) !== null;
}

/** The signals a step crossed, measured against the step before it. */
export function crossings(row, previous) {
    const crossed = [];

    if (row.errorRate > ERROR_RATE_LIMIT)
        crossed.push({ signal: 'errors', detail: `${(row.errorRate * 100).toFixed(1)}% of requests failed` });

    if (previous && beyond(previous.p95, row.p95, P95_JUMP_RATIO, P95_JUMP_FLOOR_MS) !== null) {
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

const SQL_WAITS = { lockWaitMs: 'lock waits', pageLatchWaitMs: 'page latch waits', logWaitMs: 'log flush waits' };

// A message faulted into an error queue, or a deadlock, is work refused
// outright, with no threshold to be a multiple of, so it ranks first.
const OUTRIGHT = Infinity;

/**
 * The service a container runs: the one whose name, as Compose spells it, is
 * the container's Compose service, alone or with `-api` (the gateway's
 * container is `gateway`); otherwise that Compose service, as for `sql`.
 */
export function containerOwner(container, services) {
    const compose = container.service ?? container.name;
    const owner = services.find((service) =>
        [compose, `${compose}-api`].includes(service.toLowerCase().replaceAll('.', '-')));
    return owner ?? compose;
}

/**
 * Each service's own signals at a step, measured against the step before it
 * as crossings() measures the journey's, with how far past its threshold
 * each went. A container that names no limit may use `cores`; SQL Server's
 * waits are judged only with `holdSeconds`, their floor.
 */
export function serviceCrossings(row, previous, { cores = null, holdSeconds = null } = {}) {
    const crossed = [];
    const add = (service, signal, past, detail) => crossed.push({ service, signal, past, detail });

    for (const now of row.requestTypes ?? []) {
        const before = (previous?.requestTypes ?? [])
            .find((r) => r.service === now.service && r.request === now.request);
        const past = beyond(before?.p95Seconds * 1000, now.p95Seconds * 1000, P95_JUMP_RATIO, P95_JUMP_FLOOR_MS);
        if (past !== null) {
            add(now.service, 'p95', past, `${now.request} p95 ${Math.round(before.p95Seconds * 1000)} ms to ` +
                `${Math.round(now.p95Seconds * 1000)} ms`);
        }
    }

    for (const errors of row.serverErrors ?? []) {
        if (errors.share > ERROR_RATE_LIMIT)
            add(errors.service, 'errors', errors.share / ERROR_RATE_LIMIT, `5xx ${(errors.share * 100).toFixed(1)}%`);
    }

    for (const outbox of row.outboxes ?? []) {
        const past = beyond(outbox.middle, outbox.end, GROWTH_RATIO, OUTBOX_GROWTH_FLOOR_SECONDS);
        if (past !== null) {
            add(outbox.service, 'outbox', past,
                `oldest outbox row ${outbox.middle.toFixed(1)} s to ${outbox.end.toFixed(1)} s over the hold`);
        }
    }

    for (const queue of row.queues ?? []) {
        const past = beyond(queue.middle, queue.end, GROWTH_RATIO, QUEUE_GROWTH_FLOOR_MESSAGES);
        if (past !== null) {
            add(queue.service ?? queue.queue, 'queue', past,
                `${queue.queue} ${Math.round(queue.middle)} to ${Math.round(queue.end)} queued over the hold`);
        }
    }

    for (const queue of row.faultedQueues ?? []) {
        const gained = queue.end - (queue.start ?? 0);
        if (gained > 0) {
            add(queue.service ?? queue.queue, 'faulted', OUTRIGHT,
                `${queue.queue} gained ${Math.round(gained)} over the hold`);
        }
    }

    const known = [...new Set(crossed.map((c) => c.service).concat(
        (row.requestTypes ?? []).map((r) => r.service), (row.serverErrors ?? []).map((e) => e.service),
        (row.outboxes ?? []).map((o) => o.service)))];

    for (const container of (row.containers ?? []).filter((c) => c.service !== GENERATOR_SERVICE)) {
        const owner = containerOwner(container, known);
        const usable = container.cores ?? cores;
        const share = usable > 0 && Number.isFinite(container.cpuMean) ? container.cpuMean / (usable * 100) : null;

        if (share !== null && share >= CONTAINER_CPU_LIMIT)
            add(owner, 'cpu', share / CONTAINER_CPU_LIMIT, `CPU ${Math.round(share * 100)}% of ${usable} cores`);

        const past = beyond(container.memoryMiddle, container.memoryEnd, GROWTH_RATIO, MEMORY_GROWTH_FLOOR_BYTES);
        if (past !== null) {
            add(owner, 'memory', past, `memory ${Math.round(container.memoryMiddle / 1024 ** 2)} MiB to ` +
                `${Math.round(container.memoryEnd / 1024 ** 2)} MiB over the hold`);
        }
    }

    if (row.sql?.deadlocks > 0)
        add('sql', 'deadlocks', OUTRIGHT, `${row.sql.deadlocks} deadlocks`);

    for (const [key, name] of Object.entries(SQL_WAITS)) {
        const past = holdSeconds > 0 ? beyond(previous?.sql?.[key], row.sql?.[key], WAIT_JUMP_RATIO, holdSeconds * 1000)
            : null;
        if (past !== null)
            add('sql', 'waits', past, `${name} ${Math.round(previous.sql[key])} ms to ${Math.round(row.sql[key])} ms`);
    }

    return crossed;
}

/**
 * The step to name a service at: the one whose errors passed the abort rate
 * where the error stop fired, and otherwise the knee, or null. It reads the
 * step's own flag, since ending() calls an abort on a saturated machine the
 * generator's.
 */
export function givingUpStep(rows, found) {
    return rows.find((row) => row.aborted)?.step ?? found?.step ?? null;
}

// Whether serviceCrossings() lacked a reading: at the step, any Prometheus
// field, SQL Server's counters or the containers, which the sampler reads;
// at the step before, only the request types and the counters it compares.
function short(row, previous) {
    const failed = (r, field) => (r.serviceReadsFailed ?? []).includes(field);
    const now = (row.serviceReadsFailed ?? []).length > 0 || !row.sql || !(row.containers?.length > 0);
    return now || (previous !== null && (failed(previous, 'requestTypes') || !previous.sql));
}

// Infinity less Infinity is NaN, which sort() reads as a tie.
function furthest(a, b) {
    return b.past - a.past;
}

/**
 * The services whose own signals crossed at `step`, furthest past its own
 * threshold first, each with its signals in the same order, and a `past` of
 * null for work refused outright, which ranks ahead of them all; null when
 * there is no step to name one at. A step the run never reached is skipped,
 * as knee() skips it, so the step before is the last one measured.
 */
export function firstToGiveUp(rows, step, options = {}) {
    const measured = rows.filter((row) => row.requests > 0);
    const at = measured.findIndex((row) => row.step === step);

    if (at < 0)
        return null;

    const byService = new Map();

    for (const crossed of serviceCrossings(measured[at], measured[at - 1] ?? null, options)) {
        const seen = byService.get(crossed.service) ?? [];
        seen.push(crossed);
        byService.set(crossed.service, seen);
    }

    const services = [...byService.entries()]
        .map(([service, signals]) => {
            const ordered = signals.sort(furthest);
            return { service, past: ordered[0].past, signals: ordered };
        })
        .sort((a, b) => furthest(a, b) || a.service.localeCompare(b.service));

    // JSON has no Infinity, so summary.json carries an outright refusal's distance as null.
    const written = (past) => (Number.isFinite(past) ? past : null);

    const unread = short(measured[at], measured[at - 1] ?? null);

    return {
        step,
        vus: measured[at].vus,
        unread,
        services: services.map((s) => ({
            service: s.service,
            past: written(s.past),
            signals: s.signals.map(({ signal, past, detail }) => ({ signal, past: written(past), detail })),
        })),
    };
}

/**
 * The run's machine-readable `First to give up:` line. A step lacking a
 * reading the rules need names what crossed, but never that nothing did.
 */
export function firstLine(first) {
    if (!first)
        return 'First to give up: not judged; no step was the knee or past the abort rate';

    if (first.services.length === 0 && first.unread)
        return `First to give up: not judged; step ${first.step}'s service figures were not all read`;

    if (first.services.length === 0)
        return `First to give up: none named; step ${first.step} crossed only journey-wide signals`;

    return `First to give up: ${first.services.map((s) =>
        `${s.service}: ${s.signals.map((signal) => signal.detail).join('; ')}`).join(' | ')}`;
}

/** Why a step measured the workstation rather than the platform, or null when it did not. */
export function generatorLimit(row, generator = 'local') {
    if (row.vusReached > 0 && row.vusReached < row.vus)
        return `k6 ran ${row.vusReached} of ${row.vus} VUs`;

    if (generator === 'local' && row.hostCpu >= HOST_CPU_LIMIT)
        return `the machine's CPU at ${Math.round(row.hostCpu)}% over the hold`;

    return null;
}

/**
 * Why the run ended, read at the first step a stop rule flagged, or else the
 * last that carried requests: the judge flags a step after its hold, so the
 * next step's hold may begin before the abort lands. The generator
 * comes first, when that step measured the workstation, whatever stopped it;
 * then the knee stop, the error abort, a run cut short with no rule firing,
 * or the top of a staircase that held, a level's being the cap's.
 */
export function ending(rows, plan) {
    const measured = rows.filter((row) => row.requests > 0);
    const at = measured.find((row) => row.kneeStop || row.aborted) ?? measured[measured.length - 1];

    if (!at)
        return { reason: 'not measured', step: null, vus: null, detail: null };

    const generator = generatorLimit(at, plan.generator);
    const reason = generator ? 'generator'
        : at.kneeStop ? 'knee'
            : at.aborted ? 'errors'
                : at.step < rows[rows.length - 1].step ? 'interrupted'
                    : plan.capped ? 'cap' : 'held';

    return { reason, step: at.step, vus: at.vus, detail: generator };
}

/** The run's machine-readable `Ended:` line. */
export function endedLine(ended, plan) {
    const where = ended.step === null ? '' : ` at step ${ended.step} (${ended.vus} VUs)`;
    const detail = ended.detail ? `: ${ended.detail}` : '';
    return `Ended: ${ended.reason}${where}${detail}; level ${plan.level ?? 'none'}, stop rule ${plan.stopRule}`;
}

// THE STATEMENTS: what each database's statements spent over a hold, from
// Query Store, whose interval sample.mjs sets to QUERY_STORE_INTERVAL_MINUTES
// for the run. An autocommit statement's log flush is its own Tran Log IO; an
// explicit transaction's COMMIT is its own batch, so its flush stays in the
// instance-wide log wait above.
export const QUERY_STORE_INTERVAL_MINUTES = 1;
export const QUERY_STORE_TOP = 5;
const QUERY_STORE_READ = 20;
const STATEMENT_CHARS = 90;

// The Compose project's SQL container, named as the README's blocks name it.
export const SQL_CONTAINER = 'commerce-sql-1';

// The platform's databases Query Store records; model carries it too, as the template of the next one.
export const QUERY_STORE_DATABASES =
    'SET NOCOUNT ON; SELECT name FROM sys.databases WHERE is_query_store_on = 1 AND database_id > 4;';

const ISO = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z$/;
const DATABASE = /^\w+$/;

/**
 * sqlcmd's arguments for `docker exec`, the password read inside the container as its healthcheck reads it,
 * and -b so that a T-SQL error is a failed exit rather than a Msg line on a successful one.
 */
export function sqlcmdArgs(query, database = null) {
    if (database !== null && !DATABASE.test(database))
        throw new Error(`not a database name sqlcmd is handed: "${database}"`);

    return [
        'exec', SQL_CONTAINER, 'sh', '-c', 'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" exec "$@"', 'sh',
        '/opt/mssql-tools18/bin/sqlcmd', '-S', 'localhost', '-U', 'sa', '-C', '-b', '-h', '-1', '-W', '-s', '|',
        ...(database === null ? [] : ['-d', database]),
        '-Q', query,
    ];
}

/**
 * Query Store's reading of one database over the whole intervals inside a
 * window: the statements that took longest, one `S` line each, then every
 * statement's waits by category, one `W` line each.
 */
export function queryStoreSql(startIso, endIso) {
    if (!ISO.test(startIso) || !ISO.test(endIso))
        throw new Error(`Query Store windows are ISO instants in UTC, not "${startIso}" and "${endIso}"`);

    // Without its declarations, which end at the first ')' a letter follows ("datetime2(7))UPDATE"), and as a
    // bounded type: sqlcmd cuts an nvarchar(max) at 256 characters, inside a long EF declaration list.
    const body = "CASE WHEN x.query_sql_text LIKE '(@%' AND PATINDEX('%)[A-Za-z]%', x.query_sql_text) > 0 " +
        "THEN SUBSTRING(x.query_sql_text, PATINDEX('%)[A-Za-z]%', x.query_sql_text) + 1, 400) " +
        'ELSE x.query_sql_text END';
    const text = `CAST(REPLACE(REPLACE(REPLACE(${body}, CHAR(13), ' '), CHAR(10), ' '), '|', '/') AS nvarchar(200))`;

    return [
        'SET NOCOUNT ON;',
        `DECLARE @from datetimeoffset = '${startIso}', @to datetimeoffset = '${endIso}';`,
        'DECLARE @top TABLE (query_id bigint PRIMARY KEY, execs bigint, cpu_ms bigint, duration_ms bigint);',
        'INSERT INTO @top',
        `SELECT TOP (${QUERY_STORE_READ}) p.query_id, SUM(rs.count_executions),`,
        '    CAST(SUM(rs.avg_cpu_time * rs.count_executions) / 1000 AS bigint),',
        '    CAST(SUM(rs.avg_duration * rs.count_executions) / 1000 AS bigint)',
        'FROM sys.query_store_runtime_stats AS rs',
        'INNER JOIN sys.query_store_runtime_stats_interval AS i',
        '    ON i.runtime_stats_interval_id = rs.runtime_stats_interval_id',
        'INNER JOIN sys.query_store_plan AS p',
        '    ON p.plan_id = rs.plan_id',
        'WHERE i.start_time >= @from AND i.end_time <= @to',
        'GROUP BY p.query_id',
        'ORDER BY SUM(rs.avg_duration * rs.count_executions) DESC;',
        `SELECT 'S', t.query_id, t.execs, t.cpu_ms, t.duration_ms, ${text}`,
        'FROM @top AS t',
        'INNER JOIN sys.query_store_query AS q',
        '    ON q.query_id = t.query_id',
        'INNER JOIN sys.query_store_query_text AS x',
        '    ON x.query_text_id = q.query_text_id;',
        "SELECT 'W', p.query_id, ws.wait_category_desc, CAST(SUM(ws.total_query_wait_time_ms) AS bigint)",
        'FROM sys.query_store_wait_stats AS ws',
        'INNER JOIN sys.query_store_runtime_stats_interval AS i',
        '    ON i.runtime_stats_interval_id = ws.runtime_stats_interval_id',
        'INNER JOIN sys.query_store_plan AS p',
        '    ON p.plan_id = ws.plan_id',
        'WHERE i.start_time >= @from AND i.end_time <= @to',
        'GROUP BY p.query_id, ws.wait_category_desc;',
    ].join('\n');
}

// A parameterised statement's text opens with its declarations, "(@p0 int,@p1 nvarchar(4000))UPDATE ...".
const DECLARATIONS = /^\(@.*?\)(?=\s*(?:SELECT|INSERT|UPDATE|DELETE|WITH|MERGE|SET|DECLARE)\b)/is;

/** A statement's text without its declarations, cut to what one summary line holds and safe in a code span. */
export function statementText(text) {
    const body = String(text ?? '').replace(DECLARATIONS, '').replace(/`/g, "'").replace(/\s+/g, ' ').trim();
    return body.length > STATEMENT_CHARS ? `${body.slice(0, STATEMENT_CHARS - 1)}…` : body;
}

/** One database's `S` and `W` lines as its statements, each with its waits, and its waits by category. */
export function parseQueryStore(database, output) {
    const statements = new Map();
    const waits = [];

    for (const line of String(output ?? '').split(/\r?\n/)) {
        const fields = line.split('|');

        if (fields[0] === 'S' && fields.length >= 6) {
            statements.set(fields[1], {
                database,
                id: Number(fields[1]),
                executions: Number(fields[2]),
                cpuMs: Number(fields[3]),
                durationMs: Number(fields[4]),
                text: statementText(fields.slice(5).join('|')),
                waits: [],
            });
        } else if (fields[0] === 'W' && fields.length === 4) {
            waits.push({ id: fields[1], category: fields[2], ms: Number(fields[3]) });
        }
    }

    for (const wait of waits)
        statements.get(wait.id)?.waits.push({ category: wait.category, ms: wait.ms });

    return { statements: [...statements.values()], waits: waits.map(({ category, ms }) => ({ category, ms })) };
}

/**
 * A step's statements across every database: the QUERY_STORE_TOP that took
 * longest, each with its largest waits, and every statement's waits by
 * category; null when no whole interval lay inside the hold.
 */
export function queryStoreRow(databases) {
    const statements = databases.flatMap((d) => d.statements);
    const totals = new Map();

    for (const wait of databases.flatMap((d) => d.waits))
        totals.set(wait.category, (totals.get(wait.category) ?? 0) + wait.ms);

    if (statements.length === 0 && totals.size === 0)
        return null;

    const byMs = (a, b) => b.ms - a.ms;

    return {
        statements: statements
            .sort((a, b) => b.durationMs - a.durationMs)
            .slice(0, QUERY_STORE_TOP)
            .map((s) => ({ ...s, waits: [...s.waits].sort(byMs).slice(0, 3) })),
        waits: [...totals.entries()].map(([category, ms]) => ({ category, ms })).sort(byMs),
    };
}

/** A step's line of summary.md's statements section; `{ unread: true }` is a read that failed, null an empty one. */
export function queryStoreLine(step, row) {
    if (row?.unread)
        return `- Step ${step}: Query Store not read; Not measured says why`;

    if (!row)
        return `- Step ${step}: no whole Query Store interval inside the hold`;

    const s = (ms) => (ms / 1000).toFixed(1);
    const statements = row.statements.map((st) =>
        `${st.database} \`${st.text}\` ${s(st.durationMs)} s in ${st.executions} runs, CPU ${s(st.cpuMs)} s` +
        st.waits.map((w) => `, ${w.category} ${s(w.ms)} s`).join(''));

    return `- Step ${step}: ${statements.join('; ') || 'no statement'}; ` +
        `waits ${row.waits.map((w) => `${w.category} ${s(w.ms)} s`).join(', ') || 'none'}`;
}
