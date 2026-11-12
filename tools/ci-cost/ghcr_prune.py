#!/usr/bin/env python3
"""Delete the GHCR versions ADR-199 does not keep. Without --delete it only reports them.

    python tools/ci-cost/ghcr_prune.py [--repo owner/name] [--delete | --unmatrixed]
"""

from __future__ import annotations

import argparse
import re
import sys
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta, timezone
from pathlib import Path

from ci_cost import (Fetch, Manifest, SEARCH_CAP, WORKERS, gh, origin_repo, package_root, paged, parse, registry,
                     versions_path)

KEEP_DAYS = 14
KEEP_NEWEST = 10
# A six-hourly run at this cap outpaces what publish-images pushed in Window B, and limits what one bad run can do.
MAX_DELETES = 400
COMMIT_TAG = re.compile(r"[0-9a-f]{40}")
REFERRER_TAG = re.compile(r"sha256-[0-9a-f]{64}")
# deploy.yml's run-name, which is the one place the API keeps a dispatch's inputs.
ROLLOUT_TITLE = re.compile(r"Roll \S+ to (.*)")
ROLLOUT_RUNS = "actions/workflows/deploy.yml/runs?event=workflow_dispatch"
CI_WORKFLOW = Path(__file__).parents[2] / ".github" / "workflows" / "ci.yml"
IMAGE_LEG = re.compile(r"^\s+image: ([a-z0-9-]+)\s*$", re.MULTILINE)


def image_packages(repo: str, workflow: Path = CI_WORKFLOW) -> list[str]:
    """The packages `ci.yml`'s matrix pushes (ADR-200), under the repository's name, since `$REGISTRY` carries it."""
    images = IMAGE_LEG.findall(workflow.read_text(encoding="utf-8"))
    if not images:
        raise LookupError(f"{workflow} names no image, so the matrix's shape moved and this read is what is broken")
    return sorted({f"{repo.split('/')[1]}/{image}" for image in images})


def unmatrixed(fetch: Fetch, repo: str, root: str, matrix: list[str]) -> list[str]:
    """The repository's container packages the matrix no longer names, which the prune never judges again.

    The owner's package listing answers a job's token with HTTP 400 (ADR-200), so only a person's token reads it.
    """
    listed = {package["name"] for package in paged(fetch, f"{root}?package_type=container")
              if (package.get("repository") or {}).get("full_name") == repo}
    return sorted(listed - set(matrix))


def rolled_tags(fetch: Fetch, repo: str) -> set[str]:
    """Every commit a dispatch of the rollout named. A title this cannot read refuses, since a miss loses an image.

    A tag that is no commit names nothing: `image_revision` refuses that rollout, so no release pins what it named.
    """
    runs = f"repos/{repo}/{ROLLOUT_RUNS}"
    total = fetch(f"{runs}&per_page=1")["total_count"]
    if total > SEARCH_CAP:
        raise LookupError(f"{total} rollouts, more than the runs endpoint answers: a rolled tag could be missed")
    tags = set()
    for run in paged(fetch, runs, "workflow_runs"):
        match = ROLLOUT_TITLE.fullmatch(run["display_title"])
        if not match:
            raise LookupError(f"run {run['id']} is titled {run['display_title']!r}, which names no tag: read what "
                              "it rolled, keep that image by a tag of its own, and delete the run")
        if COMMIT_TAG.fullmatch(match[1]):
            tags.add(match[1])
    return tags


def tags_of(version: dict) -> list[str]:
    return version["metadata"]["container"]["tags"]


def touched(version: dict) -> datetime:
    # A rebuild of identical bytes tags an existing version, which moves only its updated_at.
    return max(parse(version["created_at"]), parse(version["updated_at"]))


def kept_for_themselves(versions: list[dict], rolled: set[str], now: datetime) -> list[dict]:
    """The versions kept whatever else names them: recent, among the newest images, rolled, or with another tag.

    Another tag is neither a commit nor a referrers tag: `main`, one put on by hand, and the rollout's own on the
    digest it verified (ADR-201), so a rebuilt commit tag moves alone.
    """
    cut = now - timedelta(days=KEEP_DAYS)
    images = [v for v in versions if any(COMMIT_TAG.fullmatch(tag) for tag in tags_of(v))]
    newest = sorted(images, key=touched, reverse=True)[:KEEP_NEWEST]
    return [v for v in versions if v in newest or touched(v) >= cut or any(
        tag in rolled or not (COMMIT_TAG.fullmatch(tag) or REFERRER_TAG.fullmatch(tag)) for tag in tags_of(v))]


