# Harness boundaries

**What the agent harness grants this repository's commands, what it refuses
them, and every place a grant is wider than the operation it was added for.**
This was `CLAUDE.md`'s *What cuts across them* section until it reached 582
lines. It is load-bearing whenever you touch `.claude/` — a command's
frontmatter, a helper script, `settings.json`, a hook, a subagent profile — and
inert for every other kind of change, which is what earns it a file rather than
a permanent seat in every session's context.

**The content is verbatim in its arguments**, on
[`pr-decision-log.md`](pr-decision-log.md)'s terms and for its reason: a summary
of an argument is how a rule gets "corrected" back. Four edits were made on the
way out, and naming them is cheaper than a claim that does not survive a grep.
The `### What cuts across them` heading became this file's title. One paragraph
arrived from `CLAUDE.md`'s *The commands* section — the one recording that
#150's suppression clause moved three grants along before it stuck — because
its subject is a grant and not a command; its opening was rebased from "That
last clause", which pointed at a paragraph that did not travel with it. And
**two self-references** were rebased the same way: the grant inventory's count
of rotted totals said "in this file" of `CLAUDE.md` and named two siblings that
now live in two other files, and the sandbox residual said the commands cite
"this file" — true while it sat in `CLAUDE.md`, and a contradiction of the
paragraph below the moment it moved. Not one argument was shortened, and no
paragraph was dropped.

**The second of those was found by review rather than by the move**, which is
worth recording where the edits are listed: a self-reference that stays *true
of its old home* reads correctly in isolation and only contradicts something
two screens away. Grep for "this file" after extracting prose, not just for
links.

**No headings were introduced.** The section is one long run of bold-led
paragraphs and it stays that way — inventing headings would mean deciding where
the topic boundaries are, which is an edit to the argument wearing a
navigation aid's clothes. Grep the bold lead-ins; they are the index.

**`/ship` and both sweeps cite this file for the sandbox boundary and its
residuals.** **A new residual is stated here**, and `CLAUDE.md` carries the
pointer rather than a second copy.

> **A grant is not a whitelist, and that is the trap under everything below.**
> If you read one paragraph here, read the one beginning *A grant is not a
> whitelist* — `allowed-tools` is an auto-approval list, so omitting a tool from
> a command's frontmatter withholds nothing. Refusing a capability takes a
> **deny**.

> **This file is outside the blueprint tree, so nothing structural catches its
> drift.** `/check-links` checks `docs/backend-architecture/` and the root
> README's entry point into it, so a file anywhere else is outside its scope
> whatever links it, and `/validate-blueprint` does not name this one —
> `testing.md` and `roadmap.md` are the only two of those siblings it does. The
> one rule in `CLAUDE.md` covers this file, and that is all that does.
>
> **The siblings are described rather than listed on purpose.** Three copies of
> this callout named three different sets the moment two new files arrived,
> which is what a list with no code to check it against does.
>
> **A predicate is only better than a list if it is true.** These three
> callouts first said this file is in no index “like every document under
> `docs/` that is not the blueprint tree”, which is false twice over:
> `docs/runbooks/README.md` is an index of the runbooks beside it, and the
> root `README.md` links `docs/roadmap.md`. Scope is the durable fact and
> being unindexed never was — a false predicate is a stale list with an
> argument in front of it.

---

**File permission rules take `Edit(...)`, never `Write(...)`.** `Edit(path)`
covers every file-editing tool, `Write` included; a `Write(path)` rule matches
nothing and Claude Code **refuses to start** until it is removed:

```
Permission deny rule (.claude\settings.json): Write(.remember/**) is not matched
by file permission checks — only Edit(path) rules are.
```

So `Edit(.remember/**)` and `Edit(./.remember/**)` are the whole of the
`.remember/` protection, and the absence of a `Write` twin is correct rather
than a gap. This has been "fixed" twice by adding the twin back, and both times
it broke startup. **A reviewer who has not run the harness cannot see this;
check a permission claim against the harness before acting on it.**

**The `Edit` denies bind the agent's own tooling**, in both spellings each:
`.claude/scripts/**`, `.claude/sandbox/**`, `.claude/commands/**`,
`.claude/agents/**`, `.claude/hooks/**`, `.claude/skills/**`,
`.claude/settings.json` and `.claude/settings.local.json`.
Read the list in `.claude/settings.json`, do not count it here — it has
already grown twice, once inside the pull request that introduced it. The
review loops grant those helpers by name, so a session that could rewrite
one before invoking it would make every fixed endpoint a fiction. The
sandbox `Dockerfile` is on the list for the same reason at one remove: it
is a *build input to the security boundary*, so a session able to edit it
could add an entrypoint reading the credentials the following `docker run`
mounts in.

**The last three arrived with #33, and the argument for them is the first two's
applied one level up.** `commands/`, `agents/` and `settings.json` are the
files that *grant* what the first two protect. Ten commands carry an
unrestricted `Edit` or `Write` and three of them read untrusted input by
design, so a single applied edit could append a grant to a command's
`allowed-tools`, remove a line from this deny list, or rewrite
`.claude/agents/security-auditor.md` — whose read-only guarantee is precisely
its `Read, Grep, Glob` tool list, as `/security-sweep` says outright: read-only
there "is a property of the agent's tool grant, not a word in its prompt".

**`.claude/skills/**` is the same argument one directory over.** A skill's
`allowed-tools` is auto-approval, so a session that can rewrite `SKILL.md`
widens the next invocation's Bash surface. The deny list in
`.claude/settings.json` is the owner of the paths; this paragraph is the
reason `skills/` is on it.

**The index skill's prefix grants are the same speed bump as `git *--output*`.**
`Bash(bash .claude/skills/codebase-index/scripts/cbx search:*)` matches the
typed string; the shell still runs `$(…)` and a second command on that line.
`.claude/hooks/guard-index-argv.py` is the argv-level guard: substitutions,
extra command runs, write redirections, and `graph` / `clean` / `init` /
`watch` are refused.
The `cbx` wrapper is the other half — whitelist and
`CBX_NO_SKILL_AUTO_UPDATE=1` — and is not a substitute for the hook.

**`settings.json`'s own entry self-locks, and that is a working constraint, not
a curiosity.** Once it denies itself, the session cannot edit it again —
including to undo the edit. So a change to it is one edit that lands complete,
and it goes **last** in any PR that also touches `commands/` or `scripts/`;
split across two edits, the second is refused and whatever the first omitted
ships missing.

**The lock is not instantaneous, and the gap is a trap rather than a
convenience.** Measured on Claude Code 2.1.247, in the session that restored
these denies for the harness-bounds PR: immediately after writing
`Edit(.claude/scripts/**)` back, a `Write` creating a file under
`.claude/scripts/` **still succeeded**; minutes later an `Edit` under
`.claude/hooks/` and an `Edit` of `settings.json` were both refused. The file
is re-read, but not at the instant it is written.

So **a probe taken right after the restore reports the deny as inert and is
simply early** — and an agent that treats that reading as the answer will
conclude it still has access it is about to lose. Verify a restore by reading
the file, never by trying the thing it forbids.

**The practical consequence is an ordering rule for the human doing the
lifting**: restore the lines only when the work is genuinely finished, because
the window closes on its own schedule and a defect found afterwards needs the
lift granted again. That happened on the PR that added the argv guard — the
guard shipped refusing its own commit message, and the fix needed a second
lift.

