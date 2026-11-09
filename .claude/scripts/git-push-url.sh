# Sourced by the three helpers that force a push, never run: the URL the force goes to. A spelled refspec pins the ref
# and not the URL, so `pushurl`, `pushInsteadOf` or `insteadOf` can point the force at another repository, where only
# that repository's branch not being at the leased commit would stop it. Each helper refuses at its own exit code; why
# is docs/harness-boundaries.md's (*Grant inventory and push helpers*).

# `host|path` of a remote URL, or non-zero when it has no such shape. The whole
# path counts, since its last components alone let `https://h/evil/o/r` collide
# with `https://h/o/r`. The host is empty for a local path and lower-cased
# otherwise; a user, a leading slash, a hosted path's trailing `.git` and https
# or ssh at its default port do not count, so those forms of one repository agree.
repo_key() {
  local url=$1 host path port
  # A query or a fragment is not part of the path a server routes on, so the
  # path of `https://host/x/y#/o/r` would read as `x/y#/o/r` and push to `x/y`.
  [[ $url != *[\?#]* ]] || return 1
  url=${url%/}
  if [[ $url =~ ^([A-Za-z][A-Za-z0-9+.-]*)://([^@/]*@)?([^:/]+)(:[0-9]+)?/(.*)$ ]]; then
    # Another port is another server. Only https and ssh are compared: `http` and
    # `git` can be read or changed on the way, and the rest fail closed.
    case "${BASH_REMATCH[1]}${BASH_REMATCH[4]}" in
      https | https:443 | ssh | ssh:22) port= ;;
      https:* | ssh:*) port=${BASH_REMATCH[4]} ;;
      *) return 1 ;;
    esac
    host=${BASH_REMATCH[3]}$port
    path=${BASH_REMATCH[5]}
  elif [[ $url =~ ^([^@/:]+@)?([A-Za-z0-9.-]{2,}):([^/:].*)$ ]]; then
    host=${BASH_REMATCH[2]}
    path=${BASH_REMATCH[3]}
  else
    # Only a plain or drive path may be hostless. Anything else with a colon is a
    # scheme or scp-style address that fits neither form above (`ssh://h:o/r`,
    # `git@h:/o/r`, `file:///o/r`, `ext::cmd`), and has no key.
    [[ $url =~ ^([A-Za-z]:)?[^:]*$ ]] || return 1
    host=
    path=$url
  fi
  if [ -n "$host" ]; then
    path=${path#/}
    path=${path%.git}
    # A hosted path names an owner and a repository at least.
    [[ $path == */* ]] || return 1
  fi
  # A dot or empty segment is a different path to the server than it reads here.
  case "/${path#/}/" in
    */./* | */../* | *//*) return 1 ;;
  esac
  printf '%s|%s' "$(printf '%s' "$host" | tr 'A-Z' 'a-z')" "$path"
}

# Non-zero, with the reason on stderr, unless a force goes to one URL, and its
# lease is read from one, each naming the host and path the remote was added
# with. A rewrite between https and ssh on one host passes; another host, port or
# path, or a transport with no key, does not. Its callers test its status, which
# suspends `set -e` inside it, so each read says its own failure.
origin_pushes_where_added() {
  local urls pushes added configured resolved fetched
  urls=$(git remote get-url --push --all origin) ||
    { echo "cannot read origin's push URLs, so nothing is published" >&2; return 1; }
  # A remote with several `url` values pushes to every one of them.
  pushes=$(printf '%s\n' "$urls" | wc -l)
  pushes=$((pushes))  # BSD wc pads its count, and arithmetic reads it as a number
  [ "$pushes" -eq 1 ] ||
    { echo "origin has $pushes push URLs; a force goes to every one of them, so nothing is published" >&2; return 1; }
  # A `pushurl` hides the `url` values from the count above, and the fetch and the
  # lease read the first of them where `--get` answers the last.
  added=$(git config --get-all remote.origin.url) || added=
  case "$added" in
    *$'\n'*) echo "origin has more than one url, and a force need not go to the one read; nothing is published" >&2
             return 1 ;;
  esac
  configured=$(repo_key "$added") || configured=
  resolved=$(repo_key "$urls") || resolved=
  [ -n "$configured" ] && [ "$configured" = "$resolved" ] ||
    { echo "origin pushes to '$resolved' but was added as '$configured'; a redirected force is not published" >&2
      return 1; }
  # `insteadOf` moves the fetch and `pushInsteadOf` can put the push back, which
  # leaves the comparison above whole with the lease read from elsewhere.
  fetched=$(git remote get-url origin) ||
    { echo "cannot read origin's fetch URL, so nothing is published" >&2; return 1; }
  fetched=$(repo_key "$fetched") || fetched=
  [ "$configured" = "$fetched" ] ||
    { echo "origin fetches from '$fetched' but was added as '$configured'; a lease read there is none here; nothing is published" >&2
      return 1; }
}
