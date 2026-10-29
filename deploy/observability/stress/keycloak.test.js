// Run with `node --test "deploy/observability/stress/*.test.js"`. The subject
// is the policy the run sets on the realm and the one it puts back.
import test from 'node:test';
import assert from 'node:assert/strict';
import { CHEAP_HASH, hashPolicy } from './keycloak.js';

const cheap = CHEAP_HASH.join(' and ');

test('a realm with no policy runs on the cheap hash and is put back to no policy', () => {
    for (const current of [undefined, null, ''])
        assert.deepEqual(hashPolicy(current), { run: cheap, restore: '' });
});

test('the realm\'s other terms hold over the run, and its own hash terms give way to the cheap ones', () => {
    const current = 'length(12) and hashAlgorithm(argon2) and hashIterations(3) and notUsername(undefined)';

    assert.deepEqual(hashPolicy(current), {
        run: `length(12) and notUsername(undefined) and ${cheap}`,
        restore: current,
    });
});

test('a run cut short leaves the cheap terms, which the next run never restores', () => {
    assert.deepEqual(hashPolicy(cheap), { run: cheap, restore: '' });
    assert.deepEqual(hashPolicy(`length(12) and ${cheap}`), { run: `length(12) and ${cheap}`, restore: 'length(12)' });
});