**`.claude/settings.local.json` was the gap a review found**, and it is the
enumeration lesson in miniature: an exact-file rule cannot cover a sibling, and
Claude Code loads both files. Denying `.claude/**` wholesale was considered and
refused — `.claude/worktrees/` is where `/branch` puts working checkouts, so
that blanket would deny editing the repository itself while a worktree run is
live. A test pins both halves: every loaded settings file denied, and the
worktree root never denied.

Changing any of them is a human's edit, made with the deny lifted. Like the
push denies it is defence in depth — `Bash` redirection can still write a file
— but it removes the quiet path.

**`.claude/hooks/**` joined the list when the first hook landed, and the way it
joined is the lesson.** It had been excluded on a stated condition — "no hook is
configured here" — which was true, and which is the kind of exemption that
expires without anyone noticing, because nothing re-reads the condition when
the fact changes. #30's argv guard made it false. A hook is the sharpest case
on the list: it grants nothing, but it **runs on every Bash call**, so a session
able to rewrite one could delete its own guard and then act. `settings.json`
denies it, and a case in `test_harness_denies.py` asserts the deny, so the next
hook arrives behind a control rather than behind a sentence that used to be
true.

**The external review runs in a container over a disposable clone — not a
worktree — and it had TWO residuals, which were not independent; the egress
one is closed and the credential one is what stands.** The boundary
is `.claude/sandbox/Dockerfile`; a worktree could not be the thing mounted,
because a worktree's `.git` is a file pointing back into this checkout, which
is the one path the container must not reach. No `gh` token, no SSH keys, no
host filesystem beyond the clone, non-root inside, and `bypassPermissions` is
no longer the risk it was because the blast radius is the box.

**Egress is confined to `api.x.ai` and `auth.x.ai` since #17**, and it takes
two containers of the reviewer image: the reviewer on a network created with
`--internal`, which Docker gives no gateway and whose embedded resolver
answers `SERVFAIL` for any name outside it, and `egress-proxy.py` as the one
member with a second leg on the bridge — a CONNECT-only tunnel with a host
allow-list that the reviewer reaches through `HTTPS_PROXY`, which grok
honours (measured: without it the same call hangs, with it `api.x.ai`
answers). It is not a grant wider than its operation: the session runs
nothing new to bring it up, and the proxy carries no clone and no credential.
**The credential half is narrowed rather than closed**, which this paragraph
asserted as settled until #58: where `XAI_API_KEY` is unset or unusable,
`grok-review.sh` copies `~/.grok/auth.json` in, and that file carries a
refresh-token-bearing OAuth session for the x.ai account. The three things
enumerated as absent genuinely are; a fourth was never enumerated. What the
proxy changes is what the crossing credential can reach — the two hosts the
session is for and nothing else — which is why the egress half was the one to
close: it was what made the crossing one exploitable. What remains is the
session's own blast radius against x.ai, which no boundary here can shrink.
Prefer `XAI_API_KEY`, which is scoped, revocable and crosses no file; on this
host it authenticates against a team with no credits, so the fallback is the
path that actually runs. **An authenticated call through the proxy is the
one thing the measurement did not reach** — the classifier refused the probe
that would have copied the host's session into a container — so the first
real review behind it is that measurement, and the proxy logs a `deny` line
naming any host it refuses.

Stated here as well as in the script because `/ship` and both sweeps cite
this file for the boundary and its residuals. The reviewer also has **no .NET
SDK**, so `dotnet test` is this host's gate and never the review's.

**A grant is not a whitelist, and this is the trap under every row below.**
`allowed-tools` is an **auto-approval list**: the harness documents that it
"does not restrict which tools are available: every tool remains callable, and
your permission settings still govern tools that are not listed". So *omitting*
a tool from a command's frontmatter withholds nothing — it only decides
whether the call prompts. Refusing a capability takes a **deny**:
`permissions.deny` in `.claude/settings.json` for the repository, or the
`disallowed-tools` frontmatter key for one command, which removes the named
tools from the pool while it runs. Precedence is **deny → ask → allow**,
first
match wins, so a deny beats every allow including a frontmatter one.
Measured, not read: a `general-purpose` subagent spawned fine under
`--allowedTools "Agent(Explore)"`, and was refused under
`--disallowedTools "Agent(general-purpose)"` with `Agent type 'general-purpose'
has been denied by permission rule 'Agent(general-purpose)' from cliArg` — and
again, from a command's own frontmatter, `from command`.

**A helper is the answer whenever a git grant is wider than the operation it
buys**, because **an allow rule is a prefix and cannot exclude a flag**. Each
of these was confirmed by running the offending form rather than reasoning
about it:

| Raw grant | What it also bought |
|---|---|
| `Bash(git switch:*)` | `--discard-changes` and `-C` — and the flags **combine**, so `git switch -fC <name> <start>` defeats any `Bash(git switch -C:*)` deny |
| `Bash(git worktree add:*)` | `-B`, which resets an existing branch rather than creating one |
| `Bash(git checkout -b:*)` | the trailing flag — `git checkout -b <name> -f origin/main` discards tracked modifications |
| `Bash(git branch:*)` | `git branch -fd <name>` — force and delete behind a spelling the `-d`/`-D`/`--delete` denies do not match |
| `Bash(git reset HEAD:*)` | `git reset HEAD --hard` — the `--hard` deny matches the other word order |
| `Bash(git log:*)`, `Bash(git diff:*)`, `Bash(git show:*)` | `--output=<path>`, which is an arbitrary file write with `--format=` choosing the bytes. Reproduced: `git log -1 --format=%s --output=<scratch>` wrote the commit subject, silently |
| `Bash(git fetch:*)`, `Bash(git pull --ff-only:*)` | a URL in the repository position, and `ext::<cmd>` is a git transport that **runs its argument as a command** |

**The reset grant does not narrow, and the attempt is the sharpest lesson
here.** It was "fixed" to `Bash(git reset HEAD --:*)` on the reasoning that
`--` turns a later flag into a pathspec. True of *git*, irrelevant to the
*rule*: an allow rule is a prefix match, and `git reset HEAD --hard` starts
with `git reset HEAD --`, so the narrowed grant admitted the exact command it
excluded — while the commit message said the hole was closed. **The git
behaviour was verified and the matching was not.** Anything whose safety
depends on what follows a token needs a helper, or a **deny**, not a cleverer
allow.

**A deny is the thing an allow cannot be: `*` matches at any position in it,
including the middle.** `Bash(git *--output*)` refuses `git log`, `git diff`
and `git show` carrying `--output` anywhere in the argument list, and leaves
plain `git log` alone — both halves measured, because a deny that blocked the
command outright would read the same from the failing side. Removing the three
read grants instead would have bought nothing: the harness treats read-only
forms of `git` as promptless built-ins whatever the allow list says, and its
own note is that "to require a prompt for one of these commands, add an `ask`
or `deny` rule".

**"Including the middle" is the measurement; the documented rule is wider, and
the two are worth keeping apart.** What was measured here is a wildcard between
two literals. The harness's own reference says a Bash rule's wildcards "can
appear at any position in the command, including at the beginning, middle, or
end" — so a pattern *starting* with `*`, which this repository had no precedent
for, is supported rather than merely untested. That was read out of the docs
after a branch shipped two such denies and flagged them as an unverified guess,
which is the right order: **write down which half you measured and which half
you looked up.** Two neighbouring constraints came from the same page and
match what is recorded above — the `:*` form is recognised only at the end of a
pattern, and a trailing `*` preceded by a space enforces a word boundary, so
`Bash(ls *)` misses `lsof` where `Bash(ls*)` catches it.

