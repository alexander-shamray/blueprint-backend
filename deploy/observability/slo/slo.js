// The k6 SLO run of §13.7 and §15.1 — the first real gate after the staging
// deploy.
//
// IT IS NOT A SMOKE TEST, and §15.1 declines to have one for the reason §12.1
// gives: a gate nobody has defined is a gate that gets configured to pass. This
// stage is named for what it asserts. It is also NOT a capacity test — it
// catches the regression where a query loses its index and goes from 40 ms to
// 4 s, which no unit test will find.
//
//   # CLIENT_SECRET comes from the runner's MASKED environment and never from
//   # `-e`: a `-e CLIENT_SECRET=…` puts the real value in k6's process
//   # arguments, and CI logs the command line. k6 exposes system environment
//   # variables through __ENV by default, so inheriting it needs no flag.
//   export CLIENT_SECRET="$SLO_RUN_CLIENT_SECRET"
//
//   k6 run \
//     -e BASE_URL=https://staging.example.com \
//     -e PROM_URL=http://prometheus.observability:9090 \
//     -e TOKEN_URL=https://id.staging.example.com/realms/commerce/protocol/openid-connect/token \
//     -e CLIENT_ID=slo-run \
//     -e SLO_PRODUCT_ID=<a product with a row in ordering.ProductPrices> \
//     deploy/observability/slo/slo.js
//
// WHAT THE SLO CLIENT MUST BE GRANTED, because nothing in this repository
// creates it: the token needs `orders:write` (§11.4's permission claim) and a
// `sub` that parses as a GUID — `HttpContextCurrentUser.Id` calls
// `Guid.Parse` on it, and an order's owner is bound from the principal rather
// than from the request. §14.1's Compose realm has no such client; it grants
// `orders:write` to `demo` only. A staging realm has to add one, and that is a
// deployment obligation this file states rather than assumes.
//
// SLO_PRODUCT_ID must have a row in `ordering.ProductPrices` for the run's
// currency, or §6.6's worked case applies and every order is refused with 422.
//
// TWO INSTRUMENTS, DELIBERATELY. k6 measures wall-clock at the client, which
// includes the edge, TLS and the network; §13.7's command and query rows read
// `request.duration`, which is dispatcher entry to result. k6's number is
// therefore strictly LARGER than the thing the SLO is about, so the thresholds
// below are a coarse guard and Prometheus adjudicates in teardown(). Asserting
// only k6's would fail a healthy service on a slow link; asserting only
// Prometheus's would let a broken edge through with perfect handler timings.
import http from 'k6/http';
import { check, fail } from 'k6';
import { Trend } from 'k6/metrics';

const BASE_URL = __ENV.BASE_URL;
const PROM_URL = __ENV.PROM_URL;
const TOKEN_URL = __ENV.TOKEN_URL;
const CLIENT_ID = __ENV.CLIENT_ID;
const CLIENT_SECRET = __ENV.CLIENT_SECRET;

// Wall-clock, split by what the request IS rather than by route, so the two
// numbers line up with §13.7's two rows.
const commandDuration = new Trend('slo_command_duration', true);
const queryDuration = new Trend('slo_query_duration', true);

