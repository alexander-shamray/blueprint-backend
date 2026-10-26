// The stress run of deploy/observability/README.md: distinct signed-in users
// walk the domain's typical journey through the gateway, and their number
// rises in steps until the platform gives out. It is NOT the SLO run and NOT
// a gate: it asserts nothing, and stops early only once a whole step has
// clearly broken. summarise.mjs turns what it writes into the per-step table
// and names the knee.
//
// It runs against the Compose stack started with
// deploy/compose/docker-compose.stress.yml (ADR-157), whose relaxed limiter
// and unpaced workers are what make the services, not the limiter or a third
// party's modelled rate, the thing measured. Against the baseline stack every
// step past the first is a wall of 429s.
//
//   # From the environment, never `-e`, for slo.js's reason.
//   export KEYCLOAK_ADMIN_PASSWORD=admin
//
//   k6 run \
//     -e BASE_URL=http://localhost:5000 \
//     -e KEYCLOAK_URL=http://localhost:8080 \
//     -e STRESS_PRODUCTS=<ids with a row in ordering.ProductPrices, comma-separated> \
//     -e STRESS_CURRENCY=<their currency> \
//     -e STRESS_OUT=artifacts/stress/<run> \
//     deploy/observability/stress/stress.js
//
// Optional: STRESS_STEPS ("10,50,100,200,400"), STRESS_RAMP_SECONDS (30),
// STRESS_HOLD_SECONDS (180), STRESS_ABORT_ERROR_RATE (0.2, or 0 for never).
import http from 'k6/http';
import exec from 'k6/execution';
import { fail, sleep } from 'k6';
import { Counter, Rate, Trend } from 'k6/metrics';
import { randomBytes } from 'k6/crypto';
import {
    DEFAULT_HOLD_SECONDS,
    DEFAULT_RAMP_SECONDS,
    maxOrders,
    parseSeconds,
    parseSteps,
    stages,
    stepAt,
    windows,
} from './steps.js';

const BASE_URL = __ENV.BASE_URL;
const KEYCLOAK_URL = __ENV.KEYCLOAK_URL;
const OUT = __ENV.STRESS_OUT;
const PRODUCTS = (__ENV.STRESS_PRODUCTS || '').split(',').map((id) => id.trim()).filter(Boolean);

const STEPS = parseSteps(__ENV.STRESS_STEPS);
const RAMP_SECONDS = parseSeconds(__ENV.STRESS_RAMP_SECONDS, DEFAULT_RAMP_SECONDS, 'STRESS_RAMP_SECONDS');
const HOLD_SECONDS = parseSeconds(__ENV.STRESS_HOLD_SECONDS, DEFAULT_HOLD_SECONDS, 'STRESS_HOLD_SECONDS');
const HOLDS = windows(STEPS, RAMP_SECONDS, HOLD_SECONDS);

// Unset is the default; an explicit 0 never aborts.
const ABORT_ERROR_RATE = __ENV.STRESS_ABORT_ERROR_RATE === undefined || __ENV.STRESS_ABORT_ERROR_RATE === ''
    ? 0.2
    : Number(__ENV.STRESS_ABORT_ERROR_RATE);

// THE JOURNEY'S SHAPE. A quarter of sessions order, the rest browse and
// quote: a shop converts far fewer, but every order is the saga, the outbox,
// four services' consumers and the workers, so this share keeps the write path
// loaded at every step instead of only at the last. Users pause one to five
// seconds between steps, as a person reading a page does; without the pause
// each VU is a tight loop and the VU count stops meaning users.
const ORDER_SHARE = 0.25;
const THINK_MIN_SECONDS = 1;
const THINK_MAX_SECONDS = 5;

// At most three product pages a session, chosen from the listing just read.
const PRODUCT_VIEWS_MAX = 3;

// The shortest way to an order: the listing, the address and the quote, each
// followed by the shortest pause; a listing that failed or came back empty
// leaves no product page. maxOrders() reads it, to size the stock setup()
// checks for.
const SHORTEST_SESSION_SECONDS = 3 * THINK_MIN_SECONDS;

// Orders are spread over every product STRESS_PRODUCTS names, so the run
// loads the saga and the queues rather than one stock row's lock. One id
// there is the hot-row variant, which stresses Inventory's reservation path.
const STOCK = 100000;

// Below the gateway's own deadline would cut a request the edge was about to
// answer with a 504; above it, the 504 arrives first and is counted as a 5xx.
const REQUEST_TIMEOUT = '30s';

