"""The quality sets docs/token-plan.md step 1 owes, and the recall score against them.

Subcommands: build-sweeps, build-reviews, score, run. README.md owns the invocations,
what each set holds and what a score means.
"""

import argparse
import collections
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
SWEEPS = HERE / "sweeps.json"
REVIEWS = HERE / "reviews.json"

SWEEP_TRAILER = "Filed by an authorised sweep"
REVIEWERS = ("Copilot",)
LINE_WINDOW = 10
TEXT_CAP = 400
TOKEN_KEYS = ("input_tokens", "cache_creation_input_tokens", "cache_read_input_tokens", "output_tokens")
SHA = re.compile(r"\b[0-9a-f]{40}\b")
FOUND_AT = re.compile(r"(?i)\bfound by\b.*?\bat ([0-9a-f]{7,40})\b")
PATH = re.compile(r"[A-Za-z0-9_.@+-]+(?:/[A-Za-z0-9_.@+-]+)*\.(?:cs|py|sh|ps1|md|ya?ml|json|props|csproj|sql|tpl|txt|slnx)\b")


def run(*argv, cwd=None, check=True, stdin=None):
    done = subprocess.run(argv, cwd=cwd or REPO, input=stdin, capture_output=True, encoding="utf-8", errors="replace")
    if check and done.returncode:
        raise SystemExit(f"{' '.join(argv[:3])} exited {done.returncode}: {done.stderr.strip()[:300]}")
    return done


def git(*args, cwd=None, check=True):
    return run("git", *args, cwd=cwd, check=check)


def git_bash():
    """Git's own bash beside git.exe where there is one: a bare `bash` from Windows Python is the WSL relay."""
    exe = shutil.which("git")
    for parent in Path(exe).resolve().parents if exe else ():
        for relative in ("bin/bash.exe", "usr/bin/bash.exe"):
            if (parent / relative).exists():
                return str(parent / relative)
    return "bash"


def tree_paths(commit, cache={}):
    """Every path at a commit, or None when the commit is not in this clone."""
    if commit not in cache:
        done = git("ls-tree", "-r", "--full-tree", "--name-only", commit, check=False)
        cache[commit] = set(done.stdout.splitlines()) if done.returncode == 0 else None
    return cache[commit]


def resolve_commit(text):
    """A full sha for a sha or an abbreviation this clone holds, else None."""
    done = git("rev-parse", "--verify", "--quiet", f"{text}^{{commit}}", check=False)
    return done.stdout.strip() or None


def pinned_commit(body):
    """The commit a sweep issue was found on: its pinned sha, else 'found ... at <sha>'."""
    full = SHA.search(body)
    if full:
        return full.group(0)
    short = FOUND_AT.search(body)
    return resolve_commit(short.group(1)) if short else None


def affected_text(title, body):
    """Where a sweep issue says the defect is: its Affected lines section, else everything."""
    section = re.search(r"(?ms)^## Affected lines\s*\n(.*?)(?=^## |\Z)", body)
    return title + "\n" + (section.group(1) if section else body)


def issue_paths(title, body, paths_at_commit):
    """The paths the issue's location names that exist at its commit, in first-named order."""
    found = []
    by_name = {}
    for path in paths_at_commit:
        by_name.setdefault(path.rsplit("/", 1)[-1], []).append(path)
    for token in PATH.findall(affected_text(title, body)):
        token = token.removeprefix("./")
        if token in paths_at_commit:
            hit = token
        elif "/" not in token and len(by_name.get(token, ())) == 1:
            hit = by_name[token][0]
        else:
            continue
        if hit not in found:
            found.append(hit)
    return found


def sweep_entry(issue):
    """One entry for an issue a sweep filed, or None when it is not one."""
    body = issue["body"] or ""
    if SWEEP_TRAILER not in body:
        return None
    labels = {label["name"] for label in issue["labels"]}
    commit = pinned_commit(body)
    present = tree_paths(commit) if commit else None
    return {
        "issue": issue["number"],
        "title": issue["title"],
        "kind": "security" if "security" in labels else "bug",
        "severity": next((s for s in ("critical", "high", "medium", "low") if s in labels), None),
        "commit": commit if present is not None else None,
        "paths": issue_paths(issue["title"], body, present) if present is not None else [],
    }


