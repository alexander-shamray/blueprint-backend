"""check.py's per-service outbox-gauge check, over a throwaway source tree."""

import pathlib
import shutil
import tempfile
import unittest
from unittest import mock

import check

DISPATCHER = "services.AddHostedService<OutboxDispatcher>();"
METRICS = "services.AddSingleton<OutboxMetrics>();"
UNINSTRUMENTED = "Svc hosts OutboxDispatcher and registers no OutboxMetrics"


class OutboxMetricsTests(unittest.TestCase):
    def tree(self, registrations: str, domain: bool = True) -> pathlib.Path:
        """A src/Services tree holding one service, `Svc`, whose infrastructure registers `registrations`."""
        root = pathlib.Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, root, ignore_errors=True)
        service = root / "src" / "Services" / "Svc"
        if domain:
            (service / "Svc.Domain").mkdir(parents=True)
            (service / "Svc.Domain" / "Svc.Domain.csproj").write_text("<Project />\n", encoding="utf-8")
        infrastructure = service / "Svc.Infrastructure"
        infrastructure.mkdir(parents=True)
        (infrastructure / "DependencyInjection.cs").write_text(registrations, encoding="utf-8")
        return root

    def failures_for(self, root: pathlib.Path) -> list[str]:
        """Every failure the check raises over `root`, with the real tree's exemptions set aside."""
        with (
            mock.patch.object(check, "ROOT", root),
            mock.patch.object(check, "OUTBOX_METRICS_EXEMPT", {}),
            mock.patch.object(check, "failures", []) as failures,
        ):
            check.check_outbox_metrics_per_service()
            return list(failures)

    def test_a_service_registering_both_is_instrumented(self) -> None:
        found = self.failures_for(self.tree(f"{DISPATCHER}\n{METRICS}\n"))

        self.assertFalse([f for f in found if f.startswith("Svc ")], found)

    def test_a_dispatcher_without_the_metrics_is_refused(self) -> None:
        """The control for the cases below: it is the same shape with the registration absent."""
        found = self.failures_for(self.tree(f"{DISPATCHER}\n"))

        self.assertTrue(any(f.startswith(UNINSTRUMENTED) for f in found), found)

    def test_a_line_commented_out_registration_is_not_one(self) -> None:
        found = self.failures_for(self.tree(f"{DISPATCHER}\n// {METRICS}\n"))

        self.assertTrue(any(f.startswith(UNINSTRUMENTED) for f in found), found)

    def test_a_block_commented_out_registration_is_not_one(self) -> None:
        found = self.failures_for(self.tree(f"{DISPATCHER}\n/* {METRICS} */\n"))

        self.assertTrue(any(f.startswith(UNINSTRUMENTED) for f in found), found)

    def test_a_comment_naming_the_dispatcher_does_not_make_a_host(self) -> None:
        """Counted, it would add a service with no Domain project to the hosts and fail the other direction."""
        found = self.failures_for(self.tree(f"// {DISPATCHER}\n", domain=False))

        self.assertFalse([f for f in found if f.startswith("Svc ")], found)
        self.assertTrue(any("found no service hosting OutboxDispatcher" in f for f in found), found)


if __name__ == "__main__":
    unittest.main()
