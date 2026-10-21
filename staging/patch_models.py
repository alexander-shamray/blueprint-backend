"""Pin the reviewer and the two auditors to Opus, and add the case that pins it.

Run by apply.sh as `patch_models.py <root> test` and then `patch_models.py <root> agents`, so the case is seen to fail
before the profiles change.
"""

import sys
from pathlib import Path

AGENTS = ("branch-reviewer", "bug-auditor", "security-auditor")

CASE_ANCHOR = '        self.assertNotRegex(profile, r"(?m)^skills:")\n'
CASE = '''
    def test_the_reviewer_and_the_auditors_run_on_opus(self):
        # An error from any of the three is a silent miss, so none takes the session's model (token-plan step 7d).
        for name in ("branch-reviewer", "bug-auditor", "security-auditor"):
            with self.subTest(profile=name):
                profile = (AGENTS / f"{name}.md").read_text(encoding="utf-8")
                self.assertEqual(["opus"], frontmatter_list(profile, "model"))
'''


def rewrite(path, change):
    text = path.read_bytes().decode("utf-8")
    newline = "\r\n" if "\r\n" in text else "\n"
    lf = text.replace("\r\n", "\n")
    out = change(lf)
    assert out != lf, f"{path}: nothing changed"
    path.write_bytes(out.replace("\n", newline).encode("utf-8"))


def add_case(text):
    assert "run_on_opus" not in text, "case already present"
    assert text.count(CASE_ANCHOR) == 1, "anchor not found exactly once"
    return text.replace(CASE_ANCHOR, CASE_ANCHOR + CASE)


def add_model(text):
    head, sep, body = text.partition("\n---\n")
    assert sep and head.startswith("---\n"), "no closed frontmatter"
    assert "\nmodel:" not in head, "model already set"
    lines = head.split("\n")
    tools = [i for i, line in enumerate(lines) if line.startswith("tools:")]
    assert len(tools) == 1, "expected one tools: line"
    lines.insert(tools[0] + 1, "model: opus")
    return "\n".join(lines) + sep + body


def main(root, part):
    claude = Path(root) / ".claude"
    if part == "test":
        rewrite(claude / "scripts" / "test_command_grants.py", add_case)
    elif part == "agents":
        for name in AGENTS:
            rewrite(claude / "agents" / f"{name}.md", add_model)
    else:
        raise SystemExit(f"unknown part {part!r}")


if __name__ == "__main__":
    main(*sys.argv[1:])
