"""The release inputs the rollout refuses before Helm reads them: a tag, a digest,
the revision a tag names and the tag a running release was installed with."""

from __future__ import annotations

import re
from pathlib import Path

from descriptor_model import PlanError, ROOT, entries


# Helm's release-name alphabet: a DNS-1123 label, at most 53 characters so the
# suffixed resource names still fit 63. Every workload key is one, and `check`
# holds it to this before a shell reads it as a word or a path.
RELEASE_NAME = re.compile(r"^[a-z0-9]([-a-z0-9]{0,51}[a-z0-9])?$")


def migration_prefix(workload: str, plan_document: dict, root: Path = ROOT) -> str | None:
    """The `<workload>-migrate-` a Job name would start with, where there is one.

    None for the gateway, which owns no database (§10.1, §15.3). Derived from
    the templates on disk, because a chart gains a migrator by having the file.
    """
    workloads = entries(plan_document.get("workloads", {}))
    chart = workloads.get(workload, {}).get("chart")
    if not chart:
        return None
    if not (root / "deploy" / "helm" / chart / "templates" / "migrate-job.yaml").is_file():
        return None
    return f"{workload}-migrate-"


def validate_tag(tag: str, job_prefix: str | None = None) -> None:
    """Refuse a tag Helm's `--set-string` would read as more than a tag.

    `strvals` splits on a comma, so `deadbeef,image.registry=x` sets the registry
    too; the rule is `commerce.tag`'s (DNS-1123 labels, 63 characters at most).
    """
    if not tag:
        raise PlanError("image tag is empty: §15.3 refuses a deploy that cannot name its image")
    if len(tag) > 63:
        raise PlanError(
            f"image tag is {len(tag)} characters. It becomes "
            "app.kubernetes.io/version, and a label value may not exceed 63"
        )
    for segment in tag.split("."):
        if not re.fullmatch(r"[a-z0-9]([a-z0-9-]*[a-z0-9])?", segment):
            raise PlanError(
                f"image tag {tag!r} is not usable: the segment {segment!r} is not a "
                "DNS-1123 label. Each dot-separated segment must be lowercase "
                "alphanumerics and dashes, starting and ending alphanumeric — "
                "which also excludes the comma and equals that Helm's "
                "--set-string would read as a second assignment"
            )

    # `_migration-job.tpl` names the Job `<workload>-migrate-<tag>` and refuses
    # it past 63 at render time, after the stable track has been scaled up;
    # this moves that refusal in front of the scale-up (§7.4). The prefix is
    # the workload's descriptor's, so each deployable brings its own budget.
    if job_prefix is not None and len(job_prefix) + len(tag) > 63:
        raise PlanError(
            f"image tag {tag!r} is {len(tag)} characters, and the migration Job "
            f"would be named {job_prefix + tag!r} at {len(job_prefix) + len(tag)}. "
            "Kubernetes copies that onto every pod as the `job-name` label, which "
            f"may not exceed 63, so this workload allows {63 - len(job_prefix)} "
            "(§7.4). Refused here rather than by `helm upgrade` after the stable "
            "track has been scaled up"
        )


# The rule is `commerce.digest`'s (ADR-081). Held here as well because the
# rollout hands the value to `--set-string`, where a comma is a second
# assignment, exactly as it is for the tag.
DIGEST = re.compile(r"sha256:[0-9a-f]{64}")


def validate_digest(digest: str) -> None:
    """Refuse a digest Helm's `--set-string` would read as more than a digest."""
    if not DIGEST.fullmatch(digest):
        raise PlanError(
            f"image digest {digest!r} is not usable: it is sha256: and 64 lowercase "
            "hex characters, as a registry reports it (ADR-081). Anything else "
            "would reach `--set-string` unchecked, where a comma is a second "
            "assignment"
        )


def verified_digest(verified: object) -> str:
    """The one digest every signature `cosign verify -o json` accepted is over.

    Read from what was verified, not looked up again from the tag, which an
    overwrite could answer differently (ADR-081).
    """
    if not isinstance(verified, list) or not verified:
        raise PlanError("cosign verified no signature, so there is no digest to pin (ADR-081)")
    digests = set()
    for entry in verified:
        try:
            digests.add(entry["critical"]["image"]["docker-manifest-digest"])
        except (KeyError, TypeError) as error:
            raise PlanError(
                "a verified signature names no critical.image.docker-manifest-digest, "
                "so the digest it covers cannot be read (ADR-081)"
            ) from error
    if len(digests) != 1:
        raise PlanError(
            f"the verified signatures cover {len(digests)} different digests, "
            "and the rollout can pin only one (ADR-081)"
        )
    (digest,) = digests
    if not isinstance(digest, str):
        raise PlanError("a verified signature's digest is not a string (ADR-081)")
    validate_digest(digest)
    return digest


# §15.2 builds both of a service's images with the commit as the tag, so a
# tag here is a commit object name. ADR-050 is why the rollout insists on it:
# every fact this script derives from `src/` describes the running image, and
# it can only be read from the revision that image was built from.
IMAGE_REVISION = re.compile(r"^[0-9a-f]{40}$")


def image_revision(tag: str) -> str:
    """The commit an image tag names, or a refusal.

    Full length and lower case, because an abbreviation resolves too and the
    rollout would report a revision spelled unlike the tag (ADR-050).
    """
    if not IMAGE_REVISION.fullmatch(tag):
        raise PlanError(
            f"the image tag {tag!r} names no revision. §15.2 tags every image "
            "with the commit it was built from, and §15.5's rollout reads that "
            "image's probe routes, registrations and entry assembly out of the "
            "same commit (ADR-050). A tag that is not a commit leaves all three "
            "read from whatever the runner happened to check out"
        )
    return tag


def installed_tag(values: dict) -> str:
    """The image tag the running release was installed with.

    `helm get values --all` is the only place to read the revision behind the
    stable track's probe routes; every chart requires the tag (§15.3).
    """
    tag = values.get("image", {}).get("tag")
    if not isinstance(tag, str) or not tag.strip():
        raise PlanError(
            "the installed release names no image.tag, so the revision its "
            "probe routes come from cannot be read (ADR-050)"
        )
    return tag.strip()
