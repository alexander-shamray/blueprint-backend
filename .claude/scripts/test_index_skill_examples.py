"""Every `search` line under the skill carries the limit `SKILL.md` owns, read
from its own sentence and never spelled here: a rule only the examples execute
cannot be held by the prose above them, and a copy of the value would go stale
in the direction that reports success."""

import re
import shlex
import unittest
from pathlib import Path

SKILL_DIR = Path(__file__).resolve().parent.parent / "skills" / "codebase-index"
SKILL = SKILL_DIR / "SKILL.md"

OWNER = re.compile(r"\*\*`search` takes `(--limit [^`]*)`\*\*")
# The frontmatter's grant spells `cbx search:*`, which is a grant and no example.
EXAMPLE = re.compile(r"scripts/cbx search [^`\n]*")


def limits_in(line):
    """Every `--limit` value on a command line, whole rather than by prefix."""
    tokens = shlex.split(line)
    return [tokens[index + 1] for index, token in enumerate(tokens)
            if token == "--limit" and index + 1 < len(tokens)]


def owned_limit(text=None):
    """The one whole value the sentence in `SKILL.md` sets, read by the rule
    the examples are held to: a prefix match would hand out `3` from either
    `--limit 3 --limit 30` or `--limit 3oops`."""
    sentence = OWNER.search(SKILL.read_text(encoding="utf-8") if text is None else text)
    assert sentence, "SKILL.md has no sentence saying what `search` takes"
    found = limits_in(sentence.group(1))
    assert len(found) == 1, f"the sentence names {len(found)}: {sentence.group(1)}"
    assert found[0].isdigit(), f"the sentence's limit is not a number: {found[0]}"
    return found[0]


def search_examples():
    """Every `search` command under the skill, with where it was found: a
    fenced line or a table row's code span alike."""
    for path in sorted(SKILL_DIR.rglob("*.md")):
        for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
            for example in EXAMPLE.findall(line):
                yield path, number, example


class EverySearchExampleCarriesTheOwnedLimit(unittest.TestCase):
    def test_the_skill_owns_a_limit(self):
        self.assertRegex(owned_limit(), r"^\d+$")

    def test_there_are_examples_to_check(self):
        """An empty set would pass the assertion below, so a reflow hiding
        every example would report the rule enforced."""
        files = {path.name for path, _number, _line in search_examples()}

        self.assertEqual({"SKILL.md", "commands.md"}, files)

    def test_the_owner_is_read_by_the_rule_it_owns(self):
        for sentence in ("**`search` takes `--limit 3 --limit 30`**", "**`search` takes `--limit 3oops`**",
                         "**`search` takes `--limit`**"):
            with self.subTest(sentence=sentence), self.assertRaises(AssertionError):
                owned_limit(sentence)
        self.assertEqual("3", owned_limit("**`search` takes `--limit 3`**"))

    def test_a_grant_is_not_an_example(self):
        self.assertEqual([], EXAMPLE.findall("Bash(bash .claude/skills/codebase-index/scripts/cbx search:*)"))

    def test_a_substring_of_the_value_is_not_the_value(self):
        drifted = f"scripts/cbx search 'X' --limit {owned_limit()}0 --session <tag> --json"

        self.assertNotEqual([owned_limit()], limits_in(drifted))

    def test_every_example_passes_the_owned_limit(self):
        """Exactly one whole value: a second `--limit` would let a line carry
        the owned value and contradict it."""
        for path, number, line in search_examples():
            with self.subTest(file=path.name, line=number):
                self.assertEqual([owned_limit()], limits_in(line), f"{path.name}:{number}: {line}")


if __name__ == "__main__":
    unittest.main()