// Refreshed this far ahead of expiry, so a request never carries a token that
// lapses in flight; the realm's access token lives five minutes.
const TOKEN_MARGIN_SECONDS = 30;

const OPS = ['listing', 'product', 'address_read', 'address_save', 'quote', 'order', 'my_orders', 'token'];
const FAILURE_KINDS = ['429', '4xx', '5xx', 'timeout', 'connection'];

// Each journey request's duration, Keycloak's token calls left out, so a slow
// identity provider is reported apart from the platform it fronts.
const journeyDuration = new Trend('stress_journey_duration', true);
const failures = new Counter('stress_failures');
const failed = new Rate('stress_failed');

// handleSummary reports only the submetrics a threshold names, so every step's
// figures are named here; each passes on any value, and only the per-step
// error rate can stop the run. Its evaluation waits for the step's hold to
// end, so the step is judged rather than its first seconds. k6 counts that
// wait from the test's start, setup() included, so the judgement can begin up
// to setup()'s length before the hold ends, over the hold's requests so far.
function thresholds() {
    const named = {};

    for (const hold of HOLDS) {
        named[`stress_journey_duration{step:${hold.step}}`] = ['max>=0'];

        for (const op of OPS)
            named[`http_req_duration{step:${hold.step},op:${op}}`] = ['max>=0'];

        for (const kind of FAILURE_KINDS)
            named[`stress_failures{step:${hold.step},kind:${kind}}`] = ['count>=0'];

        named[`stress_failed{step:${hold.step}}`] = ABORT_ERROR_RATE > 0
            ? [{ threshold: `rate<${ABORT_ERROR_RATE}`, abortOnFail: true, delayAbortEval: `${hold.end}s` }]
            : ['rate>=0'];
    }

    return named;
}

export const options = {
    scenarios: {
        journey: {
            executor: 'ramping-vus',
            startVUs: 0,
            stages: stages(STEPS, RAMP_SECONDS, HOLD_SECONDS),
            // A step-down lets each session finish its request rather than cut it.
            gracefulRampDown: '10s',
            gracefulStop: '10s',
            exec: 'session',
        },
    },
    thresholds: thresholds(),
    summaryTrendStats: ['avg', 'min', 'med', 'max', 'p(95)', 'p(99)', 'count'],
    // Users are made and stock is set before the first VU starts.
    setupTimeout: '10m',
};

const USERS = STEPS[STEPS.length - 1];

function userName(index) {
    return `stress-${String(index).padStart(4, '0')}`;
}

export function setup() {
    // A run with no product or no output directory measures nothing it can report.
    const missing = ['BASE_URL', 'KEYCLOAK_URL', 'KEYCLOAK_ADMIN_PASSWORD', 'STRESS_CURRENCY', 'STRESS_OUT']
        .filter((name) => !__ENV[name]);

    if (PRODUCTS.length === 0)
        missing.push('STRESS_PRODUCTS');

    if (missing.length > 0)
        fail(`Stress run is not configured: missing ${missing.join(', ')}`);

    const realm = `${KEYCLOAK_URL}/admin/realms/commerce`;
    const admin = adminHeaders();

    // The client every user signs in through, and whose service account
    // restocks. Made here rather than in realm-export.json, as the SLO run's
    // is: a deployment's realm must never carry a password-grant client.
    const client = ensureClient(realm, admin);
    const api = one(http.get(`${realm}/clients?clientId=commerce-api`, admin), 'commerce-api client');
    const ordersWrite = roleOf(realm, admin, api.id, 'orders:write');

    // A fresh password a run, kept in memory: nothing written to disk holds it.
    const password = hex(randomBytes(24));

    // A fresh admin token a batch: the master realm's lasts a minute, and four hundred users take longer.
    for (let first = 1; first <= USERS; first += 25)
        ensureUsers(realm, adminHeaders(), api.id, ordersWrite, password, first, Math.min(first + 24, USERS));

    const secret = http.get(`${realm}/clients/${client.id}/client-secret`, adminHeaders()).json('value');
    const service = tokenRequest({ grant_type: 'client_credentials', client_id: 'stress-run', client_secret: secret });

    if (service.status !== 200)
        fail(`service-account token returned ${service.status}: ${service.body}`);

    restock(service.json('access_token'));

    return { password, secret, startedAt: Date.now() };
}