// THE RATE BUDGETS, and why they are low. The run drives the public edge —
// `/api/v1/...` exists only after YARP strips the prefix — so §10.3's limiter
// is in front of every request:
//
//   anonymous     (catalog-public)  100 req / minute / IP,      QueueLimit 0
//   authenticated (ordering)        300 tokens / minute / sub,  QueueLimit 10
//
// An earlier draft ran 30/s and 5/s — 1,800/min and 300/min. The first is
// eighteen times the anonymous budget, so everything past the first hundred
// listings in each window is a 429; the second sits exactly ON the
// authenticated ceiling, where arrival jitter turns a slice of the writes into
// 429s and QueueLimit 10 is not a margin on 300.
//
// With `checks: ['rate==1']` that fails the run on a HEALTHY platform, and
// `http_req_failed` does not count 429 — so without the checks threshold the
// durations would look fast and it would pass instead. Both outcomes are the
// trap this file already guards against for 422, arriving through the limiter
// rather than the domain.
//
// **Do not raise these to make the run heavier**, and do not point BASE_URL at
// a service port to escape the limiter: `/api/v1/...` 404s there, and the edge
// is precisely what the client-side thresholds exist to see (§13.7). If more
// load is genuinely wanted, the limits in Gateway.Api/Program.cs and these
// numbers move in the same change — which is why both are written here.
export const options = {
    scenarios: {
        // The anonymous product listing — §10.2's `catalog-public` route is
        // GET-only and names `anonymous`, YARP's reserved value for
        // AllowAnonymous, so this needs no token and is the cleanest read-path
        // signal available. The route used to name no policy and mean the same
        // thing; ADR-030's fallback ended that, and a route that said nothing
        // would now 401 every request this scenario makes.
        query: {
            executor: 'constant-arrival-rate',
            // 60/min against §10.3's `anonymous` budget of 100/min per IP,
            // fixed window, QueueLimit = 0. See THE RATE BUDGETS below.
            rate: 1,
            timeUnit: '1s',
            duration: '3m',
            preAllocatedVUs: 10,
            maxVUs: 30,
            exec: 'browseCatalog',
        },
        // Placing an order is the write path: one aggregate, the outbox row in
        // the same transaction (§6.3), and the saga started off the back of it.
        command: {
            executor: 'constant-arrival-rate',
            // 180/min against §10.3's `authenticated` budget of 300/min per
            // subject, token bucket, QueueLimit = 10. See below.
            rate: 3,
            timeUnit: '1s',
            duration: '3m',
            preAllocatedVUs: 10,
            maxVUs: 30,
            exec: 'placeOrder',
            startTime: '10s',
        },
    },
    thresholds: {
        // Generous against §13.7's 80 ms and 100 ms, for the reason at the top
        // of this file: these include everything k6 can see and the SLO does
        // not. A breach here is a real problem; passing here is not a pass.
        slo_query_duration: ['p(95)<400'],
        slo_command_duration: ['p(95)<600'],
        // Not the availability SLO — see teardown().
        http_req_failed: ['rate<0.01'],
        // WITHOUT THIS, EVERY check() BELOW IS DECORATION. k6 does not fail the
        // run on a failed check unless a threshold names the `checks` metric —
        // it prints a red line in the summary and exits 0. So a write path
        // returning 422 for every request would leave the command scenario
        // "fast", satisfy the duration thresholds, and pass. That is exactly
        // the trap placeOrder's own comment describes.
        checks: ['rate==1'],
    },
};

export function setup() {
    // Fail fast and loudly on missing configuration. A run that quietly skips
    // the command scenario because it has no credentials reports a pass for a
    // gate that did not execute, which is the failure mode §15.1's whole
    // "no smoke stage" argument is about.
    // SLO_PRODUCT_ID belongs on this list and was missed when it was written.
    // An unset k6 env var is `""`, not undefined, so the command scenario would
    // have run happily with an empty product id and been refused on every
    // request — a configuration failure wearing the costume of a load result.
    const missing = [
        'BASE_URL',
        'PROM_URL',
        'TOKEN_URL',
        'CLIENT_ID',
        'CLIENT_SECRET',
        'SLO_PRODUCT_ID',
    ].filter((name) => !__ENV[name]);

    if (missing.length > 0)
        fail(`SLO run is not configured: missing ${missing.join(', ')}`);

    const response = http.post(TOKEN_URL, {
        grant_type: 'client_credentials',
        client_id: CLIENT_ID,
        client_secret: CLIENT_SECRET,
    });

    if (response.status !== 200)
        fail(`token endpoint returned ${response.status}: ${response.body}`);

    return { token: response.json('access_token'), startedAt: Date.now() };
}

export function browseCatalog() {
    const response = http.get(
        `${BASE_URL}/api/v1/catalog/products?limit=20`,
        { tags: { slo: 'query' } });

    queryDuration.add(response.timings.duration);
    check(response, { 'catalog listing is 200': (r) => r.status === 200 });
}