def build_sweeps(issues):
    entries = [e for e in (sweep_entry(i) for i in issues) if e]
    return sorted(entries, key=lambda e: e["issue"])


def clean(body):
    """A comment's text on one line, capped, with any value after a password key masked.

    A review about a connection-string guard quotes a sample password, and the secret scan
    cannot tell a sample from a credential.
    """
    text = re.sub(r"(?i)(password|pwd)(\s*=\s*)\S+", r"\1\2<value>", " ".join(body.split()))
    return text[:TEXT_CAP].rstrip()


def review_entries(comments, base_of):
    """Entries from a repository's review comments: each top-level one by a reviewer in REVIEWERS.

    base_of maps a pull request number to its base sha; a comment on a commit this clone lacks,
    or on a pull request with no base, is dropped, because nothing can be run against it.
    """
    entries = []
    for comment in comments:
        if comment["user"]["login"] not in REVIEWERS or comment.get("in_reply_to_id"):
            continue
        pr = int(comment["pull_request_url"].rsplit("/", 1)[-1])
        line = comment.get("line") or comment.get("original_line")
        head = comment["original_commit_id"]
        if not line or pr not in base_of or tree_paths(head) is None:
            continue
        entries.append({
            "id": comment["id"], "pr": pr, "base": base_of[pr], "head": head,
            "reviewer": comment["user"]["login"], "path": comment["path"], "line": line,
            "text": clean(comment["body"]),
        })
    return sorted(entries, key=lambda e: e["id"])


def gh_json(*args):
    text = run("gh", *args).stdout
    decoder, at, out = json.JSONDecoder(), 0, []
    while at < len(text):
        while at < len(text) and text[at].isspace():
            at += 1
        if at >= len(text):
            break
        part, at = decoder.raw_decode(text, at)
        out += part if isinstance(part, list) else [part]
    return out


def build_reviews():
    comments = gh_json("api", "repos/{owner}/{repo}/pulls/comments?per_page=100", "--paginate")
    wanted = sorted({int(c["pull_request_url"].rsplit("/", 1)[-1]) for c in comments if c["user"]["login"] in REVIEWERS})
    base_of = {}
    for number in wanted:
        pull = gh_json("api", f"repos/{{owner}}/{{repo}}/pulls/{number}")[0]
        if pull.get("merged_at"):
            base_of[number] = pull["base"]["sha"]
    return review_entries(comments, base_of)


def hit(entry, finding):
    if "paths" in entry:
        return finding["file"] in entry["paths"]
    return finding["file"] == entry["path"] and abs(int(finding["line"]) - entry["line"]) <= LINE_WINDOW


def score(entries, findings, commit):
    """Recall over the entries pinned to one commit: which were found, which missed, and the count."""
    key = "commit" if entries and "paths" in entries[0] else "head"
    scoped = [e for e in entries if e[key] and commit and e[key].startswith(commit)]
    if key == "commit":
        scoped = [e for e in scoped if e["paths"]]
    found = [e for e in scoped if any(hit(e, f) for f in findings)]
    miss = [e for e in scoped if e not in found]
    ids = "issue" if key == "commit" else "id"
    return {"commit": commit, "reported": len(findings), "entries": len(scoped), "found": len(found),
            "recall": round(len(found) / len(scoped), 3) if scoped else None,
            "missed": [e[ids] for e in miss]}


def agent_json(text):
    """The JSON object an agent returned, however much prose or fencing surrounds it."""
    for match in re.finditer(r"\{", text):
        try:
            value, _ = json.JSONDecoder().raw_decode(text, match.start())
        except ValueError:
            continue
        if isinstance(value, dict) and "findings" in value:
            return value
    return None


def agent_definition(name):
    """The checkout's own definition of an agent, as the --agents JSON: a commit older than the
    agent has no file for it, and the point is to run today's reviewer on yesterday's tree."""
    text = (REPO / ".claude" / "agents" / f"{name}.md").read_text(encoding="utf-8")
    _, front, prompt = text.split("---\n", 2)
    fields = dict(line.split(": ", 1) for line in front.strip().splitlines() if ": " in line)
    return json.dumps({name: {"description": fields["description"], "prompt": prompt.strip(),
                              "tools": [t.strip() for t in fields["tools"].split(",")]}})


