"""This repository's own case for dispatch_run.py: its token keys are `tools/quality-sets`' own.

`test_dispatch_run.py` is the payload's and holds every case a target without that tool runs; this one is local
because only this repository has the file it reads.
"""

import ast
import unittest
from pathlib import Path

import dispatch_run as dr


class TheTokenKeysAreQualitySetsOwn(unittest.TestCase):

    def test_token_keys_are_quality_sets_own(self):
        here = Path(__file__).resolve()
        owner = next((p / "tools" / "quality-sets" / "quality_sets.py" for p in here.parents
                      if (p / "tools" / "quality-sets" / "quality_sets.py").is_file()), None)
        self.assertIsNotNone(owner, "tools/quality-sets/quality_sets.py not found above this suite")
        tree = ast.parse(owner.read_text(encoding="utf-8"))
        value = next(ast.literal_eval(node.value) for node in tree.body if isinstance(node, ast.Assign)
                     and any(getattr(t, "id", None) == "TOKEN_KEYS" for t in node.targets))
        self.assertEqual(value, dr.TOKEN_KEYS)


if __name__ == "__main__":
    unittest.main()
