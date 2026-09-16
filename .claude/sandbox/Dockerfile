# The external reviewer's boundary.
#
# A disposable worktree bounds only a reviewer's edits, not the host's
# filesystem, network and credentials: `gh` is authenticated on the host, and a
# deny list bounds the session, never the subprocess it starts. What does not
# enter this image is the point: no gh token, no SSH keys, no ~/.aws, no host
# filesystem beyond the one directory the review is about.
#
# What does cross is one of two things: XAI_API_KEY by environment, or — when
# no usable key is present — writable copies of three files: ~/.grok/auth.json,
# agent_id and config.toml. Writable because grok refreshes a session mid-run;
# copies because the container must not be able to rewrite this host's
# credential file. All three are grok's and only grok's.
#
# Egress is confined to api.x.ai and auth.x.ai by two containers of this image,
# the reviewer's and egress-proxy.py's; grok-review.sh's header argues how, and
# states the crossing credential's residual.

FROM debian:trixie-slim

# git, because /review-branch spends its whole life in it. curl and CA certs for
# the pinned release download below and the model calls afterwards.
#
# python3 for the one gate the review is asked to run itself: the licence gate
# is stdlib-only and needs no restore, which is why §15.1 puts it ahead of the
# build. python-is-python3 because review-branch.md invokes
# `python licence_gate.py` and Debian's python3 package ships no `python`. The
# .NET SDK is absent: `dotnet test` needs a Docker daemon for its
# Testcontainers suites, which here would mean Docker-in-Docker inside a
# sandbox built to take capability away, so the host owns that gate
# (review-branch.md).
RUN apt-get update \
    && apt-get install --yes --no-install-recommends \
        ca-certificates \
        curl \
        git \
        python3 \
        python-is-python3 \
    && rm --recursive --force /var/lib/apt/lists/*

# The reviewer's only way out, baked into the image rather than bind-mounted:
# a mount would need `:Z` on an SELinux host, and `:Z` relabels the file it
# names — which would be a file inside this repository's checkout. On PATH
# under a bare name, because grok-review.sh runs under MSYS on one host and
# MSYS rewrites a leading-slash argument into a Windows path; `--workdir
# /review` already costs that script an exclusion, and this would be a second.
# Root-owned and readable by everyone, which is all a script needs. It is run
# by grok-review.sh in a second container of this image — no clone, no
# credential, and a leg on the bridge the reviewer's container does not have.
COPY --chmod=755 egress-proxy.py /usr/local/bin/egress-proxy

# Not root. The container is the boundary, but a reviewer that is root inside it
# is one escape away from being root outside, and it needs no privilege a plain
# user lacks.
#
# The uid is an argument because it has to match the host's on Linux: the clone
# and the credential copies are bind-mounted with their host ownership, and
# safe.directory only silences git's ownership check — it grants no filesystem
# access. Docker Desktop maps ownership and hides this.
#
# An id the base image already uses is reused, not created: `groupadd` and
# `useradd` refuse an id that exists, and debian:trixie-slim already holds gid
# 20 (dialout, a typical macOS primary group), gid 100 (users, a common Linux
# one) and uid/gid 65534 (nobody/nogroup). A taken uid has its account renamed
# into `reviewer` rather than joined by `useradd --non-unique`, which would
# leave the base's name first in /etc/passwd, so getpwuid would answer `nobody`
# with a home of /nonexistent. The name and the home are load-bearing —
# grok-review.sh mounts the credentials at /home/reviewer/.grok/, and `USER`
# below is by name — so the last line checks both.
#
# 0:0 is refused, because reuse would rename root into `reviewer` and hand the
# review a root shell. grok-review.sh refuses 0 before the build; this is the
# same refusal for a build invoked by hand.
ARG REVIEWER_UID=1000
ARG REVIEWER_GID=1000
RUN set -eu; \
    [ "${REVIEWER_UID}" -ne 0 ] && [ "${REVIEWER_GID}" -ne 0 ] || \
        { echo "refusing REVIEWER_UID=${REVIEWER_UID} REVIEWER_GID=${REVIEWER_GID}: the reviewer is non-root by design, and reusing root's account would make it root" >&2; exit 1; }; \
    getent group "${REVIEWER_GID}" >/dev/null \
        || groupadd --gid "${REVIEWER_GID}" reviewer; \
    existing="$(getent passwd "${REVIEWER_UID}" | cut -d: -f1)"; \
    if [ -n "${existing}" ]; then \
        usermod --login reviewer --comment "" --gid "${REVIEWER_GID}" \
            --home /home/reviewer --shell /bin/sh "${existing}"; \
        mkdir -p /home/reviewer; \
        chown "${REVIEWER_UID}:${REVIEWER_GID}" /home/reviewer; \
        chmod 700 /home/reviewer; \
    else \
        useradd --create-home --uid "${REVIEWER_UID}" --gid "${REVIEWER_GID}" reviewer; \
    fi; \
    [ "$(id -u reviewer)" = "${REVIEWER_UID}" ] \
        && [ "$(id -g reviewer)" = "${REVIEWER_GID}" ] \
        && [ "$(getent passwd reviewer | cut -d: -f6)" = /home/reviewer ] \
        && [ -d /home/reviewer ]
USER reviewer
ENV HOME=/home/reviewer

# The release artefact, fetched directly as `reviewer`, so the CLI sits in this
# user's home beside the credentials mounted into it.
#
# `--fail`, and never a pipe into a shell: `RUN a | b` reports only b's status
# under /bin/sh, and bash handed an empty script exits 0, which publishes an
# image with no grok in it. An empty or truncated download cannot match the
# digest pin either.
#
# The version tracks the host's client: a different client can resolve the
# host's OAuth session to a newly created team carrying no credits, so every
# review dies on `403 permission-denied` while the host keeps working against
# the same file. A credential must mean the same thing on both sides of the
# mount.
#
# PATH is set before the install rather than after, so the `grok --version`
# below is a real check rather than an exit 127 that says nothing about
# whether grok installed.
ENV PATH="/home/reviewer/.grok/bin:${PATH}"

# The binary's digest is checked before anything executes it. This image is
# the security boundary grok-review.sh hands the credentials to, so an
# unverified fetch gives anyone able to alter the artefact or its delivery —
# upstream, or DNS/CDN/TLS interception on the developer's network — code
# execution inside the boundary on the next build. x.ai's installer is not
# used: it performs no verification of the binary it downloads, and it runs
# the binary before any later check, so a malicious binary could use that
# execution to put the expected bytes where the check reads them.
#
# The digests were taken by fetching each artefact and hashing it: trust on
# first use, after which a change to what x.ai serves for a pinned version
# fails the build, and moving the pin is a reviewed diff — the property
# Directory.Packages.props and global.json are here for. An architecture with
# no recorded digest fails rather than building unverified.
#
# What replaces the installer is the steps it performs on Linux: the binary in
# `downloads/`, `chmod +x`, relative symlinks for `grok` and `agent` in `bin/`
# (relative because they survive a bind-mounted `$HOME`), and the `[cli]` block
# in `config.toml`. Its shell completions are omitted, because producing them
# runs the binary and no reviewer has a shell to complete in. `grok --version`
# is the binary's first execution, after the digest matched.
ARG GROK_VERSION=1.0.5
ARG GROK_SHA256_X86_64=9ba87444e1819e8f6104adbbf4676a870c204380aa5c3e1c38a926c4ea677238
ARG GROK_SHA256_AARCH64=1c1fe67d7c35497fb09f44a451f57acc3787add4c9aea2c56f5c7c75dc5ffcf1
# Compared with `[ "$a" = "$b" ]` rather than piped into `sha256sum --check`,
# because the recorded digest arrives through a build argument: a stray
# character on it — a CR from a CRLF checkout of this file, most plausibly —
# would make `--check` fail to parse its input, which is a different error from
# a mismatch and reads like a broken command rather than a bad digest. A string
# comparison says which value disagreed, whatever is wrong with either.
RUN set -eu; \
    case "$(uname -m)" in \
        x86_64|amd64) arch=x86_64; want="${GROK_SHA256_X86_64}" ;; \
        aarch64|arm64) arch=aarch64; want="${GROK_SHA256_AARCH64}" ;; \
        *) echo "no recorded grok ${GROK_VERSION} digest for $(uname -m); add one rather than building an unverified reviewer" >&2; exit 1 ;; \
    esac; \
    curl --fail --silent --show-error --location \
        "https://x.ai/cli/grok-${GROK_VERSION}-linux-${arch}" --output /tmp/grok; \
    got="$(sha256sum /tmp/grok | cut -d' ' -f1)"; \
    [ "$got" = "${want}" ] || \
        { echo "grok ${GROK_VERSION} ${arch} sha256 ${got} does not match the recorded ${want}" >&2; exit 1; }; \
    mkdir -p "${HOME}/.grok/downloads" "${HOME}/.grok/bin"; \
    mv /tmp/grok "${HOME}/.grok/downloads/grok-linux-${arch}"; \
    chmod +x "${HOME}/.grok/downloads/grok-linux-${arch}"; \
    ln -sf "../downloads/grok-linux-${arch}" "${HOME}/.grok/bin/grok"; \
    ln -sf "../downloads/grok-linux-${arch}" "${HOME}/.grok/bin/agent"; \
    printf '[cli]\ninstaller = "internal"\n' > "${HOME}/.grok/config.toml"; \
    grok --version

# The mounted clone carries its host ownership, so git refuses it as dubiously
# owned. Declared here rather than left to the reviewer, because a reviewer that
# edits git configuration to get started needs a grant that includes doing so.
RUN git config --global --add safe.directory /review

# The review's only view of the repository, bind-mounted at run time. A clone
# rather than a worktree: a worktree's .git is a file pointing back into the
# main checkout, which is exactly the path this image refuses to mount, so git
# inside would resolve nothing.
WORKDIR /review
