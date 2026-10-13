#!/usr/bin/env python3
"""What the gates would catch, which running them against this repository does not say.

Most checks here are negative cases, because a gate only ever observed green
has not been shown to look at anything; the positive controls say what a clean
input looks like.

Some tests have no defect in them at all. Their subject is the gate's own
parser: an empty pattern list, an empty matrix, an empty stage, each a state in
which every other check passes over an inventory it never read.

    py -3.12 -m unittest discover -s .github/pipeline-gate
"""
from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

import pipeline_gate

# A workflow with the two blocks the gates read, at ci.yml's real indentation.
# Indentation is load-bearing here: `read_filters` finds the end of the block
# by it, and `check_images` matches filter names on a twelve-space indent.
WORKFLOW_HEAD = """\
env:
  REGISTRY: ghcr.io/${{ github.repository }}

jobs:
  changes:
    steps:
      - uses: dorny/paths-filter@v4
        with:
          predicate-quantifier: 'some-with-excludes'
          filters: |
            shared: &shared
              - 'Directory.Packages.props'
              - 'src/BuildingBlocks/**'
            catalog:
              - *shared
              - 'src/Services/Catalog/**'
            gateway:
              - *shared
              - 'src/Gateway/**'
            deploy:
              - 'deploy/**'
              - '!deploy/compose/**'

    outputs:
      catalog: ${{ steps.changes.outputs.catalog }}
      gateway: ${{ steps.changes.outputs.gateway }}

  images:
    strategy:
      matrix:
        include:
          - filter: catalog
            image: catalog-api
            dockerfile: src/Services/Catalog/Catalog.Api/Dockerfile
          - filter: gateway
            image: gateway
            dockerfile: src/Gateway/Gateway.Api/Dockerfile
    steps:
      - uses: actions/checkout@v4
        if: ${{ needs.changes.outputs[matrix.filter] == 'true' }}

      - name: Build ${{ matrix.image }}
        if: ${{ needs.changes.outputs[matrix.filter] == 'true' }}
        run: >
          docker build
          --file ${{ matrix.dockerfile }}
          --tag ${{ matrix.image }}:${{ github.sha }}
          --tag $REGISTRY/${{ matrix.image }}:${{ github.sha }}
          .

"""

SBOM_STEP = """\
      # A comment between steps belongs to neither.
      - name: SBOM for ${{ matrix.image }}
        if: ${{ needs.changes.outputs[matrix.filter] == 'true' }}
        uses: anchore/sbom-action@66cbf4bc1f1c0d2edc94016e65bc221b6bb0ad6c # v0.24.3
        with:
          image: ${{ matrix.image }}:${{ github.sha }}
          format: spdx-json
          output-file: sbom-${{ matrix.image }}.spdx.json
"""

# The steps ADR-109 adds, each under the one condition the gate looks for.
MAIN_PUSH = (
    "${{ needs.changes.outputs[matrix.filter] == 'true' && "
    "contains(fromJSON('[\"push\",\"workflow_dispatch\"]'), github.event_name) && "
    "github.ref == 'refs/heads/main' }}"
)

SIGNING_STEPS = f"""\
      - name: Sign in to the registry
        if: {MAIN_PUSH}
        uses: docker/login-action@dbcb813823bdd20940b903addbd779551569679f # v4.6.0
        with:
          registry: ghcr.io

      - name: Push ${{{{ matrix.image }}}}
        id: push
        if: {MAIN_PUSH}
        run: |
          docker push "$REGISTRY/${{{{ matrix.image }}}}:${{{{ github.sha }}}}"
          echo "digest=sha256:0" >> "$GITHUB_OUTPUT"

      - name: Install cosign
        if: {MAIN_PUSH}
        uses: sigstore/cosign-installer@6f9f17788090df1f26f669e9d70d6ae9567deba6 # v4.1.2

      - name: Sign ${{{{ matrix.image }}}}
        if: {MAIN_PUSH}
        run: cosign sign --yes "$REGISTRY/${{{{ matrix.image }}}}@$DIGEST"
        env:
          DIGEST: ${{{{ steps.push.outputs.digest }}}}

      - name: Provenance for ${{{{ matrix.image }}}}
        if: {MAIN_PUSH}
        uses: actions/attest-build-provenance@4d101475d8b20a2381f78447822ac1eab6504dd8 # v4.2.2
        with:
          subject-name: ${{{{ env.REGISTRY }}}}/${{{{ matrix.image }}}}
          subject-digest: ${{{{ steps.push.outputs.digest }}}}
          push-to-registry: true

      - name: SBOM attestation for ${{{{ matrix.image }}}}
        if: {MAIN_PUSH}
        uses: actions/attest-sbom@c604332985a26aa8cf1bdc465b92731239ec6b9e # v4.1.0
        with:
          subject-name: ${{{{ env.REGISTRY }}}}/${{{{ matrix.image }}}}
          subject-digest: ${{{{ steps.push.outputs.digest }}}}
          sbom-path: sbom-${{{{ matrix.image }}}}.spdx.json
          push-to-registry: true
"""

WORKFLOW = WORKFLOW_HEAD + SBOM_STEP + SIGNING_STEPS