def closure(manifest: Manifest, package: str, version: dict, referrers: dict[str, str]) -> set[str]:
    """The digests one version needs: an index's children, and each one's signature and attestations."""
    needed: set[str] = set()
    waiting = [version["name"]]
    while waiting:
        digest = waiting.pop()
        if digest in needed:
            continue
        needed.add(digest)
        waiting += [child["digest"] for child in manifest(package, digest).get("manifests", [])]
        if digest.replace(":", "-") in referrers:
            waiting.append(referrers[digest.replace(":", "-")])
    return needed


def deletion_order(version: dict) -> tuple[int, datetime]:
    """Images, then other tags, then what they name, so a capped run never leaves a tag that no longer pulls."""
    tags = tags_of(version)
    return (0 if any(COMMIT_TAG.fullmatch(tag) for tag in tags) else 1 if tags else 2, touched(version))


def plan(manifest: Manifest, package: str, versions: list[dict], rolled: set[str], now: datetime,
         workers: int = WORKERS) -> list[dict]:
    """What may go. Any manifest left unread raises, so a package is never judged on half its graph."""
    kept = kept_for_themselves(versions, rolled, now)
    referrers = {tag: v["name"] for v in versions for tag in tags_of(v) if REFERRER_TAG.fullmatch(tag)}
    needed = {v["name"] for v in kept}
    with ThreadPoolExecutor(workers) as pool:
        for digests in pool.map(lambda v: closure(manifest, package, v, referrers), [v for v in kept if tags_of(v)]):
            needed |= digests
    return [v for v in versions if v["name"] not in needed]


def main(argv: list[str] | None = None, fetch: Fetch = gh, manifest: Manifest | None = None,
         now: datetime | None = None, delete: Fetch | None = None, packages: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--repo", help="owner/name; the origin remote's by default")
    parser.add_argument("--delete", action="store_true", help="delete what the report names")
    parser.add_argument("--unmatrixed", action="store_true",
                        help="only list the packages the image matrix dropped; needs a person's token, not a job's")
    args = parser.parse_args(argv)
    repo = args.repo or origin_repo()
    root = package_root(fetch, repo)
    if args.unmatrixed:
        dropped = unmatrixed(fetch, repo, root, packages or image_packages(repo))
        for package in dropped:
            print(f"{package}: in no image leg of ci.yml, so no run prunes it")
        print(f"{len(dropped):,} packages the matrix dropped")
        return 1 if dropped else 0
    now = now or datetime.now(timezone.utc)
    manifest = manifest or registry(repo)
    rolled = rolled_tags(fetch, repo)

    failed = 0
    doomed: list[tuple[str, dict]] = []
    for package in packages or image_packages(repo):
        try:
            versions = list(paged(fetch, versions_path(root, package)))
            going = plan(manifest, package, versions, rolled, now)
        except LookupError as error:
            print(f"{package}: nothing judged, a read was refused: {error}")
            failed += 1
            continue
        print(f"{package}: {len(versions):,} versions, {len(going):,} not kept")
        doomed += [(package, version) for version in going]

    doomed.sort(key=lambda pair: deletion_order(pair[1]))
    print(f"{len(doomed):,} versions in all, of which a run deletes at most {MAX_DELETES:,}")
    if not args.delete:
        print("Nothing deleted: this run was not asked to.")
        return 1 if failed else 0

    remove = delete or (lambda path: gh(path, "DELETE"))
    deleted = 0
    # A tag that would not go keeps what it names until a later run gets past it.
    blocked: set[str] = set()
    for package, version in doomed[:MAX_DELETES]:
        if package in blocked and not any(COMMIT_TAG.fullmatch(tag) for tag in tags_of(version)):
            continue
        try:
            remove(f"{versions_path(root, package)}/{version['id']}")
            deleted += 1
        except LookupError as error:
            print(f"{package}@{version['name']} not deleted: {error}")
            failed += 1
            blocked.add(package)
    print(f"{deleted:,} versions deleted")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
