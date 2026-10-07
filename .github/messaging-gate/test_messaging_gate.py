#!/usr/bin/env python3
"""What the messaging gate would catch, which running it against this repository does not say.

    py -3.12 -m unittest discover -s .github/messaging-gate
"""
from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

import messaging_gate

CHAPTER = """\
# 3. Bounded contexts

## 3.2 Service responsibilities

| Service | Owns | Publishes (events) | Consumes (events) | Accepts (commands) |
|---|---|---|---|---|
| **Catalog** | Product | `ProductPublished` | `StockLevelChanged` | — |
| **Ordering** | Order | `OrderPlaced` | `OrderPlaced` (its own — the saga starts on it), `ProductPublished` | `CancelOrder` |
| **Inventory** | StockItem | `StockLevelChanged` | `OrderPlaced` | — |
| **Web.Bff** — a host, not a service | A projection | — | `OrderPlaced` | — |

Prose after the table.
"""

FILES = {
    "src/Services/Catalog/Catalog.Application/Integration/CatalogIntegrationEventMapper.cs":
        "private static ProductPublished ToContract(ProductPublishedDomainEvent e) => new()",
    "src/Services/Catalog/Catalog.Infrastructure/Messaging/StockLevelConsumer.cs":
        "x.AddConsumer<IntegrationEventConsumer<StockLevelChanged>>();\n"
        "e.ConfigureConsumer<IntegrationEventConsumer<StockLevelChanged>>(context);",
    "src/Services/Ordering/Ordering.Application/Integration/OrderingIntegrationEventMapper.cs":
        "private static OrderPlaced ToContract(OrderPlacedDomainEvent e) => new()",
    "src/Services/Ordering/Ordering.Infrastructure/Messaging/DependencyInjection.cs":
        "e.ConfigureConsumer<IntegrationEventConsumer<ProductPublished>>(context);\n"
        "e.ConfigureConsumer<CommandConsumer<CancelOrder, CancelOrderCommand>>(context);",
    "src/Services/Ordering/Ordering.Infrastructure/Messaging/OrderFulfilmentSaga.cs":
        "public sealed class OrderFulfilmentSaga : MassTransitStateMachine<OrderFulfilmentState>\n"
        "    public Event<OrderPlaced> OrderPlaced { get; private set; } = null!;",
    "src/Services/Inventory/Inventory.Application/Integration/InventoryIntegrationEventMapper.cs":
        "private static StockLevelChanged ToContract(StockLevelChangedDomainEvent e) => new()",
    "src/Services/Inventory/Inventory.Infrastructure/Messaging/DependencyInjection.cs":
        "e.ConfigureConsumer<IntegrationEventConsumer<OrderPlaced>>(context);",
    "src/BFF/Web.Bff/Messaging/DependencyInjection.cs":
        "e.ConfigureConsumer<IntegrationEventConsumer<OrderPlaced>>(context);",
    "src/BFF/Web.Bff.Migrator/Program.cs": "// no messaging here",
}


class Fixture(unittest.TestCase):
    """A tree shaped like this one, small enough to break on purpose."""

    def setUp(self) -> None:
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.owed = self.root / "owed.txt"
        self.write(str(messaging_gate.CHAPTER), CHAPTER)
        for path, text in FILES.items():
            self.write(path, text)

    def write(self, path: str, text: str) -> None:
        target = self.root / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text, encoding="utf-8")

    def edit(self, path: str, old: str, new: str) -> None:
        target = self.root / path
        text = target.read_text(encoding="utf-8")
        self.assertIn(old, text)
        target.write_text(text.replace(old, new), encoding="utf-8")

    def check(self) -> list[str]:
        return messaging_gate.check(self.root, self.owed)