export function placeOrder(data) {
    // THE WIRE SHAPE PlaceOrderCommand BINDS, and nothing else. It is
    // `(Items, ShippingAddress, Currency)`; an earlier draft of this file sent
    // `lines` and no address, which binds to nothing, fails PlaceOrderValidator
    // and returns 400 before the domain runs — so the "422 storm" the check
    // below guards against would have been a 400 storm that never reached it.
    // PlaceOrderTests.PlaceAsync is the working request; read it before
    // editing this.
    const body = JSON.stringify({
        items: [{ productId: __ENV.SLO_PRODUCT_ID, quantity: 1 }],
        shippingAddress: {
            line1: '1 Example Street',
            line2: null,
            city: 'London',
            postalCode: 'EC1A 1BB',
            country: 'GB',
        },
        currency: 'GBP',
    });

    const response = http.post(`${BASE_URL}/api/v1/orders`, body, {
        headers: {
            'Content-Type': 'application/json',
            Authorization: `Bearer ${data.token}`,
        },
        tags: { slo: 'command' },
    });

    commandDuration.add(response.timings.duration);

    // 200 IS THE SUCCESS, not 201: ToHttpResult maps a successful Result<T> to
    // Results.Ok (§10.5), which is what Catalog's POST returns too, and
    // PlaceOrderTests pins it deliberately. Asserting 201 would have failed
    // every accepted order — and with `checks: ['rate==1']` below, every
    // healthy run. A gate that fails on a healthy platform is a gate that gets
    // switched off, which is the reason two §13.7 rows are named as not
    // evaluated rather than asserted.
    //
    // 422 is the domain refusing — §10.5 maps Error.Rule there — and it is
    // checked separately rather than folded into a status class, because a run
    // whose product has no price row returns 422 for every request and would
    // otherwise look like a clean load test with a suspiciously fast write
    // path. That is §6.6's worked case, and docs/runbooks/business-volume.md
    // is the procedure for it.
    check(response, {
        'order accepted': (r) => r.status === 200,
        'order not refused by the domain': (r) => r.status !== 422,
    });
}