function adminHeaders() {
    const response = http.post(`${KEYCLOAK_URL}/realms/master/protocol/openid-connect/token`, {
        grant_type: 'password',
        client_id: 'admin-cli',
        username: 'admin',
        password: __ENV.KEYCLOAK_ADMIN_PASSWORD,
    });

    if (response.status !== 200)
        fail(`Keycloak admin token returned ${response.status}`);

    return {
        headers: {
            Authorization: `Bearer ${response.json('access_token')}`,
            'Content-Type': 'application/json',
        },
    };
}

function one(response, what) {
    if (response.status !== 200)
        fail(`reading ${what} returned ${response.status}: ${response.body}`);

    const found = response.json();

    if (!Array.isArray(found) || found.length !== 1)
        fail(`expected one ${what}, found ${Array.isArray(found) ? found.length : 'none'}`);

    return found[0];
}

function roleOf(realm, admin, clientId, name) {
    const response = http.get(`${realm}/clients/${clientId}/roles/${encodeURIComponent(name)}`, admin);

    if (response.status !== 200)
        fail(`reading role ${name} returned ${response.status}`);

    return { id: response.json('id'), name: response.json('name') };
}

// Confidential, so its service account can hold inventory:admin, and with
// direct grants on, so a VU signs in with a password as no browser does: the
// run measures the platform behind the token, not a login page.
function ensureClient(realm, admin) {
    const query = `${realm}/clients?clientId=stress-run`;

    if ((http.get(query, admin).json() || []).length === 0) {
        const created = http.post(`${realm}/clients`, JSON.stringify({
            clientId: 'stress-run',
            enabled: true,
            publicClient: false,
            serviceAccountsEnabled: true,
            standardFlowEnabled: false,
            directAccessGrantsEnabled: true,
            // `basic` carries the mapper that writes `sub`; naming the list replaces the realm's.
            defaultClientScopes: ['basic', 'commerce-api'],
        }), admin);

        if (created.status !== 201)
            fail(`creating stress-run returned ${created.status}: ${created.body}`);
    }

    const client = one(http.get(query, admin), 'stress-run client');
    const account = http.get(`${realm}/clients/${client.id}/service-account-user`, admin).json('id');
    const api = one(http.get(`${realm}/clients?clientId=commerce-api`, admin), 'commerce-api client');

    // Granting a held role changes nothing, so this is safe on every run.
    const granted = http.post(
        `${realm}/users/${account}/role-mappings/clients/${api.id}`,
        JSON.stringify([roleOf(realm, admin, api.id, 'inventory:admin')]),
        admin);

    if (granted.status !== 204)
        fail(`granting inventory:admin returned ${granted.status}`);

    return client;
}

// Made once, then found: a second run resets each password and grants what is
// already held. A Keycloak user's id is a GUID, which is the `sub` an order's
// owner is bound from.
function ensureUsers(realm, admin, apiId, role, password, first, last) {
    const names = [];
    for (let i = first; i <= last; i++)
        names.push(userName(i));

    const found = http.batch(names.map((name) =>
        ['GET', `${realm}/users?username=${name}&exact=true`, null, admin]));

    const ids = names.map((name, i) => {
        const match = (found[i].json() || []).find((user) => user.username === name);
        if (match)
            return match.id;

        const created = http.post(`${realm}/users`, JSON.stringify({
            username: name,
            enabled: true,
            email: `${name}@stress.invalid`,
            emailVerified: true,
            firstName: 'Stress',
            lastName: name,
            requiredActions: [],
        }), admin);

        if (created.status !== 201)
            fail(`creating ${name} returned ${created.status}: ${created.body}`);

        return created.headers.Location.split('/').pop();
    });

    const set = http.batch(ids.flatMap((id) => [
        ['PUT', `${realm}/users/${id}/reset-password`,
            JSON.stringify({ type: 'password', value: password, temporary: false }), admin],
        ['POST', `${realm}/users/${id}/role-mappings/clients/${apiId}`, JSON.stringify([role]), admin],
    ]));

    const refused = set.filter((response) => response.status !== 204);

    if (refused.length > 0)
        fail(`preparing users ${first}-${last}: ${refused.length} calls refused, the first ${refused[0].status}`);
}