class TableAgainstCode(Fixture):
    def test_the_fixture_is_clean(self) -> None:
        self.assertEqual(self.check(), [])

    def test_a_consumer_the_table_does_not_name_is_caught_on_its_row(self) -> None:
        self.edit("src/Services/Inventory/Inventory.Infrastructure/Messaging/DependencyInjection.cs",
                  "OrderPlaced>>(context);",
                  "OrderPlaced>>(context);\ne.ConfigureConsumer<IntegrationEventConsumer<ProductPublished>>(context);")

        self.assertEqual(self.check(), [
            "Inventory's code consumes ProductPublished and §3.2's Inventory row does not say so"])

    def test_a_publish_the_code_does_not_make_is_caught(self) -> None:
        self.edit("src/Services/Catalog/Catalog.Application/Integration/CatalogIntegrationEventMapper.cs",
                  "ProductPublished ToContract", "ProductRenamed ToContract")

        problems = self.check()
        self.assertIn("§3.2's Catalog row publishes ProductPublished and the code does not", problems)
        self.assertIn("Catalog's code publishes ProductRenamed and §3.2's Catalog row does not say so", problems)

    def test_a_registration_with_no_binding_is_not_a_consumer(self) -> None:
        # Registration alone binds nothing, which is the case a broker-side map would show as missing.
        self.edit("src/Services/Catalog/Catalog.Infrastructure/Messaging/StockLevelConsumer.cs",
                  "e.ConfigureConsumer<IntegrationEventConsumer<StockLevelChanged>>(context);", "")

        self.assertIn("§3.2's Catalog row consumes StockLevelChanged and the code does not", self.check())

    def test_a_saga_event_is_a_consumer_and_only_inside_a_state_machine(self) -> None:
        self.edit("src/Services/Ordering/Ordering.Infrastructure/Messaging/OrderFulfilmentSaga.cs",
                  "MassTransitStateMachine<OrderFulfilmentState>", "SomethingElse")

        self.assertIn("§3.2's Ordering row consumes OrderPlaced and the code does not", self.check())

    def test_a_command_consumer_is_an_accepted_command(self) -> None:
        self.edit("src/Services/Ordering/Ordering.Infrastructure/Messaging/DependencyInjection.cs",
                  "CommandConsumer<CancelOrder,", "CommandConsumer<ConfirmOrder,")

        problems = self.check()
        self.assertIn("§3.2's Ordering row accepts CancelOrder and the code does not", problems)
        self.assertIn("Ordering's code accepts ConfirmOrder and §3.2's Ordering row does not say so", problems)

    def test_a_service_without_a_row_and_a_row_without_a_service_are_both_caught(self) -> None:
        self.write("src/Services/Shipping/Shipping.Worker/Program.cs", "// a new service")
        self.edit(str(messaging_gate.CHAPTER), "| **Inventory** |", "| **Stock** |")

        problems = self.check()
        self.assertIn("Shipping is a host under src/ and has no row in §3.2's table", problems)
        self.assertIn("§3.2's Stock row names no host under src/Services or src/BFF", problems)

    def test_the_bffs_projects_are_one_host(self) -> None:
        self.assertIn("Web.Bff", messaging_gate.host_directories(self.root))
        self.assertNotIn("Web.Bff.Migrator", messaging_gate.host_directories(self.root))


class Owed(Fixture):
    MAPPER = "src/Services/Catalog/Catalog.Application/Integration/CatalogIntegrationEventMapper.cs"

    def setUp(self) -> None:
        super().setUp()
        # The table still says Catalog publishes ProductPublished; the code no longer does.
        self.write(self.MAPPER, "// nothing mapped yet")

    def test_an_owed_name_is_excused_and_only_that_one(self) -> None:
        self.owed.write_text("# a comment\nCatalog publishes ProductPublished #471\n", encoding="utf-8")

        self.assertEqual(self.check(), [])

    def test_without_the_line_the_difference_is_a_problem(self) -> None:
        self.assertEqual(self.check(), ["§3.2's Catalog row publishes ProductPublished and the code does not"])

    def test_a_line_that_is_no_longer_a_difference_is_caught(self) -> None:
        self.owed.write_text("Catalog publishes ProductPublished #471\nInventory publishes StockLevelChanged #1\n",
                             encoding="utf-8")

        self.assertEqual(self.check(), [
            "owed.txt says Inventory publishes StockLevelChanged is unbuilt, and it is no longer a difference: "
            "delete the line"])

    def test_code_ahead_of_the_table_cannot_be_owed(self) -> None:
        # Owed is for a row the code has not caught up with; a name the table lacks is the table's to fix.
        self.write(self.MAPPER, "private static Unrelated ToContract(UnrelatedDomainEvent e) => new()")
        self.owed.write_text("Catalog publishes ProductPublished #471\nCatalog publishes Unrelated #1\n",
                             encoding="utf-8")

        problems = self.check()
        self.assertIn("Catalog's code publishes Unrelated and §3.2's Catalog row does not say so", problems)
        self.assertIn("owed.txt says Catalog publishes Unrelated is unbuilt, and it is no longer a difference: "
                      "delete the line", problems)

    def test_a_malformed_line_is_refused(self) -> None:
        self.owed.write_text("Catalog publishes ProductPublished\n", encoding="utf-8")

        self.assertIn("owed.txt:1", self.check()[0])


