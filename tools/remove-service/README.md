# The remove-service tool

[ADR-062](../../docs/backend-architecture/adr/ADR-062-the-domain-is-a-reference-implementation.md)
tells an adopter to replace the reference services, and
[the scaffold](../new-service/README.md) only adds them. This is its inverse:

```bash
python tools/remove-service/remove_service.py Yankee --dry-run
python tools/remove-service/remove_service.py Yankee
```

| | |
|---|---|
| `name` | The service as it is spelt under `src/Services`, PascalCase. `Catalog` is refused: every render copies it |
| `--dry-run` | Print what would be deleted and edited, and what still names the service, and change nothing |
| `--repo-root` | Defaults to this script's repository |

It deletes `src/Services/<Name>`, every `tests/<Name>.*` directory and the
service's Compose unit, and it reverts each edit the scaffold makes to a
shared file. **Which files those are is the scaffold README's table**, and the
suite reads that table: it renders a service, removes it, and compares the
whole tree. A shared file the scaffold learns to edit fails that comparison
until this tool learns to revert it. The tool holds its own list in code, and
the table is what that list is checked against.

**It then prints every file that still names the service.** Contracts under
`Common.Contracts`, the gateway's routes and permissions, the BFF, the Helm
charts, the chapters and the ADRs all stay, because whether a contract dies
with its owner or a chapter's evidence moves is the adopter's decision and
not a text substitution. The list is what is left to read.

## What it refuses, and what it skips

- **A refusal writes nothing.** The name must be a directory under
  `src/Services`, and the solution folder and the Compose include must each be
  found once. A splice found twice is refused too: the tool removes one and will
  not guess which.
- **A splice the service no longer carries is skipped and reported.** The
  reference services were rendered once and edited since, so Payments,
  Shipping and Notifications have no Redis user, and removing them says so
  rather than failing.
- **Half of a Compose pair is refused**, because §14.1's rule is a migrator
  and one host.

## What it does not do

**Nothing checks that the tree is clean.** Run it on a clean worktree: the
deletion is a `rm -rf`, and `git restore` is the only way back. Allow-list
entries the scaffold wrote go with the service's block; entries a person wrote
for the service's own trees go too, and a comment above one stays and appears
in the list of mentions. The block ends at the first blank line after its
entries, so an entry added by hand below that stays.

Stdlib only, Python 3.12. `cd tools/remove-service && py -3.12 -m unittest`
runs the suite, which copies the template and the shared files into a temporary
directory and never edits the checkout.