// Each product must cover its share of every order the staircase could
// place, or the saga cancels orders out_of_stock after their 200. A product
// short of it is set to an absolute count, so a repeat sets the same one; one
// that already covers it is left as it is.
function restock(token) {
    const auth = { headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' } };
    const needed = Math.ceil(
        maxOrders(STEPS, RAMP_SECONDS, HOLD_SECONDS, SHORTEST_SESSION_SECONDS, ORDER_SHARE) / PRODUCTS.length);
    const available = (product) =>
        Number(http.get(`${BASE_URL}/api/v1/inventory/stock/${product}`, auth).json('available'));

    for (const product of PRODUCTS) {
        if (available(product) >= needed)
            continue;

        const put = http.put(
            `${BASE_URL}/api/v1/inventory/stock/${product}`, JSON.stringify({ onHand: STOCK }), auth);

        // A command with no value answers 204 (§10.5's ToHttpResult); the read below is the real check.
        if (put.status !== 204)
            fail(`restocking ${product} returned ${put.status}: ${put.body}`);

        const after = available(product);

        if (!(after >= needed))
            fail(`product ${product} has ${after} available and the staircase can order ${needed} of it`);
    }
}

// Per VU, since each VU has its own runtime: the user's tokens and when they lapse.
let signedIn = null;

function tokenRequest(form) {
    return http.post(`${KEYCLOAK_URL}/realms/commerce/protocol/openid-connect/token`, form, {
        tags: { op: 'token' },
        timeout: REQUEST_TIMEOUT,
    });
}

function bearer(data) {
    const now = Date.now() / 1000;

    if (signedIn && signedIn.accessUntil - TOKEN_MARGIN_SECONDS > now)
        return signedIn.access;

    const user = userName(exec.vu.idInTest);
    const refreshable = signedIn && signedIn.refreshUntil - TOKEN_MARGIN_SECONDS > now;

    const response = tagged('token', () => tokenRequest(refreshable
        ? {
            grant_type: 'refresh_token',
            client_id: 'stress-run',
            client_secret: data.secret,
            refresh_token: signedIn.refresh,
        }
        : {
            grant_type: 'password',
            client_id: 'stress-run',
            client_secret: data.secret,
            username: user,
            password: data.password,
        }));

    if (!record('token', response, [200])) {
        signedIn = null;
        return null;
    }

    signedIn = {
        access: response.json('access_token'),
        accessUntil: now + Number(response.json('expires_in')),
        refresh: response.json('refresh_token'),
        refreshUntil: now + Number(response.json('refresh_expires_in')),
    };

    return signedIn.access;
}

// The step tag is read at each request, because a step changes mid-session.
// It is timed from the scenario's start, which the stages count from: the
// test run's own clock also counts setup(), minutes of it at four hundred users.
function tagged(op, call) {
    exec.vu.metrics.tags.step = stepAt((Date.now() - exec.scenario.startTime) / 1000, HOLDS);
    return call();
}

function kindOf(response) {
    if (response.status === 429) return '429';
    if (response.status >= 500) return '5xx';
    if (response.status >= 400) return '4xx';
    if (response.error_code === 1050) return 'timeout';
    return 'connection';
}

// True for an expected status; anything else is counted by kind, so the
// summary splits a limiter's 429 from a timeout and from a refused connection.
function record(op, response, expected) {
    const ok = expected.includes(response.status);

    if (op !== 'token')
        journeyDuration.add(response.timings.duration, { op });

    failed.add(!ok, { op });

    if (!ok)
        failures.add(1, { op, kind: kindOf(response) });

    return ok;
}

function think() {
    sleep(THINK_MIN_SECONDS + (Math.random() * (THINK_MAX_SECONDS - THINK_MIN_SECONDS)));
}

function pick(list) {
    return list[Math.floor(Math.random() * list.length)];
}

function call(op, method, path, body, token, expected) {
    const params = {
        headers: { 'Content-Type': 'application/json' },
        tags: { op },
        timeout: REQUEST_TIMEOUT,
    };

    if (token)
        params.headers.Authorization = `Bearer ${token}`;

    const response = tagged(op, () => http.request(method, `${BASE_URL}${path}`, body, params));
    record(op, response, expected);

    return response;
}

const ADDRESS = JSON.stringify({
    line1: '1 Example Street',
    line2: null,
    city: 'London',
    postalCode: 'EC1A 1BB',
    country: 'GB',
});

// One session: what a signed-in shopper does on a visit. Every request goes
// through the gateway, as the web client's do.
export function session(data) {
    // The listing is anonymous, as a visitor's first page is.
    const listing = call('listing', 'GET', '/api/v1/catalog/products?limit=20', null, null, [200]);
    think();

    const listed = listing.status === 200 ? (listing.json('items') || []).map((item) => item.productId) : [];
    const views = 1 + Math.floor(Math.random() * PRODUCT_VIEWS_MAX);

    for (let i = 0; i < views && listed.length > 0; i++) {
        call('product', 'GET', `/api/v1/catalog/products/${pick(listed)}`, null, null, [200]);
        think();
    }

    const token = bearer(data);

    // Nothing past the listing is reachable without a token; the failure is already counted.
    if (!token) {
        think();
        return;
    }

    // A first visit has no saved address, and saves one.
    const saved = call('address_read', 'GET', '/bff/v1/saved-address', null, token, [200, 404]);

    if (saved.status === 404)
        call('address_save', 'PUT', '/bff/v1/saved-address', ADDRESS, token, [200]);

    think();

    const product = pick(PRODUCTS);
    call('quote', 'POST', '/bff/v1/checkout/quote', JSON.stringify({
        currency: __ENV.STRESS_CURRENCY,
        lines: [{ productId: product, quantity: 1 }],
    }), token, [200]);
    think();

    if (Math.random() >= ORDER_SHARE)
        return;

    // PlaceOrderCommand's wire shape, as slo.js's placeOrder sends it; 200 is the success.
    call('order', 'POST', '/api/v1/orders', JSON.stringify({
        commandId: uuid(),
        items: [{ productId: product, quantity: 1 }],
        shippingAddress: JSON.parse(ADDRESS),
        currency: __ENV.STRESS_CURRENCY,
    }), token, [200]);
    think();

    call('my_orders', 'GET', '/bff/v1/orders?limit=10', null, token, [200]);
    think();
}

function hex(bytes) {
    return Array.from(new Uint8Array(bytes), (b) => b.toString(16).padStart(2, '0')).join('');
}

// A version-4 UUID, as slo.js builds one, so an order is never a replay of another.
function uuid() {
    const b = new Uint8Array(randomBytes(16));
    b[6] = (b[6] & 0x0f) | 0x40;
    b[8] = (b[8] & 0x3f) | 0x80;
    const h = hex(b.buffer);
    return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`;
}

// What summarise.mjs reads: the run's own clock and plan, and every per-step
// figure the thresholds above named. Of setup()'s data only its finishing time
// is read, which is when the scenario's stages start; the rest holds this
// run's password and client secret and is never written. A run whose setup()
// refused has no such time, and measured nothing to place.
export function handleSummary(data) {
    const endedAt = Date.now();
    const startedAt = (data.setup_data && data.setup_data.startedAt) || endedAt - data.state.testRunDurationMs;
    const metrics = data.metrics;

    const steps = HOLDS.map((hold) => {
        const sub = (name, tags) => metrics[`${name}{${tags}}`];
        const values = (name, tags) => (sub(name, tags) || { values: {} }).values;

        return {
            ...hold,
            journey: values('stress_journey_duration', `step:${hold.step}`),
            ops: Object.fromEntries(OPS.map((op) => [op, values('http_req_duration', `step:${hold.step},op:${op}`)])),
            failures: Object.fromEntries(FAILURE_KINDS.map((kind) =>
                [kind, values('stress_failures', `step:${hold.step},kind:${kind}`).count || 0])),
            failed: values('stress_failed', `step:${hold.step}`),
            aborted: Object.values((sub('stress_failed', `step:${hold.step}`) || {}).thresholds || {})
                .some((threshold) => threshold.ok === false),
        };
    });

    const raw = {
        startedAt,
        endedAt,
        plan: {
            steps: STEPS,
            rampSeconds: RAMP_SECONDS,
            holdSeconds: HOLD_SECONDS,
            orderShare: ORDER_SHARE,
            thinkSeconds: [THINK_MIN_SECONDS, THINK_MAX_SECONDS],
            products: PRODUCTS.length,
            abortErrorRate: ABORT_ERROR_RATE,
        },
        steps,
    };

    return {
        [`${OUT}/raw.json`]: JSON.stringify(raw, null, 2),
        stdout: `Stress run: ${steps.filter((s) => s.journey.count).length} of ${steps.length} steps measured; ` +
            `raw results in ${OUT}/raw.json. Next: node deploy/observability/stress/summarise.mjs ${OUT}\n`,
    };
}
