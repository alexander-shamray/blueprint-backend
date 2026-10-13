"""provision.py against a gh that keeps a repository's settings in memory."""

import json
import unittest

import provision
import store_check


class FakeGh:
    """The settings the provisioner reads and writes, answering as gh does."""

    def __init__(self, environment=True, policies=("main",), variables=(), secrets=(), repo_variables=None):
        self.environment = environment
        self.policies = list(policies)
        self.variables = dict.fromkeys(variables, "v")
        self.secrets = dict.fromkeys(secrets, "s")
        self.repo_variables = dict(repo_variables or {})
        self.calls = []

    def __call__(self, args, stdin=None):
        self.calls.append((list(args), stdin))
        head = args[:2]
        if head == ["api", "repos/{owner}/{repo}/environments/production"]:
            if not self.environment:
                raise store_check.Refusal("gh api: Not Found (HTTP 404)")
            return json.dumps({"deployment_branch_policy": {"custom_branch_policies": bool(self.policies)}})
        if head == ["api", "repos/{owner}/{repo}/environments/production/deployment-branch-policies"]:
            return json.dumps({"branch_policies": [{"name": name} for name in self.policies]})
        if args[:3] == ["variable", "list", "--env"]:
            return json.dumps([{"name": name} for name in self.variables])
        if args[:3] == ["secret", "list", "--env"]:
            return json.dumps([{"name": name} for name in self.secrets])
        if args[:2] == ["variable", "list"]:
            return json.dumps([{"name": k, "value": v} for k, v in self.repo_variables.items()])
        if args[:2] == ["variable", "set"] and "--env" in args:
            self.variables[args[2]] = args[args.index("--body") + 1]
            return ""
        if args[:2] == ["variable", "set"]:
            self.repo_variables[args[2]] = args[args.index("--body") + 1]
            return ""
        if args[:2] == ["secret", "set"]:
            self.secrets[args[2]] = stdin
            return ""
        raise AssertionError(f"unexpected gh call: {args}")

    def writes(self):
        return [args for args, _ in self.calls if args[1] == "set"]


def everything() -> dict[str, str]:
    return {name: f"value-of-{name}" for name in provision.VARIABLES + provision.SECRETS}


class CheckTests(unittest.TestCase):
    def test_a_provisioned_environment_has_no_problem(self):
        gh = FakeGh(variables=provision.VARIABLES, secrets=provision.SECRETS,
                    repo_variables={provision.OPT_IN: "enabled"})
        self.assertEqual(provision.problems(gh), [])

    def test_each_missing_name_is_listed(self):
        gh = FakeGh(variables=provision.VARIABLES[1:], secrets=provision.SECRETS[:-1],
                    repo_variables={provision.OPT_IN: "enabled"})
        found = " ".join(provision.problems(gh))
        self.assertIn(provision.VARIABLES[0], found)
        self.assertIn(provision.SECRETS[-1], found)
        self.assertNotIn(provision.SECRETS[0], found)

    def test_the_heartbeat_secret_is_one_of_them(self):
        self.assertIn(store_check.HEARTBEAT, provision.SECRETS)

    def test_a_job_that_is_not_opted_in_is_a_problem(self):
        gh = FakeGh(variables=provision.VARIABLES, secrets=provision.SECRETS)
        self.assertIn(provision.OPT_IN, " ".join(provision.problems(gh)))

    def test_a_missing_environment_is_one_problem_and_nothing_else_is_asked(self):
        gh = FakeGh(environment=False)
        self.assertEqual(len(provision.problems(gh)), 1)
        self.assertIn("production", provision.problems(gh)[0])

    def test_an_environment_open_to_other_branches_is_a_problem(self):
        for policies in ((), ("main", "feature/x")):
            gh = FakeGh(policies=policies, variables=provision.VARIABLES, secrets=provision.SECRETS,
                        repo_variables={provision.OPT_IN: "enabled"})
            self.assertIn("main", " ".join(provision.problems(gh)), policies)

    def test_an_opt_in_with_another_value_is_a_problem(self):
        gh = FakeGh(variables=provision.VARIABLES, secrets=provision.SECRETS,
                    repo_variables={provision.OPT_IN: "true"})
        self.assertIn(provision.OPT_IN, " ".join(provision.problems(gh)))


class ApplyTests(unittest.TestCase):
    def test_every_name_is_set_and_the_opt_in_is_last(self):
        gh = FakeGh()
        provision.apply(gh, everything())
        self.assertEqual(provision.problems(gh), [])
        self.assertEqual(gh.writes()[-1][2], provision.OPT_IN)
        self.assertEqual(sorted(gh.variables), sorted(provision.VARIABLES))
        self.assertEqual(sorted(gh.secrets), sorted(provision.SECRETS))

    def test_a_secret_travels_on_stdin_and_never_in_an_argument(self):
        gh = FakeGh()
        env = everything()
        provision.apply(gh, env)
        for args, stdin in gh.calls:
            if args[:2] == ["secret", "set"]:
                self.assertEqual(stdin, env[args[2]])
                self.assertNotIn(env[args[2]], args)
        self.assertNotIn("--body", [a for args, _ in gh.calls if args[:2] == ["secret", "set"] for a in args])

    def test_an_unset_value_sets_nothing(self):
        gh = FakeGh()
        env = everything()
        del env[provision.SECRETS[0]], env[provision.VARIABLES[0]]
        with self.assertRaises(store_check.Refusal) as caught:
            provision.apply(gh, env)
        self.assertIn(provision.SECRETS[0], str(caught.exception))
        self.assertIn(provision.VARIABLES[0], str(caught.exception))
        self.assertEqual(gh.writes(), [])

    def test_an_environment_open_to_other_branches_sets_nothing(self):
        gh = FakeGh(policies=())
        with self.assertRaises(store_check.Refusal):
            provision.apply(gh, everything())
        self.assertEqual(gh.writes(), [])

    def test_a_missing_environment_sets_nothing(self):
        gh = FakeGh(environment=False)
        with self.assertRaises(store_check.Refusal):
            provision.apply(gh, everything())
        self.assertEqual(gh.writes(), [])

    def test_the_addressing_variable_is_set_only_when_given(self):
        gh = FakeGh()
        provision.apply(gh, everything())
        self.assertNotIn(store_check.ADDRESSING, gh.variables)
        provision.apply(gh, {**everything(), store_check.ADDRESSING: "virtual"})
        self.assertEqual(gh.variables[store_check.ADDRESSING], "virtual")

    def test_an_addressing_the_check_would_refuse_sets_nothing(self):
        gh = FakeGh()
        with self.assertRaisesRegex(store_check.Refusal, store_check.ADDRESSING):
            provision.apply(gh, {**everything(), store_check.ADDRESSING: "vhost"})
        self.assertEqual(gh.writes(), [])

    def test_a_second_run_changes_nothing(self):
        gh = FakeGh()
        provision.apply(gh, everything())
        before = (dict(gh.variables), dict(gh.secrets), dict(gh.repo_variables))
        provision.apply(gh, everything())
        self.assertEqual((gh.variables, gh.secrets, gh.repo_variables), before)


if __name__ == "__main__":
    unittest.main()