DEPLOY_WORKFLOW = """\
env:
  REGISTRY: ghcr.io/${{ github.repository }}

jobs:
  rollout:
    steps:
      - name: The tag has to be a tag
        run: python deploy/canary/canary.py validate-tag --value "$TAG"

      - name: The images are the ones CI built and signed
        run: |
          cosign verify "$REGISTRY/$1:$TAG" \\
            --certificate-identity "$CERTIFICATE_IDENTITY" \\
            --certificate-oidc-issuer https://token.actions.githubusercontent.com
          python deploy/canary/canary.py verified-digest --file "$RUNNER_TEMP/v.json"
        env:
          CERTIFICATE_IDENTITY: https://github.com/${{ github.repository }}/.github/workflows/ci.yml@refs/heads/main

      - name: The stable track's facts
        run: |
          helm get values "$WORKLOAD" -n "$NAMESPACE" -o json > stable.json

      # A comment naming helm upgrade is not a command.
      - name: Promote
        run: |
          helm upgrade "$WORKLOAD" "deploy/helm/$CHART" \\
            -f stable-values.yaml --set-string image.tag="$TAG" \\
            --set-string image.digest="$API_DIGEST" \\
            --set-string image.migratorDigest="$MIGRATOR_DIGEST" \\
            --atomic
          helm upgrade --install "$WORKLOAD-canary" "deploy/helm/$CHART" \\
            --set-string image.digest="$API_DIGEST" --set-string image.migratorDigest="$MIGRATOR_DIGEST"
"""


class Fixture(unittest.TestCase):
    """A checkout shaped like this one, small enough to break on purpose."""

    def setUp(self) -> None:
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)

        for path in (
            "src/BuildingBlocks/Common.Domain",
            "src/Services/Catalog/Catalog.Api",
            "src/Gateway/Gateway.Api",
        ):
            (self.root / path).mkdir(parents=True)
        for path in (
            "src/Services/Catalog/Catalog.Api/Dockerfile",
            "src/Gateway/Gateway.Api/Dockerfile",
        ):
            (self.root / path).write_text("FROM scratch\n", encoding="utf-8")

        self.workflow = self.root / ".github" / "workflows" / "ci.yml"
        self.workflow.parent.mkdir(parents=True)
        self.write(WORKFLOW)
        self.deploy = self.workflow.parent / "deploy.yml"
        self.write_deploy(DEPLOY_WORKFLOW)

        # The module reads ROOT and WORKFLOW as module-level constants, which
        # is right for a script and awkward for a suite. Patched rather than
        # parameterised so the production call sites stay argument-free.
        for name, path in (("WORKFLOW", self.workflow), ("DEPLOY", self.deploy)):
            original = getattr(pipeline_gate, name)
            setattr(pipeline_gate, name, path)
            self.addCleanup(lambda name=name, original=original: setattr(pipeline_gate, name, original))

    def write(self, text: str) -> None:
        self.workflow.write_text(text, encoding="utf-8")

    def write_deploy(self, text: str) -> None:
        self.deploy.write_text(text, encoding="utf-8")