class UsageLimit(SystemExit):
    """An agent stopped on a usage-limit notice: every later run would, so the run ends, unscored."""


def unwrap(stdout):
    """The reply text and token usage in a `claude -p --output-format json` result.

    Output that is not that envelope is the reply itself, with no usage; an envelope with no
    reply text, as an error result is, keeps its usage and has the raw output as its reply.
    """
    try:
        result = json.loads(stdout)
    except ValueError:
        return stdout, {}
    if not isinstance(result, dict):
        return stdout, {}
    usage = result.get("usage")
    text = result.get("result")
    return text if isinstance(text, str) else stdout, usage if isinstance(usage, dict) else {}


def tokens(spent):
    """The four counts of a tally, always all four, and their sum as `total`."""
    counts = {key: spent.get(key, 0) for key in TOKEN_KEYS}
    return {**counts, "total": sum(counts.values())}


def ask(claude, agent, prompt, cwd, add_dirs=(), spent=None):
    """One non-interactive agent run; its findings, or SystemExit when it returned no JSON.

    spent, a Counter, gains the run's tokens whether or not it returned findings.
    """
    extra = [arg for d in add_dirs for arg in ("--add-dir", str(d))]
    done = run(claude, "-p", prompt, "--agents", agent_definition(agent), "--agent", agent,
               "--output-format", "json", *extra, cwd=cwd)
    text, usage = unwrap(done.stdout)
    if spent is not None:
        spent.update({key: usage[key] for key in TOKEN_KEYS if isinstance(usage.get(key), int)})
    value = agent_json(text)
    if value is None:
        if "usage limit" in text.lower():
            raise UsageLimit(f"{agent} stopped on a usage-limit notice: {text.strip()[:200]}")
        raise SystemExit(f"{agent} returned no findings JSON: {text.strip()[:200]}")
    return value["findings"]


def review_findings(claude, entries, pr, head, spent=None):
    """Run branch-reviewer once over a pull request's diff at one commit, in a detached worktree."""
    base = next((e["base"] for e in entries if e["pr"] == pr and e["head"] == head), None)
    if base is None:
        raise SystemExit(f"reviews.json holds no entry for PR {pr} at {head}")
    work = Path(tempfile.mkdtemp(prefix="qs-review-"))
    git("worktree", "add", "--detach", str(work / "tree"), head)
    try:
        tree = work / "tree"
        out = tree / "artifacts" / "review"
        out.mkdir(parents=True)
        (out / f"pr{pr}.diff").write_text(git("diff", f"{base}...{head}").stdout, encoding="utf-8")
        prompt = (f"Review the diff at artifacts/review/pr{pr}.diff, a full pass. "
                  "There is no locality verdict: name it in skipped. Return the JSON your profile declares.")
        return ask(claude, "branch-reviewer", prompt, tree, spent=spent)
    finally:
        git("worktree", "remove", "--force", str(work / "tree"), check=False)
        shutil.rmtree(work, ignore_errors=True)


def sweep_findings(claude, kind, commit, spent=None):
    """Run one auditor per slice of a full first round over a detached worktree at a commit.

    spent, a Counter, gains the tokens of every run, a retried or failed slice's included.
    """
    root = Path(tempfile.gettempdir()) / f"secsweep-{os.urandom(3).hex()}"
    git("worktree", "add", "--detach", str(root), commit)
    slices = Path(f"{root}.slices")
    try:
        run(git_bash(), str(REPO / ".claude" / "scripts" / "sweep-slices.sh"), kind, str(root), "full", cwd=root)
        found, failed = [], []
        listings = sorted(slices.glob("*.txt"), key=lambda p: int(p.stem))
        for number, listing in enumerate(listings, 1):
            prompt = (f"Root path: {root}. Scope: the whole tree. Slice list: {listing}. "
                      "Round 1, nothing known. Return the JSON your profile declares.")
            slice_spent = collections.Counter()
            for attempt in (1, 2):
                try:
                    found += ask(claude, f"{kind}-auditor", prompt, root, [slices], slice_spent)
                    break
                except SystemExit as error:
                    if isinstance(error, UsageLimit):
                        raise
                    print(f"slice {listing.name} attempt {attempt}: {error}", file=sys.stderr)
            else:
                failed.append(listing.name)
            if spent is not None:
                spent.update(slice_spent)
            print(f"slice {number}/{len(listings)}: {len(found)} findings so far, "
                  f"{tokens(slice_spent)['total']} tokens in the slice", file=sys.stderr, flush=True)
        return found, failed, len(listings)
    finally:
        git("worktree", "remove", "--force", str(root), check=False)
        shutil.rmtree(slices, ignore_errors=True)