**It raises the cost of the naive spelling and it is not a boundary**, and the
distinction is the whole of the reset-grant lesson one paragraph up. A
permission rule matches the command *string*; the shell reassembles adjacent
quoted fragments before `exec`, so `--out''put=<path>` reaches `git` as
`--output=<path>` while never presenting contiguous `--output` to the matcher.
Copilot raised this against the rule as shipped and it is accepted. **The
concatenation is now MEASURED**, where this paragraph used to say it was not:
`printf '%s' --out''put=/tmp/x` prints `--output=/tmp/x`, and so does
`--"out"'put'=`. It had rested on documented quote-removal semantics because
the earlier probe was refused by the classifier layer — a second net worth
noting and not evidence. It is evidence now, and it says the deny was genuinely
defeatable rather than theoretically so.

**What closed it is the second of the two things named here as owed: a rule
over the executed argv rather than the typed string.**
`.claude/hooks/guard-git-argv.py`
is a `PreToolUse` hook on `Bash` that resolves the command the way the shell
would — quote removal, heredoc bodies, comments, line separation and, since
#183, redirections — and judges the argv that is left, so the fragments are
rejoined before anything is compared and the dodge stops working.
**It reaches further than any rule could**, and that is the part worth carrying:
hooks run on every tool call in the loop, including the read-only `git` forms
the harness waves through as promptless built-ins, where no allow or deny rule
is consulted at all. Measured, not read — the docs do not say so, and a
`git log --out''put=` probe was refused by the hook with no file written.

The deny stays beside it as defence in depth. **A substring deny over a shell
command string can never be more than a speed bump**, which is still the
generalisation worth carrying past this one flag — what changed is that the
speed bump is no longer the only thing there.

**The hook's own residual, stated rather than left to be found:** `shlex`
resolves quoting and not expansion, so a flag assembled at run time —
`F=--output=x; git log $F` — arrives as the token `$F` and is not seen. Closing
that needs the argv after expansion, which no hook is given.

**This paragraph once claimed more than that, and a reviewer was right to say
so.** "Every spelling a caller can type literally" was false while
`git log "$(git push origin +HEAD:main)"` was one `shlex` token and two commands
to the shell — a command substitution is *executed*, not quoted away, and
calling it inert because it survived tokenisation intact is the same mistake as
reading a heredoc body as an argument list.

**That sentence then said both were closed, and they were closed in two
functions that disagreed with each other.** The stripper knew a heredoc body
was data; the extractor ran on the raw string ahead of it, with a quote tracker
of its own and no notion of heredocs or comments. So a substitution inside a
`<<'EOF'` body was refused although the shell never expands one there, and a
substitution inside a bare `<<EOF` body was **admitted** although it does — an
apostrophe in the body being an opening quote to the extractor and an ordinary
character to bash. Raised in review; both directions verified against the guard
as shipped, and the bash behaviour measured rather than argued.

**A second one was found while fixing it, one layer down and the same shape.**
`shlex.shlex` sets `commenters = "#"` and honours it at any character position,
where bash starts a comment only where `#` begins a word — so
`git log --grep=#x ; git push origin +HEAD:main` tokenised to three tokens, the
push left with the comment, and the hook reported no offence at all. Measured
under bash with a `git` shim: both invocations run.

Both are closed by giving the guard **one** model of where the shell expands
things — `expandable_regions`, over the quote-and-comment-aware scanner the
heredoc opener already used — rather than a second model per function. Command
substitution is now judged wherever bash would perform it and nowhere else.

**And then the bound was wrong for a fourth reason, which had nothing to do
with the shell at all.** Every version of this paragraph has named the residual
in shell terms — quoting, then expansion — while `git -c` sat one layer up:
`git -c "alias.x=!echo PWNED" x` runs the echo, and git executes a long list of
config keys besides — `core.pager`, `core.editor`, `core.sshCommand`,
`core.hooksPath`, `diff.external`, `credential.helper`. Measured in a scratch
repository. The hook admitted every one of them, because they are ordinary
arguments to an ordinary `git`, and nothing about the SHELL is involved. The
option is refused outright now, in the global position only, since `-c` after
`commit` means "reuse this message" and breaking that is how a guard gets
turned off. Enumerating the executing keys would be the deny-list this file
already refuses twice over.

**So state the bound as what has been looked for, not as what is left.** The
residual named today is what the shell **computes** rather than what a caller
writes, and it has two measured shapes: a flag or command assembled from a
variable (`F=--output=x; git log $F`), and a substitution whose OUTPUT becomes
the command line (`sh -c "$(echo 'git push origin +HEAD:main')"`). Closing
either needs the argv after expansion, which no hook is given. Both are pinned
as **admitted** in the suite, on the same argument as the degraded-check case:
a residual nobody can run is one the next reader assumes was closed.

Three earlier versions of that sentence were each falsified by a spelling
nobody had gone looking for, and the pattern is that the bound gets narrower
every time somebody probes rather than reasons. **It is a claim about the last
search, not a proof.**

**The `::` in a value collides with the `:*` suffix syntax, and the collision
fails silent in one direction and loud in the other.** `Bash(git *ext::*)`
passes settings validation and then matches nothing, because the trailing `:*`
is consumed as the prefix-wildcard form and the literal becomes `git *ext:` —
a probe of `git log -1 ext::foo` ran clean under it. Writing `Bash(git
*ext::**)` to dodge that is rejected at startup: *"The `:*` pattern must be at
the end."* So **`ext::` cannot be expressed in a Bash rule at all**, and the
transport is closed on the allow side instead — **and the two grants do it by
two different mechanisms, which an earlier revision of this paragraph ran
together.**

**Since #30 it is also closed on a third mechanism, and that one is not a
rule.**
`.claude/hooks/guard-git-argv.py` refuses an argv element containing `ext::` on
a git subcommand that takes a **repository** — `fetch`, `clone`, `pull`,
`push`, `remote` and their neighbours. **Not every element after `git`**, which
is what this sentence said until the scoping landed and did not reconcile it:
the transport is only meaningful where git expects a repository, and judging it
everywhere made a branch name or a path carrying the sequence indistinguishable
from a use of it — a commit body arguing about the transport included. A hook
is not bound by the pattern grammar at all,
which is why it can express what `Bash(...)` provably cannot — and the whole
argument below about which grant pins what remains worth reading, because a
hook is one file and defence in depth is the reason the allow side was narrowed
in the first place.

`Bash(git fetch origin:*)` **pins the remote**: a literal remote name occupies
the repository position, so a URL never reaches it. That is the control the
sentence describes, and it is real.

`Bash(git pull --ff-only)` pins **nothing** — no remote name appears in it at
all. What it does is drop the `:*`, so the grant is the documented
no-argument invocation and nothing else. Whether that closes
`git pull --ff-only ext::<cmd>` turns on the prefix-match rule stated in the
reset-grant paragraph above — **an allow rule is a prefix match** — which has
never been measured for the no-wildcard case. (Named rather than counted: a
numbered offset inside a section still being edited is how this repository's
callout totals used to rot.) If that holds for a grant without `:*` as well, the
pull side is still open and belongs in the residual inventory rather than in
this paragraph. Nobody has run the probe. Until someone does, treat the pull
grant as *narrowed, not proven* — the two words this repository keeps having to
tell apart.

