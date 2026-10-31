// Samples the machine's CPU, every container's CPU and memory, and SQL
// Server's deadlock and wait counters, every few seconds until the stress run
// writes raw.json, as JSON lines summarise.mjs reads. All are read from
// outside the stack, so no exporter is added to the stack it measures.
//
//   node deploy/observability/stress/sample.mjs artifacts/stress/<run>
//
// Start it before k6 and leave it running; it stops itself once k6 has
// written its results, once a `stop` file appears beside them, or after
// LIMIT_MS should k6 never finish.
import { execFile } from 'node:child_process';
import { appendFileSync, existsSync, mkdirSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import { join } from 'node:path';
import { promisify } from 'node:util';
import { QUERY_STORE_DATABASES, QUERY_STORE_INTERVAL_MINUTES, parseCpu, parseMemory, sqlcmdArgs } from './report.js';
import { RUN_LIMIT_SECONDS } from './steps.js';

const run = promisify(execFile);

const out = process.argv[2];

if (!out) {
    console.error('usage: node deploy/observability/stress/sample.mjs <output directory>');
    process.exit(2);
}

mkdirSync(out, { recursive: true });

const SAMPLES = join(out, 'samples.jsonl');
const DONE = join(out, 'raw.json');
const STOP = join(out, 'stop');
const QUERY_STORE = join(out, 'query-store.json');

// One `docker stats --no-stream` takes about two seconds, so five is the
// tightest pace that is not one read queued behind the last.
const INTERVAL_MS = 5000;

// The longest run steps.js's plan() takes, and an hour for setup().
const LIMIT_MS = (RUN_LIMIT_SECONDS + (60 * 60)) * 1000;

// Cumulative since the engine started, so a step's figure is its change.
// Deadlocks, then time spent waiting on locks, on page latches (a hot page),
// and on the log flush every commit waits for.
const SQL = [
    'SET NOCOUNT ON;',
    'SELECT',
    "(SELECT cntr_value FROM sys.dm_os_performance_counters WHERE counter_name LIKE 'Number of Deadlocks/sec%'",
    "    AND instance_name = '_Total'),",
    "(SELECT SUM(wait_time_ms) FROM sys.dm_os_wait_stats WHERE wait_type LIKE 'LCK[_]%'),",
    "(SELECT SUM(wait_time_ms) FROM sys.dm_os_wait_stats WHERE wait_type LIKE 'PAGELATCH[_]%'),",
    "(SELECT wait_time_ms FROM sys.dm_os_wait_stats WHERE wait_type = 'WRITELOG');",
].join(' ');

// Each container's Compose service and the cores it may use, read at the
// start and again for a container started since, as the k6 run's is, so
// report.js's serviceCrossings() can name the service a container runs and
// hold its CPU to its own share. A container with no limit may use them all.
async function identities() {
    try {
        const options = { encoding: 'utf8', windowsHide: true };
        const ids = (await run('docker', ['ps', '-q'], options)).stdout.split(/\s+/).filter(Boolean);
        const format = '{{.Name}},{{.HostConfig.NanoCpus}},{{index .Config.Labels "com.docker.compose.service"}}';
        const { stdout } = await run('docker', ['inspect', '--format', format, ...ids], options);

        return new Map(stdout.split('\n').filter((line) => line.trim()).map((line) => {
            const [name, nanoCpus, service] = line.trim().split(',');
            return [name.replace(/^\//, ''), {
                service: service && service !== '<no value>' ? service : null,
                cores: Number(nanoCpus) > 0 ? Number(nanoCpus) / 1e9 : null,
            }];
        }));
    } catch (error) {
        console.error(`sampler: no container identities, ${String(error.message ?? error).split('\n')[0]}`);
        return new Map();
    }
}

let known = await identities();

async function containers() {
    try {
        const { stdout } = await run('docker', ['stats', '--no-stream', '--format', '{{json .}}'], {
            encoding: 'utf8',
            windowsHide: true,
        });
        const stats = stdout.split('\n').filter((line) => line.trim()).map((line) => JSON.parse(line));

        if (stats.some((stat) => !known.has(stat.Name)))
            known = await identities();

        return stats.map((stat) => {
            return {
                name: stat.Name,
                service: known.get(stat.Name)?.service ?? null,
                cores: known.get(stat.Name)?.cores ?? null,
                cpu: parseCpu(stat.CPUPerc),
                memory: parseMemory(stat.MemUsage),
            };
        });
    } catch (error) {
        return { error: String(error.message ?? error).split('\n')[0] };
    }
}

async function sqlcmd(query, database = null) {
    return (await run('docker', sqlcmdArgs(query, database), { encoding: 'utf8', windowsHide: true })).stdout;
}

async function sql() {
    try {
        const [deadlocks, lockWaitMs, pageLatchWaitMs, logWaitMs] = (await sqlcmd(SQL)).trim().split('|').map(Number);
        return { deadlocks, lockWaitMs, pageLatchWaitMs, logWaitMs };
    } catch (error) {
        return { error: String(error.message ?? error).split('\n')[0] };
    }
}

// Query Store keeps a statement's figures per interval, an hour unless set,
// which no step is as short as; so each database's is set to
// QUERY_STORE_INTERVAL_MINUTES for the run, recorded in query-store.json
// before it changes and put back as the sampler stops.
async function narrowQueryStore() {
    const changed = [];

    try {
        const listing = await sqlcmd(QUERY_STORE_DATABASES);

        for (const database of listing.split(/\r?\n/).map((line) => line.trim()).filter((n) => /^\w+$/.test(n))) {
            const options = 'SET NOCOUNT ON; SELECT interval_length_minutes FROM sys.database_query_store_options;';
            const interval = Number((await sqlcmd(options, database)).trim());

            if (!Number.isInteger(interval) || interval === QUERY_STORE_INTERVAL_MINUTES)
                continue;

            changed.push({ database, interval });
            writeFileSync(QUERY_STORE, `${JSON.stringify(changed, null, 2)}\n`);
            await sqlcmd(`ALTER DATABASE [${database}] SET QUERY_STORE ` +
                `(INTERVAL_LENGTH_MINUTES = ${QUERY_STORE_INTERVAL_MINUTES});`);
        }
    } catch (error) {
        console.error(`sampler: Query Store not narrowed, ${String(error.message ?? error).split('\n')[0]}`);
    }

    return changed;
}

async function restoreQueryStore(changed) {
    for (const { database, interval } of changed) {
        try {
            await sqlcmd(`ALTER DATABASE [${database}] SET QUERY_STORE (INTERVAL_LENGTH_MINUTES = ${interval});`);
        } catch (error) {
            console.error(`sampler: ${database}'s Query Store interval left at ${QUERY_STORE_INTERVAL_MINUTES} ` +
                `minutes, not put back to ${interval}: ${String(error.message ?? error).split('\n')[0]}`);
        }
    }
}

// The machine's own CPU, Docker's share and k6's when it runs here, as the
// busy share of every processor's time since the last read: report.js's
// generatorLimit() reads it to tell a saturated workstation from a platform
// giving out.
function processorTimes() {
    return os.cpus().reduce((sum, cpu) => {
        const { idle, ...busy } = cpu.times;
        return { idle: sum.idle + idle, total: sum.total + idle + Object.values(busy).reduce((a, b) => a + b, 0) };
    }, { idle: 0, total: 0 });
}

let lastTimes = processorTimes();

function hostCpu() {
    const now = processorTimes();
    const total = now.total - lastTimes.total;
    const busy = total - (now.idle - lastTimes.idle);
    lastTimes = now;
    return total > 0 ? (busy / total) * 100 : null;
}

const started = Date.now();
const narrowed = await narrowQueryStore();

// A signal ends the loop rather than the process, so the interval is still put back.
let signalled = false;

for (const signal of ['SIGINT', 'SIGTERM', 'SIGBREAK'])
    process.on(signal, () => { signalled = true; });

while (!existsSync(DONE) && !existsSync(STOP) && !signalled && Date.now() - started < LIMIT_MS) {
    const at = Date.now();
    const [stats, counters] = await Promise.all([containers(), sql()]);

    // A failed read is written as one, so a gap in the table says why.
    appendFileSync(SAMPLES, `${JSON.stringify({
        at,
        host: { cpu: hostCpu() },
        containers: Array.isArray(stats) ? stats : [],
        containersError: Array.isArray(stats) ? undefined : stats.error,
        sql: counters.error ? undefined : counters,
        sqlError: counters.error,
    })}\n`);

    await new Promise((resolve) => setTimeout(resolve, Math.max(0, INTERVAL_MS - (Date.now() - at))));
}

await restoreQueryStore(narrowed);

const why = existsSync(DONE) ? 'k6 wrote its results'
    : existsSync(STOP) || signalled ? 'it was asked to' : 'the time limit passed';
console.log(`sampler: stopped, ${why}`);