def sample_heads(entries, count, least=3):
    """count heads spread evenly over the pull requests, from those holding at least `least` entries."""
    per_head = {}
    for entry in entries:
        key = (entry["pr"], entry["head"])
        per_head[key] = per_head.get(key, 0) + 1
    heads = sorted(key for key, n in per_head.items() if n >= least)
    if count >= len(heads):
        return heads
    return [heads[i * len(heads) // count] for i in range(count)]


def load(path):
    return json.loads(Path(path).read_text(encoding="utf-8"))


def dump(path, value):
    """One entry a line, so a rebuilt set diffs by entry."""
    rows = ",\n".join(" " + json.dumps(entry, ensure_ascii=False) for entry in value)
    Path(path).write_text(f"[\n{rows}\n]\n", encoding="utf-8", newline="\n")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    sub = parser.add_subparsers(dest="cmd", required=True)
    sub.add_parser("build-sweeps")
    sub.add_parser("build-reviews")
    scoring = sub.add_parser("score")
    scoring.add_argument("--set", required=True)
    scoring.add_argument("--findings", required=True, help='{"commit": "<sha>", "findings": [{"file", "line"}]}')
    running = sub.add_parser("run")
    running.add_argument("what", choices=("review", "sweep"))
    running.add_argument("--pr", type=int)
    running.add_argument("--head", help="review: one of the pull request's heads in reviews.json; default every head")
    running.add_argument("--sample", type=int, help="review: instead of --pr, this many heads spread over the set")
    running.add_argument("--repeat", type=int, default=1, help="review: runs per head, since a score varies run to run")
    running.add_argument("--kind", choices=("bug", "security"))
    running.add_argument("--commit")
    running.add_argument("--claude", default="claude")
    args = parser.parse_args(argv)

    if args.cmd == "build-sweeps":
        issues = gh_json("issue", "list", "--state", "all", "--limit", "2000",
                         "--json", "number,title,body,labels")
        dump(SWEEPS, build_sweeps(issues))
    elif args.cmd == "build-reviews":
        dump(REVIEWS, build_reviews())
    elif args.cmd == "score":
        given = load(args.findings)
        print(json.dumps(score(load(args.set), given["findings"], given["commit"])))
    elif args.what == "review":
        entries = load(REVIEWS)
        if args.sample:
            targets = sample_heads(entries, args.sample)
        else:
            heads = [args.head] if args.head else sorted({e["head"] for e in entries if e["pr"] == args.pr})
            if not heads or not args.pr:
                raise SystemExit("run review: --pr names a pull request with entries in reviews.json")
            targets = [(args.pr, head) for head in heads]
        for pr, head in targets:
            for attempt in range(args.repeat):
                spent = collections.Counter()
                found = review_findings(args.claude, entries, pr, head, spent)
                result = score(entries, [{"file": f["file"], "line": f.get("line", 0)} for f in found], head)
                print(json.dumps({"pr": pr, "attempt": attempt + 1, **result, "tokens": tokens(spent)}), flush=True)
    else:
        if not (args.kind and args.commit):
            raise SystemExit("run sweep: --kind and --commit are required")
        full = resolve_commit(args.commit)
        if full is None:
            raise SystemExit(f"{args.commit} is not a commit in this clone")
        spent = collections.Counter()
        found, failed, slices = sweep_findings(args.claude, args.kind, full, spent)
        print(json.dumps({"kind": args.kind, "slices": slices, "failed": failed,
                          **score(load(SWEEPS), found, full), "tokens": tokens(spent)}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
