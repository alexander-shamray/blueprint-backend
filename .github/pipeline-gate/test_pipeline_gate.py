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

import re
import tempfile
import time
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
    if: ${{ github.ref != 'refs/heads/main' }}
    strategy: &image-legs
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
          --label org.opencontainers.image.revision=${{ github.sha }}
          .

      - name: SBOM for ${{ matrix.image }}
        if: ${{ needs.changes.outputs[matrix.filter] == 'true' }}
        uses: anchore/sbom-action@66cbf4bc1f1c0d2edc94016e65bc221b6bb0ad6c # v0.24.3
        with:
          image: ${{ matrix.image }}:${{ github.sha }}
          output-file: sbom-${{ matrix.image }}.spdx.json

  publish-images:
    if: ${{ contains(fromJSON('["push","workflow_dispatch"]'), github.event_name) && github.ref == 'refs/heads/main' }}
    permissions:
      packages: write
    strategy: *image-legs
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
          --label org.opencontainers.image.revision=${{ github.sha }}
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

      - name: Move the main tag of ${{{{ matrix.image }}}}
        if: {MAIN_PUSH}
        run: |
          tag="$REGISTRY/${{{{ matrix.image }}}}:main"
          docker buildx imagetools inspect "$REGISTRY/${{{{ matrix.image }}}}:main"
          ancestor=0
          git merge-base --is-ancestor "$named" "$GITHUB_SHA" || ancestor=$?
          case "$ancestor" in
            0) ;;
            1) echo "a newer build moved it"; exit 0 ;;
            *) echo "could not compare"; exit 1 ;;
          esac
          docker tag "$REGISTRY/${{{{ matrix.image }}}}:${{{{ github.sha }}}}" "$REGISTRY/${{{{ matrix.image }}}}:main"
          docker push "$REGISTRY/${{{{ matrix.image }}}}:main"
          moved=$(docker buildx imagetools inspect "$tag")
          if [ "$moved" != "$DIGEST" ]; then
            if [ -n "$later" ]; then
              exit 0
            fi
            echo "the tag names other bytes, not exit 1"
            exit 1
          fi
        env:
          DIGEST: ${{{{ steps.push.outputs.digest }}}}
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
          gh attestation verify "oci://x" --repo "$GITHUB_REPOSITORY" \\
            --signer-workflow "$GITHUB_REPOSITORY/.github/workflows/ci.yml" --source-ref refs/heads/main
          docker buildx imagetools create --tag "$REGISTRY/$1:$RECORD" "$REGISTRY/$1@$VERIFIED"
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

    def test_a_quantifier_only_a_comment_names_is_caught(self) -> None:
        """ci.yml's comments name the word too, so a search of the text passed a key deleted or changed."""
        for line in ("# predicate-quantifier: 'some-with-excludes'", "predicate-quantifier: 'every'"):
            with self.subTest(line=line):
                self.write(WORKFLOW.replace(
                    "predicate-quantifier: 'some-with-excludes'", f"{line}\n          # some-with-excludes"))

                problems = pipeline_gate.check_filters(self.root)

                self.assertTrue(any("predicate-quantifier" in problem for problem in problems), problems)

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

    def test_a_double_quoted_or_bare_item_is_a_filter(self) -> None:
        """Valid YAML either way, and dropped it reads as a deployable no filter reaches."""
        for item in ('"src/Gateway/**"', "src/Gateway/**", "'src/Gateway/**' # a comment"):
            with self.subTest(item=item):
                self.write(WORKFLOW.replace("- 'src/Gateway/**'", f"- {item}"))

                self.assertEqual(pipeline_gate.check_filters(self.root), [])

    def test_each_spelling_of_an_item_is_read(self) -> None:
        for line, expected in (
            ("  - 'a/**'", "a/**"),
            ('  - "!a/**"', "!a/**"),
            ("  - a/**", "a/**"),
            ("  - a/** # why", "a/**"),
            ("  - a/b#c", "a/b#c"),
            ("  - 'a' 'b'", None),
            ('  - "a\\"b"', None),
            ("  - *shared", None),
            ("  - [a]", None),
            ("  - added: '!a/**'", None),
            ("  - a/**:", None),
            ("  - a:b/**", "a:b/**"),
            ("  -'a'", None),
        ):
            with self.subTest(line=line):
                self.assertEqual(pipeline_gate.filter_item(line), expected)

    def test_a_long_run_of_spaces_is_read_one_way(self) -> None:
        """A run the pattern could divide many ways would take exponential time on a line it then refuses."""
        for tail in (": x", "\u00a0"):
            with self.subTest(tail=tail):
                started = time.monotonic()
                self.assertIsNone(pipeline_gate.filter_item("  - a" + " " * 26 + tail))
                self.assertLess(time.monotonic() - started, 1)
        self.assertEqual(pipeline_gate.filter_item("  - a  b   # why"), "a  b")


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

    def test_a_job_level_condition_on_a_filter_is_caught(self) -> None:
        """A skipped matrix job is never expanded, so GitHub names its one
        check with `${{ matrix.image }}` unevaluated, on the pull request that
        reads it. Each step tests its own filter instead."""
        self.write(WORKFLOW.replace(
            "    if: ${{ github.ref != 'refs/heads/main' }}\n",
            "    if: ${{ needs.changes.outputs.catalog == 'true' }}\n"))

        problems = pipeline_gate.check_images(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("job-level `if: ${{ needs.changes.outputs.catalog == 'true' }}`", problems[0])

    def test_a_job_level_condition_folded_over_lines_is_read_whole(self) -> None:
        self.write(WORKFLOW.replace(
            "    if: ${{ github.ref != 'refs/heads/main' }}\n",
            "    if: >-\n"
            "      ${{ needs.changes.outputs.catalog == 'true'\n"
            "      || needs.changes.outputs.gateway == 'true' }}\n"))

        problems = pipeline_gate.check_images(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("|| needs.changes.outputs.gateway == 'true' }}", problems[0])

    def test_an_images_job_with_no_condition_is_caught(self) -> None:
        """It would build every image twice on main, beside publish-images."""
        self.write(WORKFLOW.replace("    if: ${{ github.ref != 'refs/heads/main' }}\n", ""))

        problems = pipeline_gate.check_images(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("twice on main", problems[0])

    def test_a_renamed_images_job_fails_rather_than_passing_empty(self) -> None:
        """A key the parser cannot find reads as None rather than as no `if:`,
        which fails with a message of its own, so it has to fail by itself."""
        self.write(WORKFLOW.replace("  images:\n", "  build-images:\n"))

        problems = pipeline_gate.check_images(self.root)

        self.assertTrue(any("found no `images:` job" in problem for problem in problems), problems)

    def test_a_publish_job_a_pull_request_can_reach_is_caught(self) -> None:
        """The job holds what signs and pushes, so its own condition keeps it
        off a pull request whatever each step's says (ADR-197)."""
        self.write(WORKFLOW.replace(
            "    if: ${{ contains(fromJSON('[\"push\",\"workflow_dispatch\"]'), github.event_name) "
            "&& github.ref == 'refs/heads/main' }}\n",
            ""))

        problems = pipeline_gate.check_images(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("publish-images", problems[0])

    def test_a_publish_job_a_branch_dispatch_can_reach_is_caught(self) -> None:
        """Every word the right condition holds, joined so a dispatch from any branch builds."""
        self.write(WORKFLOW.replace(
            "    if: ${{ contains(fromJSON('[\"push\",\"workflow_dispatch\"]'), github.event_name) "
            "&& github.ref == 'refs/heads/main' }}\n    permissions:",
            "    if: ${{ contains(fromJSON('[\"push\",\"workflow_dispatch\"]'), github.event_name) "
            "|| github.ref == 'refs/heads/main' }}\n    permissions:"))

        problems = pipeline_gate.check_images(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("publish-images", problems[0])

    def test_a_renamed_publish_job_fails_rather_than_passing_empty(self) -> None:
        self.write(WORKFLOW.replace("  publish-images:\n", "  release-images:\n"))

        problems = pipeline_gate.check_images(self.root)

        self.assertTrue(any("found no `publish-images:` job" in problem for problem in problems), problems)

    def test_a_build_main_runs_and_no_pull_request_proved_is_caught(self) -> None:
        """The pull request's build is the proof of main's only if the lines match."""
        head, tail = WORKFLOW.split("  publish-images:\n")
        self.write(head + "  publish-images:\n" + tail.replace("          --label", "          --pull\n          --label", 1))

        problems = pipeline_gate.check_images(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("not the line", problems[0])

    def test_a_publish_job_with_its_own_matrix_is_caught(self) -> None:
        """A second list drifts from the first, and the inventory reads pairs from either."""
        self.write(WORKFLOW.replace("    strategy: *image-legs\n", "    strategy:\n      fail-fast: false\n"))

        problems = pipeline_gate.check_images(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("image-legs", problems[0])

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
    """ADR-071's coverage: every build either image job runs has its SBOM."""

    def test_a_build_with_no_sbom_step_is_caught(self) -> None:
        self.write(WORKFLOW.replace(SBOM_STEP, ""))

        problems = pipeline_gate.check_images(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("publish-images job builds ${{ matrix.image }}:${{ github.sha }} and no anchore/sbom-action@", problems[0])

    def test_a_pull_request_build_with_no_sbom_step_is_caught(self) -> None:
        """Its own job, so reading only the job that signs would leave it out."""
        head, tail = WORKFLOW.split("      - name: SBOM for ${{ matrix.image }}\n", 1)
        pull_request_sbom, rest = tail.split("  publish-images:\n", 1)
        self.write(head + "  publish-images:\n" + rest)

        problems = pipeline_gate.check_images(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("the images job builds", problems[0])

    def test_an_sbom_of_another_reference_is_caught(self) -> None:
        self.write(WORKFLOW.replace(
            "          image: ${{ matrix.image }}:${{ github.sha }}",
            "          image: ${{ matrix.image }}:latest"))

        self.assertEqual(len(pipeline_gate.check_images(self.root)), 2)

    def test_an_sbom_action_pinned_to_a_tag_is_caught(self) -> None:
        """A tag can be moved to other code; the action reads the image."""
        self.write(WORKFLOW.replace("@66cbf4bc1f1c0d2edc94016e65bc221b6bb0ad6c # v0.24.3", "@v0"))

        problems = pipeline_gate.check_images(self.root)

        self.assertEqual(len(problems), 2)
        self.assertTrue(all("pinned to a commit" in problem for problem in problems), problems)

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

        self.assertEqual(len(problems), 3)
        self.assertTrue(all("vacuously" in problem for problem in problems))

    def test_a_build_or_push_form_no_check_reads_is_caught(self) -> None:
        """Beside the plain build the vacuity guards stay quiet, so the form itself is what fails."""
        guard = "        if: ${{ needs.changes.outputs[matrix.filter] == 'true' }}\n"
        for body, form in (
            ('        run: docker buildx build --push --tag "$REGISTRY/x:main" .\n', "docker buildx build"),
            ('        run: docker image push "$REGISTRY/x:main"\n', "docker image push"),
            ("        uses: docker/build-push-action@v6\n", "docker/build-push-action@v6"),
        ):
            with self.subTest(form=form):
                self.write(WORKFLOW.replace(SBOM_STEP, f"      - name: Another build\n{guard}{body}\n{SBOM_STEP}", 1))

                problems = [p for p in pipeline_gate.check_images(self.root) if "no check here reads" in p]

                self.assertEqual(len(problems), 1, problems)
                self.assertIn(f"uses `{form}`", problems[0])

    def test_the_step_reader_joins_a_folded_run_and_keeps_with_inputs(self) -> None:
        steps = pipeline_gate.read_job_steps(WORKFLOW_HEAD + SBOM_STEP, "publish-images")

        self.assertEqual(len(steps), 3)
        self.assertIn("--label org.opencontainers.image.revision=${{ github.sha }} .", steps[1]["run"])
        self.assertEqual(steps[2]["image"], "${{ matrix.image }}:${{ github.sha }}")
        self.assertNotIn("comment", " ".join(steps[1].values()))


def retried(step: str, step_id: str, label: str) -> str:
    """One action step as its first attempt, the note and its second attempt."""
    guard = f"${{{{ steps.{step_id}.outcome == 'failure' }}}}"
    first = step.replace("        uses:", f"        id: {step_id}\n        continue-on-error: true\n        uses:", 1)
    note = (
        f"      - name: Note the failed {label}\n"
        f"        if: {guard}\n"
        f'        run: bash {pipeline_gate.RETRY_HELPER} note "{label}"\n'
    )
    second = re.sub(r"        if: .*", f"        if: {guard}", step, count=1)
    return first + "\n" + note + "\n" + second


RETRIED_SBOM = retried(SBOM_STEP, "sbom", "SBOM")


class RetryTests(Fixture):
    """A step that may fail is followed by its noted second attempt, and the
    duplicate leaves every other images check true."""

    def test_a_retried_workflow_is_clean(self) -> None:
        """Its subject is the duplicate: every reader takes the first attempt
        for the step, and the helper in front of a command hides neither."""
        workflow = WORKFLOW.replace(SBOM_STEP, RETRIED_SBOM)
        helper = f'bash {pipeline_gate.RETRY_HELPER} run'
        workflow = workflow.replace("          docker build\n", f'          {helper} "Build"\n          docker build\n')
        workflow = workflow.replace('run: cosign sign', f'run: {helper} "Sign" cosign sign')
        self.assertIn(pipeline_gate.RETRY_HELPER, workflow)
        self.write(workflow)

        self.assertEqual(pipeline_gate.check_images(self.root), [])

    def test_a_first_attempt_with_no_second_is_caught(self) -> None:
        """The defect a retry can bring: the step's failure passes, and the main
        tag moves onto an image with no signature or attestation behind it."""
        first = SBOM_STEP.replace("        uses:", "        id: sbom\n        continue-on-error: true\n        uses:", 1)
        self.write(WORKFLOW.replace(SBOM_STEP, first))

        problems = pipeline_gate.check_images(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("no note", problems[0])

    def test_a_quoted_continue_on_error_is_read(self) -> None:
        """Folded into the key above, it would pass a step that may fail unnoted."""
        for key in ('"continue-on-error"', "'continue-on-error'", "continue-on-error "):
            with self.subTest(key=key):
                first = SBOM_STEP.replace("        uses:", f"        id: sbom\n        {key}: true\n        uses:", 1)

                problems = pipeline_gate.check_retries(WORKFLOW.replace(SBOM_STEP, first))

                self.assertEqual(len(problems), 1, problems)
                self.assertIn("no note", problems[0])

    def test_a_step_line_it_cannot_read_is_refused(self) -> None:
        for line in (
            "      - { name: Sign, continue-on-error: true, run: cosign sign }\n",
            "        - continue-on-error: true\n",
            "    - run: cosign sign\n",
        ):
            with self.subTest(line=line):
                problems = pipeline_gate.check_retries(WORKFLOW.replace(SBOM_STEP, SBOM_STEP + line))

                self.assertEqual(len(problems), 1, problems)
                self.assertIn(f"holds `{line.strip()}`, a step line this check cannot read", problems[0])

    def test_a_first_attempt_with_no_id_is_caught(self) -> None:
        """Nothing can read the outcome of a step with no id."""
        workflow = WORKFLOW.replace(SBOM_STEP, RETRIED_SBOM.replace("        id: sbom\n", ""))

        problems = pipeline_gate.check_retries(workflow)

        self.assertEqual(len(problems), 1)
        self.assertIn("no `id`", problems[0])

    def test_a_retry_nothing_notes_is_caught(self) -> None:
        """A retry hides the failure it recovered from unless something records it."""
        note, _, rest = RETRIED_SBOM.partition("      - name: Note the failed SBOM\n")
        kept = rest.split("\n\n", 1)[1]

        problems = pipeline_gate.check_retries(WORKFLOW.replace(SBOM_STEP, note + kept))

        self.assertTrue(any("no note" in problem for problem in problems), problems)

    def test_a_note_guarded_on_another_step_is_caught(self) -> None:
        """It would not run when this step failed, so the retry would go unrecorded."""
        guard = "${{ steps.sbom.outcome == 'failure' }}"
        wrong = RETRIED_SBOM.replace(guard, "${{ steps.push.outcome == 'failure' }}", 1)

        problems = pipeline_gate.check_retries(WORKFLOW.replace(SBOM_STEP, wrong))

        self.assertEqual(len(problems), 1)
        self.assertIn("no note", problems[0])

    def test_a_second_attempt_guarded_on_another_step_is_caught(self) -> None:
        """It would not run when this step failed, so the failure would pass."""
        head, second = RETRIED_SBOM.rsplit("        if: ", 1)
        wrong = head + "        if: ${{ steps.push.outcome == 'failure' }}\n" + second.split("\n", 1)[1]

        problems = pipeline_gate.check_retries(WORKFLOW.replace(SBOM_STEP, wrong))

        self.assertEqual(len(problems), 1)
        self.assertIn("second attempt", problems[0])

    def test_a_second_attempt_that_may_fail_too_is_caught(self) -> None:
        head, second = RETRIED_SBOM.rsplit("        uses:", 1)
        wrong = head + "        continue-on-error: true\n        uses:" + second

        problems = pipeline_gate.check_retries(WORKFLOW.replace(SBOM_STEP, wrong))

        self.assertTrue(any("fails the job when it fails too" in problem for problem in problems), problems)

    def test_a_second_attempt_doing_something_else_is_caught(self) -> None:
        """What is signed or inventoried has to be the same whichever attempt ran."""
        head, second = RETRIED_SBOM.rsplit("          output-file: sbom-", 1)
        wrong = head + "          output-file: other-" + second

        problems = pipeline_gate.check_retries(WORKFLOW.replace(SBOM_STEP, wrong))

        self.assertEqual(len(problems), 1)
        self.assertIn("not what the first attempt does", problems[0])

    def test_keeping_the_note_may_fail_alone(self) -> None:
        """Losing the record of a retry is no reason to fail the leg it recovered."""
        keep = (
            "      - name: Keep what a retry recovered from\n"
            "        continue-on-error: true\n"
            "        uses: actions/upload-artifact@v7\n"
            "        with:\n"
            f"          path: ${{{{ runner.temp }}}}/{pipeline_gate.RETRY_RECORD}\n"
        )

        self.assertEqual(pipeline_gate.check_retries(WORKFLOW + keep), [])

    def test_another_upload_that_may_fail_is_caught(self) -> None:
        """The exemption is the record's: any other upload's failure would pass with no retry."""
        keep = (
            "      - name: Keep the inventory\n"
            "        continue-on-error: true\n"
            "        uses: actions/upload-artifact@v7\n"
            "        with:\n"
            "          path: sbom.spdx.json\n"
        )

        problems = pipeline_gate.check_retries(WORKFLOW + keep)

        self.assertEqual(len(problems), 1)
        self.assertIn("no `id`", problems[0])

    def test_this_repositorys_image_steps_are_retried(self) -> None:
        """The subject is the real workflow: `check_retries` only holds the
        retries there are, so a step that loses its retry is caught here."""
        root = Path(__file__).resolve().parents[2]
        workflow = (root / ".github/workflows/ci.yml").read_text(encoding="utf-8")
        self.assertEqual(pipeline_gate.check_retries(workflow), [])

        run = f"{pipeline_gate.RETRY_HELPER} run"
        for job, actions in (
            ("images", (pipeline_gate.SBOM_ACTION,)),
            (pipeline_gate.PUBLISH_JOB, (
                pipeline_gate.SBOM_ACTION,
                pipeline_gate.PROVENANCE_ACTION,
                pipeline_gate.SBOM_ATTESTATION_ACTION,
            )),
        ):
            steps = pipeline_gate.read_job_steps(workflow, job)
            builds = [step for step in steps if pipeline_gate.DOCKER_BUILD.search(step.get("run", ""))]
            self.assertTrue(builds)
            self.assertTrue(all(run in step["run"] for step in builds), job)
            for action in actions:
                attempts = [step for step in steps if step.get("uses", "").strip().startswith(action)]
                self.assertEqual(len(attempts), 2, f"{job}: {action}")
                self.assertIn("continue-on-error", attempts[0])
            keep = [step for step in steps if "image-retries.txt" in step.get("path", "")]
            self.assertEqual(len(keep), 1, job)
            self.assertTrue(keep[0]["uses"].startswith(pipeline_gate.NOTE_ARTIFACT_ACTION))

        signs = [
            step for step in pipeline_gate.read_job_steps(workflow, pipeline_gate.PUBLISH_JOB)
            if "cosign sign" in step.get("run", "")
        ]
        self.assertEqual(len(signs), 1)
        self.assertIn(run, signs[0]["run"])

    def test_what_a_retry_recovered_from_reaches_the_tracker(self) -> None:
        """The artifacts' names and the job that posts them agree only by text,
        so a rename would leave every retry unposted and nothing red."""
        root = Path(__file__).resolve().parents[2]
        workflow = (root / ".github/workflows/ci.yml").read_text(encoding="utf-8")
        report = pipeline_gate.read_job_steps(workflow, "image-retry-report")

        patterns = [step.get("pattern", "").strip() for step in report]
        self.assertIn("image-retry-*", patterns)
        self.assertTrue(any("gh issue comment 1406" in step.get("run", "") for step in report))
        for job in ("images", pipeline_gate.PUBLISH_JOB):
            names = [step.get("name", "") for step in pipeline_gate.read_job_steps(workflow, job)]
            self.assertIn("image-retry-${{ matrix.image }}", [name.strip() for name in names])


GRANTED = """\
on:
  pull_request:
  push:
    branches: [main]

permissions:
  contents: read

jobs:
  build:
    steps:
      - uses: actions/checkout@v7

  report:
    permissions:
      issues: write
    steps:
      - run: gh issue comment 1 --body hello

  publish:
    if: ${{ github.event_name == 'push' && github.ref == 'refs/heads/main' }}
    permissions:
      contents: read
      packages: write
    steps:
      - uses: actions/checkout@v7

  scheduled:
    if: ${{ github.event_name == 'schedule' || github.event_name == 'workflow_dispatch' }}
    permissions:
      issues: write
    steps:
      - uses: actions/checkout@v7
"""


class GrantTests(unittest.TestCase):
    """ADR-197: nothing a pull request's branch can run holds a write grant."""

    def setUp(self) -> None:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)
        self.folder = self.root / ".github" / "workflows"
        self.folder.mkdir(parents=True)
        self.write(GRANTED)

    def write(self, text: str, name: str = "ci.yml") -> None:
        (self.folder / name).write_text(text, encoding="utf-8")

    def test_the_fixture_is_clean(self) -> None:
        """Main-only, schedule-only and no checkout: the three ways out."""
        self.assertEqual(pipeline_gate.check_grants(self.root), [])

    def test_a_grant_beside_a_checkout_on_a_pull_request_is_caught(self) -> None:
        """ADR-197's shape: the branch's MSBuild files run beside the job's token."""
        self.write(GRANTED.replace(
            "  build:\n    steps:",
            "  build:\n    permissions:\n      contents: read\n      packages: write\n    steps:"))

        problems = pipeline_gate.check_grants(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("job `build`", problems[0])
        self.assertIn("packages: write", problems[0])

    def test_a_grant_beside_a_checkout_is_caught_under_a_commented_or_quoted_job_key(self) -> None:
        for key in ("  build: # the solution", "  'build':", '  "build":'):
            with self.subTest(key=key):
                self.write(GRANTED.replace(
                    "  build:\n    steps:",
                    f"{key}\n    permissions:\n      packages: write\n    steps:"))

                problems = pipeline_gate.check_grants(self.root)

                self.assertEqual(len(problems), 1, problems)
                self.assertIn("job `build`", problems[0])

    def test_a_grant_beside_a_reusable_workflow_call_on_a_pull_request_is_caught(self) -> None:
        """The called workflow's trigger is workflow_call, so the caller is the one place the grant is judged."""
        for call in ("./.github/workflows/build.yml", "octo/tools/.github/workflows/build.yml@v1"):
            with self.subTest(call=call):
                self.write(GRANTED.replace(
                    "  build:\n    steps:\n      - uses: actions/checkout@v7\n",
                    f"  build:\n    permissions:\n      packages: write\n    uses: {call}\n"))

                problems = pipeline_gate.check_grants(self.root)

                self.assertEqual(len(problems), 1, problems)
                self.assertIn("job `build` holds packages: write and calls a reusable workflow", problems[0])

    def test_a_job_key_this_check_cannot_read_is_refused(self) -> None:
        self.write(GRANTED.replace("  build:\n", "  build: &build\n"))

        problems = pipeline_gate.check_grants(self.root)

        self.assertTrue(any("`build: &build`, a key this check cannot read" in p for p in problems), problems)

    def test_a_checkout_in_the_report_job_is_caught(self) -> None:
        """Its grant is harmless only while it holds none of the branch."""
        self.write(GRANTED.replace(
            "      - run: gh issue comment 1 --body hello\n",
            "      - uses: actions/checkout@v7\n      - run: gh issue comment 1 --body hello\n"))

        problems = pipeline_gate.check_grants(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("job `report`", problems[0])

    def test_a_condition_that_lets_a_pull_request_in_is_caught(self) -> None:
        self.write(GRANTED.replace(
            "    if: ${{ github.event_name == 'push' && github.ref == 'refs/heads/main' }}\n",
            "    if: ${{ github.event_name == 'pull_request' || github.ref == 'refs/heads/main' }}\n"))

        problems = pipeline_gate.check_grants(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("job `publish`", problems[0])

    def test_a_condition_that_names_no_event_or_ref_is_caught(self) -> None:
        """A filter or a variable decides nothing about which event ran."""
        self.write(GRANTED.replace(
            "    if: ${{ github.event_name == 'push' && github.ref == 'refs/heads/main' }}\n",
            "    if: ${{ needs.changes.outputs.catalog == 'true' }}\n"))

        problems = pipeline_gate.check_grants(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("job `publish`", problems[0])

    def test_a_condition_a_pull_request_can_satisfy_is_caught(self) -> None:
        """Naming the event or the ref is not excluding a pull request: each of
        these reads as main-only to a substring test and runs on one."""
        for condition in (
            "github.event_name == 'push' || github.actor != 'nobody'",
            "github.ref == 'refs/heads/main' || always()",
            "always() || (github.event_name == 'push')",
            "!(github.event_name == 'push')",
            "github.event_name != 'push'",
            "github.ref != 'refs/heads/main'",
            "(always()) || (vars.x == 'y' && github.ref == 'refs/heads/main')",
            "always() || vars.x == 'y' && github.ref == 'refs/heads/main'",
        ):
            with self.subTest(condition=condition):
                self.write(GRANTED.replace(
                    "    if: ${{ github.event_name == 'push' && github.ref == 'refs/heads/main' }}\n",
                    f"    if: ${{{{ {condition} }}}}\n"))

                problems = pipeline_gate.check_grants(self.root)

                self.assertEqual(len(problems), 1)
                self.assertIn("job `publish`", problems[0])

    def test_a_condition_that_excludes_a_pull_request_passes(self) -> None:
        """The shapes this repository's own jobs carry, with the positive controls
        the refusals above need."""
        for condition in (
            "github.event_name == 'push' && github.ref == 'refs/heads/main'",
            "contains(fromJSON('[\"push\",\"workflow_dispatch\"]'), github.event_name) "
            "&& github.ref == 'refs/heads/main'",
            "(github.event_name == 'schedule' || github.event_name == 'workflow_dispatch') "
            "&& vars.CHECK_SCHEDULED == 'enabled'",
            "always() && github.ref == 'refs/heads/main'",
            "(github.event_name == 'push') && (vars.x == 'y')",
        ):
            with self.subTest(condition=condition):
                self.write(GRANTED.replace(
                    "    if: ${{ github.event_name == 'push' && github.ref == 'refs/heads/main' }}\n",
                    f"    if: ${{{{ {condition} }}}}\n"))

                self.assertEqual(pipeline_gate.check_grants(self.root), [])

    def test_a_folded_condition_is_read_whole(self) -> None:
        self.write(GRANTED.replace(
            "    if: ${{ github.event_name == 'push' && github.ref == 'refs/heads/main' }}\n",
            "    if: >-\n      ${{ github.event_name == 'push'\n      && github.ref == 'refs/heads/main' }}\n"))

        self.assertEqual(pipeline_gate.check_grants(self.root), [])

    def test_a_flow_mapping_is_a_grant(self) -> None:
        """Read as no grant, it would pass beside the checkout it is held for."""
        self.write(GRANTED.replace(
            "  build:\n    steps:",
            "  build:\n    permissions: { contents: read, packages: write }\n    steps:"))

        problems = pipeline_gate.check_grants(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("packages: write", problems[0])

    def test_a_quoted_grant_is_a_grant(self) -> None:
        """Valid YAML either way, and the same grant."""
        for permissions in (
            "    permissions:\n      packages: 'write'\n",
            '    permissions:\n      packages: "write"\n',
            '    permissions: "write-all"\n',
            "    permissions: 'write-all'\n",
        ):
            with self.subTest(permissions=permissions):
                self.write(GRANTED.replace("  build:\n    steps:", f"  build:\n{permissions}    steps:"))

                problems = pipeline_gate.check_grants(self.root)

                self.assertEqual(len(problems), 1)
                self.assertIn("job `build`", problems[0])

    def test_a_quoted_scope_is_a_grant(self) -> None:
        """The closing quote sits between the scope and its colon; still the same grant."""
        for permissions in (
            "    permissions:\n      'packages': write\n",
            '    permissions:\n      "packages": "write"\n',
            "    permissions: { 'packages': write }\n",
            '    permissions: { contents: read, "packages": write }\n',
            "    permissions:\n      packages : write\n",
            "    permissions :\n      packages: write\n",
        ):
            with self.subTest(permissions=permissions):
                self.write(GRANTED.replace("  build:\n    steps:", f"  build:\n{permissions}    steps:"))

                problems = pipeline_gate.check_grants(self.root)

                self.assertEqual(len(problems), 1)
                self.assertIn("packages: write", problems[0])

    def test_a_quoted_job_condition_still_keeps_a_pull_request_out(self) -> None:
        self.write(GRANTED.replace("    if: ${{ github.event_name", '    "if": ${{ github.event_name'))

        self.assertEqual(pipeline_gate.check_grants(self.root), [])

    def test_a_commented_grant_is_not_a_grant(self) -> None:
        self.write(GRANTED.replace(
            "  build:\n    steps:",
            "  build:\n    permissions:\n      # packages: write\n      contents: read\n    steps:"))

        self.assertEqual(pipeline_gate.check_grants(self.root), [])

    def test_a_trigger_written_as_a_list_is_read(self) -> None:
        """The other two spellings of `on:`; a trigger it cannot read is a workflow it skips."""
        for trigger in (
            "on:\n  - pull_request\n  - push\n",
            "on: [pull_request, push]\n",
            "on: pull_request\n",
            # yamllint's truthy rule asks for the quoted key.
            '"on": [pull_request, push]\n',
            "'on':\n  'pull_request':\n",
            # The review events start from a pull request's branch too.
            "on:\n  pull_request_review:\n",
            "on: [pull_request_review_comment]\n",
            # A space before the key's colon is still the key.
            "on :\n  pull_request:\n",
        ):
            with self.subTest(trigger=trigger):
                self.write(GRANTED.replace(
                    "on:\n  pull_request:\n  push:\n    branches: [main]\n", trigger
                ).replace(
                    "  build:\n    steps:",
                    "  build:\n    permissions:\n      packages: write\n    steps:"))

                self.assertEqual(len(pipeline_gate.check_grants(self.root)), 1)

    def test_a_trigger_it_cannot_read_is_refused(self) -> None:
        for trigger in (
            "on: {pull_request: {}}\n", "on:\n  pull_request: {types: [opened]}\n", "on:\n- pull_request\n",
            "on: [pull_request,\n  push]\n", "on: &events [pull_request]\n", "on:\n  pull_request: *types\n",
            "",
        ):
            with self.subTest(trigger=trigger):
                self.write(GRANTED.replace("on:\n  pull_request:\n  push:\n    branches: [main]\n", trigger))

                problems = pipeline_gate.check_grants(self.root)

                self.assertEqual(len(problems), 1, problems)
                self.assertIn("ci.yml writes `on:` in a form this check cannot read", problems[0])

    def test_a_grant_at_the_workflow_level_reaches_a_job_with_no_block(self) -> None:
        self.write(GRANTED.replace("permissions:\n  contents: read\n\njobs:", "permissions:\n  packages: write\n\njobs:"))

        problems = pipeline_gate.check_grants(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("job `build`", problems[0])

    def test_write_all_is_a_grant(self) -> None:
        self.write(GRANTED.replace(
            "  build:\n    steps:",
            "  build:\n    permissions: write-all\n    steps:"))

        problems = pipeline_gate.check_grants(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("write-all", problems[0])

    def test_a_workflow_a_pull_request_never_starts_is_not_read(self) -> None:
        self.write(GRANTED.replace("  pull_request:\n", "").replace(
            "  build:\n    steps:",
            "  build:\n    permissions:\n      packages: write\n    steps:"))

        self.assertEqual(pipeline_gate.check_grants(self.root), [])

    def test_pull_request_target_is_a_pull_request(self) -> None:
        self.write(GRANTED.replace("  pull_request:\n", "  pull_request_target:\n").replace(
            "  build:\n    steps:",
            "  build:\n    permissions:\n      packages: write\n    steps:"))

        self.assertEqual(len(pipeline_gate.check_grants(self.root)), 1)

    def test_a_main_ref_test_excludes_nothing_on_pull_request_target(self) -> None:
        """That run's github.ref is the base branch, so the test holds for every
        pull request into main."""
        only_the_ref = "    if: ${{ github.ref == 'refs/heads/main' }}\n"
        condition = "    if: ${{ github.event_name == 'push' && github.ref == 'refs/heads/main' }}\n"
        self.write(GRANTED.replace(condition, only_the_ref))
        self.assertEqual(pipeline_gate.check_grants(self.root), [])

        self.write(GRANTED.replace(condition, only_the_ref).replace("  pull_request:\n", "  pull_request_target:\n"))
        problems = pipeline_gate.check_grants(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("job `publish`", problems[0])

    def test_an_event_test_still_excludes_on_pull_request_target(self) -> None:
        self.write(GRANTED.replace("  pull_request:\n", "  pull_request_target:\n"))

        self.assertEqual(pipeline_gate.check_grants(self.root), [])

    def test_every_workflow_is_read(self) -> None:
        self.write(GRANTED.replace(
            "  build:\n    steps:",
            "  build:\n    permissions:\n      packages: write\n    steps:"), "other.yaml")

        problems = pipeline_gate.check_grants(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("other.yaml", problems[0])

    def test_no_grant_anywhere_fails_rather_than_passing_empty(self) -> None:
        """The gate's own subject: a parser that read no permissions block finds no fault."""
        self.write(GRANTED.replace(": write", ": read"))

        problems = pipeline_gate.check_grants(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("vacuously", problems[0])

    def test_no_workflow_fails_rather_than_passing_empty(self) -> None:
        (self.folder / "ci.yml").unlink()

        problems = pipeline_gate.check_grants(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("vacuously", problems[0])

    def test_the_shipped_workflows_are_clean(self) -> None:
        """Its subject is the repository's own: the fixture says nothing about ci.yml."""
        self.assertEqual(pipeline_gate.check_grants(), [])


class JobReaderTests(unittest.TestCase):
    """A comment at a job key's indent sits inside the job, so no reader ends the job at it."""

    TEXT = (
        "jobs:\n"
        "  build:\n"
        "  # A comment at the job key's indent, before the condition.\n"
        "    if: github.event_name == 'push'\n"
        "    outputs:\n"
        "      first: x\n"
        "  # And inside the outputs.\n"
        "      second: y\n"
        "    steps:\n"
        "      - run: one\n"
        "  # And between two steps.\n"
        "      - run: two\n"
        "  next:\n"
        "    outputs:\n"
        "      third: z\n"
        "    steps:\n"
        "      - run: three\n"
    )

    def test_the_condition_is_read_past_the_comment(self) -> None:
        self.assertEqual(pipeline_gate.read_job_condition(self.TEXT, "build"), "github.event_name == 'push'")

    def test_the_outputs_are_read_past_the_comment(self) -> None:
        self.assertEqual(pipeline_gate.read_job_outputs(self.TEXT, "build"), {"first", "second"})

    def test_the_steps_are_read_past_the_comment(self) -> None:
        self.assertEqual(pipeline_gate.read_job_steps(self.TEXT, "build"), [{"run": "one"}, {"run": "two"}])

    def test_quoted_keys_are_read(self) -> None:
        """Valid YAML that Actions honours, so a reader that skips them fails open."""
        text = (
            self.TEXT.replace("    if:", '    "if" :')
            .replace("    steps:\n      - run: one", "    'steps' :\n      - 'run': one\n        \"if\": always()")
        )
        self.assertEqual(pipeline_gate.read_job_condition(text, "build"), "github.event_name == 'push'")
        self.assertEqual(
            pipeline_gate.read_job_steps(text, "build"), [{"run": "one", "if": "always()"}, {"run": "two"}]
        )

    def test_a_bare_dash_opens_a_step_of_its_own(self) -> None:
        """Folded into the step above, its keys would overwrite that step's, and a first one would vanish."""
        text = self.TEXT.replace("      - run: one\n", "      -\n        run: zero\n      - run: one\n").replace(
            "      - run: two\n", "      - # a comment\n        run: two\n        if: always()\n")

        steps, unread = pipeline_gate._scan_job_steps(text, "build")

        self.assertEqual(steps, [{"run": "zero"}, {"run": "one"}, {"run": "two", "if": "always()"}])
        self.assertEqual(unread, [])

    def test_a_bare_dash_whose_keys_this_reader_cannot_place_is_refused(self) -> None:
        """Dropped, a deeper `continue-on-error` would leave check_retries a step it skips."""
        text = self.TEXT.replace("      - run: two\n", "      -\n            continue-on-error: true\n")

        steps, unread = pipeline_gate._scan_job_steps(text, "build")

        self.assertEqual(steps, [{"run": "one"}, {}])
        self.assertEqual(unread, ["continue-on-error: true"])


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

    def test_a_build_whose_main_tag_never_moves_is_caught(self) -> None:
        """ADR-134: the moving tag is what a consumer pulls, so a leg without it leaves a stale set."""
        problems = self.broken(':main"\n', ':next"\n')

        self.assertEqual(len(problems), 1)
        self.assertIn("no push of the main tag", problems[0])

    def test_a_main_tag_that_may_move_back_is_caught(self) -> None:
        """A late build must leave a newer tag alone, which takes the ancestry question."""
        problems = self.broken("git merge-base --is-ancestor", "git log")

        self.assertEqual(len(problems), 1)
        self.assertIn("is an ancestor of this build's", problems[0])

    def test_a_main_tag_that_ignores_the_ancestry_answer_is_caught(self) -> None:
        """The question is still asked, so only a read of what decides on its answer sees any of these."""
        for old, new in (
                ("|| ancestor=$?", "|| true"),
                ('1) echo "a newer build moved it"; exit 0 ;;', '1) echo "a newer build moved it" ;;'),
                ('*) echo "could not compare"; exit 1 ;;', '*) echo "could not compare, exit 1" ;;'),
                ('            *) echo "could not compare"; exit 1 ;;\n', ""),
                # An exit that only some runs reach, or another exit reached first.
                ('1) echo "a newer build moved it"; exit 0 ;;', '1) [ -n "$x" ] && exit 0 ;;'),
                ('*) echo "could not compare"; exit 1 ;;', '*) [ -n "$x" ] && exit 1 ;;'),
                ('*) echo "could not compare"; exit 1 ;;', '*) exit 0; exit 1 ;;'),
                ('1) echo "a newer build moved it"; exit 0 ;;', "1) ( exit 0 ) ;;"),
                ('1) echo "a newer build moved it"; exit 0 ;;', "1) true | exit 0 ;;"),
                ('case "$ancestor" in', 'case "$other" in')):
            with self.subTest(new=new):
                problems = self.broken(old, new)

                self.assertEqual(len(problems), 1, problems)
                self.assertIn("does not act on the answer", problems[0])

    def test_a_main_tag_mismatch_that_does_not_exit_is_caught(self) -> None:
        """The comparison is still made, so only a read of its branch sees the failure go unreported."""
        nested = '              exit 0\n            fi\n            echo "the tag names other bytes, not exit 1"\n'
        mismatch = "            exit 1\n          fi"
        for old, new in (
                ("            exit 1\n          fi", "          fi"),
                # Only inside the nested branch, or only in the branch the digests agree in.
                (nested + "            exit 1\n", nested.replace("exit 0", "exit 1")),
                ("            exit 1\n          fi", "          else\n            exit 1\n          fi"),
                # Another exit reached first, or one that only some runs reach.
                (mismatch, "            exit 0\n" + mismatch),
                (mismatch, '            [ -n "$x" ] && exit 1\n          fi'),
                (mismatch, '            case "$x" in\n              y) exit 1 ;;\n            esac\n          fi'),
                (mismatch, "            for x in a; do\n              exit 1\n            done\n          fi"),
                (mismatch, '            [ -n "$x" ] && { echo "no"; exit 1; }\n          fi'),
                (mismatch, '            [ -n "$x" ] && \\\n              exit 1\n          fi'),
                (mismatch, "            ( exit 1 )\n          fi"),
                (mismatch, '            [ -n "$x" ] && {\n              echo "no"\n'
                           "              exit 1\n            }\n          fi"),
                # Nested deeper than one level.
                (mismatch, '            [ -n "$x" ] && { { exit 1; }; }\n          fi'),
                (mismatch, "            ( ( ( echo ) ); exit 1 )\n          fi"),
                (mismatch, '            [ -n "$x" ] && { case $y in a) echo;; esac; exit 1; }\n          fi')):
            with self.subTest(new=new):
                problems = self.broken(old, new)

                self.assertEqual(len(problems), 1, problems)
                self.assertIn("does not reach `exit 1` on every run", problems[0])

    def test_an_exit_after_a_finished_command_list_or_group_is_not_a_problem(self) -> None:
        """Every run still reaches it, so refusing it would fail a step that is right."""
        mismatch = "            exit 1\n          fi"
        for old, new in (
                ('*) echo "could not compare"; exit 1 ;;', '*) echo "x" || true; exit 1 ;;'),
                (mismatch, "            docker logout || true; exit 1\n          fi"),
                (mismatch, "            docker logout || true\n" + mismatch),
                (mismatch, '            [ -n "$x" ] && { echo "no"; }\n' + mismatch)):
            with self.subTest(new=new):
                self.assertEqual(self.broken(old, new), [])

    def test_a_main_tag_that_asks_after_it_pushes_is_caught(self) -> None:
        guard = '          git merge-base --is-ancestor "$named" "$GITHUB_SHA" || ancestor=$?' + chr(10)
        push = '          docker push "$REGISTRY/${{ matrix.image }}:main"' + chr(10)
        self.assertIn(guard, WORKFLOW)
        self.assertIn(push, WORKFLOW)
        self.write(WORKFLOW.replace(guard, "").replace(push, push + guard))

        problems = pipeline_gate.check_images(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("before it pushes", problems[0])

    def test_a_mention_of_the_guard_in_a_comment_is_caught(self) -> None:
        guard = 'git merge-base --is-ancestor "$named" "$GITHUB_SHA"'

        problems = self.broken(guard, "# " + guard)

        self.assertEqual(len(problems), 1)

    def test_a_build_without_its_revision_label_is_caught(self) -> None:
        problems = self.broken("--label org.opencontainers.image.revision=", "--label org.example.rev=")

        self.assertEqual(len(problems), 1)
        self.assertIn("labels no `org.opencontainers.image.revision=`", problems[0])

    def test_a_main_tag_moved_off_main_is_caught(self) -> None:
        move = SIGNING_STEPS.split("      - name: Move the main tag")[1]
        self.write(WORKFLOW.replace(move, move.replace(MAIN_PUSH, "${{ always() }}")))

        problems = pipeline_gate.check_images(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("the push of the main tag does not run under the main-push condition", problems[0])

    def test_a_condition_holding_every_term_but_not_as_and_terms_is_caught(self) -> None:
        """Substrings passed each of these, and each runs the step on main for a leg that built nothing."""
        filtered = "needs.changes.outputs[matrix.filter] == 'true'"
        main = "github.ref == 'refs/heads/main'"
        event = "contains(fromJSON('[\"push\",\"workflow_dispatch\"]'), github.event_name)"
        for condition in (
            f"${{{{ {filtered} || {event} && {main} }}}}",
            f"${{{{ {filtered} && ({event} || {main}) }}}}",
            f"${{{{ {filtered} && github.event_name != 'push' && {main} && '\"push\"' }}}}",
            f"${{{{ {filtered} && contains(fromJSON('[\"push\",\"pull_request\"]'), github.event_name) && {main} }}}}",
        ):
            with self.subTest(condition=condition):
                move = SIGNING_STEPS.split("      - name: Move the main tag")[1]
                self.write(WORKFLOW.replace(move, move.replace(MAIN_PUSH, condition)))

                problems = pipeline_gate.check_images(self.root)

                self.assertEqual(len(problems), 1, problems)
                self.assertIn("the push of the main tag does not run under the main-push condition", problems[0])

    def test_a_single_push_event_test_is_a_main_push(self) -> None:
        step = {"if": "${{ needs.changes.outputs[matrix.filter] == 'true' && github.event_name == 'push' "
                      "&& github.ref == 'refs/heads/main' }}"}

        self.assertTrue(pipeline_gate._on_main_push(step))

    def test_a_main_tag_moved_before_its_attestations_is_caught(self) -> None:
        move = "      - name: Move the main tag" + SIGNING_STEPS.split("      - name: Move the main tag")[1]
        self.write(WORKFLOW.replace(move, "").replace(
            "      - name: Provenance", move + "\n      - name: Provenance", 1))

        problems = pipeline_gate.check_images(self.root)

        self.assertEqual(len(problems), 2)
        self.assertIn("the main tag names an image before its", problems[0])

    def test_a_main_tag_moved_before_the_signature_is_caught(self) -> None:
        move = "      - name: Move the main tag" + SIGNING_STEPS.split("      - name: Move the main tag")[1]
        self.write(WORKFLOW.replace(move, "").replace(
            "      - name: Sign ${{ matrix.image }}", move + "\n      - name: Sign ${{ matrix.image }}", 1))

        problems = pipeline_gate.check_images(self.root)

        self.assertTrue(any("before its signature exists" in problem for problem in problems), problems)

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

    # The push step's condition alone: the job's own `if:` says the same, and
    # breaking both would pass on the job's problem whatever the step check reads.
    PUSH_STEP = f"id: push\n        if: {MAIN_PUSH}\n"

    def test_a_step_on_a_pull_request_event_is_caught_even_with_the_ref_test(self) -> None:
        """A quoted `push` beside a pull request event is still a pull request."""
        problems = self.broken(self.PUSH_STEP, self.PUSH_STEP.replace('"workflow_dispatch"', '"pull_request"'))

        self.assertEqual(len(problems), 1, problems)
        self.assertIn("the `docker push` does not run under the main-push condition", problems[0])

    def test_a_step_missing_the_ref_test_is_caught(self) -> None:
        problems = self.broken(self.PUSH_STEP, self.PUSH_STEP.replace(" && github.ref == 'refs/heads/main'", ""))

        self.assertEqual(len(problems), 1, problems)
        self.assertIn("the `docker push` does not run under the main-push condition", problems[0])

    def test_a_main_tag_never_read_back_from_the_registry_is_caught(self) -> None:
        """The local image's digests name the signed one whatever the registry's tag now names."""
        for old, new in (
            ('          moved=$(docker buildx imagetools inspect "$tag")\n', ""),
            ('docker buildx imagetools inspect "$tag")', "docker inspect --format '{{json .RepoDigests}}' \"$tag\")"),
            ('[ "$moved" != "$DIGEST" ]', '[ -n "$moved" ]'),
            # The digest read rather than the tag, and the digest named rather than compared.
            ('inspect "$tag")', 'inspect "$REGISTRY/${{ matrix.image }}@$DIGEST")'),
            ('if [ "$moved" != "$DIGEST" ]; then', 'if echo "$moved $DIGEST"; then'),
            # A comparison of some other variable than the one the read assigned.
            ('[ "$moved" != "$DIGEST" ]', '[ "$DIGEST" != "$DIGEST" ]'),
            # The test turned round, so the branch that exits is the one the digests agree in.
            ('[ "$moved" != "$DIGEST" ]', '[ "$moved" = "$DIGEST" ]'),
            # `$tag` read, but naming something other than the main tag.
            ('tag="$REGISTRY/${{ matrix.image }}:main"', 'tag="$REGISTRY/${{ matrix.image }}:${{ github.sha }}"'),
        ):
            with self.subTest(old=old):
                problems = self.broken(old, new)

                self.assertEqual(len(problems), 1, problems)
                self.assertIn("does not read the tag back from the registry", problems[0])

    def test_a_push_of_a_reference_the_build_never_tagged_is_caught(self) -> None:
        problems = self.broken('docker push "$REGISTRY/${{ matrix.image }}:${{ github.sha }}"',
                               'docker push "ghcr.io/x/${{ matrix.image }}:${{ github.sha }}"')

        self.assertTrue(any("tags no `$REGISTRY/...` reference" in problem for problem in problems))

    def test_a_build_that_quotes_the_tag_is_not_a_problem(self) -> None:
        problems = self.broken("--tag $REGISTRY/${{ matrix.image }}:${{ github.sha }}",
                               '--tag "$REGISTRY/${{ matrix.image }}:${{ github.sha }}"')

        self.assertEqual(problems, [])

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

    def test_a_quoted_or_commented_registry_is_the_same_address(self) -> None:
        address = "ghcr.io/${{ github.repository }}"
        for spelling in (f'"{address}"', f"'{address}'", f"{address}  # the one owner"):
            with self.subTest(spelling=spelling):
                self.assertEqual(self.broken(f"REGISTRY: {address}", f"REGISTRY: {spelling}", deploy=True), [])

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

    def test_a_verify_that_records_no_tag_on_what_it_verified_is_caught(self) -> None:
        """ADR-201: without the tag, a rebuilt commit tag leaves the pinned digest to the prune."""
        problems = self.broken("imagetools create", "imagetools inspect", deploy=True)

        self.assertEqual(len(problems), 1)
        self.assertIn("a tag the prune keeps", problems[0])

    def test_a_provenance_check_that_does_not_pin_the_ref_is_caught(self) -> None:
        """The signer workflow alone admits ci.yml on any branch."""
        problems = self.broken("--source-ref refs/heads/main", "--limit 1", deploy=True)

        self.assertEqual(len(problems), 1)
        self.assertIn("pinning the provenance to main", problems[0])

    def test_a_verify_that_takes_an_identity_pattern_is_caught(self) -> None:
        """The flag is a prefix of its `-regexp` form, so a substring admits any identity."""
        for old, new in (
            ('--certificate-identity "$CERTIFICATE_IDENTITY"', "--certificate-identity-regexp '.*'"),
            ('--certificate-identity "$CERTIFICATE_IDENTITY"',
             '--certificate-identity "$CERTIFICATE_IDENTITY" --certificate-identity-regexp=.+'),
        ):
            with self.subTest(new=new):
                problems = self.broken(old, new, deploy=True)

                self.assertEqual(len(problems), 1, problems)
                self.assertIn("pinned to ci.yml on main", problems[0])
                self.assertIn("-regexp", problems[0])

    def test_a_verify_that_takes_an_issuer_pattern_is_caught(self) -> None:
        problems = self.broken("--certificate-oidc-issuer https://token.actions.githubusercontent.com",
                               "--certificate-oidc-issuer-regexp 'https://.*'", deploy=True)

        self.assertEqual(len(problems), 1, problems)
        self.assertIn("given an issuer", problems[0])

    def test_a_verify_for_another_repository_is_caught(self) -> None:
        problems = self.broken("https://github.com/${{ github.repository }}/", "https://github.com/someone/else/",
                               deploy=True)

        self.assertEqual(len(problems), 1, problems)
        self.assertIn("pinned to ci.yml on main", problems[0])

    def test_an_identity_the_flag_does_not_read_is_caught(self) -> None:
        """The pinned value left in `env:` proves nothing once the flag reads something else."""
        for old, new in (
            ('--certificate-identity "$CERTIFICATE_IDENTITY"', '--certificate-identity "$OTHER_IDENTITY"'),
            ('cosign verify "$REGISTRY',
             'CERTIFICATE_IDENTITY=https://github.com/a/b/.github/workflows/ci.yml@refs/heads/main\n'
             '          cosign verify "$REGISTRY'),
        ):
            with self.subTest(new=new):
                problems = self.broken(old, new, deploy=True)

                self.assertEqual(len(problems), 1, problems)
                self.assertIn("pinned to ci.yml on main", problems[0])

    def test_an_identity_written_in_the_run_is_admitted(self) -> None:
        self.assertEqual(self.broken(
            '"$CERTIFICATE_IDENTITY"', '"https://github.com/$GITHUB_REPOSITORY/.github/workflows/ci.yml@refs/heads/main"',
            deploy=True), [])

    def test_every_verify_is_pinned_not_only_the_first(self) -> None:
        problems = self.broken(
            "          python deploy/canary/canary.py verified-digest",
            "          cosign verify \"$REGISTRY/$1:$RECORD\" --certificate-identity-regexp '.*' \\\n"
            "            --certificate-oidc-issuer https://token.actions.githubusercontent.com\n"
            "          python deploy/canary/canary.py verified-digest", deploy=True)

        self.assertEqual(len(problems), 1, problems)
        self.assertIn("-regexp", problems[0])

    def test_a_provenance_check_for_another_repository_is_caught(self) -> None:
        for old, new, why in (
            ('--repo "$GITHUB_REPOSITORY"', "--repo someone/else", "to this repository:"),
            ('--signer-workflow "$GITHUB_REPOSITORY/.github/workflows/ci.yml"',
             '--signer-workflow "$GITHUB_REPOSITORY/.github/workflows/any.yml"', "this repository's ci.yml"),
            ('--repo "$GITHUB_REPOSITORY"', '--owner "$GITHUB_REPOSITORY_OWNER"', "to this repository:"),
        ):
            with self.subTest(new=new):
                problems = self.broken(old, new, deploy=True)

                self.assertEqual(len(problems), 1, problems)
                self.assertIn(why, problems[0])

    def test_a_verify_step_with_no_cosign_verify_command_fails_rather_than_passing(self) -> None:
        """`cosign verify-attestation` locates the step, so a reader that finds no command must say so."""
        problems = self.broken(
            'cosign verify "$REGISTRY/$1:$TAG" \\\n            --certificate-identity "$CERTIFICATE_IDENTITY"',
            "cosign verify-attestation \"$REGISTRY/$1:$TAG\" \\\n            --certificate-identity-regexp '.*'",
            deploy=True)

        self.assertEqual(len(problems), 1, problems)
        self.assertIn("no `cosign verify` command", problems[0])

    def test_a_path_qualified_cosign_is_read(self) -> None:
        problems = self.broken('cosign verify "$REGISTRY/$1:$TAG" \\\n            --certificate-identity "$CERT',
                               '/usr/local/bin/cosign verify "$REGISTRY/$1:$TAG" \\\n'
                               "            --certificate-identity-regexp '.*' --certificate-identity \"$CERT",
                               deploy=True)

        self.assertEqual(len(problems), 1, problems)
        self.assertIn("-regexp", problems[0])

    def test_words_after_a_hash_inside_a_word_or_a_redirection_still_reach_cosign(self) -> None:
        """Bash opens a comment only at a word's start, and runs on past a redirection's target."""
        for words in ("--annotations=a#b --certificate-identity-regexp '.*'",
                      "--annotations=a\\ #b --certificate-identity-regexp '.*'",
                      "> out.json --certificate-identity-regexp '.*'",
                      "2>&1 --certificate-identity-regexp '.*'"):
            with self.subTest(words=words):
                problems = self.broken(
                    "--certificate-oidc-issuer https://token.actions.githubusercontent.com\n",
                    f"--certificate-oidc-issuer https://token.actions.githubusercontent.com {words}\n", deploy=True)

                self.assertEqual(len(problems), 1, problems)
                self.assertIn("-regexp", problems[0])
        self.assertEqual(self.broken(
            "--certificate-oidc-issuer https://token.actions.githubusercontent.com\n",
            "--certificate-oidc-issuer https://token.actions.githubusercontent.com # --certificate-identity-regexp\n",
            deploy=True), [])

    def test_a_verify_line_that_will_not_split_fails_rather_than_passing(self) -> None:
        problems = self.broken('cosign verify "$REGISTRY/$1:$TAG"', 'cosign verify "$REGISTRY/$1:$TAG', deploy=True)

        self.assertTrue(any("does not split into shell words" in problem for problem in problems), problems)

    def test_an_operator_run_together_with_a_redirection_ends_the_command(self) -> None:
        """shlex reads `&&>` or `&<` as one word; bash ends cosign there, so the flags after it are the next command's."""
        for operator in ("&&>", ";>", "&<", "|<"):
            with self.subTest(operator=operator):
                problems = self.broken(
                    "--certificate-oidc-issuer https://token.actions.githubusercontent.com\n",
                    "--certificate-oidc-issuer https://token.actions.githubusercontent.com "
                    f'--certificate-identity "$EVIL" {operator}/dev/null true '
                    '--certificate-identity "$CERTIFICATE_IDENTITY"\n', deploy=True)

                self.assertEqual(len(problems), 1, problems)
                self.assertIn("pinned to ci.yml on main", problems[0])

    def test_a_substitution_among_the_verify_words_fails_rather_than_passing(self) -> None:
        """Bash runs on past the substitution's `)`, so a flag after it would reach cosign unread."""
        for words in ("--annotations=$(echo x) --certificate-identity-regexp '.*'",
                      "--attachment-tag-prefix <(true) --certificate-identity-regexp '.*'",
                      "--annotations=`echo a;b` --certificate-identity-regexp '.*'",
                      "--annotations=${unset:-;} --certificate-identity-regexp '.*'",
                      "$'\\x2d-certificate-identity-regexp' '.*'",
                      "{--certificate-identity-regexp,--annotations} '.*'",
                      "{\"a b\",--certificate-identity-regexp} '.*'"):
            with self.subTest(words=words):
                problems = self.broken(
                    "--certificate-oidc-issuer https://token.actions.githubusercontent.com\n",
                    f"--certificate-oidc-issuer https://token.actions.githubusercontent.com {words}\n", deploy=True)

                self.assertEqual(len(problems), 1, problems)
                self.assertIn("does not split into shell words", problems[0])

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

    def test_a_helm_upgrade_with_its_global_flags_first_is_still_read(self) -> None:
        """Helm takes `-n prod upgrade`; a reader that wants the subcommand first misses the call."""
        for command in ('helm -n "$NAMESPACE" upgrade', "helm --namespace=prod upgrade", "helm --debug upgrade",
                        "helm --kube-context prod --debug upgrade"):
            with self.subTest(command=command):
                problems = self.broken("--atomic\n", f'--atomic\n          {command} "$WORKLOAD" x\n', deploy=True)

                self.assertEqual(len(problems), 2, problems)
                self.assertTrue(all(command in problem for problem in problems), problems)
        self.assertIsNone(pipeline_gate.HELM_UPGRADE.search('helm -n prod get values "$WORKLOAD"'))
        self.assertIsNone(pipeline_gate.HELM_COMMAND.search("helm --version"))

    def test_a_flags_first_helm_call_before_the_verify_is_caught(self) -> None:
        problems = self.broken(
            "      - name: The images are the ones CI built and signed",
            "      - name: Early\n        run: helm -n prod status x\n\n"
            "      - name: The images are the ones CI built and signed",
            deploy=True)

        self.assertEqual(len(problems), 1, problems)
        self.assertIn("runs after the first step that runs helm", problems[0])

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
        workflow = (root / ".github/workflows/ci.yml").read_text(encoding="utf-8")
        steps = pipeline_gate.read_job_steps(workflow, "publish-images")
        uses = [step.get("uses", "").split("@")[0] for step in steps]

        self.assertTrue(pipeline_gate.read_job_condition(workflow, "publish-images"))
        self.assertIn("anchore/sbom-action", [
            step.get("uses", "").split("@")[0] for step in pipeline_gate.read_job_steps(workflow, "images")])

        for action in ("docker/login-action", "sigstore/cosign-installer", "actions/attest-build-provenance",
                       "actions/attest-sbom"):
            self.assertIn(action, uses)
        self.assertTrue(any("cosign sign" in step.get("run", "") for step in steps))
        self.assertTrue(any("docker push" in step.get("run", "") for step in steps))
        rollout = pipeline_gate.read_job_steps(
            (root / ".github/workflows/deploy.yml").read_text(encoding="utf-8"), "rollout")
        self.assertTrue(any("cosign verify" in step.get("run", "") for step in rollout))
        deploy = (root / ".github/workflows/deploy.yml").read_text(encoding="utf-8")
        self.assertGreaterEqual(len(pipeline_gate.read_helm_upgrades(deploy)), 2)
        verifies = pipeline_gate.read_command_arguments(deploy, ("cosign", "verify"))
        self.assertGreaterEqual(len(verifies), 2)
        self.assertTrue(all("--certificate-identity" in arguments for arguments in verifies))
        self.assertTrue(pipeline_gate.read_command_arguments(deploy, ("gh", "attestation", "verify")))


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

    def test_a_flow_mapping_step_or_a_quoted_uses_key_is_still_read(self) -> None:
        self.write("ci.yml", (
            "jobs:\n  a:\n    steps:\n"
            "      - { name: Helm, uses: azure/setup-helm@v4 }\n"
            "      - \"uses\": 'dorny/paths-filter@v3'\n"
            "      - {uses: actions/checkout@v4}\n"))

        problems = pipeline_gate.check_actions(self.root)

        self.assertEqual(len(problems), 2, problems)
        self.assertIn("azure/setup-helm@v4", problems[0])
        self.assertIn("dorny/paths-filter@v3", problems[1])

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
            # Any spelling of the key, quoted or in a flow mapping, so a form the reader skips is counted.
            keys = sum(
                len(re.findall(r"\buses['\"]?\s*:", line))
                for line in text.splitlines() if not line.lstrip().startswith("#"))
            total += keys
            with self.subTest(workflow=path.name):
                self.assertEqual(len(pipeline_gate.read_uses(text)), keys)
        # A workflow that runs no action has none to read; none anywhere is a reader or a directory broken.
        self.assertGreater(total, 0)


ALERT = (
    "name: Main red\n"
    "on:\n"
    "  workflow_run:\n"
    "    workflows: [CI, 'Helm']\n"
    "    types: [completed]\n"
    "    branches: [main]\n"
    "jobs:\n"
    "  read-cancelled:\n"
    "    if: >-\n"
    "      github.event.workflow_run.event == 'push' &&\n"
    "      github.event.workflow_run.conclusion == 'cancelled'\n"
    "    runs-on: ubuntu-latest\n"
    "  rerun:\n"
    "    needs: read-cancelled\n"
    "    if: >-\n"
    "      always() &&\n"
    "      github.event.workflow_run.event == 'push' &&\n"
    "      github.event.workflow_run.run_attempt == 1 &&\n"
    "      (contains(fromJSON('[\"failure\", \"timed_out\", \"startup_failure\"]'), "
    "github.event.workflow_run.conclusion) ||\n"
    "      contains(fromJSON('[\"failure\", \"cancelled\"]'), needs.read-cancelled.result) ||\n"
    "      needs.read-cancelled.outputs.reason != '')\n"
    "    runs-on: ubuntu-latest\n"
    "  file:\n"
    "    needs: [read-cancelled, rerun]\n"
    "    if: >-\n"
    "      always() &&\n"
    "      github.event.workflow_run.event == 'push' &&\n"
    "      (contains(fromJSON('[\"failure\", \"timed_out\", \"startup_failure\"]'), "
    "github.event.workflow_run.conclusion) ||\n"
    "      contains(fromJSON('[\"failure\", \"cancelled\"]'), needs.read-cancelled.result) ||\n"
    "      needs.read-cancelled.outputs.reason != '') &&\n"
    "      (needs.rerun.result != 'success' ||\n"
    "      !contains(fromJSON('[\"passed\", \"replaced\", \"superseded\"]'), needs.rerun.outputs.second)) &&\n"
    "      (github.event.workflow_run.run_attempt == 1 ||\n"
    "      github.event.workflow_run.triggering_actor.login != 'github-actions[bot]')\n"
    "    runs-on: ubuntu-latest\n"
)


class AlertTests(unittest.TestCase):
    """Every workflow a push to main runs is one the red-main alert watches, and nothing else is."""

    def setUp(self) -> None:
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.workflows = self.root / ".github" / "workflows"
        self.workflows.mkdir(parents=True)
        self.write(pipeline_gate.ALERT_WORKFLOW, ALERT)
        self.write("ci.yml", "name: CI\non:\n  pull_request:\n  push:\n    branches: [main]\n")
        self.write("helm.yml", (
            "name: Helm\non:\n  push:\n    # main only\n    branches:\n      - main\n"
            "    paths: ['deploy/helm/**']\n"))
        self.write("gate.yml", "name: Commit gate\non:\n  pull_request:\n    types: [opened]\n")

    def write(self, name: str, text: str) -> None:
        (self.workflows / name).write_text(text, encoding="utf-8")

    def test_every_pushed_workflow_watched_is_clean(self) -> None:
        self.assertEqual(pipeline_gate.check_alerts(self.root), [])

    def test_a_pushed_workflow_the_alert_does_not_watch_is_caught(self) -> None:
        self.write("compose.yml", "name: Compose\non:\n  push:\n    branches: [main]\n")

        problems = pipeline_gate.check_alerts(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("compose.yml runs on a push to main", problems[0])
        self.assertIn("'Compose', so a red run of it alerts nobody", problems[0])

    def test_an_on_written_as_a_flow_mapping_is_refused_rather_than_read_as_no_push(self) -> None:
        self.write("compose.yml", "name: Compose\non: {push: {branches: [main]}}\n")

        problems = pipeline_gate.check_alerts(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("compose.yml writes `on:` as a flow mapping", problems[0])

    def test_a_pushed_workflow_in_another_readable_form_is_still_caught(self) -> None:
        """Each once read as no push, so the alert stopped asking for the watch."""
        for on in ('"on":\n  push:\n    branches: [main]\n', "on:\n  - push\n", "on:\n  push:\n    branches: ['m*']\n"):
            with self.subTest(on=on):
                self.write("compose.yml", "name: Compose\n" + on)

                problems = pipeline_gate.check_alerts(self.root)

                self.assertEqual(len(problems), 1, problems)
                self.assertIn("compose.yml runs on a push to main", problems[0])

    def test_an_on_this_check_cannot_read_is_refused(self) -> None:
        for on in ("on:\n  push: {branches: [main]}\n", "on:\n- push\n", "on:\n"):
            with self.subTest(on=on):
                self.write("compose.yml", "name: Compose\n" + on)

                problems = pipeline_gate.check_alerts(self.root)

                self.assertEqual(len(problems), 1, problems)
                self.assertIn("compose.yml writes `on:` as a flow mapping or another form", problems[0])

    def test_a_watched_workflow_written_as_a_flow_mapping_has_the_refusal_as_its_only_problem(self) -> None:
        self.write("helm.yml", "name: Helm\non: {push: {branches: [main]}}\n")

        problems = pipeline_gate.check_alerts(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("helm.yml writes `on:` as a flow mapping", problems[0])

    def test_a_name_with_a_trailing_comment_is_read_without_it(self) -> None:
        """Read with its comment, the name is unwatched and the watched one matches nothing: two false refusals."""
        for line in ("name: Helm # the charts", "name: 'Helm'  # the charts", 'name: "Helm" # x'):
            with self.subTest(line=line):
                self.write("helm.yml", line + "\non:\n  push:\n    branches: [main]\n")

                self.assertEqual(pipeline_gate.check_alerts(self.root), [])
        self.assertEqual(pipeline_gate.read_workflow_name("name: a#b\n"), "a#b")
        self.assertEqual(pipeline_gate.read_workflow_name("name: 'a # b'\n"), "a # b")

    def test_a_pushed_workflow_with_no_name_is_caught(self) -> None:
        self.write("anonymous.yml", "on:\n  push:\n    branches: [main]\n")

        self.assertIn("has no `name:`", pipeline_gate.check_alerts(self.root)[0])

    def test_a_watched_name_no_workflow_carries_is_caught(self) -> None:
        self.write(pipeline_gate.ALERT_WORKFLOW, ALERT.replace("'Helm'", "Helm, Hlem"))

        problems = pipeline_gate.check_alerts(self.root)

        self.assertEqual(problems, [f"{pipeline_gate.ALERT_WORKFLOW} watches 'Hlem', which no workflow is named"])

    def test_a_watched_workflow_that_never_runs_on_a_push_to_main_is_caught(self) -> None:
        self.write(pipeline_gate.ALERT_WORKFLOW, ALERT.replace("'Helm'", "Helm, Commit gate"))

        self.assertIn("'Commit gate', which never runs on a push to main", pipeline_gate.check_alerts(self.root)[0])

    def test_a_red_conclusion_the_filing_job_drops_is_caught(self) -> None:
        for conclusion, listed, kept in (("failure", '"failure", "timed_out"', '"timed_out"'),
                                         ("timed_out", '"timed_out", "startup', '"startup'),
                                         ("startup_failure", ', "startup_failure"', "")):
            with self.subTest(conclusion=conclusion):
                self.assertIn(listed, ALERT)
                self.write(pipeline_gate.ALERT_WORKFLOW, ALERT.replace(listed, kept))

                self.assertEqual(pipeline_gate.check_alerts(self.root), [
                    f"{pipeline_gate.ALERT_WORKFLOW}'s `file` job does not file on `{conclusion}`, "
                    "so a run ending so alerts nobody"])

    def test_a_cancelled_run_nothing_reads_is_caught(self) -> None:
        for dropped in (" &&\n      github.event.workflow_run.conclusion == 'cancelled'",
                        "      always() &&\n",
                        "      contains(fromJSON('[\"failure\", \"cancelled\"]'), needs.read-cancelled.result) ||\n",
                        "\"failure\", ",
                        ", \"cancelled\"",
                        " ||\n      needs.read-cancelled.outputs.reason != ''"):
            with self.subTest(dropped=dropped):
                self.assertIn(dropped, ALERT)
                # The last occurrence: `"failure", ` is in the conclusions' list too, which must keep it.
                self.write(pipeline_gate.ALERT_WORKFLOW, "".join(ALERT.rsplit(dropped, 1)))

                problems = pipeline_gate.check_alerts(self.root)

                self.assertEqual(len(problems), 1)
                self.assertIn("so a job past its time limit alerts nobody", problems[0])

    def test_a_rerun_that_would_rerun_its_own_reruns_is_caught(self) -> None:
        for edit, replacement in (("      github.event.workflow_run.run_attempt == 1 &&\n", ""),
                                  ("  rerun:\n", "  later:\n")):
            with self.subTest(edit=edit):
                self.assertIn(edit, ALERT)
                self.write(pipeline_gate.ALERT_WORKFLOW, ALERT.replace(edit, replacement))

                problems = pipeline_gate.check_alerts(self.root)

                self.assertEqual(len(problems), 1)
                self.assertIn("each rerun would start another", problems[0])

    def test_a_filing_job_that_trusts_a_rerun_it_did_not_hear_from_is_caught(self) -> None:
        for dropped, kept in (("(needs.rerun.result != 'success' ||\n      !", "(!"), ("!contains", "contains")):
            with self.subTest(dropped=dropped):
                self.assertEqual(ALERT.count(dropped), 1)
                self.write(pipeline_gate.ALERT_WORKFLOW, ALERT.replace(dropped, kept))

                problems = pipeline_gate.check_alerts(self.root)

                self.assertEqual(len(problems), 1, problems)
                self.assertIn("so a second red attempt alerts nobody", problems[0])

    def test_a_condition_holding_every_term_joined_otherwise_is_caught(self) -> None:
        """Each edit keeps every term as text, so only a read of the operands hears it."""
        for old, new, problem in (
                ("needs.read-cancelled.result) ||", "needs.read-cancelled.result) &&",
                 "so a job past its time limit alerts nobody"),
                ("(contains(", "(!contains(", "does not file on `failure`"),
                ("run_attempt == 1 &&", "run_attempt == 10 &&", "each rerun would start another"),
                ("(needs.rerun.result != 'success' ||", "(needs.rerun.result != 'success' &&",
                 "so a second red attempt alerts nobody")):
            with self.subTest(new=new):
                self.assertIn(old, ALERT)
                # The last occurrence: the `rerun` job's own group comes first, and these read `file`'s.
                self.write(pipeline_gate.ALERT_WORKFLOW, new.join(ALERT.rsplit(old, 1)))

                self.assertTrue(any(problem in found for found in pipeline_gate.check_alerts(self.root)))

    def test_a_term_no_check_reads_beside_the_ones_they_do_is_caught(self) -> None:
        """Each would keep its job from a run it must hear, with every term a check reads still there."""
        last = "'github-actions[bot]')\n"
        terms = (
            ("read-cancelled", "  read-cancelled:\n    if: >-\n", "false"),
            ("rerun", "  rerun:\n    needs: read-cancelled\n    if: >-\n", "github.event.workflow_run.run_number == 0"),
            ("file", "  file:\n    needs: [read-cancelled, rerun]\n    if: >-\n", "false"),
            # A second list of conclusions after the one read, which a red run must be on too.
            ("file", last, "contains(fromJSON('[\"neutral\"]'), github.event.workflow_run.conclusion)"),
        )
        for job, anchor, term in terms:
            with self.subTest(job=job, term=term):
                self.assertEqual(ALERT.count(anchor), 1)
                added = f"{anchor[:-1]} &&\n      {term}\n" if anchor == last else f"{anchor}      {term} &&\n"
                self.write(pipeline_gate.ALERT_WORKFLOW, ALERT.replace(anchor, added))

                problems = pipeline_gate.check_alerts(self.root)

                self.assertEqual(problems, [
                    f"{pipeline_gate.ALERT_WORKFLOW}'s `{job}` job's `if:` holds `{term}`, an `&&` term no check "
                    "here reads, so it could keep the job from a run it must hear"])

    def test_a_filing_job_that_files_its_reruns_own_attempt_is_caught(self) -> None:
        for old, new in (
                (" &&\n      (github.event.workflow_run.run_attempt == 1 ||\n"
                 "      github.event.workflow_run.triggering_actor.login != 'github-actions[bot]')", ""),
                ("(github.event.workflow_run.run_attempt == 1 ||", "(github.event.workflow_run.run_attempt == 1 &&"),
                ("login != 'github-actions[bot]'", "login == 'github-actions[bot]'")):
            with self.subTest(new=new):
                self.assertIn(old, ALERT)
                self.write(pipeline_gate.ALERT_WORKFLOW, ALERT.replace(old, new))

                problems = pipeline_gate.check_alerts(self.root)

                self.assertTrue(any("or one a person started" in problem for problem in problems), problems)

    def test_this_repositorys_alert_passes(self) -> None:
        """The subject is the real file: a term the readers refuse there is a false refusal, not a finding."""
        alert = pipeline_gate.ROOT / ".github" / "workflows" / pipeline_gate.ALERT_WORKFLOW
        text = alert.read_text(encoding="utf-8")
        self.assertEqual(pipeline_gate.check_alert_conclusions(text) + pipeline_gate.check_alert_rerun(text), [])

    def test_no_alert_and_an_empty_list_fail_rather_than_passing(self) -> None:
        self.write(pipeline_gate.ALERT_WORKFLOW, "name: Main red\non:\n  workflow_run:\n    types: [completed]\n")
        self.assertIn("vacuously", pipeline_gate.check_alerts(self.root)[0])

        (self.workflows / pipeline_gate.ALERT_WORKFLOW).unlink()
        self.assertIn("would file nothing", pipeline_gate.check_alerts(self.root)[0])

    def test_no_pushed_workflow_fails_rather_than_passing(self) -> None:
        self.write("ci.yml", "name: CI\non:\n  pull_request:\n")
        self.write("helm.yml", "name: Helm\non:\n  pull_request:\n")

        self.assertTrue(any("vacuously" in problem for problem in pipeline_gate.check_alerts(self.root)))

    def test_the_reader_tells_a_push_to_main_from_one_that_skips_it(self) -> None:
        cases = {
            "on: push\n": True,
            "on: [pull_request, push]\n": True,
            "on:\n  push:\n": True,
            "on:\n  push:\n    paths: ['src/**']\n": True,
            "on:\n  push:\n    branches-ignore: [release]\n": True,
            "on:\n  push:\n    branches: [release]\n": False,
            "on:\n  push:\n    branches-ignore:\n      - main\n": False,
            "on:\n  push:\n    tags: ['v*']\n": False,
            "on:\n  pull_request:\n    branches: [main]\n": False,
            "on:\n  workflow_dispatch:\njobs:\n  push:\n    branches: [main]\n": False,
            # A quoted key, as yamllint's truthy rule asks, and a list of events.
            '"on":\n  push:\n    branches: [main]\n': True,
            "'on':\n  'push':\n": True,
            "on:\n  - pull_request\n  - push\n": True,
            "on:\n  - pull_request\n": False,
            # Each entry a glob as GitHub matches it, a later `!` overruling.
            "on:\n  push:\n    branches: ['ma*']\n": True,
            "on:\n  push:\n    branches: ['**']\n": True,
            "on:\n  push:\n    branches: ['**/main']\n": True,
            "on:\n  push:\n    branches: ['mai+n']\n": True,
            "on:\n  push:\n    branches: ['release/*']\n": False,
            "on:\n  push:\n    branches: ['*/main']\n": False,
            "on:\n  push:\n    branches: ['**', '!main']\n": False,
            "on:\n  push:\n    branches-ignore: ['m*']\n": False,
            "on:\n  push:\n    branches-ignore: ['ma?in']\n": False,
            "on:\n  push:\n    branches-ignore: ['release/**']\n": True,
        }
        for text, expected in cases.items():
            with self.subTest(text=text):
                self.assertEqual(pipeline_gate.runs_on_main_push(text), expected)

    def test_the_reader_sees_every_push_trigger_in_this_repositorys_workflows(self) -> None:
        """The subject is coverage: a pushed workflow the reader misses is one the alert never has to watch."""
        pushed = 0
        for path in (pipeline_gate.ROOT / ".github" / "workflows").glob("*.y*ml"):
            text = path.read_text(encoding="utf-8")
            on = text.split("\njobs:")[0]
            with self.subTest(workflow=path.name):
                self.assertEqual(
                    pipeline_gate.runs_on_main_push(text),
                    "\n  push:\n    branches: [main]\n" in on)
            pushed += "\n  push:" in on
        # Every push trigger here is filtered to main, so each one found is a name the alert must list.
        self.assertGreater(pushed, 0)
        alert = pipeline_gate.ROOT / ".github" / "workflows" / pipeline_gate.ALERT_WORKFLOW
        self.assertEqual(len(pipeline_gate.read_watched(alert.read_text(encoding="utf-8"))), pushed)


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

    def test_a_namespace_that_is_more_than_a_dotted_name_is_caught(self) -> None:
        """An OR-ed value is one term to the splitter; the later shards repeating it would clear the cascade."""
        for namespace in (
            "Payments.|FullyQualifiedName~Shipping.",
            "(Payments.)",
            "Payments.~Shipping.",
            "!Payments.",
            "Payments.*",
        ):
            with self.subTest(namespace=namespace):
                # Every later shard repeats the string, so only the term's shape can fail.
                self.write(WORKFLOW + SHARDS.replace("~Payments.", f"~{namespace}"))

                problems = pipeline_gate.check_shards()

                self.assertTrue(
                    any("is not a FullyQualifiedName term" in problem for problem in problems),
                    problems,
                )

    def test_a_dotted_namespace_with_or_without_a_trailing_dot_is_read(self) -> None:
        """The control for the cases above: what the matrix writes today still parses."""
        for namespace in ("Payments.", "Platform.IntegrationTests.", "Payments"):
            with self.subTest(namespace=namespace):
                found = pipeline_gate.SELECTS_TERM.fullmatch(f"FullyQualifiedName~{namespace}")

                self.assertIsNotNone(found)
                self.assertEqual(found.group(2), namespace)

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


def trx(executed: int, assembly: str, tests: list[str], skipped: tuple[str, ...] = ()) -> str:
    # The class name is deliberately NOT derived from the assembly: two
    # projects holding a `Suite` is what the identity has to survive, and a
    # fixture that made them differ would pass whether or not the production
    # key carries the assembly. A skipped test is in `total` and not in `executed`.
    definitions = "".join(
        f'<UnitTest id="{assembly}-{name}" name="{name}"><TestMethod codeBase="/x/{assembly}.dll" '
        f'className="Suite" name="{name}" /></UnitTest>'
        for name in tests
    )
    results = "".join(
        f'<UnitTestResult testId="{assembly}-{name}" testName="{name}" '
        f'outcome="{"NotExecuted" if name in skipped else "Passed"}" />'
        for name in tests
    )
    return (
        '<?xml version="1.0" encoding="UTF-8"?>'
        '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
        f'<Results>{results}</Results><TestDefinitions>{definitions}</TestDefinitions>'
        f'<ResultSummary><Counters total="{executed + len(skipped)}" executed="{executed}" '
        f'passed="{executed}" failed="0" notExecuted="{len(skipped)}" /></ResultSummary>'
        "</TestRun>"
    )


class CodeTests(unittest.TestCase):
    """What skips the stages, over the real workflow with one thing broken in it."""

    GATE = f"        if: {pipeline_gate.CODE_GATE}\n"

    def setUp(self) -> None:
        self.text = pipeline_gate.WORKFLOW.read_text(encoding="utf-8")

    def broken(self, old: str, new: str, count: int = 1) -> str:
        self.assertIn(old, self.text)
        return self.text.replace(old, new, count)

    def problems(self, text: str) -> str:
        return "\n".join(pipeline_gate.check_code(workflow_text=text))

    def test_the_real_workflow_is_clean(self) -> None:
        self.assertEqual(pipeline_gate.check_code(), [])

    def test_the_scan_sees_the_documents_tests_read_today(self) -> None:
        code = pipeline_gate.read_filters_by_name(self.text)[pipeline_gate.CODE_FILTER]
        named = {path for _source, path in pipeline_gate.read_tested_paths(pipeline_gate.ROOT, code)}
        self.assertLessEqual({"docs/personal-data.md", "docs/backend-architecture/13-observability.md"}, named)

    def test_a_new_exclusion_is_scanned_for_what_tests_read_under_it(self) -> None:
        text = self.broken("              - '!docs/**'\n", "              - '!docs/**'\n              - '!deploy/helm/**'\n")
        problems = self.problems(text)
        self.assertIn("tests/Common.Web.Tests/RequestTimeoutTests.cs names deploy/helm/?", problems)

    def test_an_exclusion_no_search_can_be_derived_from_is_caught(self) -> None:
        text = self.broken("              - '!docs/**'\n", "              - '!docs/*/generated.md'\n")
        self.assertIn("neither a whole tree nor a name at the root", self.problems(text))

    def test_a_test_reading_an_excluded_root_name_is_caught(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "tests" / "A"
            source.mkdir(parents=True)
            (root / "src").mkdir()
            (root / "tools").mkdir()
            (source / "A.cs").write_text(
                'var root_read = Path.Combine(root, "README.md");\n'
                'var nested = Path.Combine(root, "deploy", "compose", "README.md");\n',
                encoding="utf-8",
            )
            problems = "\n".join(pipeline_gate.check_code(root, self.text))
        self.assertIn("tests/A/A.cs names README.md", problems)
        self.assertNotIn("names compose", problems)
        self.assertEqual(problems.count("tests/A/A.cs names"), 1)

    def test_each_excluded_root_name_in_a_list_is_caught(self) -> None:
        """The first root name's match spans the list, and the later names in it are judged too."""
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "tests" / "A"
            source.mkdir(parents=True)
            (root / "src").mkdir()
            (root / "tools").mkdir()
            (source / "A.cs").write_text('string[] read = ["README.md", "Program.cs", "CLAUDE.md"];\n', encoding="utf-8")
            code = pipeline_gate.read_filters_by_name(self.text)[pipeline_gate.CODE_FILTER]
            named = [path for _source, path in pipeline_gate.read_tested_paths(root, code)]
        self.assertEqual(named, ["README.md", "CLAUDE.md"])

    def test_a_document_a_test_reads_that_stage_inputs_lacks_is_caught(self) -> None:
        text = self.broken("              - 'docs/personal-data.md'\n", "")
        self.assertIn("docs/personal-data.md", self.problems(text))

    def test_an_allow_list_is_caught(self) -> None:
        text = self.broken("              - '**'\n", "              - 'src/**'\n")
        self.assertIn("a new path nobody listed", self.problems(text))

    def test_a_positive_pattern_beside_the_exclusions_is_caught(self) -> None:
        text = self.broken("              - '!docs/**'\n", "              - '!docs/**'\n              - 'tools/**'\n")
        self.assertIn("a new path nobody listed", self.problems(text))

    def test_a_negation_in_stage_inputs_is_caught(self) -> None:
        text = self.broken("              - 'docs/personal-data.md'\n", "              - '!docs/personal-data.md'\n")
        self.assertIn("narrowing it", self.problems(text))

    def test_a_double_quoted_exclusion_is_judged(self) -> None:
        """Dropped, it would pass while a test-only change skipped every stage."""
        exclusion = '              - "!deploy/helm/**"\n'
        text = self.broken("              - '!docs/**'\n", "              - '!docs/**'\n" + exclusion)
        self.assertIn("tests/Common.Web.Tests/RequestTimeoutTests.cs names deploy/helm/?", self.problems(text))

    def test_a_bare_item_beside_the_exclusions_is_judged(self) -> None:
        text = self.broken("              - '!docs/**'\n", "              - '!docs/**'\n              - tools/**\n")
        self.assertIn("a new path nobody listed", self.problems(text))

    def test_a_filter_line_it_cannot_read_is_refused(self) -> None:
        """A new spelling the reader drops would leave its pattern unjudged, so it fails instead."""
        lines = ("- ['!tests/**']", "- '!tests/**' '!src/**'", '- "!tests\\x2F**"', "- !tests/**", "- added: '!tests/**'")
        for line in lines:
            with self.subTest(line=line):
                text = self.broken("              - '!docs/**'\n", f"              - '!docs/**'\n              {line}\n")
                self.assertIn(f"holds `{line}`, a line this check cannot read", self.problems(text))

    def test_glob_syntax_the_classifier_cannot_read_is_caught(self) -> None:
        text = self.broken("              - '!docs/**'\n", "              - '!docs/{a,b}/**'\n")
        self.assertIn("does not read", self.problems(text))

    def test_a_renamed_filter_fails_rather_than_passing_empty(self) -> None:
        text = self.broken("            code:\n", "            renamed:\n")
        self.assertIn("what is broken", self.problems(text))

    def test_an_output_that_can_skip_a_push_is_caught(self) -> None:
        text = self.broken("github.event_name != 'pull_request' || ", "")
        self.assertIn("github.event_name != 'pull_request'", self.problems(text))

    def test_an_output_that_ignores_stage_inputs_is_caught(self) -> None:
        text = self.broken(" || steps.changes.outputs.stage-inputs != 'false'", "")
        self.assertIn("stage-inputs", self.problems(text))

    def test_an_output_a_lost_step_output_turns_false_is_caught(self) -> None:
        for name in ("code", "stage-inputs"):
            with self.subTest(name=name):
                text = self.broken(
                    f"steps.changes.outputs.{name} != 'false'",
                    f"steps.changes.outputs.{name} == 'true'",
                )
                self.assertIn(f"steps.changes.outputs.{name} != 'false'", self.problems(text))

    def test_an_output_whose_terms_are_joined_or_negated_otherwise_is_caught(self) -> None:
        """Every term is still there as text, so only a read of the `||` operands sees either."""
        for old, new, term in (
                (" || steps.changes.outputs.code", " && steps.changes.outputs.code",
                 "github.event_name != 'pull_request'"),
                ("steps.changes.outputs.stage-inputs != 'false'",
                 "!(steps.changes.outputs.stage-inputs != 'false')", "steps.changes.outputs.stage-inputs != 'false'")):
            with self.subTest(new=new):
                self.assertIn(term, self.problems(self.broken(old, new)))

    def test_an_output_that_is_gone_is_caught(self) -> None:
        text = re.sub(r"^      code: .*\n", "", self.text, count=1, flags=re.MULTILINE)
        self.assertNotEqual(text, self.text)
        self.assertIn("does not read", self.problems(text))

    def test_a_job_level_condition_on_each_gated_job_is_caught(self) -> None:
        for job, old in (
            ("build", "  build:\n    needs:"),
            ("integration", "  integration:\n"),
            ("stage-gate", "  stage-gate:\n    needs:"),
        ):
            with self.subTest(job=job):
                head = old.split("\n", 1)
                text = self.broken(old, f"{head[0]}\n    if: {pipeline_gate.CODE_GATE}\n{head[1]}")
                self.assertIn(f"the {job} job carries a job-level", self.problems(text))

    def test_a_step_with_no_gate_in_each_gated_job_is_caught(self) -> None:
        for job, before in (
            ("build", "      - name: Unit stage\n"),
            ("integration", "      - name: Integration stage, ${{ matrix.shard }}\n        id: stage\n"),
            ("stage-gate", "      - name: Report domain-layer coverage\n"),
        ):
            with self.subTest(job=job):
                text = self.broken(f"{before}{self.GATE}", before)
                self.assertIn(f"the {job} job's step", self.problems(text))

    def test_a_step_gate_widened_or_negated_is_caught(self) -> None:
        """The gate is still there as text, so only a read of the `&&` terms sees either."""
        before = "      - name: Unit stage\n"
        for guard in (f"{pipeline_gate.CODE_GATE} || always()", f"!({pipeline_gate.CODE_GATE})"):
            with self.subTest(guard=guard):
                text = self.broken(f"{before}{self.GATE}", f"{before}        if: {guard}\n")
                self.assertIn("the build job's step", self.problems(text))

    def test_an_always_step_beside_nothing_a_skipped_step_never_leaves_is_caught(self) -> None:
        """A change no stage reads skips every gated step, and these would still run."""
        old = "always() && hashFiles('TestResults/sightings/*.txt') != ''"
        for new in (
            "always()",
            "always() || hashFiles('TestResults/sightings/*.txt') != ''",
            "always() && steps.stage.outputs.retried != 'true'",
        ):
            with self.subTest(new=new):
                self.assertIn("the integration job's step", self.problems(self.broken(old, new)))

    def test_a_renamed_job_fails_rather_than_passing_empty(self) -> None:
        text = self.broken("  stage-gate:\n    needs:", "  stages:\n    needs:")
        self.assertIn("found no steps", self.problems(text))

    def test_no_test_naming_a_document_fails_rather_than_passing_empty(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "src").mkdir()
            (root / "tests").mkdir()
            (root / "tools").mkdir()
            problems = "\n".join(pipeline_gate.check_code(root, self.text))
        self.assertIn("the scan is what is broken", problems)

    def test_the_reader_finds_each_spelling_of_a_document_path(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "tests" / "A"
            source.mkdir(parents=True)
            (root / "src").mkdir()
            (root / "tools").mkdir()
            (source / "A.cs").write_text(
                'var a = Path.Combine(root, "docs", "a.md");\n'
                'var b = Path.Combine(root, "docs",\n    "sub",\n    "b.md");\n'
                'var c = Locate("docs/c.md");\n'
                'var d = Path.Combine(root, "docs", name);\n'
                'var e = Locate(".github/e.yml");\n',
                encoding="utf-8",
            )
            (source / "A.csproj").write_text('<None Include="..\\..\\docs\\f.md" />\n', encoding="utf-8")
            code = pipeline_gate.read_filters_by_name(self.text)[pipeline_gate.CODE_FILTER]
            named = {path for _source, path in pipeline_gate.read_tested_paths(root, code)}
        self.assertEqual(
            named,
            {"docs/a.md", "docs/sub/b.md", "docs/c.md", "docs/?", ".github/e.yml", "docs/f.md"},
        )

    def test_a_path_built_from_a_variable_is_refused_as_unlisted(self) -> None:
        filters = pipeline_gate.read_filters_by_name(self.text)
        self.assertFalse(pipeline_gate.is_code(filters, "docs/?"))

    def test_a_tracked_path_holding_a_line_break_is_caught(self) -> None:
        for name in ("src/App/a\nb.cs", "src/App/a\rb.cs", "src/App/a b.cs", "src/App/a b.cs"):
            with self.subTest(name=name):
                problems = pipeline_gate.check_tracked_names(["src/App/Fine.cs", name])
                self.assertEqual(len(problems), 1)
                self.assertIn("holds a line break", problems[0])
        self.assertEqual(pipeline_gate.check_tracked_names(["src/App/Fine.cs", "docs/Übersicht.md"]), [])

    def test_a_tree_git_cannot_list_fails_rather_than_passing(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for tree in ("src", "tests", "tools"):
                (root / tree).mkdir()
            self.assertIn("git ls-files read nothing", "\n".join(pipeline_gate.check_code(root, self.text)))

    def test_a_path_holding_a_newline_is_still_code(self) -> None:
        filters = pipeline_gate.read_filters_by_name(self.text)
        self.assertTrue(pipeline_gate.is_code(filters, "src/App/a\nb.cs"))
        self.assertFalse(pipeline_gate.is_code(filters, "docs/a\nb.md"))

    def test_the_globs_read_as_picomatch_reads_them(self) -> None:
        for pattern, path, expected in (
            ("**", "a/b/c.cs", True),
            ("**", ".github/x", True),
            ("docs/**", "docs/a/b.md", True),
            ("docs/**", "docs2/a.md", False),
            ("*.md", "README.md", True),
            ("*.md", "src/README.md", False),
            ("docs/personal-data.md", "docs/personal-data.md", True),
            ("docs/personal-data.md", "docs/personal-dataXmd", False),
        ):
            with self.subTest(pattern=pattern, path=path):
                self.assertEqual(pipeline_gate._glob(pattern, path), expected)


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

    def test_a_stage_whose_tests_were_all_skipped_ran_no_tests(self) -> None:
        """A skipped test is `NotExecuted`, in `total` and every `TestMethod`
        alike, so neither may be what clears a floor or covers a project."""
        architecture = self.stage("architecture", trx(18, "Catalog.Domain.Tests", ["arch"]))
        unit = self.stage("unit", trx(600, "Catalog.Domain.Tests", ["a"]))
        integration = self.stage(
            "integration", trx(0, "Platform.IntegrationTests", ["c", "d"], skipped=("c", "d")))

        problems = pipeline_gate.check_stages([architecture, unit, integration], self.solution)

        self.assertTrue(any("integration stage ran no tests" in problem for problem in problems), problems)
        self.assertTrue(any("Platform.IntegrationTests ran in no stage" in problem for problem in problems), problems)

    def test_a_skipped_test_is_not_an_overlap(self) -> None:
        architecture = self.stage("architecture", trx(18, "Catalog.Domain.Tests", ["arch"]))
        unit = self.stage("unit", trx(600, "Catalog.Domain.Tests", ["a", "shared"], skipped=("shared",)))
        integration = self.stage(
            "integration",
            trx(160, "Platform.IntegrationTests", ["c"]),
            trx(1, "Catalog.Domain.Tests", ["shared"]),
        )

        self.assertEqual(pipeline_gate.check_stages([architecture, unit, integration], self.solution), [])

    def test_a_counters_element_with_no_executed_count_is_refused(self) -> None:
        unit = self.stage("unit", trx(600, "Catalog.Domain.Tests", ["a"]).replace(' executed="600"', ""))

        with self.assertRaises(SystemExit) as raised:
            pipeline_gate.read_stage(unit)

        self.assertIn("no `executed` count", str(raised.exception))

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