export function teardown(data) {
    // The authoritative half. Every row below names the instrument §13.7 says
    // it reads; a row whose signal does not exist is NOT asserted here and is
    // listed at the bottom instead, because an SLO nobody can compute is worse
    // than an absence somebody has written down.
    // SECONDS, AND ROUNDED UP. `Math.round` over minutes under-covers whenever
    // the run rounds down: this one lasts about 190 seconds — a 180-second
    // query scenario plus the command's ten-second offset — which rounds to
    // `3m` and silently drops the query scenario's first ten seconds. An early
    // server-side regression escapes the authoritative check entirely.
    //
    // A window slightly longer than the run is harmless: it can only pull in
    // quiet time before traffic started.
    const window = `${Math.max(1, Math.ceil((Date.now() - data.startedAt) / 1000))}s`;

    const rows = [
        {
            name: 'Command p95 < 100 ms',
            // SCOPED TO THE EXACT REQUEST THIS RUN DRIVES, not to `*Command`.
            // Staging carries traffic this run did not generate, and pooling
            // every command histogram lets fast unrelated types dilute a slow
            // PlaceOrderCommand until the quantile passes — a gate reporting
            // on a population it did not create. The same applies to the query
            // row below, scoped to Catalog's listing.
            //
            // The trade is that a renamed request type turns this row into NO
            // DATA rather than a wrong number, and teardown() fails on an
            // absent series. That is the direction to fail in.
            //
            // **THIS STILL DOES NOT ISOLATE THIS RUN'S TRAFFIC, and it cannot.**
            // Scoping to one request type narrows the population; it does not
            // fence it. Anything else calling PlaceOrderCommand during the
            // window lands in the same histogram, and enough fast ambient
            // traffic dilutes a regression this run generated.
            //
            // Fencing it would need a run identifier carried into the SERVER's
            // metric, and `RequestMetrics` tags `request` and `outcome` only —
            // deliberately, because §13.3's cardinality rule rules out a
            // per-run tag. So the requirement moves to the environment instead:
            // **run this against a quiescent staging target.** It is a
            // precondition of the gate rather than something the query can
            // enforce, which is why it is written here and in the README rather
            // than left for a confusing result to teach later.
            query: `histogram_quantile(0.95, sum by (le) (rate(request_duration_seconds_bucket{service_name="Ordering.Api", request="PlaceOrderCommand"}[${window}])))`,
            limit: 0.1,
        },
        {
            name: 'Query p95 < 80 ms',
            query: `histogram_quantile(0.95, sum by (le) (rate(request_duration_seconds_bucket{service_name="Catalog.Api", request="GetProductsQuery"}[${window}])))`,
            limit: 0.08,
        },
        {
            name: 'Outbox oldest, broker lane, p99 < 5 s',
            // `max(…)` collapses to ONE series. Every replica exports the same
            // database-wide gauge, so this returns three vectors on Ordering's
            // chart — and instantQuery reads result[0], which would silently
            // ignore a breach on either of the others.
            query: `max(quantile_over_time(0.99, outbox_oldest_age_seconds{lane="Broker"}[${window}]))`,
            limit: 5,
        },
        {
            name: 'Outbox oldest, local lane, p99 < 1 s',
            query: `max(quantile_over_time(0.99, outbox_oldest_age_seconds{lane="Local"}[${window}]))`,
            limit: 1,
        },
        {
            name: 'Read-model staleness, own events, p99 < 1 s',
            // Every order this run places reaches §6.6's OrderSummaryProjection on
            // the local lane, and ProjectionInvoker records the lag once it applies.
            query: `histogram_quantile(0.99, sum by (le) (rate(projection_lag_seconds_bucket{service_name="Ordering.Api"}[${window}])))`,
            limit: 1,
        },
    ];

    const breaches = [];

    for (const row of rows) {
        const value = instantQuery(row.query);

        if (value === null) {
            // An absent series is a breach, not a pass. This is the whole
            // lesson of §13.6's callout: the dashboard is empty either way,
            // whether the system is healthy or the metric was never published.
            breaches.push(`${row.name}: NO DATA (query returned no series)`);
            continue;
        }

        if (value > row.limit)
            breaches.push(`${row.name}: measured ${value.toFixed(4)}, limit ${row.limit}`);
    }

    // TWO OF §13.7's SEVEN ROWS ARE NOT EVALUATED HERE, and each is named
    // rather than quietly dropped — §13.7 cut two rows outright on the same
    // rule: an SLO that cannot be evaluated is not a weak SLO, it is a claim
    // that the service is meeting a bar nobody is checking.
    //
    //   * Availability (99.9% MONTHLY) — a three-minute run cannot compute a
    //     monthly objective. `http_req_failed` above bounds the error rate
    //     DURING the run, which is a different and much weaker claim.
    //
    //   * Event end-to-end (`messaging.delivery.lag`) — recorded by
    //     IntegrationEventConsumer<T> at consume start. This run places orders;
    //     the consumer that records it handles Catalog's product events, which
    //     neither scenario produces. Asserting it against traffic that cannot
    //     generate it would fail every run for a reason that is not a
    //     regression.
    //
    // Asserting either would have made this gate fail permanently on
    // a healthy platform, which is how a gate gets switched off.
    console.log(
        'SLO run: not evaluated — availability (monthly), ' +
        'messaging.delivery.lag (not exercised by this traffic).');

    if (breaches.length > 0)
        fail(`SLO run failed:\n  ${breaches.join('\n  ')}`);
}

function instantQuery(query) {
    const response = http.get(`${PROM_URL}/api/v1/query?query=${encodeURIComponent(query)}`);

    if (response.status !== 200)
        fail(`Prometheus returned ${response.status} for: ${query}`);

    const result = response.json('data.result');

    if (!result || result.length === 0)
        return null;

    const value = Number(result[0].value[1]);

    return Number.isNaN(value) ? null : value;
}
