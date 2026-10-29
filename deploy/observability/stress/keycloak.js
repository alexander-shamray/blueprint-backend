// The realm's password policy over a stress run. A sign-in checks a password
// at the hash it was stored at, the realm's policy when it was set and argon2
// by default, and re-hashes it when the policy has moved. The run lowers the
// policy before it sets the users' passwords, so the ramp's thousands of
// sign-ins check a cheap hash; teardown() puts the realm's own back.
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