class Closure(Fixture):
    BFF_ROW = "| **Web.Bff** — a host, not a service | A projection | — | `OrderPlaced` | — |"

    def test_a_consumed_event_no_row_publishes_is_caught(self) -> None:
        self.edit(str(messaging_gate.CHAPTER), self.BFF_ROW, self.BFF_ROW.replace("OrderPlaced", "OrderShipped"))
        self.write("src/BFF/Web.Bff/Messaging/DependencyInjection.cs",
                   "e.ConfigureConsumer<IntegrationEventConsumer<OrderShipped>>(context);")

        self.assertEqual(self.check(), [
            "§3.2 has OrderShipped consumed and published by [], not exactly one row"])

    def test_a_published_event_no_row_consumes_is_caught(self) -> None:
        chapter = str(messaging_gate.CHAPTER)
        self.edit(chapter, "`OrderPlaced` (its own — the saga starts on it), ", "")
        self.edit(chapter, "| **Inventory** | StockItem | `StockLevelChanged` | `OrderPlaced` |",
                  "| **Inventory** | StockItem | `StockLevelChanged` | — |")
        self.edit(chapter, self.BFF_ROW, self.BFF_ROW.replace("`OrderPlaced`", "—"))
        for path in ("src/Services/Inventory/Inventory.Infrastructure/Messaging/DependencyInjection.cs",
                     "src/BFF/Web.Bff/Messaging/DependencyInjection.cs",
                     "src/Services/Ordering/Ordering.Infrastructure/Messaging/OrderFulfilmentSaga.cs"):
            self.write(path, "")

        self.assertEqual(self.check(), ["§3.2 has OrderPlaced published by ['Ordering'] and consumed by no row"])


class Parser(Fixture):
    def test_no_table_fails_rather_than_passing_empty(self) -> None:
        self.write(str(messaging_gate.CHAPTER), "## 3.2 Service responsibilities\n\nNo table.\n")

        self.assertIn("found no rows", self.check()[0])

    def test_no_host_fails_rather_than_passing_empty(self) -> None:
        empty = Path(self.directory.name) / "empty"
        (empty / messaging_gate.CHAPTER).parent.mkdir(parents=True)
        (empty / messaging_gate.CHAPTER).write_text(CHAPTER, encoding="utf-8")

        self.assertIn("found no host", messaging_gate.check(empty, self.owed)[0])

    def test_a_consumes_cell_reads_its_names_and_not_its_glosses(self) -> None:
        table = messaging_gate.read_table(CHAPTER)

        self.assertEqual(table["Ordering"].consumes, {"OrderPlaced", "ProductPublished"})
        self.assertEqual(table["Web.Bff"].publishes, set())


class ThisRepository(unittest.TestCase):
    """The subject is coverage: a host or a row the gate cannot see is one it never compares."""

    def test_every_host_under_src_has_a_row_and_every_row_a_host(self) -> None:
        hosts = set(messaging_gate.host_directories(messaging_gate.ROOT))
        services = {d.name for d in (messaging_gate.ROOT / "src/Services").iterdir() if d.is_dir()}
        table = messaging_gate.read_table((messaging_gate.ROOT / messaging_gate.CHAPTER).read_text(encoding="utf-8"))

        self.assertTrue(services <= hosts, services - hosts)
        self.assertIn("Web.Bff", hosts)
        self.assertEqual(hosts, set(table))

    def test_every_row_reads_something_from_the_code(self) -> None:
        # A reader that silently found nothing would pass every comparison over a row that states nothing.
        for name, host in messaging_gate.read_code(messaging_gate.ROOT).items():
            with self.subTest(host=name):
                self.assertTrue(host.publishes | host.consumes, name)

    def test_this_repository_passes(self) -> None:
        self.assertEqual(messaging_gate.check(), [])


if __name__ == "__main__":
    unittest.main()
