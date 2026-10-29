// The realm's password policy over a stress run. Keycloak hashes a password
// at sign-in at the realm's policy, argon2 by default, so a ramp's thousands of
// first sign-ins cost the machine cores the hold never spends. The run lowers
// it, and the users' passwords are set under it, so a sign-in checks a cheap
// hash and has nothing to re-hash; teardown() puts the realm's own back.
export const CHEAP_HASH = ['hashAlgorithm(pbkdf2-sha256)', 'hashIterations(1)'];

const HASH_TERM = /^hash(Algorithm|Iterations)\(/;

function terms(policy) {
    return (policy || '').split(/\s+and\s+/).map((term) => term.trim()).filter(Boolean);
}

// A run cut short leaves the cheap terms in the realm, so they are never what
// the next run restores. Keycloak reads an empty policy as its defaults.
export function hashPolicy(current) {
    const own = terms(current).filter((term) => !CHEAP_HASH.includes(term));

    return {
        run: [...own.filter((term) => !HASH_TERM.test(term)), ...CHEAP_HASH].join(' and '),
        restore: own.join(' and '),
    };
}
