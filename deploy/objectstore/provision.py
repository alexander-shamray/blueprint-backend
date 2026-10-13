#!/usr/bin/env python3
"""Say what the production Environment still lacks for object-store.yml's deployed job, or set it.

The job is skipped until the Environment holds what it reads (docs/secrets.md, ADR-110, ADR-114), and a skipped
job files nothing. `py -3.12 deploy/objectstore/provision.py check` lists what is missing; `apply` sets it from
the same names in the caller's environment, the opt-in last, so a half-provisioned job never runs."""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
from typing import Callable

import store_check

ENVIRONMENT = "production"
OPT_IN = "OBJECT_STORE_CHECK_SCHEDULED"
ENABLED = "enabled"

VARIABLES = (store_check.ENDPOINT, store_check.BUCKET, store_check.REGION)
SECRETS = tuple(name for pair in store_check.PAIRS.values() for name in pair) + (store_check.HEARTBEAT,)

# The environment's own address, which gh expands from the checkout's remote.
ENV_API = f"repos/{{owner}}/{{repo}}/environments/{ENVIRONMENT}"

# gh(args, stdin) -> stdout, raising store_check.Refusal on a non-zero exit.
Gh = Callable[..., str]


def gh(args: list[str], stdin: str | None = None) -> str:
    done = subprocess.run(["gh", *args], input=None if stdin is None else stdin.encode("utf-8"),
                          capture_output=True, check=False)
    if done.returncode != 0:
        # stderr names the failed call; gh never echoes a secret it was handed on stdin.
        raise store_check.Refusal(f"gh {' '.join(args[:2])}: " + done.stderr.decode("utf-8", "replace").strip())
    return done.stdout.decode("utf-8", "replace")



def names(run: Gh, args: list[str]) -> set[str]:
    return {row["name"] for row in json.loads(run(args))}


def environment_problems(run: Gh) -> list[str]:
    """The Environment must exist and admit `main` alone: the job sends the pairs to the endpoint."""
    try:
        policy = json.loads(run(["api", ENV_API]))["deployment_branch_policy"] or {}
        branches = json.loads(run(["api", f"{ENV_API}/deployment-branch-policies"]))["branch_policies"]
    except store_check.Refusal as error:
        return [f"the {ENVIRONMENT} Environment cannot be read, so it is missing or not yours to read ({error})"]
    if not policy.get("custom_branch_policies") or [b["name"] for b in branches] != ["main"]:
        return [f"the {ENVIRONMENT} Environment must admit deployments from main alone (docs/secrets.md)"]
    return []


def problems(run: Gh) -> list[str]:
    """Everything the deployed job needs that is not there; empty is a job that can run."""
    found = environment_problems(run)
    if found:
        return found
    have_variables = names(run, ["variable", "list", "--env", ENVIRONMENT, "--json", "name"])
    have_secrets = names(run, ["secret", "list", "--env", ENVIRONMENT, "--json", "name"])
    found = [f"variable {name} is not set on {ENVIRONMENT}" for name in VARIABLES if name not in have_variables]
    found += [f"secret {name} is not set on {ENVIRONMENT}" for name in SECRETS if name not in have_secrets]
    repo = {row["name"]: row["value"] for row in json.loads(run(["variable", "list", "--json", "name,value"]))}
    if repo.get(OPT_IN) != ENABLED:
        found.append(f"repository variable {OPT_IN} is not {ENABLED}, so the deployed job is skipped")
    return found


def apply(run: Gh, env: dict[str, str]) -> list[str]:
    """Set every name from `env`, the opt-in last; nothing is set unless all of it can be."""
    unset = [name for name in VARIABLES + SECRETS if not env.get(name)]
    if unset:
        raise store_check.Refusal("not configured: " + ", ".join(unset))
    found = environment_problems(run)
    if found:
        raise store_check.Refusal(found[0])
    done = []
    for name in VARIABLES + ((store_check.ADDRESSING,) if env.get(store_check.ADDRESSING) else ()):
        run(["variable", "set", name, "--env", ENVIRONMENT, "--body", env[name]])
        done.append(name)
    for name in SECRETS:
        # On stdin, so the value is in no process listing.
        run(["secret", "set", name, "--env", ENVIRONMENT], env[name])
        done.append(name)
    run(["variable", "set", OPT_IN, "--body", ENABLED])
    return done + [OPT_IN]


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("check", "apply"))
    command = parser.parse_args(argv).command
    try:
        if command == "apply":
            for name in apply(gh, dict(os.environ)):
                print(f"set {name}")
        found = problems(gh)
    except store_check.Refusal as error:
        print(f"::error::{error}")
        return 2
    for line in found:
        print(f"::error::{line}")
    if not found:
        print("The production Environment holds what the deployed job reads.")
    return 1 if found else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
