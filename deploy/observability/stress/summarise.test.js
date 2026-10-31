// Run with `node --test "deploy/observability/stress/*.test.js"`. The subject
// is the commit summary.json names when summarise.mjs is started from outside
// its checkout, as stack.ps1 run by path from another directory starts it.
import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = fileURLToPath(new URL('.', import.meta.url));

test('the summary names the commit of the checkout holding summarise.mjs, not of the caller', () => {
    const elsewhere = mkdtempSync(join(os.tmpdir(), 'stress-summary-'));

    try {
        // A run that reached no step reads no Prometheus series, so only the machine is measured.
        writeFileSync(join(elsewhere, 'raw.json'),
            JSON.stringify({
                plan: { steps: [], rampSeconds: 30, holdSeconds: 60, orderShare: 0.2, products: 1 },
                steps: [],
                startedAt: 0,
                endedAt: 0,
            }));
        execFileSync(process.execPath, [join(here, 'summarise.mjs'), elsewhere],
            { cwd: elsewhere, env: { ...process.env, PROM_URL: 'http://127.0.0.1:9' }, stdio: 'pipe' });

        const summary = JSON.parse(readFileSync(join(elsewhere, 'summary.json'), 'utf8'));
        const commit = execFileSync('git', ['rev-parse', 'HEAD'], { cwd: here, encoding: 'utf8' }).trim();

        assert.equal(summary.machine.commit, commit);
    } finally {
        rmSync(elsewhere, { recursive: true, force: true });
    }
});