class FilterTests(Fixture):
    def test_the_fixture_is_clean(self) -> None:
        """Without this the negative cases below prove nothing: a check that
        fails on everything catches every defect and is worthless."""
        self.assertEqual(pipeline_gate.check_filters(self.root), [])

    def test_a_new_service_with_no_filter_is_caught(self) -> None:
        """§15.1's quieter half. `src/Services/` is spoken for by its
        siblings' filters, so the inventory looks complete right up until one
        service stops being deployed."""
        (self.root / "src/Services/Inventory").mkdir(parents=True)

        problems = pipeline_gate.check_filters(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("src/Services/Inventory", problems[0])

    def test_a_new_top_level_deployable_with_no_filter_is_caught(self) -> None:
        """The loud half, and the one that actually happened: `src/Gateway/**`
        and `src/BFF/**` were both deployables CI never rebuilt."""
        (self.root / "src/Worker").mkdir(parents=True)

        problems = pipeline_gate.check_filters(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("src/Worker", problems[0])

    def test_a_missing_predicate_quantifier_is_caught(self) -> None:
        """Without it the `!deploy/compose/**` exclusion is decoration, and a
        compose-only change deploys. Nothing else in the file would say so."""
        self.write(WORKFLOW.replace("predicate-quantifier: 'some-with-excludes'", ""))

        problems = pipeline_gate.check_filters(self.root)

        self.assertTrue(any("predicate-quantifier" in problem for problem in problems))

    def test_an_unparsed_block_fails_rather_than_passing_empty(self) -> None:
        """The gate's own subject. Every check above asks whether a directory
        appears in the pattern list; against an empty list they all pass and
        the gate reports a complete inventory it never read."""
        self.write(WORKFLOW.replace("filters: |", "filters: >"))

        problems = pipeline_gate.check_filters(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("parser", problems[0])

    def test_a_parent_is_covered_by_its_children(self) -> None:
        """§15.1's model, and the reason `covers` matches on a prefix.

        No filter names `src/Services`; it is "spoken for by its siblings'
        filters", which is exactly why that section asks for the children to be
        checked separately. A rule demanding `src/Services/**` would fail on
        the correct workflow.
        """
        self.assertTrue(pipeline_gate.covers("src/Services/Catalog/**", "src/Services"))
        self.assertTrue(pipeline_gate.covers("src/Services/Catalog/**", "src/Services/Catalog"))
        self.assertFalse(pipeline_gate.covers("src/Gateway/**", "src/Services"))

    def test_a_file_level_pattern_satisfies_the_check(self) -> None:
        """The reach's cost, pinned so it is visible rather than discovered.

        This gate proves every deployable is reachable by some filter, not that
        any filter is complete. Closing it would mean rejecting the prefix
        match the test above requires, so the residual is stated in `covers`
        and asserted here rather than left to be found later and read as a bug.
        """
        self.assertTrue(
            pipeline_gate.covers("src/Gateway/Program.cs", "src/Gateway"),
            "if this ever becomes False, `covers` got stricter and the parent "
            "case above is what to re-check",
        )

    def test_a_negated_pattern_covers_nothing(self) -> None:
        """`!deploy/compose/**` excludes; reading it as coverage would let an
        exclusion satisfy the inventory it exists to carve out of."""
        self.assertFalse(pipeline_gate.covers("!src/Gateway/**", "src/Gateway"))


class ImageTests(Fixture):
    def test_the_fixture_is_clean(self) -> None:
        self.assertEqual(pipeline_gate.check_images(self.root), [])

    def test_a_dockerfile_no_matrix_entry_builds_is_caught(self) -> None:
        """The gap compose.yml names: filtered to deploy/compose/**, that smoke
        surfaces a broken Dockerfile on the next compose PR rather than on the
        change that broke it."""
        (self.root / "src/BFF/Web.Bff").mkdir(parents=True)
        (self.root / "src/BFF/Web.Bff/Dockerfile").write_text("FROM scratch\n", encoding="utf-8")

        problems = pipeline_gate.check_images(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("src/BFF/Web.Bff/Dockerfile", problems[0])

    def test_a_matrix_entry_naming_no_dockerfile_is_caught(self) -> None:
        """The reverse direction. Path-filtered, this fails on the day the
        entry is next selected — months after the rename, in a job nobody
        connects to it."""
        (self.root / "src/Gateway/Gateway.Api/Dockerfile").unlink()

        problems = pipeline_gate.check_images(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("does not exist", problems[0])

    def test_an_entry_reading_an_undefined_filter_is_caught(self) -> None:
        """A case neither direction can see.

        An undefined `filter` reads as the empty string, not an error, so every
        build step is skipped and only the guard step notices."""
        self.write(WORKFLOW.replace("- filter: gateway", "- filter: edge"))

        problems = pipeline_gate.check_images(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("'edge'", problems[0])

    def test_a_dockerfile_paired_with_the_wrong_filter_is_caught(self) -> None:
        """The failure neither direction of the inventory can reach.

        Pairing the gateway's Dockerfile with `filter: catalog` names a filter
        that exists and builds a Dockerfile that exists — both directions pass,
        and the name check passes too, because the name is real. What is wrong
        is the wiring: a gateway-only change then builds no gateway image, and
        a Catalog change builds one nobody asked for. Well-formed and pointed
        at the wrong service.
        """
        self.write(WORKFLOW.replace(
            "          - filter: gateway\n            image: gateway",
            "          - filter: catalog\n            image: gateway"))

        problems = pipeline_gate.check_images(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("wired to the wrong service", problems[0])

    def test_a_filter_the_changes_job_does_not_export_is_caught(self) -> None:
        """Defining a filter and exporting it are two different things.

        `needs.changes.outputs.gateway` reads a JOB output, so with its
        `outputs:` line gone every gateway leg reads an empty one and fails
        its guard step."""
        self.write(WORKFLOW.replace(
            "      gateway: ${{ steps.changes.outputs.gateway }}\n", ""))

        problems = pipeline_gate.check_images(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("does not export it", problems[0])

    def test_a_job_level_condition_is_caught(self) -> None:
        """A skipped matrix job is never expanded, so GitHub names its one
        check with `${{ matrix.image }}` unevaluated."""
        self.write(WORKFLOW.replace(
            "  images:\n",
            "  images:\n    if: ${{ needs.changes.outputs.catalog == 'true' }}\n"))

        problems = pipeline_gate.check_images(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("job-level `if: ${{ needs.changes.outputs.catalog == 'true' }}`", problems[0])

    def test_a_job_level_condition_folded_over_lines_is_read_whole(self) -> None:
        self.write(WORKFLOW.replace(
            "  images:\n",
            "  images:\n"
            "    if: >-\n"
            "      ${{ needs.changes.outputs.catalog == 'true'\n"
            "      || needs.changes.outputs.gateway == 'true' }}\n"))

        problems = pipeline_gate.check_images(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("|| needs.changes.outputs.gateway == 'true' }}", problems[0])

    def test_a_renamed_images_job_fails_rather_than_passing_empty(self) -> None:
        """The condition check passes on no `if:`, so a key the parser cannot
        find has to fail on its own."""
        self.write(WORKFLOW.replace("  images:\n", "  build-images:\n"))

        problems = pipeline_gate.check_images(self.root)

        self.assertTrue(any("found no `images:` job" in problem for problem in problems), problems)

    def test_the_filter_parser_keeps_them_apart(self) -> None:
        """The subject of the check above. `read_filters` flattens, which is
        right for asking whether a directory is reachable by ANY filter and
        useless for asking which — a parser that returned one bag would make
        every pairing look correct."""
        by_name = pipeline_gate.read_filters_by_name(WORKFLOW)

        self.assertEqual(sorted(by_name), ["catalog", "deploy", "gateway", "shared"])
        self.assertIn("src/Gateway/**", by_name["gateway"])
        self.assertNotIn("src/Gateway/**", by_name["catalog"])

    def test_an_unparsed_matrix_fails_rather_than_passing_empty(self) -> None:
        """The gate's own subject again, from the images side."""
        self.write(WORKFLOW.replace("dockerfile:", "file:"))

        problems = pipeline_gate.check_images(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("parser", problems[0])


class SbomTests(Fixture):
    """ADR-071's coverage: every build the images job runs has its SBOM."""

    def test_a_build_with_no_sbom_step_is_caught(self) -> None:
        self.write(WORKFLOW.replace(SBOM_STEP, ""))

        problems = pipeline_gate.check_images(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("builds ${{ matrix.image }}:${{ github.sha }} and no anchore/sbom-action@", problems[0])

    def test_an_sbom_of_another_reference_is_caught(self) -> None:
        self.write(WORKFLOW.replace(
            "          image: ${{ matrix.image }}:${{ github.sha }}",
            "          image: ${{ matrix.image }}:latest"))

        self.assertEqual(len(pipeline_gate.check_images(self.root)), 1)

    def test_an_sbom_action_pinned_to_a_tag_is_caught(self) -> None:
        """A tag can be moved to other code; the action reads the image."""
        self.write(WORKFLOW.replace("@66cbf4bc1f1c0d2edc94016e65bc221b6bb0ad6c # v0.24.3", "@v0"))

        problems = pipeline_gate.check_images(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("pinned to a commit", problems[0])

    def test_an_sbom_under_another_condition_is_caught(self) -> None:
        """An SBOM step left at `always()` fails every skipped leg, and one
        narrower than the build skips legs the build ran."""
        self.write(WORKFLOW.replace(SBOM_STEP, SBOM_STEP.replace(
            "if: ${{ needs.changes.outputs[matrix.filter] == 'true' }}",
            "if: ${{ github.event_name == 'push' }}")))

        self.assertEqual(len(pipeline_gate.check_images(self.root)), 1)

    def test_an_sbom_before_the_build_is_caught(self) -> None:
        moved = WORKFLOW.replace(SBOM_STEP, "").replace(
            "      - name: Build ${{ matrix.image }}", SBOM_STEP + "\n      - name: Build ${{ matrix.image }}")
        self.write(moved)

        self.assertEqual(len(pipeline_gate.check_images(self.root)), 1)

    def test_a_job_with_no_build_fails_rather_than_passing_empty(self) -> None:
        self.write(WORKFLOW.replace("          docker build\n", "          podman build\n"))

        problems = pipeline_gate.check_images(self.root)

        self.assertEqual(len(problems), 2)
        self.assertTrue(all("vacuously" in problem for problem in problems))

    def test_the_step_reader_joins_a_folded_run_and_keeps_with_inputs(self) -> None:
        steps = pipeline_gate.read_job_steps(WORKFLOW_HEAD + SBOM_STEP, "images")

        self.assertEqual(len(steps), 3)
        self.assertIn("--tag $REGISTRY/${{ matrix.image }}:${{ github.sha }} .", steps[1]["run"])
        self.assertEqual(steps[2]["image"], "${{ matrix.image }}:${{ github.sha }}")
        self.assertNotIn("comment", " ".join(steps[1].values()))


class SupplyChainTests(Fixture):
    """ADR-109's coverage: every build is pushed, signed and attested by digest on
    main, and the rollout verifies before it touches Helm."""

    def broken(self, old: str, new: str, *, deploy: bool = False) -> list[str]:
        """The problems after one edit, with the edit proved to have landed."""
        source = DEPLOY_WORKFLOW if deploy else WORKFLOW
        self.assertIn(old, source)
        (self.write_deploy if deploy else self.write)(source.replace(old, new))
        return pipeline_gate.check_images(self.root)

    def test_the_fixture_is_clean(self) -> None:
        self.assertEqual(pipeline_gate.check_supply_chain(WORKFLOW, DEPLOY_WORKFLOW), [])

    def test_a_build_with_no_sign_step_is_caught(self) -> None:
        sign = SIGNING_STEPS.split("      - name: Sign ${{ matrix.image }}")[1].split("      - name: Provenance")[0]
        self.write(WORKFLOW.replace("      - name: Sign ${{ matrix.image }}" + sign, ""))

        problems = pipeline_gate.check_images(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("no `cosign sign`", problems[0])

    def test_a_build_that_is_never_pushed_is_caught(self) -> None:
        problems = self.broken("docker push", "docker tag")

        self.assertIn("no `docker push`", problems[0])

    def test_a_signature_by_tag_rather_than_digest_is_caught(self) -> None:
        problems = self.broken('@$DIGEST"', ':${{ github.sha }}"')

        self.assertEqual(len(problems), 1)
        self.assertIn("not the pushed digest", problems[0])

    def test_a_signature_over_another_steps_digest_is_caught(self) -> None:
        problems = self.broken("DIGEST: ${{ steps.push.outputs.digest }}", "DIGEST: ${{ steps.other.outputs.digest }}")

        self.assertEqual(len(problems), 1)
        self.assertIn("not the pushed digest", problems[0])

    def test_an_attestation_of_another_digest_is_caught(self) -> None:
        problems = self.broken("subject-digest: ${{ steps.push.outputs.digest }}", "subject-digest: sha256:0")

        self.assertEqual(len(problems), 2)
        self.assertIn("subject-digest is not the pushed digest", problems[0])

    def test_an_attestation_kept_off_the_registry_is_caught(self) -> None:
        problems = self.broken("push-to-registry: true", "push-to-registry: false")

        self.assertEqual(len(problems), 2)
        self.assertIn("not pushed to the registry", problems[0])

    def test_an_sbom_attestation_of_another_file_is_caught(self) -> None:
        """The attested inventory is the artefact's, or the two can differ."""
        problems = self.broken("sbom-path: sbom-", "sbom-path: other-")

        self.assertEqual(len(problems), 1)
        self.assertIn("not the file the SBOM step wrote", problems[0])

    def test_a_step_that_runs_on_a_pull_request_is_caught(self) -> None:
        """A pull request pushing is the failure the condition exists for."""
        only_the_filter = "${{ needs.changes.outputs[matrix.filter] == 'true' }}"
        problems = self.broken(
            f"id: push\n        if: {MAIN_PUSH}\n", f"id: push\n        if: {only_the_filter}\n")

        self.assertEqual(len(problems), 1)
        self.assertIn("main-push condition", problems[0])

    def test_a_step_on_a_pull_request_event_is_caught_even_with_the_ref_test(self) -> None:
        problems = self.broken(
            "contains(fromJSON('[\"push\",\"workflow_dispatch\"]'), github.event_name)",
            "github.event_name == 'pull_request'")

        self.assertTrue(problems)
        self.assertIn("main-push condition", problems[0])

    def test_a_step_missing_the_ref_test_is_caught(self) -> None:
        self.assertTrue(self.broken(" && github.ref == 'refs/heads/main' }}", " }}"))

    def test_a_push_of_a_reference_the_build_never_tagged_is_caught(self) -> None:
        problems = self.broken('docker push "$REGISTRY/${{ matrix.image }}:${{ github.sha }}"',
                               'docker push "ghcr.io/x/${{ matrix.image }}:${{ github.sha }}"')

        self.assertTrue(any("tags no `$REGISTRY/...` reference" in problem for problem in problems))

    def test_a_signature_before_the_push_is_caught(self) -> None:
        marker, next_marker = "      - name: Push ${{ matrix.image }}", "      - name: Install cosign"
        push = WORKFLOW[WORKFLOW.index(marker):WORKFLOW.index(next_marker)]
        self.write(WORKFLOW.replace(push, "").replace("      - name: Provenance", push + "      - name: Provenance", 1))

        problems = pipeline_gate.check_images(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("no digest to sign before the push", problems[0])

    def test_a_signature_whose_installer_comes_later_is_caught(self) -> None:
        installer = "      - name: Install cosign\n"
        block = installer + "        if: " + MAIN_PUSH + "\n        uses: sigstore/cosign-installer@6f9f17788090df1f26f669e9d70d6ae9567deba6 # v4.1.2\n\n"
        self.assertIn(block, WORKFLOW)
        sign = "      - name: Sign ${{ matrix.image }}"
        self.write(WORKFLOW.replace(block, "").replace(sign, sign + "\n", 1).replace(
            "      - name: Provenance", block + "      - name: Provenance", 1))

        problems = pipeline_gate.check_images(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("cosign is not on the runner before its installer", problems[0])

    def test_a_leg_with_no_build_fails_rather_than_passing_empty(self) -> None:
        problems = pipeline_gate.check_supply_chain(
            WORKFLOW.replace("          docker build\n", "          podman build\n"), DEPLOY_WORKFLOW)

        self.assertEqual(len(problems), 1)
        self.assertIn("vacuously", problems[0])

    def test_a_second_build_needs_its_own_steps(self) -> None:
        """Per build, not per job: a build added below the first inherits nothing."""
        second = (
            "      - name: Build second\n"
            "        if: ${{ needs.changes.outputs[matrix.filter] == 'true' }}\n"
            "        run: docker build --tag second:${{ github.sha }} .\n")
        problems = pipeline_gate.check_supply_chain(WORKFLOW + "\n" + second, DEPLOY_WORKFLOW)

        self.assertEqual(len(problems), 1)
        self.assertIn("build at step", problems[0])
        self.assertIn("no registry sign-in", problems[0])

    # deploy.yml

    def test_a_registry_that_differs_between_the_workflows_is_caught(self) -> None:
        problems = self.broken("REGISTRY: ghcr.io/${{ github.repository }}",
                               "REGISTRY: registry.example.com/commerce", deploy=True)

        self.assertEqual(len(problems), 1)
        self.assertIn("deploy.yml's is 'registry.example.com/commerce'", problems[0])

    def test_a_workflow_with_no_registry_owner_is_caught(self) -> None:
        self.write(WORKFLOW.replace("env:\n  REGISTRY: ghcr.io/${{ github.repository }}\n\n", "", 1))

        self.assertTrue(any("declares no workflow-level REGISTRY" in p for p in pipeline_gate.check_images(self.root)))

    def test_a_second_copy_of_the_address_is_caught(self) -> None:
        problems = self.broken("jobs:\n  rollout:", "jobs:\n  rollout:\n    env:\n      IMAGE: ghcr.io/me/app\n", deploy=True)

        self.assertEqual(len(problems), 1)
        self.assertIn("outside REGISTRY", problems[0])

    def test_a_comment_naming_the_address_is_not_a_copy(self) -> None:
        self.assertEqual(self.broken("jobs:\n  rollout:", "# ghcr.io/me/app\njobs:\n  rollout:", deploy=True), [])

    def test_a_rollout_with_no_verify_step_is_caught(self) -> None:
        problems = self.broken("cosign verify", "cosign triangulate", deploy=True)

        self.assertTrue(any("no `cosign verify` step" in problem for problem in problems))

    def test_a_verify_after_the_first_helm_command_is_caught(self) -> None:
        verify = DEPLOY_WORKFLOW.split("      - name: The images are the ones CI built and signed")[1].split(
            "      - name: The stable track's facts")[0]
        moved = DEPLOY_WORKFLOW.replace("      - name: The images are the ones CI built and signed" + verify, "")
        self.write_deploy(moved.replace(
            "      # A comment naming helm upgrade",
            "      - name: The images are the ones CI built and signed" + verify + "      # A comment naming helm upgrade"))

        problems = pipeline_gate.check_images(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("runs after the first step that runs helm", problems[0])

    def test_a_verify_for_another_identity_is_caught(self) -> None:
        problems = self.broken("/.github/workflows/ci.yml@refs/heads/main", "/.github/workflows/any.yml@refs/heads/main",
                               deploy=True)

        self.assertEqual(len(problems), 1)
        self.assertIn("pinned to ci.yml on main", problems[0])

    def test_a_verify_that_names_no_issuer_is_caught(self) -> None:
        problems = self.broken("--certificate-oidc-issuer", "--issuer", deploy=True)

        self.assertIn("given an issuer", problems[0])

    def test_a_verify_that_reads_no_digest_from_what_it_verified_is_caught(self) -> None:
        problems = self.broken("verified-digest", "images", deploy=True)

        self.assertIn("reading the digest it verified", problems[0])

    def test_a_helm_upgrade_with_no_digest_is_caught(self) -> None:
        problems = self.broken('--set-string image.digest="$API_DIGEST" \\\n', "\\\n", deploy=True)

        self.assertEqual(len(problems), 1)
        self.assertIn("--set-string image.digest", problems[0])

    def test_a_helm_upgrade_with_no_migrator_digest_is_caught(self) -> None:
        problems = self.broken('--set-string image.migratorDigest="$MIGRATOR_DIGEST" \\\n', "\\\n", deploy=True)

        self.assertEqual(len(problems), 1)
        self.assertIn("image.migratorDigest", problems[0])

    def test_a_digest_set_with_set_instead_of_set_string_is_caught(self) -> None:
        """`--set` types the value, and a digest of digits would be read as a number."""
        self.assertEqual(len(self.broken("--set-string image.digest=", "--set image.digest=", deploy=True)), 2)

    def test_a_rollout_with_no_helm_fails_rather_than_passing_empty(self) -> None:
        problems = pipeline_gate.check_supply_chain(WORKFLOW, "env:\n  REGISTRY: ghcr.io/${{ github.repository }}\n\n"
                                                    "jobs:\n  rollout:\n    steps:\n      - name: x\n        run: cosign verify\n")

        self.assertTrue(any("runs helm" in problem and "vacuously" in problem for problem in problems))
        self.assertTrue(any("no `helm upgrade`" in problem for problem in problems))

    def test_a_deploy_workflow_with_no_rollout_job_fails_rather_than_passing_empty(self) -> None:
        problems = pipeline_gate.check_supply_chain(WORKFLOW, DEPLOY_WORKFLOW.replace("  rollout:\n", "  roll:\n"))

        self.assertTrue(any("no `rollout` job" in problem for problem in problems))

    def test_the_readers_keep_continuations_and_skip_comments(self) -> None:
        upgrades = pipeline_gate.read_helm_upgrades(DEPLOY_WORKFLOW)

        self.assertEqual(len(upgrades), 2)
        self.assertIn("image.migratorDigest", upgrades[0])
        self.assertNotIn("comment", " ".join(upgrades))
        self.assertEqual(pipeline_gate.read_workflow_env(WORKFLOW, "REGISTRY"), "ghcr.io/${{ github.repository }}")
        self.assertIsNone(pipeline_gate.read_workflow_env(WORKFLOW, "NOPE"))

    def test_the_gate_sees_this_repositorys_signing_steps(self) -> None:
        """The subject is what the gate reads, not what it found: if the real
        workflow's steps stopped parsing, every check above would be green
        over an empty list."""
        root = Path(__file__).resolve().parents[2]
        steps = pipeline_gate.read_job_steps((root / ".github/workflows/ci.yml").read_text(encoding="utf-8"), "images")
        uses = [step.get("uses", "").split("@")[0] for step in steps]

        for action in ("docker/login-action", "sigstore/cosign-installer", "actions/attest-build-provenance",
                       "actions/attest-sbom"):
            self.assertIn(action, uses)
        self.assertTrue(any("cosign sign" in step.get("run", "") for step in steps))
        self.assertTrue(any("docker push" in step.get("run", "") for step in steps))
        rollout = pipeline_gate.read_job_steps(
            (root / ".github/workflows/deploy.yml").read_text(encoding="utf-8"), "rollout")
        self.assertTrue(any("cosign verify" in step.get("run", "") for step in rollout))
        self.assertGreaterEqual(len(pipeline_gate.read_helm_upgrades(
            (root / ".github/workflows/deploy.yml").read_text(encoding="utf-8"))), 2)


class ActionTests(unittest.TestCase):
    """Every third-party `uses:` in every workflow is a commit pin."""

    def setUp(self) -> None:
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.workflows = self.root / ".github" / "workflows"
        self.workflows.mkdir(parents=True)

    def write(self, name: str, text: str) -> None:
        (self.workflows / name).write_text(text, encoding="utf-8")

    def test_pins_first_party_tags_and_local_actions_are_clean(self) -> None:
        self.write("ci.yml", (
            "jobs:\n  a:\n    steps:\n"
            "      - uses: actions/checkout@v4\n"
            "      - uses: ./.github/actions/local\n"
            "      - name: Filter\n"
            "        uses: dorny/paths-filter@ceb8a2b8f2d89434be7ff52d3de7ec3738c5cc9d # v4.0.3\n"))

        self.assertEqual(pipeline_gate.check_actions(self.root), [])

    def test_a_third_party_action_on_a_tag_is_caught_in_any_workflow(self) -> None:
        self.write("ci.yml", "jobs:\n  a:\n    steps:\n      - uses: actions/checkout@v4\n")
        self.write("helm.yaml", "jobs:\n  a:\n    steps:\n      - uses: 'azure/setup-helm@v4'\n")

        problems = pipeline_gate.check_actions(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("helm.yaml runs azure/setup-helm@v4, a third-party action not pinned", problems[0])

    def test_a_short_sha_and_a_reusable_workflow_on_a_branch_are_caught(self) -> None:
        self.write("ci.yml", (
            "jobs:\n  a:\n    uses: org/repo/.github/workflows/x.yml@main\n"
            "  b:\n    steps:\n      - uses: dorny/paths-filter@ceb8a2b\n"))

        self.assertEqual(len(pipeline_gate.check_actions(self.root)), 2)

    def test_an_image_without_a_digest_is_caught(self) -> None:
        self.write("ci.yml", "jobs:\n  a:\n    steps:\n      - uses: docker://alpine:3\n")

        self.assertIn("not pinned to a digest", pipeline_gate.check_actions(self.root)[0])

    def test_a_comment_naming_uses_is_not_a_reference(self) -> None:
        self.write("ci.yml", (
            "jobs:\n  a:\n    steps:\n      # refuses: anything@v1\n"
            "      - uses: actions/checkout@v4\n"))

        self.assertEqual(pipeline_gate.read_uses((self.workflows / "ci.yml").read_text()), ["actions/checkout@v4"])

    def test_no_reference_fails_rather_than_passing_empty(self) -> None:
        self.write("ci.yml", "jobs: {}\n")

        self.assertIn("vacuously", pipeline_gate.check_actions(self.root)[0])

    def test_the_reader_sees_every_uses_line_in_this_repositorys_workflows(self) -> None:
        """The subject is coverage: a reference the reader skips is one never checked."""
        total = 0
        for path in (pipeline_gate.ROOT / ".github" / "workflows").glob("*.y*ml"):
            text = path.read_text(encoding="utf-8")
            lines = [line for line in text.splitlines() if "uses:" in line and not line.lstrip().startswith("#")]
            total += len(lines)
            with self.subTest(workflow=path.name):
                self.assertEqual(len(pipeline_gate.read_uses(text)), len(lines))
        # A workflow that runs no action has none to read; none anywhere is a reader or a directory broken.
        self.assertGreater(total, 0)


# The integration matrix at ci.yml's indentation, one value in the folded form.
SHARDS = """\
  integration:
    strategy:
      matrix:
        include:
          - shard: payments
            selects: FullyQualifiedName~Payments.
          - shard: ordering
            selects: >-
              FullyQualifiedName~Ordering.&FullyQualifiedName!~Payments.
          - shard: rest
            selects: FullyQualifiedName!~Payments.&FullyQualifiedName!~Ordering.
"""


class ShardTests(Fixture):
    def setUp(self) -> None:
        super().setUp()
        self.write(WORKFLOW + SHARDS)

    def test_a_cascade_is_clean(self) -> None:
        self.assertEqual(pipeline_gate.check_shards(), [])

    def test_the_folded_value_is_read_from_the_next_line(self) -> None:
        """The parser's subject: the value is the line after `>-`, not the indicator itself."""
        shards = pipeline_gate.read_shards(WORKFLOW + SHARDS)

        self.assertEqual(
            shards[1], ("ordering", "FullyQualifiedName~Ordering.&FullyQualifiedName!~Payments."))

    def test_a_remainder_missing_an_exclusion_runs_tests_twice(self) -> None:
        self.write(WORKFLOW + SHARDS.replace("FullyQualifiedName!~Payments.&FullyQualifiedName!~Ordering.", "FullyQualifiedName!~Payments."))

        problems = pipeline_gate.check_shards()

        self.assertEqual(len(problems), 1)
        self.assertIn("rest shard does not exclude Ordering.", problems[0])
        self.assertIn("two shards", problems[0])

    def test_a_deleted_remainder_is_caught(self) -> None:
        self.write(WORKFLOW + SHARDS.split("          - shard: rest")[0])

        problems = pipeline_gate.check_shards()

        self.assertEqual(len(problems), 1)
        self.assertIn("the last shard, ordering, selects Ordering.", problems[0])

    def test_an_exclusion_of_nothing_above_leaves_tests_in_no_shard(self) -> None:
        self.write(WORKFLOW + SHARDS.replace(
            "selects: FullyQualifiedName~Payments.", "selects: FullyQualifiedName~Payments.&FullyQualifiedName!~Ordering."))

        problems = pipeline_gate.check_shards()

        self.assertEqual(len(problems), 1)
        self.assertIn("payments shard excludes Ordering.", problems[0])
        self.assertIn("no shard", problems[0])

    def test_a_term_the_cascade_cannot_read_is_caught(self) -> None:
        self.write(WORKFLOW + SHARDS.replace("selects: FullyQualifiedName~Payments.", "selects: Category=Slow"))

        problems = pipeline_gate.check_shards()

        self.assertTrue(any("'Category=Slow'" in problem for problem in problems), problems)

    def test_an_entry_the_parser_skips_is_caught(self) -> None:
        """Each shape below drops out of `read_shards`, leaving a clean cascade of the rest."""
        overlapping = (
            "FullyQualifiedName~Inventory.&FullyQualifiedName!~Payments.&FullyQualifiedName!~Ordering."
        )
        for entry in (
            f"          - shard: inventory  # slowest\n            selects: {overlapping}\n",
            f"          - selects: {overlapping}\n            shard: inventory\n",
            f"          - {{shard: inventory, selects: '{overlapping}'}}\n",
            f"          - \"shard\": inventory\n            'selects': {overlapping}\n",
            "          - shard: inventory\n",
        ):
            with self.subTest(entry=entry):
                self.write(WORKFLOW + SHARDS.replace("          - shard: rest", entry + "          - shard: rest"))

                problems = pipeline_gate.check_shards()

                self.assertTrue(any("the parser read 3 entries" in problem for problem in problems), problems)

    def test_a_comment_at_job_indent_does_not_end_the_matrix(self) -> None:
        """Read as the next job, the comment would hide the entry after it from both readers."""
        self.write(
            WORKFLOW + SHARDS
            + "  # the slowest last\n"
            + "          - shard: inventory\n            selects: FullyQualifiedName~Inventory.\n"
        )

        problems = pipeline_gate.check_shards()

        self.assertTrue(any("the last shard, inventory" in problem for problem in problems), problems)

    def test_an_unparsed_matrix_fails_rather_than_passing_empty(self) -> None:
        self.write(WORKFLOW)

        problems = pipeline_gate.check_shards()

        self.assertEqual(len(problems), 1)
        self.assertIn("parser", problems[0])


def trx(total: int, assembly: str, tests: list[str]) -> str:
    # The class name is deliberately NOT derived from the assembly: two
    # projects holding a `Suite` is what the identity has to survive, and a
    # fixture that made them differ would pass whether or not the production
    # key carries the assembly.
    results = "".join(
        f'<UnitTest name="{name}"><TestMethod codeBase="/x/{assembly}.dll" '
        f'className="Suite" name="{name}" /></UnitTest>'
        for name in tests
    )
    return (
        '<?xml version="1.0" encoding="UTF-8"?>'
        '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
        f'<Results />{results}'
        f'<ResultSummary><Counters total="{total}" passed="{total}" failed="0" /></ResultSummary>'
        "</TestRun>"
    )


class StageTests(unittest.TestCase):
    def setUp(self) -> None:
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)

        self.solution = self.root / "Platform.slnx"
        self.solution.write_text(
            '<Solution>'
            '<Project Path="tests/Catalog.Domain.Tests/Catalog.Domain.Tests.csproj" />'
            '<Project Path="tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj" />'
            '<Project Path="tests/Catalog.TestSupport/Catalog.TestSupport.csproj" />'
            '<Project Path="src/Services/Catalog/Catalog.Api/Catalog.Api.csproj" />'
            "</Solution>",
            encoding="utf-8",
        )

    def stage(self, name: str, *documents: str) -> Path:
        directory = self.root / name
        directory.mkdir()
        for index, document in enumerate(documents):
            (directory / f"{index}.trx").write_text(document, encoding="utf-8")
        return directory

    def test_test_support_is_not_a_test_project(self) -> None:
        """§4.1, and the same predicate Directory.Build.props scopes CA1707
        with: EndsWith("Tests"), so `Platform.IntegrationTests` counts and
        `Catalog.TestSupport` does not."""
        self.assertEqual(
            pipeline_gate.test_projects(self.solution),
            {"Catalog.Domain.Tests", "Platform.IntegrationTests"},
        )

    def test_a_clean_run_passes(self) -> None:
        """Every stage that `STAGE_FLOORS` names runs, as in a clean run."""
        architecture = self.stage(
            "architecture", trx(18, "Catalog.Domain.Tests", ["arch"])
        )
        unit = self.stage("unit", trx(600, "Catalog.Domain.Tests", ["a", "b"]))
        integration = self.stage(
            "integration", trx(160, "Platform.IntegrationTests", ["c"])
        )

        self.assertEqual(
            pipeline_gate.check_stages([architecture, unit, integration], self.solution),
            [],
        )

    def test_an_empty_stage_is_caught(self) -> None:
        """§12.1's trap: `dotnet test` exits zero on a filter that
        selects nothing, so the step is green and the stage does not exist."""
        unit = self.stage("unit", trx(0, "Catalog.Domain.Tests", []))
        integration = self.stage("integration", trx(160, "Platform.IntegrationTests", ["c"]))

        problems = pipeline_gate.check_stages([unit, integration], self.solution)

        self.assertTrue(any("ran no tests" in problem for problem in problems))

    def test_a_stage_below_its_floor_is_caught(self) -> None:
        unit = self.stage("unit", trx(12, "Catalog.Domain.Tests", ["a"]))
        integration = self.stage("integration", trx(160, "Platform.IntegrationTests", ["c"]))

        problems = pipeline_gate.check_stages([unit, integration], self.solution)

        self.assertTrue(any("below its floor" in problem for problem in problems))

    def test_a_test_project_in_no_stage_is_caught(self) -> None:
        """The structural half, which has no number in it and therefore no
        number to go stale. A suite that stopped being selected is a suite that
        stopped running with nothing going red."""
        unit = self.stage("unit", trx(600, "Catalog.Domain.Tests", ["a"]))
        integration = self.stage("integration", trx(160, "Catalog.Domain.Tests", ["c"]))

        problems = pipeline_gate.check_stages([unit, integration], self.solution)

        self.assertTrue(any("Platform.IntegrationTests ran in no stage" in p for p in problems))

    def test_a_test_in_two_stages_is_caught(self) -> None:
        """ci.yml claims the filters are "exhaustive and disjoint by
        construction"; this is what makes the second half a check. An overlap
        on the integration stage is a container set paid for twice."""
        unit = self.stage("unit", trx(600, "Catalog.Domain.Tests", ["a", "shared"]))
        integration = self.stage(
            "integration",
            trx(160, "Platform.IntegrationTests", ["c"]),
            trx(0, "Catalog.Domain.Tests", ["shared"]),
        )

        problems = pipeline_gate.check_stages([unit, integration], self.solution)

        self.assertTrue(any("ran in both" in problem for problem in problems))

    def test_the_same_test_name_in_two_assemblies_is_not_an_overlap(self) -> None:
        """The identity carries the assembly, and without it this is a red
        build with no defect behind it. `ArchitectureTests` is very nearly a
        class of the same name in more than one project already."""
        architecture = self.stage(
            "architecture", trx(18, "Catalog.Domain.Tests", ["arch"])
        )
        unit = self.stage("unit", trx(600, "Catalog.Domain.Tests", ["a"]))
        integration = self.stage("integration", trx(160, "Platform.IntegrationTests", ["a"]))

        problems = pipeline_gate.check_stages(
            [architecture, unit, integration], self.solution
        )

        self.assertEqual(problems, [])

    def test_a_declared_stage_nobody_passed_is_caught(self) -> None:
        """The other direction, and the one that was missing.

        Rejecting an undeclared directory says nothing about a declared stage
        nobody ran. Drop the architecture invocation and its argument, let
        those tests fall into the unit filter, and the project-coverage, floor
        and overlap checks all pass while the gate reports two stages — a gate
        cannot fail on a file that is not there.
        """
        unit = self.stage("unit", trx(600, "Catalog.Domain.Tests", ["a"]))
        integration = self.stage("integration", trx(160, "Platform.IntegrationTests", ["c"]))

        problems = pipeline_gate.check_stages([unit, integration], self.solution)

        self.assertEqual(len(problems), 1)
        self.assertIn("architecture stage was never read", problems[0])

    def test_an_undeclared_stage_directory_is_caught(self) -> None:
        """A results directory nobody declared has no floor, and a check with
        no threshold is a check that did not run."""
        smoke = self.stage("smoke", trx(600, "Catalog.Domain.Tests", ["a"]))

        problems = pipeline_gate.check_stages([smoke], self.solution)

        self.assertTrue(any("not one of the stages" in problem for problem in problems))

    def test_a_stage_with_no_trx_is_not_zero_tests(self) -> None:
        """A logger that did not run and a filter that selected nothing are
        different defects, and only one of them is about the tests."""
        empty = self.root / "unit"
        empty.mkdir()

        with self.assertRaises(SystemExit) as raised:
            pipeline_gate.read_stage(empty)

        self.assertIn("no *.trx", str(raised.exception))

    def test_reading_no_stage_at_all_fails_rather_than_passing(self) -> None:
        """The gate's own subject, third instance."""
        problems = pipeline_gate.check_stages([], self.solution)

        self.assertTrue(any("vacuously" in problem for problem in problems))


if __name__ == "__main__":
    unittest.main()