**The probe that would settle it is not available from inside a session, and
that is a property of the question rather than a lack of trying.** Every exact
grant in `.claude/settings.json` — `Bash(git branch)`, `Bash(git remote -v)`
and the rest — is a **read-only** git form, and the harness treats those as
promptless built-ins whatever the allow list says. So a trailing argument on
one of them runs cleanly and demonstrates nothing about prefix matching: it
would have run either way. `git remote -v --verbose` was tried and is exactly
that null result. A decisive probe needs an exact grant on a command the
harness does not already wave through, which means adding a rule to a file the
session reads at startup — so it cannot be arranged from within the session it
would govern.

**A command's frontmatter is a grant like any other, and it is the one nobody
reads twice.** The first five rows above were all found in command frontmatter,
and for a while that supported a second claim — that the global file had it
right all along. **It did not, and the sixth row is where that broke**: the
`--output` write primitive sat in `.claude/settings.json`'s own allow list,
reachable from every command in the repository, and it had been read past for
as long as the frontmatter rows had. The lesson survives with its converse
attached: frontmatter is the grant nobody reads twice, and the global file is
the one everybody assumes somebody else already read.

**This paragraph is the inventory of grants wider than the operations they buy,
and no command file keeps a second total** — `ship.md`'s callout carried one,
and it went stale the moment a branch pinned that file's fetch grant.

**The entries are numbered and the numbering is stable; the headline count that
used to open this paragraph is gone on purpose.** It read "six" and was made
false by #56 closing the fourth, which is the third time a restated total in
this repository's guidance has rotted — `CLAUDE.md`'s callout totals and
`docs/testing.md`'s test count went the same way,
and the fix that held there was to drop the number and keep the named entries.
Ordinals are kept even where an entry is closed, so a cross-reference to "the
fourth" still lands, and each entry says its own state. Read the entries; do
not count them here.

Two are `/ship`'s:
`Bash(git worktree remove:*)` admits the `-f` that discards work, and
`Bash(gh pr merge --merge:*)` admits a trailing `--admin` that merges past
failing checks. Helpers are owed for both; until someone with the
`Edit(.claude/scripts/**)` deny lifted writes them, `/ship` carries them by
reporting its literal invocations, flags and all.

**Two more sat in the sweep command files rather than in this paragraph, and
both are now closed — which is worth recording because they were closed the
same way.** `Bash(mktemp:*)` took an arbitrary template, so it was a filesystem
write primitive able to create an empty directory or file anywhere the session
could write; `git-worktree-detach.sh` makes the directory itself now and prints
it, and both sweeps dropped the grant. `Bash(gh label create:*)` was
create-*or-overwrite* — `--force` updates an existing label's colour and
description, and `-R` put that write in any repository — and it was held as two
prose rules; `gh-label-ensure.sh` leaves no free parameter, taking one name out
of a fixed six-entry case and resolving the repository from the checkout. **The
root cause both shared is the one that generalises**: each was documented by the
operation it was added for rather than by what its prefix admits, and reading
the tool's own `--help` found something every time it was done. That they were
inventoried in a command file and not here is the same drift this paragraph
warns about, running the other way.

The third is `Bash(git fetch origin:*)`, which no longer admits a URL but still
admits a trailing flag; `--upload-pack`, `--receive-pack` and `--exec` are
denied by name, so what is left is the flag nobody has enumerated yet. The
honest fix is the helper the transport issue asked for — a
`git-fetch-origin.sh` taking a branch name and nothing else.

The fourth **was** `/review-copilot`'s three unfiltered comment feeds, and #56
closed it. It is kept here in the past tense rather than deleted, because what
made it survive three revisions is worth more than the fix: each revision
narrowed the claim and none of them narrowed it to something enforceable.

**What landed.** All three feeds are behind helpers — `pr-review-bodies.sh`,
`pr-review-comments.sh`, `pr-issue-comments.sh` — filtering on one allow-list
declared once in `copilot-authors.sh` and each printing an admitted/dropped
count. `Bash(gh pr view:*)` is gone from `review-copilot.md`'s frontmatter,
which is the half that makes it enforcement rather than courtesy: that command
used `gh pr view` for nothing but the two GraphQL feeds, and `settings.json`
carries no `gh` allow, so a raw call prompts — a stall in the unattended loop
instead of a silent pass.

**One file was not enough, and the review of the PR that closed this is what
established it.** `/ship` invokes `/review-copilot` as a skill while holding
its own frontmatter grants, and `allowed-tools` entries are cumulative
auto-approvals rather than a whitelist — so a grant removed in one file and
kept in its caller withholds nothing on the unattended path, which is the path
the issue was about.

**And `gh pr view` was not the only door, which took a third review round to
find.** `gh pr list --json reviews,comments` returns the same review bodies and
issue comments for every pull request at once — measured here, a 2,457-character
Copilot review body out of `gh pr list --state all --limit 1 --json
number,reviews`. Three commands kept that grant for the harmless job of finding
a branch's pull request, and it was a complete bypass of all three filtering
helpers. **No command grants `Bash(gh pr view:*)` or `Bash(gh pr list:*)` any
more**: `ship.md` reads state through `pr-state.sh`, `pr.md` feeds the closure
gate through `pr-closure-input.sh`, every command that needs a branch's PR
resolves it through `pr-for-branch.sh` — the test that pins the helper lists
them — `review-branch.md`, `review-copilot.md` and `ship.md` judge a
PR's changed paths against the body's `| Class |` and `| Touch set |` rows
through `pr-locality.sh`, which prints a verdict per path and never the
rows, and every one fixes its field set, because a caller that chooses
fields can choose `reviews`.

**The gate that pins this is an ALLOW-list of `gh` subcommands, and it is the
second one in this repository to be rewritten that way.** Its first version
banned `gh pr view` by name and passed while three files still granted
`gh pr list` — a deny-list passing every spelling nobody thought of, which is
exactly what the Grok verdict check did before it was inverted.

**This paragraph used to name `gh issue view` and `gh issue list` as still
admitted, and #150 removed both.** The measurement it rested on is unchanged
and was never the thing in question — `gh issue view <pr-number> --json
comments` does return an empty array, because `gh` keeps issues and pull
requests distinct even though GitHub's model does not. Membership of the
allow-list is a different fact, and it moved: `gh issue view` returns `author`
to the same session, and `gh issue list` returns `author` **and every issue
body** at once. Both went, and `gh repo view` with them.

**Two paragraphs in this file disagreed about one gate, and the stale one was
the dangerous half** — a reader reconciling a new grant against "stay admitted"
would have put back exactly what #150 closed. Raised in review, against the
branch that closed it: the record of the removal was written a thousand lines
earlier, and the rule nobody re-read was not.

**The set itself is deliberately not written here**, which is the fix rather
than an omission — it is `GH_GRANTS_THAT_CANNOT_REACH_A_FEED` in
`test_copilot_feeds.py`, beside the assertion that reads it, and a copy in this
file is what went stale. This paragraph carries the argument for why a
subcommand joins or leaves that set; the set carries the membership.

`ship.md`'s review-body and inline
reads go through the same feed helpers `/review-copilot` uses, so the two
commands share one list instead of holding two prose rules that disagreed.

**`pr.md` was the third holder and was found by a test, not by reading.** The
issue named two commands; the case whose subject is *every* command's
frontmatter caught the third. That is the gate-coverage lesson paying for
itself inside the change that added the gate.

