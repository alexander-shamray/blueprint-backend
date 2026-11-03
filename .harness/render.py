"""Render a target's .claude/settings.json from the harness base plus its config.

Stdlib only, with no package-relative import: each target holds a byte-identical copy as .harness/render.py,
where no harness_sync package exists and the drift gate runs it (owner: shared-harness spec 5.2).
"""

import copy
import json
import re


class RenderError(Exception):
    """The config asks for something the base forbids."""


_SETTINGS_KEYS = ("env", "allow", "deny", "hooks")
_WRITE_RULE = re.compile(r"^Write\s*\(")


def validate_settings(settings: object) -> None:
    """Check the shape of a config's settings block; raise RenderError naming the offending key."""
    if not isinstance(settings, dict):
        raise RenderError("'settings' must be an object")
    for key in settings:
        if key not in _SETTINGS_KEYS:
            raise RenderError(f"unknown key {key!r} in 'settings'")
    env = settings.get("env", {})
    if not isinstance(env, dict) or not all(isinstance(k, str) and isinstance(v, str) for k, v in env.items()):
        raise RenderError("'env' must be an object of strings")
    for key in ("allow", "deny"):
        value = settings.get(key, [])
        if not isinstance(value, list) or not all(isinstance(item, str) for item in value):
            raise RenderError(f"{key!r} must be a list of strings")
    hooks = settings.get("hooks", {})
    if not isinstance(hooks, dict):
        raise RenderError("'hooks' must be an object of lists, one per event")
    for event, groups in hooks.items():
        if not isinstance(groups, list):
            raise RenderError(f"'hooks' event {event!r} must be a list of matcher groups")
        for group in groups:
            inner = group.get("hooks") if isinstance(group, dict) else None
            if not isinstance(inner, list):
                raise RenderError(f"'hooks' event {event!r}: each group must be an object with a 'hooks' list")
            for hook in inner:
                if not (isinstance(hook, dict) and isinstance(hook.get("type"), str)
                        and isinstance(hook.get("command"), str)):
                    raise RenderError(f"'hooks' event {event!r}: each hook needs string 'type' and 'command'")


def _write_rules(where: str, entries: list) -> None:
    # A Write(path) rule stops Claude Code starting (spec section 6); a file rule is Edit(...).
    for entry in entries:
        if isinstance(entry, str) and _WRITE_RULE.match(entry.strip()):
            raise RenderError(f"{where} carries {entry!r}: a Write( rule stops Claude Code starting; use Edit(...)")


def _merge(base_list: list, extra: list) -> list:
    out = list(base_list)
    seen = {entry.strip() for entry in out if isinstance(entry, str)}
    for entry in extra:
        if entry.strip() not in seen:
            seen.add(entry.strip())
            out.append(entry)
    return out


def render_settings(base: dict, cfg: object) -> dict:
    """Return base plus cfg.settings; config can only add, never remove or override."""
    extra = cfg.settings  # type: ignore[attr-defined]  # duck-typed so a target can pass a SimpleNamespace
    validate_settings(extra)
    perms = base.get("permissions", {})
    base_allow, base_deny = perms.get("allow", []), perms.get("deny", [])
    cfg_allow, cfg_deny = extra.get("allow", []), extra.get("deny", [])

    _write_rules("base permissions", base_allow + base_deny)
    _write_rules("config allow", cfg_allow)
    _write_rules("config deny", cfg_deny)
    # Denies win in Claude Code, so an allow the base denies would be dead text that hides intent
    # (owner: blueprint-backend docs/superpowers/specs/2026-11-02-shared-harness-design.md, 5.2).
    denied = {entry.strip() for entry in base_deny}
    for entry in cfg_allow:
        if entry.strip() in denied:
            raise RenderError(f"config allow {entry!r} is denied by the base")

    env = dict(base.get("env", {}))
    # Windows environment names are case-insensitive, so compare casefolded.
    taken = {key.casefold() for key in env}
    for key, value in extra.get("env", {}).items():
        if key.casefold() in taken:
            raise RenderError(f"config env {key!r} is already set by the base")
        taken.add(key.casefold())
        env[key] = value

    hooks = copy.deepcopy(base.get("hooks", {}))
    for event, groups in extra.get("hooks", {}).items():
        hooks[event] = hooks.get(event, []) + copy.deepcopy(groups)

    merged_perms = copy.deepcopy(perms)
    for key, base_list, added in (("allow", base_allow, cfg_allow), ("deny", base_deny, cfg_deny)):
        if key in perms or added:
            merged_perms[key] = _merge(base_list, added)

    # Keep the base's own keys and order; add a section it lacks only when the config has content for it.
    merged = {"env": env, "permissions": merged_perms, "hooks": hooks}
    out = {key: merged[key] if key in merged else copy.deepcopy(value) for key, value in base.items()}
    for key, value in merged.items():
        if key not in out and value:
            out[key] = value
    return out


def dump_settings(settings: dict) -> str:
    """Serialise in the form backend's file has: two-space indent, key order kept, trailing newline."""
    return json.dumps(settings, indent=2, ensure_ascii=False) + "\n"
