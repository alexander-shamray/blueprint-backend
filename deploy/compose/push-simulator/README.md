# Push simulator

The push providers, simulated: a WireMock.Net server loaded with the mappings
in `mappings/`, which the adapters' own tests and this Compose unit both read.
It serves FCM's send path, Google's token endpoint and APNs' device path
behind one port. Section 7 of [the push channel design
spec](../../../docs/superpowers/specs/2026-10-14-notifications-push-channel-design.md)
is the specification; this file is what a person at the keyboard needs.

## Scripted tokens

The answer is scripted by the device token's prefix, because the token is the
one field a test or a person registering a device controls:

| Token | FCM | APNs |
|---|---|---|
| `unregistered-` | `404 UNREGISTERED` | `410 Unregistered` |
| `invalid-` | `403 SENDER_ID_MISMATCH` | `400 BadDeviceToken` |
| `rejected-` | `400 INVALID_ARGUMENT` | `400 BadTopic` |
| `unavailable-` | `503 UNAVAILABLE` | `503` |
| `slow-` | `200`, after `FcmHop`'s attempt timeout | `200`, after `ApnsHop`'s attempt timeout |
| `slow-unregistered-` | `404 UNREGISTERED`, held for a second | `410 Unregistered`, held for a second |
| any other | `200` | `200` |

Google's token endpoint, at `/token`, exchanges any assertion for an access
token an hour long.

`slow-` is the unconfirmed case: the provider took the push and the client
gave up first, so the delivery backs off rather than being retried at once.
`slow-unregistered-` is a token that rotates under a send: a new token is
registered while the call is held, and the unregistered answer that follows
must leave the row, which no longer holds the token the send used.

## What it does not check

It verifies no signature and serves HTTP/1.1, which is why Development
accepts an `http` base URL and a key the worker generates. The signatures are
proved in the adapters' unit tests, against keys the tests generate, and
outside Development the worker refuses plain HTTP and a missing key.

The server's own view is at `http://localhost:5192/__admin/mappings` and
`/__admin/requests`. A request's path and body there hold the token, which is
a simulator's and never a device's.