**#150's suppression clause moved three times in one branch, each time one
grant along from the one just closed, and it is the clearest instance of #56
this repository has.** `gh repo view` went when the suppression helper resolved
the owner itself. `gh issue view` went when a reviewer observed it returns
`author` to the same session. `gh issue list` survived both rounds and returns
`author` **and every issue body** at once. Each fix cited the same rule — a
helper that fixes its field set does not bind a caller who still holds the raw
grant — and each left the next grant standing. **The gate now asserts on the
grant rather than on the instruction line**, because a listing line naming four
fields is a rule a reader follows and a prefix grant beside it is what the
session can actually run.

**Three things the fix deliberately does not do.** It admits the repository
owner alongside Copilot's three logins, because the decision table has three
rows and dropping the owner would take away the input for the middle one —
measured on PR #147, 21 of 43 inline comments and 21 of 33 review bodies are
the owner's. It reports a dropped item's author and location but **never its
body**, since printing the text one stream over would put the injection vector
back into the transcript the filter exists to keep it out of — which narrowed
the *Anyone else* row from "report what it asked for" to a count and a
location, a deliberate loss of detail. And it is **not authentication**: a
GitHub login is not verified, so the filter refuses the ordinary stranger and
would not refuse a takeover of one of the four admitted logins.
`grok-ledger.sh`'s collaborator-permission check is the stronger form and is
not reached for, because Copilot is not a collaborator and a permission check
would drop the whole review.

**The measured logins, which the fix did not change.**
`copilot-pull-request-reviewer[bot]` is REST's spelling, from
`/pulls/{n}/reviews` — an endpoint no helper here calls. The one REST endpoint
in play, `/pulls/{n}/comments`, reports `Copilot`; the two GraphQL feeds report
the bare `copilot-pull-request-reviewer`. An earlier revision of this paragraph
called the issue-comment login REST's; `gh pr view` loads `reviews` and
`comments` through one exporter, so that could never have been true. All three
spellings stay admitted, because admitting one that never arrives costs
nothing and missing one that does is the direction that fails open.

**The issue-comment feed's Copilot login is still unobserved**, and a revision
of this paragraph once asserted it as measured. Seven PRs have now been checked
— #112, #101, #100, #99, #98, #94 and #147, the last through the new helper
itself — and none carries a Copilot-authored issue comment. So the shared
exporter says what the login *must* be and nothing has seen it. Not evidence:
an asserted measurement that never happened stops the next reader checking.

The fifth is **`git push` under the two sweeps**, and it is the one that looks
closed and is not. Both commands state a read-only boundary, and both used to
close it with "no `git push` is granted either, so the branch cannot move" —
which reads an *absence* as a control, the exact rule the sentence beside it
had just retired. `.claude/settings.json` **allows** `Bash(git push origin:*)`
and `Bash(git push -u origin:*)` globally, so a push of the current branch does
not prompt at all; only force-pushes and pushes to `main` are denied. Naming
`git push` in each sweep's `disallowed-tools` was the obvious fix and stayed
**unverified** for a while: that key's `Bash(...)` form had never been
measured here — the `Agent(...)` form is what was — and a nested `claude -p`
probe could not separate a rejected pattern from a command that failed to
load. **It is measured now**, by a throwaway command in a detached worktree
carrying `Bash(git diff:*)` in both keys: the diff was refused with the
harness's own "has been denied" text while a `Bash(wc:*)` in the same session
ran, which separates the two exactly. Both sweeps deny `git push origin`,
its `-u` form and the raw `gh issue create` by name, and the deny wins over
the global allow because precedence is deny first.

**A frontmatter deny holds for the rest of the user turn, not for the command
that states it (blueprint-admin#17).** Measured there: `/ship` ran `/commit`,
which denied `Bash(git push:*)`, and then pushed in the same turn, and the
push came back "has been denied" in under a second with no hook reason, every
time, while the identical push after a new user message ran. No command
`/ship` chains here carries that deny, and the rule is that none gains one:
**a command `/ship` runs before it pushes never denies push.** It reads as
hardening and is not — `/branch` and `/commit` leave the push to `/pr`,
`/review-copilot` pushes only an already-committed review fix, by name, and
the git-argv hook and `settings.json` refuse `main`, force and delete whoever
asks. `NothingShipChainsDeniesPush` in `test_triager_guards.py` names the
commands and fails if one denies push. A terminal, read-only command — the
two sweeps — keeps its deny, because nothing pushes after it.

**`/review-grok` is the one chained command that cannot follow that rule, so
`/ship` step 5 runs it inside an `Agent` instead.** Run inline, its bare `Bash`
deny is its boundary — it reads an untrusted review holding `Edit` — and under
the same turn-wide lifetime it would also refuse every command step 5 runs
after it: the checks, `/commit` and the push. The deny stays and the triage
moves. **On the one agent type measured, the agent keeps the deny off the push
by discarding it, so the agent alone is not the boundary**
(blueprint-admin#19): `/review-grok` loaded through the Skill tool in the main
session removed `Bash` until the next user message — background notifications
did not end it — while the same load inside a `general-purpose` agent left
`Bash` working there, and the parent's `Bash` in the same turn was unaffected.
On that path the push is safe and the triage would read an untrusted review
holding a shell.

**So the triage runs under a profile of its own, and two hooks say what a
profile cannot.** Step 5 grants exactly `Agent(review-grok-triager)`, whose
`tools:` — an allowlist — holds no `Bash` and no `Skill`; it reads
`review-grok.md` rather than loading it, so the skill load measured above
never happens there. A type list inside a subagent's `Agent` grant is ignored,
so the profile's own `PreToolUse` hook, `guard-triager-dispatch.py`, admits
`review-adjudicator` and refuses every other dispatch — the triager itself
included, which `/ship` grants and so could not deny. And a path in a
profile's `disallowedTools` removes the whole tool, while a command's
frontmatter list lasts one turn and is not applied inside an agent at all
(blueprint-admin#27), so a second hook, `guard-triager-edit.py`, on
`Edit|Write|MultiEdit|NotebookEdit`, reads the `Edit(...)` denies from
`review-grok.md` on every call — one list, no copy — and refuses a target
under any of them, matched without regard to case, and any target outside the
checkout its event's `cwd` stands in. The list is the one beside the hook, plus the edited
checkout's own when that is another file — a sibling worktree's — because an
added list can only narrow, where a replaced one would let that worktree's
branch loosen it. Run in place, hook and list are both the branch's, as
`settings.json` and every other guard are; what keeps a review from
loosening the list is that the list denies its own tree. Both fail closed, and both are wired as
the session-wide hooks are, through `py -3.12`. `test_triager_guards.py` pins
the profile, the grant, the sweeps' and the triage's deny of the new type, and
runs both hooks against every pattern that list holds.

**The outside-the-checkout refusal takes the scratchpad with it, and that is
accepted rather than excepted.** `/review-grok` keeps its resolution record in
a scratchpad, and a hook sees a path, not who chose it: an exception for temp
paths is an exception for every file outside the repository a review could
name. So on this path the triager returns the record in its report and
`/ship` writes it.

**`/ship` still holds `Agent` for every type, because `allowed-tools`
approves and does not restrict.** The grant names the triager and step 5's
text spawns it; nothing refuses `/ship` a `general-purpose` dispatch. The
control that would is a `disallowed-tools` line on `/ship`, which lasts the
turn and would bind every later step of the chain — the defect this section
opens with — and a session-wide dispatch hook cannot tell `/ship` from a
sweep spawning its auditor.

**None of the profile's runtime behaviour was measured in this repository.**
The tool allowlist and the dispatch hook were probed in blueprint-admin
(blueprint-admin#23), on the same harness; that the edit hook fires on `Edit`
inside the triager was not probed anywhere and rests on the mechanism the
dispatch hook was measured under. Grok is enabled here, so the measurement is
owed on the first `/ship` that reaches step 5: spawn the triager, have it
edit `.github/workflows/ci.yml`, and see the hook refuse it.

The sixth **was** the `--output` deny itself — the inventory's one entry that
is a *deny* rather than an allow, listed because a deny over a command string
is defeated by shell quoting. **#30 closed it, and not by improving the rule.**
`.claude/hooks/guard-git-argv.py` judges the resolved argv, so the quoted
spelling is refused too, and it fires on the promptless read-only `git` forms
no rule is consulted for. The deny stays as defence in depth and the three read
grants stay with it, because removing them still buys nothing against a
built-in. What survives as residual is one line rather than a grant: a flag the
shell *computes* — `F=--output=x; git log $F` — is not visible to a hook that
resolves quoting but not expansion. **#183's redirection strip inherits that
same bound**, and the residual is now narrower than the sentence above says. An
expansion written in the source is read four ways — empty, whitespace, its own
default, and a single-element brace range — because each is what bash does in a
shell with nothing set, and a command is admitted only if it is safe under all
of them. So `git ${x:-push} origin +HEAD:main`, `git push${IFS}origin
+HEAD:main`, `git $@push …` and `git p{u..u}sh …` are refused, and so is
`git ${N}>&1 push origin +HEAD:main`, which an earlier revision of this
paragraph named as the admitted case.

**What survives is the run-time half alone**: a value the shell is *told*
rather than one written where the guard can read it — `F=--output=x; git log
$F`, or `N=2; git log -${N}`. Closing that needs the argv after expansion,
which no hook is given.

**One construct puts that residual inside a word a caller types literally, and
it is refused for exactly that reason.** `$"…"` is a *translated*
double-quoted string: bash resolves it through gettext against `TEXTDOMAIN`
and `TEXTDOMAINDIR`, both ordinary environment variables, so a catalogue
placed in the checkout decides what the word says. Measured with a hand-built
`.mo` — `$"safe"` printed `printf`, and in command position `$"safe" RAN`
executed it; the same lookup can return `git`. Raised in review, against a
guard that had already met this form once and refused only the *expansions*
inside it, as though `$"safe"` were the word `safe`. Every locale quote is
refused now. The cost is a construct nothing in this repository writes, and
`$'…'` is unaffected: its escapes are decoded, and one outside the decoded set
was already refused.

**A shell fed by a process substitution is refused for the same reason a
printer feeding one is.** `bash < <(printf '%s' '…')` executes the
substitution's output, and `bash <(echo '…')` executes it as a file — both
ran, and every pass judged the halves apart: the inner command is data, the
redirection strip removes `< <(…)` whole because a process substitution *is*
the target, and what is left is a shell with no script. Reading the inner
command instead would be right for `<(echo '…')` and wrong for every spelling
that computes, so this joins `unmodelled_printer` rather than trying. The cost
is `bash < <(cat script.sh)`, which nothing here writes; `diff <(a) <(b)` and
`echo <(…)` are untouched, because the run has to be a shell.

**A crash in the guard is a fail-open, so a crash now refuses the command it
crashed on.** `PreToolUse` reads empty stdout as non-blocking, and a
traceback is empty stdout — so each of the four crash paths found here (two
`str.index`, one `list.index`, one recursion) admitted whatever the command
was, including a force push. Fixing them one at a time leaves the next one
open, so the direction is set at the door instead. **This is not the same
answer as the malformed-event case beside it, and the two differ on purpose**:
an unreadable hook event has established nothing about any command, so
refusing there would stop the session for a defect in this file, while a crash
while judging *this* command says this command broke the parser — and refusing
one command is proportionate, states why, and puts the traceback on stderr.

**Each of those readings was found by measurement, not by reasoning**, and the
paragraph that stood here reasoned. It claimed every unresolved shape fails
closed; the second command disproved that, and then four more readings
disproved the narrower claim that replaced it. **A guard's stated failure
direction is a claim to measure in every position**, not one to derive from the
case in front of you.

**A shell reads a script from three places and the third is a pipe**, so the
guard costs a stated over-refusal to cover it. `bash -c '…'` and
`bash <<<'…'` put the text where it can be read; `echo '…' | bash` puts it
one run away, and `cat <<'EOF' | bash` puts it in a heredoc belonging to a
run that is not a shell at all. What decides is whether some later stage of
the pipeline will *execute* its stdin, and that cannot be settled by naming
the wrappers which exec their argument — `command`, `env`, `nohup`, `nice`,
`stdbuf`, `setsid`, `timeout`, `ionice` and `chrt` are nine before anyone has
looked hard, and the tenth is the bypass. So a shell name **anywhere** in a
stdin-consuming run counts, which is the direction `DATA_ONLY_COMMANDS`
already argues for, and it costs `echo '…git push…' | grep bash` a refusal
it does not deserve. That shape needs a printer writing a push into a pipeline
whose far end merely mentions a shell; it has never been typed here, and the
alternative fails open on a wrapper nobody listed.

**A seventh thing is a gap in the mechanism rather than in a grant.** Pinning a
command to one subagent type is a **deny list of every other type**, because
the harness has no "only this type" allow — so `security-sweep.md`,
`bug-sweep.md` and `review-grok.md` each enumerate the registered types that
hold a shell, an editor or the network, and **a newly added agent under
`.claude/agents/` is admitted by default** until someone adds it to all three
lists. That is the shape this repository already knows rots; it is taken here
because the alternative on offer is prose. **It rotted once already on the day
a third agent arrived**: `review-adjudicator` was added for #149 and each of
the three commands had to name the other two profiles, which is the
enumeration's cost paid in the change that proves it.

**The eighth was the push deny-list (#23), and it closed the way the sixth
did.** Two broad allows — `Bash(git push origin:*)` and the `-u` form — paired
with a list of exact spellings, so `git push origin +HEAD:main` was a force push
to `main` carrying neither `--force` nor the literal `origin main`, and five
more slipped alongside it: `origin :branch` deletes where the `--delete` deny
does not look, `origin HEAD:refs/heads/main` is the fully-qualified spelling,
`origin feature --force` puts the flag where a leading-flag deny cannot reach.
**Adding four more denies was refused.** Deny-list enumeration trails git's
refspec grammar forever, and the grammar keeps growing — which is the issue's
own conclusion — and judging three *properties* instead was not enough either.
Two review rounds took that apart: `--force-with-lease=<ref>` is not equal to a
set entry, git accepts `--for` as an abbreviation, `-fv` bundles, `--branches`
and `--all` and `--mirror` carry no refspec to inspect, `refs/heads/*` includes
`main` while equalling nothing, and bare `git push origin` names no destination
at all. That is the deny-list trailing the grammar in parser form. **The
question is inverted now**: a push is refused unless every part of it is
recognised, so the spelling nobody has listed is refused for being
unrecognised rather than admitted for being unlisted. Its suite carries the
six the issue listed and six it did not, plus the three pushes `/ship`
actually makes, because over-reach here breaks the delivery chain and would
be found at the worst moment.

**The ninth is `/review-branch`'s pair of `dotnet` grants, and what makes it
worth its own entry is that the enumeration guarding it could never have
worked.** That command holds `Write` and held `Bash(dotnet build:*)` and
`Bash(dotnet test:*)`; its `disallowed-tools` denies every tracked root file,
read from `git ls-files` by a test so a new one is a red build. But MSBuild
imports `Directory.Build.targets` into every build of every project beneath it,
and that file **does not exist in this repository** — so it is on no list read
from what exists, and no care about that test could have put it there. Write it,
run the build the command already had, and an `Exec` in it runs. Measured, in a
scratch tree: the target fires and `dotnet build` reports success. Raised in
review.

`dotnet build` was a grant the command never used — its body names
`dotnet test` alone — and `dotnet test:*` bought two things beyond the run it
was added for: any project path, and `/p:CustomBeforeMicrosoftCommonTargets=`,
which imports whatever file it names, `suggestions.md` among them. Both are
gone, replaced by `dotnet-test.sh`, whose only variable is one word out of two.
**The helper closes the executor and not the import**, so the auto-imported
names are denied as well — in **both** commands that state editing boundaries,
because the artefact outlives the command that writes it and only one of the
two had an executor. A `Directory.Build.targets` written by
`/validate-blueprint` is executed by the next thing that builds.

**Which half is measured is recorded rather than rounded up.** The exact
filenames are the spelling both files already used and the suite reads. The
`**/*.targets`-style globs beside them are the documented gitignore syntax and
are **not** measured in a `disallowed-tools` value — belt to the names' braces,
and not the control.

**The tenth is the one grant that was never wider than its operation, and
the operation was the problem.** `/review-grok` held `Edit` and `Write` for
the job it exists to do — fix every site a review names in one pass — and
read `suggestions.md` in the same invocation, so one crafted review could
steer an edit to any undenied path, unattended, inside `/ship`'s loop (#52,
#149). Narrowing the grant was refused twice, correctly: the command needs
to write, and `allowed-tools` withholds nothing. What closed it was a
**split**: a `review-adjudicator` profile with `Read`, `Grep` and `Glob`
reads the review and returns a structured record, and the writing invocation
carries the three machinery trees in `disallowed-tools`, so a finding whose
fix lands in the machinery is refused to the step that writes by the harness
rather than by a callout. The record is the residual — it is one hop
from the prose and the parent's context receives it — and what bounds an
accepted row is a predicate on the file (its quoted text is at its site) and
the rule that an edit stays inside the row's own sites. **The
`Read(suggestions.md)` deny the first form carried is gone, and why is a
measurement rather than a preference**: a command's `disallowed-tools`
propagate to the subagents it spawns, so the adjudicator's `Read`, `Grep` and
`Glob` on the review were refused too and it returned `unreadable-review`;
and a path deny reaches a `Bash` command naming the path, so the `wc -c`
preflight was refused beside it. The harness offers no deny that reaches
the parent and not the child, so the review is readable to the writing step
and the split holds by discipline — stated in the command as its residual.
**The writing invocation denies `Bash` whole, and that is the fifth entry
arriving here**: it held `Bash(wc:*)` for a size preflight and `Bash(ls:*)`
for a link check, and a redirection on either writes what the tree deny
refuses, so the deny was defence in depth for as long as they stood beside
it. The size check moved into the adjudicator, which has no `Bash` and
returns `oversized-review`; the link check became a gated premise — the
helper suite fails on any tracked mode `120000`, so `main` carries no
symbolic link on any push, and an invocation without `Bash` cannot add one.
**The premise was `main`'s and not the reviewed branch's, and #181 closed
the difference**: the branch is what introduces files, the command runs over
it locally before CI goes red on it, so a statement about what `main` tracks
was never a statement about what this command is about to edit.
`.claude/hooks/guard-edit-target.py` is the check that premise stood in for
— the second hook in this repository, and the argument for it is one
sentence: **an edit target must be the file its path spells.** It resolves
the target of every `Edit`, `Write`, `NotebookEdit` and `MultiEdit` — the
matcher, the hook's own `EDITING_TOOLS` and the registration case all name
those four, and a boundary documented narrower than the one configured is the
drift this file exists to refuse — re-anchors it on the
resolved checkout root, and refuses the call when the two disagree — into a
denied tree, or out of the checkout. The premise stays as defence in depth
and its gate stays green. The bare tool name is the documented form of a
`disallowed-tools` entry; the pattern form is the one the fifth entry
measured.

**That guard holds no copy of any deny list, and refusing to write one is
the whole of why it is safe to run on every write this repository makes.**
A path deny is *correct* when the spelling is true of the file, so the hook
judges only the disagreement and leaves every list — `.claude/settings.json`'s
and both commands' frontmatter — to say what may be edited. Nothing here goes
stale as those change, and the PR that lifts a deny to edit a helper is not
refused by the guard on its way past. A test pins that control: a denied tree
spelled as itself is **admitted** here.

**Two properties of the resolution are load-bearing and were each found by
running it rather than by reasoning about it.** The anchor is resolved as
well as the target, because a checkout reached through a link — `/tmp` on
macOS, an 8.3 or `subst` path on Windows — would otherwise make every edit
under it look like the thing the guard refuses; and the comparison folds case
where the **filesystem** does, because Windows' `realpath` answers with the
on-disk case and `DOCS/x` is not a link. **Filesystem, not platform, and the
distinction is the second bypass this guard shipped with**:
`os.path.normcase` folds on Windows and nowhere else, while macOS mounts APFS
case-insensitively by default — so `/Users/x/Repo` and `/users/x/repo` are one
directory there, a target spelled in the other case was under no anchor at
all, and the fall-through admits. It is asked of the filesystem now, by
`stat`ing a case-flipped spelling and comparing device and inode. Raised by
Copilot; the platform default stands in only where the probe cannot run. The
two platforms also
disagree about `..` after a link — POSIX resolves it against the link's
target, Windows collapses it before the filesystem sees it — and the guard
follows each rather than picking one, measured both ways in
`.claude/scripts/test_edit_target_guard.py`.

**What an anchor is, and why it is two rules rather than one, is the part
that shipped wrong.** An anchor excuses exactly one link traversal: the one
on its own root prefix. So an anchor is a checkout **root** — the event's
`cwd` is walked up to the directory holding a `.git` and dropped if it has
none — and **every** anchor containing the target must agree, where the first
form admitted on the first that did. Either rule alone leaves the bypass: a
session standing in `docs/tree`, where `tree` links into `.claude/scripts`,
took that directory as an anchor, and re-anchoring `docs/tree/helper.sh` on
it makes the spelling and the resolution agree — so the guard admitted
precisely the write it exists to refuse. Raised by Copilot, and **measured
both ways** against the shipped commit: it admits, and the fix refuses.
Requiring agreement is also what makes an environment-supplied
`CLAUDE_PROJECT_DIR` safe to take as given, since an added anchor can then
only narrow the guard and never widen it.

**A spelling can also evade the matcher through the path grammar rather than
through a link, and two of those were measured here — both admitted before the
guard refused them.** Windows names one file in more than one alphabet: the
extended-length prefix `\\?\` and the device prefix `\\.\`, which exist
precisely to skip the normalisation a matcher depends on, and the UNC form
`\\server\share\…`, which reaches the local disk through the administrative
shares. With `.claude/sandbox/**` denied, a `Write` to
`\\?\C:\dev\ashamray\.claude\sandbox\probe-unc.txt` **was created**, and so was
one to `\\localhost\C$\dev\ashamray\.claude\sandbox\probe-share.txt`; the plain
spelling of either file is refused. Both probe files were deleted.

**So the guard refuses the family rather than the prefixes** — every spelling
beginning `\\`, unless an anchor that is itself named that way **contains the
target**. The exemption was session-wide for one round, which reopened the
bypass it was carved for: once any checkout was `\\`-spelled, an alias of that
same checkout — `\\?\UNC\localhost\C$\…`, not lexically under
`\\localhost\C$\…` — skipped the anchor loop and was admitted. Measured with
the administrative share as the project directory, and raised by Copilot. The
legitimate case it protects is a repository genuinely on a network share,
editing its own files. Enumerating the
prefixes is the deny-list shape this repository has rejected twice, and the UNC
form is exactly what such a list missed. Refused rather than resolved because a
hook can only allow or deny: it cannot hand the matcher the plain spelling it
would have judged. **The 8.3 alias is the same class again and needs no special
case**: `.claude` has the alias `CLAUDE~1` on this volume, `realpath` answers
with the long name, and the spelling therefore disagrees with the file.
Whether the matcher alone would have refused that one is untested — the hook
runs first — so it stands as defence in depth rather than as a measured gap.

**Then CI found the general form, and it replaced the list those spellings
were becoming.** A Windows runner's `GetShortPathNameW` shortens the whole
prefix rather than the leaf, so `C:\Users\RUNNER~1\…\GUARD-~1\DOCUME~1\a.md`
matched no anchor at all and fell through to the residual while resolving
squarely inside a checkout — the 8.3 case went red there having passed on a
volume where only the leaf is aliased. Case folding and Unicode composition
had each closed one spelling by teaching the comparison an equivalence, which
is a deny-list in slower clothing. **The rule now is that the residual belongs
to a file genuinely outside every checkout, and not to one inside under a name
the anchors cannot place**: a target resolving into a checkout that did not
recognise its spelling is refused, whatever alphabet the spelling is in.
Measured against the commit that shipped it — admitted there, refused after —
and the control is the worktree case, where a session standing in the aliased
directory makes it a root of its own and the spelling is recognised again.

**Unicode normalisation is the same shape on a different platform, and it took
two rounds because the first fix argued its way past the probe.** A
case-insensitive APFS volume is also insensitive to normalisation, so `é`
composed and `e` plus a combining accent name one directory there and two
strings in a comparison — a checkout prefix spelled in the other form matched
no anchor and reached the branch that admits. The fix composed to NFC
**unconditionally**, on the reasoning that composing "can never make two paths
look like one" — which is false wherever the mount is normalisation-sensitive:
NTFS and ext4 keep both names, so a link resolving to the same relative path
under the coexisting sibling compared equal to a path inside the checkout, and
the escape was admitted. Measured on NTFS against the commit that shipped it.
So normalisation is probed exactly as case is and the two answers travel
together as the anchor's traits. **Both were raised by Copilot, and the second
is the one worth carrying: an equivalence asserted about "filesystems" rather
than measured on the mount is a bypass in whichever direction the assertion is
wrong.**

**The traits are the root's, and a child directory may disagree — which is true,
and turns out not to be a bypass in the shape it was raised.** Windows sets case
sensitivity **per directory** (`fsutil file setCaseSensitiveInfo`, no privilege
needed, and only on an empty one), so a case-sensitive `docs/` keeps `BETA` and
`beta` apart while the root's traits fold them. For folding to hide anything,
though, a link's resolution would have to differ from its own path only in case
— and a link's resolution *is* its target, so the write lands on exactly the
file the path names. The guard carries an identity check for the shape that
argument does not cover — a sub-mount whose equivalences differ from the root's:
where two paths agree **only** because an equivalence was applied, `samefile` is
asked. That is safe in this position and not in the anchor test, because it
compares two concrete paths rather than deciding what counts as a root. Where
either path does not exist yet — the ordinary `Write` — there is nothing to
compare and the folded verdict stands. Raised by Copilot.

**The final review round argued that this check *admits* an alias, and it has
the direction backwards — recorded rather than acted on.** The block runs only
where `same(...)` is already true, which is the branch that **admits**; the
refusal beneath it is guarded by `not same(...)`. It can therefore only turn an
admission into a refusal, and deleting it would admit strictly more. The
narrower half of the remark is true: the *folded comparison* it sits inside can
equate two spellings a case-sensitive child keeps apart. For a deny to be
evaded that way, `resolved` and `expected` must differ **purely** by case or
normalisation — which puts them at the same position under the same parents —
and a deny pattern must then name one of those spellings and not the other.
That is the residual, stated rather than closed. **Failing closed on the
missing-leaf case was the other suggestion, and it is refused on this guard's
own rule**: `expected` rarely exists when a `Write` creates a file, so refusing
there would fire on the commonest innocent call the hook sees, and a guard that
fires on innocent traffic is one somebody turns off.

**A `..` that traverses no link is admitted, and the reason is a measurement
of the harness rather than a judgement about paths.** The case for refusing it
is that `docs/../.claude/hooks/x` carries no `.claude/**` spelling, so a
matcher reading the string would not deny it. **The matcher does not read the
string.** Measured in this checkout with `.claude/sandbox/**` denied: a `Write`
to `docs/../.claude/sandbox/probe-tmp.txt` was refused with the harness's own
*"denied by your permission settings"*, while `docs/../docs/probe-tmp.txt` was
created — so a path is normalised and then matched, and `..` is not what was
rejected. Refusing every `..` in the guard would buy nothing against the deny
list and would refuse the second of those two spellings, which is innocent
traffic. Raised by Copilot, and the premise is the half that failed; a passing
case pins the verdict and names what to invert if the harness ever stops
normalising.

**Its residual is the half the harness itself needs, stated rather than
rounded up.** The subject is a target spelled *inside* a checkout the session
is standing in; a path spelled entirely outside one is not judged, because
the session's memory and scratch state are written that way by absolute path
and refusing them would take both with it. Nothing in the exposure this
closes can spell one — a review row is one plain repository-relative path and
the adjudicator drops a row that is not — so what is owed is a rule about
which out-of-tree paths are legitimate, which is a different argument from
this one. The residual is a passing test, not a paragraph alone.
**The sweeps' item 5 (#75) closed by the same shape** — a second read-only
dispatch returns a verdict, the parent opens nothing in `$work`, and
`gh-issue-create.sh` leaves `gh issue create` with no free parameter — so
the two residuals #149 named as one class went in one change, and the raw
`Bash(gh issue create:*)` is denied by name in both sweeps now that the
fifth entry's measurement exists.

**The two sweeps are one shape asking two questions**, split by what makes a
finding rather than by where they look. `/security-sweep` files what an
attacker can reach; `/bug-sweep` files what is wrong on its own terms. Both
fork a detached worktree, verify every subagent claim before filing,
de-duplicate against the whole issue set, never fail open, and file without
fixing. Three things differ: the threshold, the fan-out cut, and what
confirmation can mean — **`/bug-sweep` executes none of the snapshot it
audits**, because building a tree executes it (MSBuild targets, source
generators, analysers, and under `dotnet test` the tree's own test code) and
the audited repository is prompt-injection input. So a defect claim there is
confirmed by reading, and the class of bug only execution catches is named as
the residual.

**Both sweeps' worktrees carry the `secsweep-` prefix**, and the second is
borrowing. `git-worktree-detach.sh` and `git-worktree-drop.sh` refuse any path
that is not `secsweep-` plus six characters under the canonical temp root —
the shape check that stops a poisoned finding from naming a sibling PR worktree
and having it deleted. Renaming the prefix would have to move in both helpers
and both callers at once, so it stands; what is lost is attribution.
