#!/usr/bin/env python3
"""Render a new service from the Catalog template (Appendix C, PR-11).

    python tools/new-service/new_service.py Yankee --port 5199

There is no template directory, and that is the design rather than an
omission. The template is `src/Services/Catalog` and `tests/Catalog.*`
themselves — the copy CI builds and `dotnet test` exercises — so there is
exactly one copy of the wiring and an improvement to Catalog reaches the next
service the next time this runs. A tokenised template beside it would be a
second copy that nothing builds and nothing reconciles, which is the drift
class this repository exists to avoid.

The price is that this file names text inside files people edit. It is paid
rather than hidden: every anchor below must match exactly once, the whole
render is built in memory and validated before anything is written, and a miss
raises ScaffoldError naming the file. A tool that fails open on its own
precondition reports success for work it did not do.

Since #161 it also LOADS `.github/secret-scan/secret_scan.py` and runs it over
what it has just rendered. §15.1's gate reads the working tree, so a service
carrying a credential-shaped literal with no accepted-finding entry beside it
cannot be committed at all — and the fingerprints those entries name are the
scanner's own, never a second implementation of its matching. Where that gate
is not in the tree the step writes nothing and says nothing, which is stated
here rather than discovered from a green run.

Stdlib only, like the licence gate, and for the same reasons: no restore, no
SDK, and it runs on Windows and on the Ubuntu runner without either noticing.

**Python 3.12 is the floor**, because that is what both CI jobs pin. A newer
interpreter on a developer machine is the hazard, not an older one: it accepts
APIs the floor does not, and the local suite goes green on code CI cannot run.
`Path.read_text(newline=…)` is 3.13 and was exactly that mistake once.
"""

from __future__ import annotations

import argparse
import importlib.util
import re
import sys
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone
from pathlib import Path, PurePosixPath
from types import ModuleType
from typing import Any

TEMPLATE = "Catalog"

# The five service projects §4.1 gives a service, its three test projects, and
# Catalog.TestSupport — which §4.1 is explicit is NOT a test project, and which
# is copied all the same: the fixture is the template's, and its second consumer
# arrives with the new service's first handler test, exactly as it did for
# Catalog in PR-10. Nine projects, not "five and four".
COPY_ROOTS = (
    "src/Services/Catalog",
    "tests/Catalog.Domain.Tests",
    "tests/Catalog.Application.Tests",
    "tests/Catalog.Api.Tests",
    "tests/Catalog.TestSupport",
)

MIGRATIONS = "src/Services/Catalog/Catalog.Infrastructure/Persistence/Migrations"

# §4.1 gives these two a Worker in place of an Api, and Notifications no Domain
# project at all. This script renders the Api shape, so it refuses them by name
# rather than producing a service that contradicts the chapter — which is the
# quiet failure the documentation's "no Worker template" note did not prevent,
# because a note is not a guard. The names go when the mode arrives.
WORKER_SERVICES = frozenset({"Shipping", "Notifications"})

# The nine projects a render creates, by suffix. Named once because the
# solution writer and the identity check below must agree about them.
PROJECT_SUFFIXES = (
    "Domain",
    "Application",
    "Infrastructure",
    "Migrator",
    "Api",
    "Domain.Tests",
    "Application.Tests",
    "Api.Tests",
    "TestSupport",
)

# Every file under COPY_ROOTS is classified here or the run fails. That is
# deliberate friction, and it is the same argument the domain allow-list gate
# makes in Catalog.Domain.Tests: extending the list is the decision the check
# exists to force. Without it, the next aggregate someone adds to Catalog ships
# silently into every service scaffolded afterwards, and no straggler check
# below would notice — a Categories folder carries none of the tokens this file
# searches for.
COPIED = frozenset(
    {
        "src/Services/Catalog/Catalog.Api/Catalog.Api.csproj",
        "src/Services/Catalog/Catalog.Api/Dockerfile",
        "src/Services/Catalog/Catalog.Api/Program.cs",
        "src/Services/Catalog/Catalog.Application/Catalog.Application.csproj",
        "src/Services/Catalog/Catalog.Application/DependencyInjection.cs",
        "src/Services/Catalog/Catalog.Application/Integration/CatalogIntegrationEventMapper.cs",
        "src/Services/Catalog/Catalog.Domain/Catalog.Domain.csproj",
        "src/Services/Catalog/Catalog.Infrastructure/Catalog.Infrastructure.csproj",
        "src/Services/Catalog/Catalog.Infrastructure/DependencyInjection.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Messaging/DependencyInjection.cs",
        "src/Services/Catalog/Catalog.Infrastructure/SqlConnectionFactory.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/CatalogDbContext.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/EfDomainEventCollector.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/OutboxPublisher.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/EfUnitOfWork.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/OutboxMessageConfiguration.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/InboxMessageConfiguration.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/IdempotencyMarkerConfiguration.cs",
        "src/Services/Catalog/Catalog.Migrator/Catalog.Migrator.csproj",
        "src/Services/Catalog/Catalog.Migrator/Dockerfile",
        "src/Services/Catalog/Catalog.Migrator/MigrationRunner.cs",
        "src/Services/Catalog/Catalog.Migrator/MigratorHost.cs",
        "src/Services/Catalog/Catalog.Migrator/Program.cs",
        "tests/Catalog.Domain.Tests/ArchitectureTests.cs",
        "tests/Catalog.Domain.Tests/Catalog.Domain.Tests.csproj",
        "tests/Catalog.Application.Tests/ArchitectureTests.cs",
        "tests/Catalog.Application.Tests/Catalog.Application.Tests.csproj",
        "tests/Catalog.Application.Tests/DependencyInjectionTests.cs",
        "tests/Catalog.Application.Tests/IdempotencyOptInTests.cs",
        "tests/Catalog.Api.Tests/ArchitectureTests.cs",
        "tests/Catalog.Api.Tests/Catalog.Api.Tests.csproj",
        "tests/Catalog.Api.Tests/DatabaseSmokeTests.cs",
        "tests/Catalog.Api.Tests/HostSmokeTests.cs",
        "tests/Catalog.Api.Tests/IdempotencyMarkerTests.cs",
        "tests/Catalog.Api.Tests/IntegrationCollection.cs",
        "tests/Catalog.Api.Tests/MessageTypeMapValidatorTests.cs",
        "tests/Catalog.Api.Tests/MessagingRegistrationTests.cs",
        "tests/Catalog.Api.Tests/InboxFilterTests.cs",
        "tests/Catalog.Api.Tests/OutboxDispatcherTests.cs",
        "tests/Catalog.Api.Tests/RetentionPurgeTests.cs",
        "tests/Catalog.Api.Tests/TransientFaultInjection.cs",
        "tests/Catalog.TestSupport/Catalog.TestSupport.csproj",
        "tests/Catalog.TestSupport/CatalogApiFactory.cs",
        # §12.4's test scheme. Copied rather than omitted even though a
        # scaffolded service has no endpoint to authorise: CatalogApiFactory
        # installs it unconditionally, so a service without this file does not
        # compile, and the first slice needs it on the day it arrives.
        "tests/Catalog.TestSupport/TestAuthHandler.cs",
        "tests/Catalog.TestSupport/Outbox/OutboxRows.cs",
        "tests/Catalog.TestSupport/Outbox/OutboxTestEvents.cs",
        "tests/Catalog.TestSupport/ServiceFixture.cs",
    }
)

# PR-10's slice, and nothing else. A scaffolded service is PR-07's state with
# the wiring accumulated through PR-14 on it — not PR-10's state with the
# nouns changed. Renaming Product to Order would hand the next service a
# deletion job and a vocabulary it did not choose.
OMITTED = frozenset(
    {
        "src/Services/Catalog/Catalog.Api/Endpoints/ProductEndpoints.cs",
        # PR-19's pricing hop, whole. §9.7 permits exactly one synchronous
        # downstream call in the platform and Catalog is the callee, so a
        # service scaffolded from it inherits a gRPC server nobody calls,
        # a contract nobody consumes and a second Kestrel endpoint serving
        # neither. The .proto is Catalog's own API rather than a shape
        # every service has.
        #
        # appsettings.json goes with it because it exists ONLY for that
        # hop: it declares the Http2 endpoint gRPC needs, and a cleartext
        # port cannot serve HTTP/1.1 and h2c at once. Omitting it returns
        # the service to the container image's own port configuration,
        # which is what every other host here uses — and NOT omitting it
        # would be worse than redundant, because that file overrides
        # ASPNETCORE_HTTP_PORTS, so a service inheriting it would silently
        # stop listening on whatever its deployment set.
        "src/Services/Catalog/Catalog.Api/appsettings.json",
        "src/Services/Catalog/Catalog.Api/Protos/pricing.proto",
        "src/Services/Catalog/Catalog.Api/Grpc/PricingService.cs",
        # Generic in subject — it translates any ValidationException into
        # InvalidArgument — and slice by requirement: it is registered on
        # AddGrpc, which leaves with the hop, so a service keeping it would
        # carry an interceptor nothing installs.
        "src/Services/Catalog/Catalog.Api/Grpc/ValidationInterceptor.cs",
        # The permission vocabulary (§11.4) is the slice's, not the service's.
        # A host with no endpoint requires no permission, and carrying
        # `ordering:write` into a service that grants it to nothing would put a
        # name in the realm nobody can act on — the same objection as a policy
        # registered and never referenced. The first slice brings the first
        # permission, and the Program.cs patch below drops the policy that
        # names this one.
        "src/Services/Catalog/Catalog.Api/CatalogPermissions.cs",
        "src/Services/Catalog/Catalog.Application/Products/GetPrices/GetPricesHandler.cs",
        "src/Services/Catalog/Catalog.Application/Products/GetPrices/GetPricesQuery.cs",
        "src/Services/Catalog/Catalog.Application/Products/GetPrices/GetPricesValidator.cs",
        "src/Services/Catalog/Catalog.Application/Products/GetPrices/ProductPriceDto.cs",
        "src/Services/Catalog/Catalog.Application/Products/GetProducts/GetProductsHandler.cs",
        "src/Services/Catalog/Catalog.Application/Products/GetProducts/GetProductsQuery.cs",
        "src/Services/Catalog/Catalog.Application/Products/GetProducts/ProductSummaryDto.cs",
        "src/Services/Catalog/Catalog.Application/Products/PublishProduct/PublishProductCommand.cs",
        "src/Services/Catalog/Catalog.Application/Products/PublishProduct/PublishProductHandler.cs",
        "src/Services/Catalog/Catalog.Application/Products/PublishProduct/PublishProductValidator.cs",
        "src/Services/Catalog/Catalog.Domain/Common/Money.cs",
        "src/Services/Catalog/Catalog.Domain/Products/IProductRepository.cs",
        "src/Services/Catalog/Catalog.Domain/Products/Product.cs",
        "src/Services/Catalog/Catalog.Domain/Products/ProductId.cs",
        "src/Services/Catalog/Catalog.Domain/Products/ProductPublishedDomainEvent.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/MoneyJsonConverter.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/ProductConfiguration.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/ProductRepository.cs",
        "tests/Catalog.Domain.Tests/MoneyTests.cs",
        "tests/Catalog.Domain.Tests/ProductTests.cs",
        "tests/Catalog.Application.Tests/CatalogIntegrationEventMapperTests.cs",
        "tests/Catalog.Application.Tests/GetPricesValidatorTests.cs",
        "tests/Catalog.Application.Tests/GetProductsHandlerTests.cs",
        "tests/Catalog.Application.Tests/OutboxSerialisationTests.cs",
        "tests/Catalog.TestSupport/Outbox/StagesThenFails.cs",
        "tests/Catalog.Application.Tests/PublishProductHandlerTests.cs",
        "tests/Catalog.Application.Tests/PublishProductValidatorTests.cs",
        "tests/Catalog.Api.Tests/OutboxTransportIdentityTests.cs",
        # The gRPC service's own suite, and it leaves for two reasons at
        # once: there is no PricingService to drive, and the channel it
        # builds needs the generated client the csproj patch below drops.
        "tests/Catalog.Api.Tests/PricingServiceTests.cs",
        # PR-26's provider verification, which leaves for a third reason on
        # top of that pair: it is one named consumer's expectations of one
        # named provider. Web.Bff asks Catalog for prices (§9.7 permits
        # exactly one synchronous hop and this is it), so a scaffolded
        # service inherits neither the RPC nor anyone consuming it — and a
        # contract copied to a service no consumer calls is an expectation
        # nobody holds, which is the one thing a consumer-driven contract
        # must never become. The csproj patch below drops the linked
        # PricingContract.cs with it, for the same reason.
        "tests/Catalog.Api.Tests/PricingContractVerificationTests.cs",
        # Both name /v1/catalog/products, so both are slice by requirement:
        # they read the host as a deployment rather than a fixture, and a
        # service with no endpoint has nothing to read. They return with the
        # first slice, beside the endpoint tests below. HostSmokeTests keeps
        # the factory they share — which is why those two tests live in a file
        # of their own rather than in it.
        "tests/Catalog.Api.Tests/EndpointSecurityTests.cs",
        # §11.4's callout, executed: every policy an endpoint names must
        # resolve. With no endpoint there is no policy to enumerate, and the
        # suite's own guard against passing vacuously is what fails first.
        #
        # IT CARRIES A SECOND GATE and dropping it drops that too: §8.5's
        # rule that an idempotent command's endpoint must require
        # authentication, since ICurrentUser.IsAuthenticated is false for an
        # anonymous request and every such caller then claims under the
        # shared "system" subject. A rendered service has neither an endpoint
        # nor an idempotent command, so nothing is unguarded today — what
        # would be unguarded is the first slice that adds both, which is why
        # the inverted floor in IdempotencyOptInTests names this file in the
        # message it fails with.
        "tests/Catalog.Api.Tests/AuthorizationPolicyTests.cs",
        # Not slice by subject — it is about EfUnitOfWork's rollback — but slice
        # by requirement: the claim is that a rejected command leaves nothing
        # tracked, and making it needs a tracked aggregate. A service with no
        # entity cannot assert it, so it returns with the first real slice.
        "tests/Catalog.Api.Tests/UnitOfWorkRollbackTests.cs",
        "tests/Catalog.Api.Tests/ProductEndpointsTests.cs",
        # Not slice, but container wiring with nothing left to wire: with the
        # handler tests gone, the collection has no member and the fixture no
        # consumer here. Both return with the service's first handler test,
        # beside the two project references and the provider package the
        # csproj patch below drops for the same reason.
        "tests/Catalog.Application.Tests/IntegrationCollection.cs",
    }
)

# Anchored edits, matched against the Catalog text BEFORE renaming, each
# asserted to occur exactly once. Removing the slice leaves these files making
# claims that are no longer true; every one of them is a claim a reader would
# otherwise trust.
#
# **A replacement may name the template only where it means the new service's
# own project.** `Catalog.Domain` in a replacement is fine — it renames to
# `Inventory.Domain` and the sentence stays true. `Catalog.Application.Tests
# carries both` is not: it renames to `Inventory.Application.Tests carries
# both`, which is a sentence about the exemplar wearing the new service's name,
# and it is false in the one file where those tests are missing. The straggler
# check cannot see this — the rename is exactly what makes the claim wrong — so
# it is carried by review and by GeneratedGuidanceIsTrue in the tests, which
# pins the sites a Grok review found this way.
PATCHES: dict[str, tuple[tuple[str, str], ...]] = {
    "src/Services/Catalog/Catalog.Application/DependencyInjection.cs": (
        ("using Catalog.Application.Products.PublishProduct;\n", ""),
        (
            "        // Explicit rather than scanned, beside the dispatcher it serves —\n"
            "        // §4.2's registration sample is the shape. §7.5's real dispatcher\n"
            "        // since PR-14; the NullDomainEventDispatcher that dropped every\n"
            "        // ProductPublishedDomainEvent between PR-10 and here is deleted, not\n"
            "        // disabled, so nothing can register it back by accident.\n",
            "        // Explicit rather than scanned, beside the dispatcher it serves —\n"
            "        // §4.2's registration sample is the shape. It stages nothing until\n"
            "        // this service has an aggregate raising domain events, and needs no\n"
            "        // null object to say so: a collector over an empty change tracker\n"
            "        // returns nothing and the dispatcher exits early (§7.5).\n",
        ),
        (
            "        services.AddValidatorsFromAssemblyContaining<PublishProductValidator>();\n",
            "        // §4.2's line spelt over the assembly rather than over a type in\n"
            "        // it, because there is no validator yet to name — and this class,\n"
            "        // the obvious anchor, is static and cannot be a type argument.\n"
            "        // Move to AddValidatorsFromAssemblyContaining<TFirstValidator>()\n"
            "        // with the first one, and add the registration test that guards\n"
            "        // it: ValidationBehavior takes IEnumerable<IValidator<T>>, so a\n"
            "        // lost scan is a pipeline that validates nothing and says so to\n"
            "        // nobody.\n"
            "        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly);\n",
        ),
    ),
    "src/Services/Catalog/Catalog.Application/Integration/CatalogIntegrationEventMapper.cs": (
        (
            "using Catalog.Domain.Products;\n"
            "using Common.Application;\n"
            "using Common.Contracts.Catalog.V1;\n"
            "using Common.Domain;\n",
            "using Common.Application;\n"
            "using Common.Domain;\n",
        ),
        (
            "    // The allow-list. Catalog's other two facts of §3.2 — PriceChanged and\n"
            "    // ProductDiscontinued — join it with the domain operations that raise\n"
            "    // them; an entry here with no domain event behind it would not compile,\n"
            "    // which is the property that keeps this list honest.\n"
            "    private static readonly Dictionary<Type, Func<IDomainEvent, object>> Registry = new()\n"
            "    {\n"
            "        // Domain type in, contract type out. The suffix (§5.5) is what makes\n"
            "        // that visible — with one name for both, this reads as identity, and\n"
            "        // §12.4's \"the domain type never reaches the broker\" would have\n"
            "        // nothing to assert against.\n"
            "        [typeof(ProductPublishedDomainEvent)] = e => ToContract((ProductPublishedDomainEvent)e)\n"
            "    };\n",
            "    // The allow-list, empty until this service publishes something. Every\n"
            "    // domain event it raises is local-only while this dictionary is empty,\n"
            "    // which is the correct state for a service with no contracts — and not\n"
            "    // a gap, because §9.3 makes translation opt-in precisely so that a new\n"
            "    // event is internal until somebody decides otherwise.\n"
            "    //\n"
            "    // An entry is one line and one private ToContract method beside it:\n"
            "    //\n"
            "    //     [typeof(OrderPlacedDomainEvent)] = e => ToContract((OrderPlacedDomainEvent)e)\n"
            "    //\n"
            "    // with the contract living in Common.Contracts under a versioned\n"
            "    // namespace (§9.2), carrying primitives only, and taking its MessageId\n"
            "    // and CorrelationId from the mapper rather than from Stage (§9.1).\n"
            "    private static readonly Dictionary<Type, Func<IDomainEvent, object>> Registry = [];\n",
        ),
        (
            "\n"
            "    // V1.ProductPublished, not ProductPublishedDomainEvent: Money is\n"
            "    // decomposed into a decimal and an ISO code, because a contract may not\n"
            "    // carry domain types (§9.1).\n"
            "    private static ProductPublished ToContract(ProductPublishedDomainEvent e) => new()\n"
            "    {\n"
            "        // Minted here and nowhere else. Stage copies both onto the row and\n"
            "        // DeliverAsync copies them onto the transport, so the body, the row,\n"
            "        // the broker header and the inbox key are one GUID (§9.1).\n"
            "        MessageId = Guid.CreateVersion7(),\n"
            "        // The product, not an ambient request id: a business correlation is\n"
            "        // what a support tool follows across services, and §9.3 sets it from\n"
            "        // the aggregate for exactly that reason.\n"
            "        CorrelationId = e.ProductId.Value,\n"
            "        OccurredAt = e.OccurredAt,\n"
            "        ProductId = e.ProductId.Value,\n"
            "        Name = e.Name,\n"
            "        ThumbnailUrl = e.ThumbnailUrl,\n"
            "        Amount = e.Price.Amount,\n"
            "        Currency = e.Price.Currency\n"
            "    };\n",
            "",
        ),
        (
            "/// §9.3's allow-list for Catalog. §5.5 states the principle — never publish a\n",
            "/// §9.3's allow-list for this service. §5.5 states the principle — never publish a\n",
        ),
    ),
    "src/Services/Catalog/Catalog.Application/Catalog.Application.csproj": (
        # The mapper's registry is emptied and its `using` removed, so nothing
        # in a generated Application project names a contract. Keeping the
        # reference would be the untruth this repository refuses everywhere
        # else: an unused project reference is a claim about the dependency
        # graph that nothing makes true. It returns with the first contract,
        # beside the first registry entry.
        (
            "    <ProjectReference Include=\"..\\..\\..\\BuildingBlocks\\Common.Contracts\\Common.Contracts.csproj\" />\n",
            "",
        ),
        (
            "    Domain, Common.Application and Common.Contracts — the §4.2 dependency\n"
            "    table's second row, complete since PR-14. Contracts arrives with the §9.3\n"
            "    mapper, which is the only type here that names one: the allow-list turns a\n"
            "    domain event into a public record, so the layer that owns the allow-list\n"
            "    is the layer that pays for the reference. §4.3's one assembly that crosses\n"
            "    a service boundary, and it crosses at the mapper.\n",
            "    Domain and Common.Application — the §4.2 dependency table's second row,\n"
            "    minus Common.Contracts. The §9.3 mapper is the only type that would name\n"
            "    a contract, and its allow-list is empty until this service publishes\n"
            "    something — so the reference joins with the first entry in it, and not\n"
            "    before. §4.3's one assembly that crosses a service boundary; it crosses\n"
            "    at the mapper or nowhere.\n",
        ),
        (
            "  <ItemGroup>\n"
            "    <!-- The read side of §6.5: query handlers use Dapper directly, never EF —\n"
            "         the architecture gate in Catalog.Application.Tests holds that line. -->\n"
            "    <PackageReference Include=\"Dapper\" />\n",
            "  <ItemGroup>\n"
            "    <!-- Dapper is not here yet: §6.5's read side uses it directly, and this\n"
            "         project has no query handler to use it. It joins with the first\n"
            "         one — an unused package reference is a claim this project would\n"
            "         not be making. -->\n",
        ),
    ),
    "src/Services/Catalog/Catalog.Infrastructure/Catalog.Infrastructure.csproj": (
        (
            "    <!-- §9.4's outbox: the entity this assembly maps, the type map, the\n"
            "         metrics and the dispatcher it hosts. PR-14's edge, and the reason\n"
            "         Common.Infrastructure stopped being a project no service referenced.\n"
            "         §8's Redis helpers ride in on this same reference and are now used\n"
            "         rather than merely available — AddRedisConnections and\n"
            "         RedisIdempotencyStore are wired in DependencyInjection.cs. This\n"
            "         comment said they were \"still nobody's dependency\" and that the first\n"
            "         service to wire them would need no new reference; the second half was\n"
            "         right and is why nothing here changed but the sentence. -->\n",
            # THE RENDERED SERVICE DOES WIRE REDIS, and this comment said it did
            # not. DependencyInjection.cs is a template file and no edit removes
            # `services.AddRedisConnections(configuration)`, so a scaffolded
            # service registers the connections, the lock factory and §8.5's
            # store on the day it is created — which is why its compose block
            # carries both ConnectionStrings__Redis* keys and why it starts at
            # all. AddRedisConnections reads both eagerly and throws naming the
            # missing one.
            #
            # Redis is therefore part of the empty-service BASELINE rather than
            # something a first slice adds, and the text below now says so. The
            # wrong version was not merely stale prose: it described the one
            # arrangement under which a rendered service would fail to start.
            "    <!-- §9.4's outbox: the entity this assembly maps, the type map, the\n"
            "         metrics and the dispatcher it hosts. §8's Redis helpers ride in on\n"
            "         the same reference and are wired from the start — the connections,\n"
            "         the lock factory and §8.5's idempotency store — so a first cache\n"
            "         read costs no new reference and no new registration. -->\n",
        ),
        (
            "    <!-- typeof(ProductPublished).Assembly, the Broker lane's half of\n"
            "         MessageTypeSource. Transitive through Catalog.Application, named\n"
            "         directly because this file names the type. -->\n",
            "    <!-- MessageTypeSource's Broker half. Named through IIntegrationEvent\n"
            "         until this service has a contract of its own, at which point the\n"
            "         anchor becomes that contract — same assembly either way. -->\n",
        ),
    ),
    "src/Services/Catalog/Catalog.Infrastructure/DependencyInjection.cs": (
        # Domain, not Domain.Products: the aggregate goes with the slice, and
        # the AssemblyMarker that MessageTypeSource anchors on stays — it lives
        # one namespace up, and dropping the using outright left the generated
        # service naming a type it could not see.
        ("using Catalog.Domain.Products;\n", "using Catalog.Domain;\n"),
        (
            "        services.AddScoped<IUnitOfWork, EfUnitOfWork>();                     // §6.3\n"
            "        services.AddScoped<IProductRepository, ProductRepository>();         // §5.6\n",
            "        services.AddScoped<IUnitOfWork, EfUnitOfWork>();                     // §6.3\n"
            "\n"
            "        // §5.6's repository registrations join with the first aggregate.\n",
        ),
        ("using Common.Contracts.Catalog.V1;\n", "using Common.Contracts;\n"),
        (
            "        services.AddSingleton(\n"
            "            new MessageTypeSource(typeof(ProductPublished).Assembly, typeof(Product).Assembly));\n",
            "        // IIntegrationEvent and AssemblyMarker stand in for the two anchors\n"
            "        // §9.4 names — this service's contracts and its domain — because it\n"
            "        // has neither yet. Both point at the right assemblies regardless, so\n"
            "        // the first contract and the first aggregate change what these lines\n"
            "        // say and not what they resolve to.\n"
            "        services.AddSingleton(\n"
            "            new MessageTypeSource(typeof(IIntegrationEvent).Assembly, typeof(AssemblyMarker).Assembly));\n",
        ),
        (
            "        // The payload format (§9.4), and the converters that make this\n"
            "        // service's value objects part of it. MoneyJsonConverter is the same\n"
            "        // decision as ProductConfiguration's ComplexProperty: Money is\n"
            "        // persisted twice, as two columns and as two JSON members, and knows\n"
            "        // about neither. Its absence is silent — a Money round-trips to zero\n"
            "        // and a null currency rather than throwing.\n"
            "        services.AddSingleton<JsonConverter, MoneyJsonConverter>();\n"
            "        services.AddSingleton<OutboxJson>();\n",
            "        // The payload format (§9.4). The first value object this service puts\n"
            "        // on a domain event needs a converter registered here: a readonly\n"
            "        // record struct deserialises to its default rather than failing,\n"
            "        // and §12.4's round-trip assertion is what catches that.\n"
            "        services.AddSingleton<OutboxJson>();\n",
        ),
        ("using System.Text.Json.Serialization;\n", ""),
    ),
    "src/Services/Catalog/Catalog.Infrastructure/Persistence/CatalogDbContext.cs": (
        (
            "        // Landed before its first find (PR-08) so that PR-10 added an\n"
            "        // IEntityTypeConfiguration<T> and not also the line that discovers\n"
            "        // it; ProductConfiguration is what it finds today. §7.2 puts mapping\n"
            "        // in these classes and never in attributes on domain types, which\n"
            "        // would put EF Core in Catalog.Domain.\n",
            "        // Here before its first find, so that the first entity adds an\n"
            "        // IEntityTypeConfiguration<T> and not also the line that discovers\n"
            "        // it. §7.2 puts mapping in these classes and never in attributes on\n"
            "        // domain types, which would put EF Core in Catalog.Domain.\n",
        ),
    ),
    "src/Services/Catalog/Catalog.Api/Catalog.Api.csproj": (
        (
            "    <!-- The server half of §9.7's one synchronous hop. Grpc.AspNetCore brings\n"
            "         Grpc.Tools and Google.Protobuf with it, which is why neither is named\n"
            "         here — Appendix B registers all four as one row because they ship and\n"
            "         version as one thing. -->\n"
            "    <PackageReference Include=\"Grpc.AspNetCore\" />\n",
            "",
        ),
        # The whole ItemGroup, not just the Protobuf line: with the contract
        # gone the group is empty, and an empty ItemGroup is a place a reader
        # looks for something that is not there.
        (
            "\n"
            "  <ItemGroup>\n"
            "    <!-- Catalog owns the contract because Catalog serves it. Web.Bff compiles\n"
            "         this same file as a Client, by link — see the comment on that\n"
            "         reference, and pricing.proto's own header.\n"
            "\n"
            "         BOTH halves, not Server alone, and the client half is here for its own\n"
            "         suite. Catalog.Api.Tests drives PricingService over the real pipeline,\n"
            "         which needs a client; generating one in the test project instead would\n"
            "         put a second copy of every message type in a compilation that already\n"
            "         references this assembly, and CS0436 is an error under ADR-019. So the\n"
            "         choice is a generated client nothing in production calls, or a\n"
            "         transport adapter no test can reach — and an untested adapter is the\n"
            "         worse of the two. Web.Bff.TestSupport's own file argues the mirror\n"
            "         image of this for the server half. -->\n"
            "    <Protobuf Include=\"Protos\\pricing.proto\" GrpcServices=\"Both\" />\n"
            "  </ItemGroup>\n",
            "",
        ),
    ),
    "src/Services/Catalog/Catalog.Api/Program.cs": (
        ("using Catalog.Api;\nusing Catalog.Api.Endpoints;\n", ""),
        ("using Catalog.Api.Grpc;\n", ""),
        # §9.7's hop is Catalog's, so its registration and its mapping both
        # leave. What does NOT leave is the middleware pair below — every
        # host validates its own tokens (§11.2) whether or not it serves
        # anything, which is the same split the permission policy takes.
        (
            "\n"
            "// §9.7's server half. The interceptor is what keeps a malformed request from\n"
            "// arriving at the caller as Unknown, which the BFF would report as its own\n"
            "// 500 rather than the caller's 400 — its own file argues that at length.\n"
            "builder.Services.AddGrpc(o => o.Interceptors.Add<ValidationInterceptor>());\n",
            "",
        ),
        (
            "\n"
            "// §9.7. Reachable only on the Http2 endpoint appsettings.json declares —\n"
            "// gRPC needs HTTP/2, and mapping it says nothing about which port serves it.\n"
            "// The [Authorize] is on the service class, not here, so it travels with the\n"
            "// type rather than with this line.\n"
            "app.MapGrpcService<PricingService>();\n",
            "",
        ),
        # The permission policies leave with the slice that names them. What
        # stays is UseAuthentication/UseAuthorization below: every host
        # validates its own tokens (§11.2) whether or not it has an endpoint,
        # and a service that acquired the middleware only with its first slice
        # would be a service whose health probes were briefly the only thing
        # anybody had checked.
        (
            "// Catalog's permission policies (§11.4). Deliberately not inside either helper\n"
            "// above: Application knows nothing about HTTP, and Common.Web must not know\n"
            "// Catalog's names. One policy, because one endpoint names one — the write\n"
            "// path. A policy nothing references would be an unused registration, and\n"
            "// §11.4's callout is about the opposite mistake: a name an endpoint uses and\n"
            "// nobody registered throws InvalidOperationException on the first request that\n"
            "// reaches it, never at startup. AuthorizationPolicyTests asserts both\n"
            "// directions, from the endpoint metadata rather than from this list.\n"
            "//\n"
            "// RequirePermission rather than RequireClaim(\"permission\", …): the claim type\n"
            "// is Common.Web's (§11.4), so a policy here and the resource-level check\n"
            "// behind ICurrentUser cannot drift apart.\n"
            "builder.Services\n"
            "    .AddAuthorizationBuilder()\n"
            "    .AddPolicy(CatalogPermissions.Write, p => p.RequirePermission(CatalogPermissions.Write));\n"
            "\n",
            "// This service registers no permission policy, because it names no endpoint\n"
            "// that needs one. The first slice brings both together (§11.4):\n"
            "//\n"
            "//     builder.Services\n"
            "//         .AddAuthorizationBuilder()\n"
            "//         .AddPolicy(<Service>Permissions.Write, p => p.RequirePermission(…));\n"
            "//\n"
            "// A policy registered before an endpoint names it is an unused registration;\n"
            "// an endpoint naming one nobody registered throws on the first request that\n"
            "// reaches it, never at startup. Add AuthorizationPolicyTests with the slice —\n"
            "// it enumerates the endpoints and requires every policy they name to resolve.\n"
            "\n",
        ),
        (
            "app.MapOpenApi();\n"
            "app.MapProductEndpoints();        // §11.4\n",
            "app.MapOpenApi();\n"
            "\n"
            "// This service maps no endpoint of its own yet. The first one goes here,\n"
            "// behind RequireAuthorization at the group (§11.4) — fail closed, and let\n"
            "// any deliberately public endpoint say AllowAnonymous out loud.\n",
        ),
    ),
    "tests/Catalog.Domain.Tests/ArchitectureTests.cs": (
        ("using Catalog.Domain.Products;\n", ""),
        (
            "        // System.Collections earned its line with the first domain event: a\n"
            "        // record's generated equality goes through EqualityComparer<T>, which\n"
            "        // lives there. No collection type appears in any domain signature.\n"
            "        // System.Linq earned its line with Money's currency guard —\n"
            "        // enumerable logic over owned values is domain work, not an I/O\n"
            "        // dependency, and §5.4's Order sample already leans on it.\n"
            "        string[] allowed = [\"Common.Domain\", \"System.Runtime\", \"System.Collections\", \"System.Linq\"];\n"
            "\n"
            "        IEnumerable<string> referenced = typeof(Product).Assembly\n",
            "        // Two entries, because two is what an empty domain references. The\n"
            "        // two that usually follow, and what earns each: System.Collections\n"
            "        // with the first domain event, whose generated record equality goes\n"
            "        // through EqualityComparer<T>, and System.Linq with the first value\n"
            "        // object doing enumerable logic over owned values — domain work,\n"
            "        // not an I/O dependency.\n"
            "        string[] allowed = [\"Common.Domain\", \"System.Runtime\"];\n"
            "\n"
            "        IEnumerable<string> referenced = typeof(AssemblyMarker).Assembly\n",
        ),
    ),
    # §8.5's opt-in gate travels to every service, and one of its three tests
    # cannot travel as written. "This service declares commands; the selector
    # found none" is the anti-vacuity half — and a scaffolded service has no
    # commands at all until its first slice, so that assertion would fail on a
    # tree that is perfectly correct.
    #
    # Deleting it is the wrong fix, and CLAUDE.md names why: a gate that
    # silently stops covering the newest surface is this repository's
    # most-repeated failure, and a vacuous gate with its vacuity check removed
    # IS that failure, written down and shipped. So the assertion is INVERTED
    # instead. The rendered service asserts it has no commands YET, which fails
    # the day it gains one — and the failure message is the instruction to
    # restore the real form. Self-clearing, on the same argument as
    # deploy/observability/awaiting-signal.yaml: a list of things known to be
    # missing needs a gate asserting they are still missing.
    "tests/Catalog.Application.Tests/IdempotencyOptInTests.cs": (
        (
            "    public void The_gate_above_is_looking_at_this_service_s_commands()\n",
            "    public void This_service_has_no_commands_for_the_gate_above_to_look_at_yet()\n",
        ),
        (
            '        Commands().ShouldNotBeEmpty("Catalog declares commands; '
            'the selector above found none");\n',
            "        Commands().ShouldBeEmpty(\n"
            '            "This service declares no commands yet, so the gate above is vacuous. '
            'The day it "\n'
            '            + "gains its first command this test fails — replace it with the '
            'ShouldNotBeEmpty "\n'
            '            + "form, which is what keeps a vacuous gate from quietly becoming a '
            'permanent one.");\n',
        ),
        # The same inversion, one gate down, and it is owed for the same
        # reason: a rendered service opts no command into idempotency, so
        # that gate's own floor would fail on a tree that is correct. It
        # clears itself the day the service opts its first command in.
        (
            '        names.ShouldNotBeEmpty("Catalog declares an idempotent command; '
            'the selector above found none");\n',
            "        names.ShouldBeEmpty(\n"
            '            "This service opts no command into idempotency yet, so the '
            'check below is "\n'
            '            + "vacuous. The day it does, this test fails — replace it '
            'with the ShouldNotBeEmpty "\n'
            '            + "form, which is what keeps a vacuous gate from quietly '
            'becoming a permanent one. "\n'
            '            + "RESTORE AuthorizationPolicyTests IN THE SAME CHANGE: §8.5 '
            'requires an idempotent "\n'
            '            + "command\'s endpoint to be authenticated, an anonymous one '
            'collapses every caller "\n'
            '            + "into the shared system subject, and this service dropped '
            'that suite as a slice "\n'
            '            + "file.");\n',
        ),
        # And a THIRD, for the same reason again — which is the argument for
        # keeping these as data rather than as a rule someone reapplies. §8.5's
        # shape gate opens with its own anti-vacuity floor over `candidates`,
        # and a rendered service has none, so the floor fails on a tree that is
        # correct exactly as the two above would. The count is what makes the
        # point: every anti-vacuity floor added to a template file is owed an
        # entry here, and the third was owed the moment the gate was rewritten
        # to §8.5's specified form.
        (
            "        candidates.ShouldNotBeEmpty(\n"
            '            "no command in this assembly implements IIdempotentCommand, '
            'so this test is " +\n'
            '            "looking at nothing — the interface has been renamed, '
            'moved, or not yet applied.");\n',
            "        candidates.ShouldBeEmpty(\n"
            '            "This service opts no command into idempotency yet, so the '
            'two shape checks below " +\n'
            '            "are vacuous. The day it does, this test fails — restore '
            'the ShouldNotBeEmpty " +\n'
            '            "form, which is what keeps a vacuous gate from quietly '
            'becoming a permanent one.");\n',
        ),
        # And a FOURTH. §8.5's nested-dispatch gate has its own floor over
        # CommandHandlers(), and a rendered service declares none — PR-10's
        # slice is what brings the first handler. The gate ITSELF needs no
        # inversion: with no handlers the offender list is empty and the
        # assertion is correct, which is exactly why the floor beneath it
        # has to be turned around instead.
        (
            "        CommandHandlers().ShouldNotBeEmpty(\n"
            '            "Catalog declares command handlers; the selector '
            'above found none");\n',
            "        CommandHandlers().ShouldBeEmpty(\n"
            '            "This service declares no command handlers yet, so the '
            'gate above is "\n'
            '            + "vacuous. The day it gains one this test fails — restore '
            'the ShouldNotBeEmpty "\n'
            '            + "form, which is what keeps a vacuous gate from quietly '
            'becoming a permanent one.");\n',
        ),
    ),
    "tests/Catalog.Application.Tests/ArchitectureTests.cs": (
        ("using Catalog.Domain.Products;\n", "using Catalog.Domain;\n"),
        (
            "        Assembly[] assemblies = [typeof(DependencyInjection).Assembly, typeof(Product).Assembly];\n",
            # 104 columns, inside CLAUDE.md's 120 budget, so the list stays on
            # one line — the wrapped form was a ragged middle the rule forbids.
            "        Assembly[] assemblies = [typeof(DependencyInjection).Assembly, typeof(AssemblyMarker).Assembly];\n",
        ),
    ),
    "tests/Catalog.Application.Tests/Catalog.Application.Tests.csproj": (
        (
            "    <!-- ServiceCollection itself, for the registration tests: the\n"
            "         abstractions package Catalog.Application compiles against has no\n"
            "         container in it to build. -->\n"
            "    <PackageReference Include=\"Microsoft.Extensions.DependencyInjection\" />\n"
            "    <!-- The handler tests seed and assert through the real CatalogDbContext\n"
            "         (§12.4's seeding rule — a raw INSERT drifts from the aggregate the\n"
            "         first time it gains a column). -->\n"
            "    <PackageReference Include=\"Microsoft.EntityFrameworkCore.SqlServer\" />\n",
            "    <!-- ServiceCollection itself, for the registration tests: the\n"
            "         abstractions package Catalog.Application compiles against has no\n"
            "         container in it to build. -->\n"
            "    <PackageReference Include=\"Microsoft.Extensions.DependencyInjection\" />\n",
        ),
        (
            "    <ProjectReference Include=\"..\\..\\src\\Services\\Catalog\\Catalog.Application\\Catalog.Application.csproj\" />\n"
            "    <!-- §12.1 homes the handler tests here, with real containers — the\n"
            "         fixture lives in TestSupport, shared with Catalog.Api.Tests, which\n"
            "         this project cannot reference. -->\n"
            "    <ProjectReference Include=\"..\\..\\tests\\Catalog.TestSupport\\Catalog.TestSupport.csproj\" />\n"
            "    <!-- CatalogDbContext by name, for seeding and read-back. The test\n"
            "         project may: §4.2's gate binds Catalog.Application, not its tests. -->\n"
            "    <ProjectReference Include=\"..\\..\\src\\Services\\Catalog\\Catalog.Infrastructure\\Catalog.Infrastructure.csproj\" />\n",
            "    <ProjectReference Include=\"..\\..\\src\\Services\\Catalog\\Catalog.Application\\Catalog.Application.csproj\" />\n"
            "    <!-- No Catalog.TestSupport and no Catalog.Infrastructure yet, and no\n"
            "         Docker with them: §12.1 homes the handler tests here against real\n"
            "         containers, and this project has no handler to test. The fixture\n"
            "         reference, the DbContext reference and the provider package all\n"
            "         return with the first one. -->\n",
        ),
    ),
    "tests/Catalog.Application.Tests/DependencyInjectionTests.cs": (
        (
            "using Catalog.Application.Products.GetPrices;\n"
            "using Catalog.Application.Products.GetProducts;\n"
            "using Catalog.Application.Products.PublishProduct;\n",
            "",
        ),
        (
            "\n"
            "    [Fact]\n"
            "    public void AddCatalogApplication_registers_the_command_validator()\n"
            "    {\n"
            "        // ValidationBehavior takes IEnumerable<IValidator<T>>, so a missing\n"
            "        // scan is not a failure — it is a pipeline that validates nothing and\n"
            "        // says so to nobody. The registration is the only place to catch it.\n"
            "        ServiceCollection services = new();\n"
            "\n"
            "        services.AddCatalogApplication();\n"
            "\n"
            "        services.ShouldContain(\n"
            "            d => d.ServiceType == typeof(FluentValidation.IValidator<PublishProductCommand>),\n"
            "            \"AddValidatorsFromAssemblyContaining is §4.2's line, and losing it fails silently\");\n"
            "\n"
            "        // A query's validator, and it is not the same assertion twice: the scan\n"
            "        // is one call, but ValidationBehavior is unconstrained (§6.3), so a\n"
            "        // query validator lost this way disables the id-list ceiling that is\n"
            "        // GetPrices' only bound — and nothing else would notice.\n"
            "        services.ShouldContain(\n"
            "            d => d.ServiceType == typeof(FluentValidation.IValidator<GetPricesQuery>));\n"
            "    }\n"
            "\n"
            "    [Fact]\n"
            "    public void AddCatalogApplication_registers_the_slice_handlers()\n"
            "    {\n"
            "        // The §6.2 scan found nothing until PR-10; these are the registrations\n"
            "        // it produces, so the scan itself is testable. Every slice adds a row\n"
            "        // here — the scan is public-only, and a handler it misses registers as\n"
            "        // nothing at all rather than as something wrong.\n"
            "        ServiceCollection services = new();\n"
            "\n"
            "        services.AddCatalogApplication();\n"
            "\n"
            "        services.ShouldContain(d =>\n"
            "            d.ServiceType == typeof(ICommandHandler<PublishProductCommand, Result<Guid>>));\n"
            "        services.ShouldContain(d =>\n"
            "            d.ServiceType == typeof(IQueryHandler<GetProductsQuery, CursorPage<ProductSummaryDto>>));\n"
            "\n"
            "        // PR-19's third slice. The scan is public-only (§6.2), so an internal\n"
            "        // handler, a rename or a missed IQueryHandler<,> registers as nothing\n"
            "        // and fails on the first gRPC call rather than at startup —\n"
            "        // ValidateOnBuild never constructs the dispatcher's handler map.\n"
            "        // PricingServiceTests would catch it, but only in the Docker suite.\n"
            "        services.ShouldContain(d =>\n"
            "            d.ServiceType == typeof(IQueryHandler<GetPricesQuery, IReadOnlyList<ProductPriceDto>>));\n"
            "    }\n"
            "}\n",
            "\n"
            "    // Two tests are missing here, and they come back separately rather\n"
            "    // than together. The first handler of either kind earns the one that\n"
            "    // asserts the §6.2 scan produced a registration; the first validator\n"
            "    // earns the one that asserts the validator scan found it. Both scans\n"
            "    // fail silently when lost, which is why neither is left implicit —\n"
            "    // and a query-only slice needs the first and not the second.\n"
            "}\n",
        ),
    ),
    "tests/Catalog.TestSupport/Outbox/OutboxRows.cs": (
        ("using Common.Contracts.Catalog.V1;\n", ""),
        (
            "    /// <summary>\n"
            "    /// A Broker-lane row carrying a real contract, so the publish half of\n"
            "    /// <c>DeliverAsync</c> is exercised against the running broker rather than\n"
            "    /// inferred from the staging tests.\n"
            "    /// </summary>\n"
            "    public static OutboxMessage Broker(ServiceFixture fixture, Guid productId) =>\n"
            "        OutboxMessage.Stage(\n"
            "            new ProductPublished\n"
            "            {\n"
            "                MessageId = Guid.CreateVersion7(),\n"
            "                CorrelationId = productId,\n"
            "                OccurredAt = Raised,\n"
            "                ProductId = productId,\n"
            "                Name = \"Walnut desk\",\n"
            "                ThumbnailUrl = null,\n"
            "                Amount = 19.99m,\n"
            "                Currency = \"EUR\"\n"
            "            },\n"
            "            OutboxLane.Broker,\n"
            "            productId,\n"
            "            fixture.MessageTypes,\n"
            "            fixture.OutboxJson);\n"
            "\n",
            "    // A Broker-lane builder returns with this service's first contract,\n"
            "    // together with the dispatcher test that uses it: staging that lane\n"
            "    // needs a type Common.Contracts publishes on this service's behalf,\n"
            "    // and the allow-list mapper is empty until there is one (§9.3).\n"
            "\n",
        ),
    ),
    "tests/Catalog.Api.Tests/OutboxDispatcherTests.cs": (
        (
            "\n"
            "    [Fact]\n"
            "    public async Task A_broker_row_is_published_and_completed()\n"
            "    {\n"
            "        // The Broker half of DeliverAsync, against the real RabbitMQ the\n"
            "        // fixture runs. Everything else here exercises the Local lane, so\n"
            "        // without this a failure in payload deserialisation, type resolution\n"
            "        // or the publish call would ship while the staging tests and the\n"
            "        // direct-bus smoke both stayed green.\n"
            "        //\n"
            "        // What is asserted is that the row completed — not what reached the\n"
            "        // transport. §12.4 refuses the latter deliberately: observing the\n"
            "        // headers needs an ITestHarness, and this fixture runs the real host\n"
            "        // against the real broker on purpose. Publishing without throwing and\n"
            "        // marking the row processed is the part this suite owns.\n"
            "        await fixture.StageOutboxAsync(OutboxRows.Broker(fixture, Guid.CreateVersion7()));\n"
            "\n"
            "        (await fixture.ProcessOutboxBatchAsync()).ShouldBe(1);\n"
            "\n"
            "        OutboxMessage row = (await fixture.OutboxAsync()).ShouldHaveSingleItem();\n"
            "        row.Lane.ShouldBe(OutboxLane.Broker);\n"
            "        row.ProcessedAt.ShouldNotBeNull();\n"
            "        row.LastError.ShouldBeNull();\n"
            "    }\n",
            "\n"
            "    // Three tests return with this service's first contract, beside the\n"
            "    // OutboxRows.Broker builder they all need — this one, the Local\n"
            "    // lane's guard below, and OutboxTransportIdentityTests, which pins\n"
            "    // §9.1's single identity onto the transport. Until then the\n"
            "    // allow-list is empty and nothing can build a contract instance, so\n"
            "    // each would assert against a row no code here can produce.\n",
        ),
        (
            "\n"
            "    [Fact]\n"
            "    public async Task An_integration_event_on_the_local_lane_never_reaches_a_projection()\n"
            "    {\n"
            "        // The mirror, and the quieter of the two: ProjectionInvoker is\n"
            "        // generic and unconstrained, so without the guard a contract would be\n"
            "        // offered to any matching IProjectionHandler<T> and the row marked\n"
            "        // processed — no publish, no handler, no trace.\n"
            "        OutboxMessage row = OutboxRows.Broker(fixture, Guid.CreateVersion7());\n"
            "        await fixture.StageOutboxAsync(row);\n"
            "        await fixture.SetOutboxLaneAsync(row.MessageId, OutboxLane.Local);\n"
            "\n"
            "        await fixture.ProcessOutboxBatchAsync();\n"
            "\n"
            "        OutboxMessage failed = (await fixture.OutboxAsync()).ShouldHaveSingleItem();\n"
            "        failed.ProcessedAt.ShouldBeNull();\n"
            "        failed.LastError.ShouldNotBeNull().ShouldContain(nameof(IDomainEvent));\n"
            "    }\n",
            "",
        ),
        # The Broker-lane guard above keeps IIntegrationEvent in use; nothing
        # left names IDomainEvent once the test that did leaves, and an unused
        # using is a claim about a dependency that is not there.
        ("using Common.Domain;\n", ""),
    ),
    "tests/Catalog.TestSupport/ServiceFixture.cs": (
        (
            "    /// this context's <c>HasDefaultSchema</c>, and is no part of what PR-08\n"
            "    /// claims.\n",
            "    /// this context's <c>HasDefaultSchema</c>, and is no part of what this\n"
            "    /// fixture claims.\n",
        ),
        (
            "/// the engine. §12.4's name and §4.1's home: the fixture serves\n"
            "/// <c>Catalog.Application.Tests</c> and <c>Catalog.Api.Tests</c>, which\n"
            "/// cannot reference each other — each declares its own\n",
            "/// the engine. §12.4's name and §4.1's home: the fixture serves\n"
            "/// <c>Catalog.Api.Tests</c> today, and the application suite the moment that\n"
            "/// suite gains a handler test — the two cannot reference each other, so each\n"
            "/// declares its own\n",
        ),
    ),
    "tests/Catalog.Api.Tests/Catalog.Api.Tests.csproj": (
        # PricingServiceTests is Catalog's and does not travel, so the package
        # it named goes with it. A reference nothing in the rendered project
        # uses is the unused-dependency claim CLAUDE.md rules out, one file
        # type over.
        (
            "    <!-- GrpcChannel and Grpc.Core's StatusCode, which PricingServiceTests uses\n"
            "         to call the gRPC server over loopback. Carried transitively through\n"
            "         Catalog.Api; named here on the same honesty rule Web.Bff.Tests states,\n"
            "         because a project that names a type declares the package rather than\n"
            "         relying on a production csproj it does not control. -->\n"
            "    <PackageReference Include=\"Grpc.Net.ClientFactory\" />\n",
            "",
        ),
        # PR-26's linked contract, dropped with the verification suite that
        # compiles it. A rendered service keeps neither: the file is Web.Bff's
        # expectations of CATALOG, so a link to it from Inventory.Api.Tests
        # would compile a contract naming a hop that service does not serve —
        # and then fail to build, because PricingContract names the generated
        # pricing types the Protobuf item above already left with the .proto.
        (
            "  <ItemGroup>\n"
            "    <!-- PR-26's consumer-driven contract, LINKED rather than referenced — the\n"
            "         same relationship pricing.proto already has, one level up. The .proto\n"
            "         is Catalog's because Catalog serves the RPC; this file is Web.Bff's\n"
            "         because only a consumer can say what it needs, and it is compiled into\n"
            "         this suite so the provider can be held to it.\n"
            "\n"
            "         A FILE and not an assembly, so no project dependency is created and\n"
            "         §4.3 is untouched: Common.Contracts is still the only assembly that\n"
            "         crosses a service boundary, and a test helper is expressly not it —\n"
            "         which is why Gateway.Api.Tests carries its own copy of Catalog's\n"
            "         TestAuthHandler rather than referencing one.\n"
            "\n"
            "         The cost is a build-time path into another suite's tree, and unlike the\n"
            "         .proto it is paid once: no Dockerfile builds a test project, so there\n"
            "         is no COPY line to keep in step with it. -->\n"
            "    <Compile Include=\"..\\Web.Bff.TestSupport\\PricingContract.cs\""
            " Link=\"Contract\\PricingContract.cs\" />\n"
            "  </ItemGroup>\n"
            "\n",
            "",
        ),
        (
            "    <!-- ServiceFixture and CatalogApiFactory (§12.4, §4.1) — the containers,\n"
            "         the migrator runs and the reset live there, shared with\n"
            "         Catalog.Application.Tests, which this project cannot reference. -->\n",
            "    <!-- ServiceFixture and CatalogApiFactory (§12.4, §4.1) — the containers,\n"
            "         the migrator runs and the reset live there. The application suite\n"
            "         becomes the second consumer with its first handler test, and the\n"
            "         two cannot reference each other — which is why the fixture has a\n"
            "         project of its own. -->\n",
        ),
    ),
    "tests/Catalog.Api.Tests/ArchitectureTests.cs": (
        # The Domain anchor, twice: the whole-service gates need one type per
        # project and a scaffolded service has no aggregate to name, so both
        # sites take the marker the same way Catalog.Application.Tests does.
        # The gates themselves travel unchanged — every one of them is about
        # the shape of the reference graph, which an empty service has as much
        # as a full one.
        ("using Catalog.Domain.Products;\n", "using Catalog.Domain;\n"),
        (
            "        typeof(Product).Assembly,\n",
            "        typeof(AssemblyMarker).Assembly,\n",
        ),
        (
            "/// Vacuously green from PR-07 until PR-10's first endpoint — a rule\n"
            "/// introduced before the violations exist is a constraint, not a backlog\n"
            "/// item — and judging real types since.\n",
            "/// Vacuously green until this service maps its first endpoint: a rule\n"
            "/// introduced before the violations exist is a constraint, not a backlog\n"
            "/// item. The rule was observed failing against a deliberately added\n"
            "/// forbidden reference before it was trusted — in the service this one\n"
            "/// was scaffolded from, not here, where there is nothing yet to judge.\n",
        ),
        # The whole gate travels now, and that is the point of the shape it
        # arrived at: it selects the entire assembly and subtracts the
        # composition root from the FAILURES, so it says something true about a
        # host with no adapters at all. A namespace selector said nothing.
        #
        # Only the two adapter names leave, because they are the exemplar's.
        (
            "        exempted.ShouldContain(\"Program\");\n",
            "        exempted.ShouldContain(\"Program\");\n"
            "\n"
            "        // The other half of this assertion belongs with the first endpoint:\n"
            "        // that the gate is judging something. Until then Program is all\n"
            "        // there is, and naming an adapter that does not exist is not an\n"
            "        // assertion — see the service this one was scaffolded from.\n",
        ),
    ),
    "tests/Catalog.Api.Tests/HostSmokeTests.cs": (
        (
            "/// string has a readiness check and a host without one does not; Catalog\n"
            "/// acquired the SQL pair in PR-08 and the bus pair in PR-13, and both\n",
            "/// string has a readiness check and a host without one does not; this\n"
            "/// service has both pairs from its first commit, and both\n",
        ),
        # The production-scheme host survives the copy — every service wants
        # one — but two claims in its comment are Catalog's rather than the
        # mechanism's. "The one host in the repository" is false the moment a
        # second service is scaffolded, and EndpointSecurityTests is omitted
        # here, so the sentence naming what restoring the base call would
        # delete names a file the reader cannot find.
        (
            "        /// The one host in the repository that keeps the production JWT scheme.\n"
            "        /// Every other factory swaps in <c>TestAuthHandler</c>, which is what\n"
            "        /// lets those suites authenticate at all — and precisely why none of\n"
            "        /// them can say whether its headers mean anything to a real\n"
            "        /// deployment. A test scheme cannot prove its own absence.\n",
            "        /// This service's one host that keeps the production JWT scheme. Every\n"
            "        /// other factory swaps in <c>TestAuthHandler</c>, which is what lets\n"
            "        /// those suites authenticate at all — and precisely why none of them\n"
            "        /// can say whether its headers mean anything to a real deployment. A\n"
            "        /// test scheme cannot prove its own absence.\n",
        ),
        (
            "            // Deliberately empty. Not \"not yet\" — restoring the base call here\n"
            "            // would silently delete EndpointSecurityTests, which is the only\n"
            "            // suite that reads this host as a deployment rather than a fixture.\n",
            "            // Deliberately empty. Not \"not yet\" — this host is the only one\n"
            "            // that reads as a deployment rather than a fixture, and restoring\n"
            "            // the base call would silently take that with it. The forged-header\n"
            "            // suite that reads it arrives with the first endpoint to forge\n"
            "            // against.\n",
        ),
    ),
    "tests/Catalog.Api.Tests/TransientFaultInjection.cs": (
        (
            "/// of the retry defect is assertable before PR-10's first aggregate exists.\n",
            "/// of the retry defect is assertable before this service has an aggregate.\n",
        ),
    ),
    "tests/Catalog.TestSupport/CatalogApiFactory.cs": (
        # The override still matters — it is what a service's first endpoint
        # test will use — but the Catalog source names EndpointSecurityTests as
        # the suite that reads it, and the scaffold omits that file. A generated
        # comment pointing at a suite the service has not got is the same class
        # of false claim as one scheduling a landed PR, which is what the
        # Catalog text used to say and what GeneratedGuidanceIsTrue caught.
        (
            "    /// Virtual, and the one override matters. A host that keeps the production\n"
            "    /// scheme is the only thing that can prove <see cref=\"TestAuthHandler\"/>'s\n"
            "    /// headers mean nothing to a real deployment, which is what\n"
            "    /// <c>EndpointSecurityTests</c> reads it for. A flag would say the same\n"
            "    /// thing; a method says it at the site that makes the decision, which is\n"
            "    /// where the argument for it belongs.\n",
            "    /// Virtual, and the one override matters. A host that keeps the production\n"
            "    /// scheme is the only thing that can prove <see cref=\"TestAuthHandler\"/>'s\n"
            "    /// headers mean nothing to a real deployment, so it arrives with the first\n"
            "    /// endpoint there is anything to forge against. A flag would say the same\n"
            "    /// thing; a method says it at the site that makes the decision, which is\n"
            "    /// where the argument for it belongs.\n",
        ),
    ),
    "tests/Catalog.TestSupport/Catalog.TestSupport.csproj": (
        (
            "    other\". PR-08 gave the fixture one consumer; PR-10's handler tests are the\n"
            "    second, which is the condition §4.1 named for this project to exist.\n",
            "    other\". The API suite is its consumer today; the application suite becomes\n"
            "    the second with its first handler test, which is the condition §4.1 names\n"
            "    for this project to exist.\n",
        ),
    ),
    # Both of the entries below carry the same argument out of a copied file:
    # *why* Catalog binds no receive endpoint is a fact about Catalog's row in
    # §3.2, and a scaffolded service inherits the state without inheriting the
    # reason. The generic replacement says the rule the reason produced.
    "tests/Catalog.Api.Tests/MessagingRegistrationTests.cs": (
        (
            "        // Asserted rather than assumed, which is PR-14's shape one lane over:\n"
            "        // that PR asserted Catalog stages no Local row rather than leaving the\n"
            "        // absence to be inferred.\n"
            "        //\n"
            "        // §3.2 gives Catalog exactly one Consumes cell — StockLevelChanged,\n"
            "        // owned by Inventory, which does not exist. Even with the contract now\n"
            "        // present (PR-15), binding it would create an endpoint whose every\n"
            "        // message reaches §9.4's throw: \"the endpoint binds this type, so\n"
            "        // something should handle it\" is one of the two sites where an empty\n"
            "        // handler list must fail, and §8.4's cache invalidator — the handler\n"
            "        // that eventually arrives — needs a cached query to invalidate.\n",
            "        // Asserted rather than assumed: an absence nobody states is an absence\n"
            "        // nobody notices changing.\n"
            "        //\n"
            "        // A consumer belongs here once §3.2 gives this service something to\n"
            "        // consume and an IIntegrationEventHandler exists for it. Binding a\n"
            "        // type with no handler registered creates an endpoint whose every\n"
            "        // message reaches §9.4's throw: \"the endpoint binds this type, so\n"
            "        // something should handle it\" is one of the two sites where an empty\n"
            "        // handler list must fail rather than proceed.\n",
        ),
        (
            "            \"a consumer here is a subscription §3.2 does not give Catalog — and one bound with no \" +\n",
            "            \"a consumer here is a subscription §3.2 does not give this service — and one bound with no \" +\n",
        ),
    ),
    "tests/Catalog.Api.Tests/InboxFilterTests.cs": (
        (
            "/// Catalog binds no receive endpoint of its own (§3.2 gives it one Consumes\n"
            "/// cell, owned by a service that does not exist), so this suite declares the\n"
            "/// endpoints it needs rather than inventing a subscription §3.2 does not give\n"
            "/// it.\n",
            "/// This service binds no receive endpoint of its own yet, so this suite\n"
            "/// declares the endpoints it needs rather than inventing a subscription §3.2\n"
            "/// does not give it.\n",
        ),
    ),
    # §8.5's marker suite travels, and its anti-vacuity floor is INVERTED for
    # the reason IdempotencyOptInTests' three are: a rendered service opts no
    # command into idempotency, so a floor asserting the selector found one
    # fails on a tree that is perfectly correct. The three integration tests
    # above it need no patch at all — they drive a probe command through §6.3
    # with a key supplied by the test, which is wiring every service has.
    #
    # This is the FOURTH inversion, and it was owed the moment the gate was
    # written rather than discovered later. Every anti-vacuity floor added to a
    # template file is owed an entry here; that is the rule, and the way to
    # find out whether it was followed is to render a service and run its
    # tests.
    "tests/Catalog.Api.Tests/IdempotencyMarkerTests.cs": (
        (
            "    public async Task The_gate_above_is_looking_at_this_service_s_operation_names()\n",
            "    public async Task This_service_has_no_operation_names_for_the_gate_above_yet()\n",
        ),
        (
            "        Operations().ShouldNotBeEmpty(\n"
            '            "no command in this assembly declares IIdempotentCommand, so the '
            'width gate is " +\n'
            '            "looking at nothing — the interface has been renamed, moved, or '
            'not yet applied");\n',
            "        Operations().ShouldBeEmpty(\n"
            '            "This service opts no command into idempotency yet, so the width '
            'gate above is " +\n'
            '            "vacuous. The day it does, this test fails — replace it with the '
            'ShouldNotBeEmpty " +\n'
            '            "form, which is what keeps a vacuous gate from quietly becoming '
            'a permanent one.");\n',
        ),
    ),
    "tests/Catalog.Api.Tests/DatabaseSmokeTests.cs": (
        (
            "/// PR-08's deliverables against a real engine: the migrator applies the schema\n",
            "/// The persistence layer against a real engine: the migrator applies the schema\n",
        ),
        (
            "        // PR-10's first aggregate. Observed red against a Clear()-less\n",
            "        // this service has an aggregate. Observed red against a Clear()-less\n",
        ),
        (
            "        schema.ShouldBe(1, \"InitialCreate's hand-written EnsureSchema creates it; "
            "AddProducts' is a no-op after it\");\n"
            "\n"
            "        // Named and ordered, not merely counted: the migrator's job is to\n"
            "        // apply every migration in sequence, and a count alone would pass on\n"
            "        // a shorter prefix of them applied twice.\n"
            "        string[] applied = await fixture.AppliedMigrationsAsync();\n"
            "        applied.Length.ShouldBe(8);\n"
            "        applied[0].ShouldEndWith(\"_InitialCreate\");\n"
            "        applied[1].ShouldEndWith(\"_AddProducts\");\n"
            "        applied[2].ShouldEndWith(\"_AddOutbox\");\n"
            "        applied[3].ShouldEndWith(\"_AddInbox\");\n"
            "        applied[4].ShouldEndWith(\"_AddOutboxRetentionIndex\");\n"
            "        applied[5].ShouldEndWith(\"_AddIdempotencyMarkers\");\n"
            "        applied[6].ShouldEndWith(\"_IdempotencyMarkerCommittedAtDefault\");\n"
            "        applied[7].ShouldEndWith(\"_AddIdempotencyMarkerRowVersion\");\n",
            "        schema.ShouldBe(1, \"InitialCreate's hand-written EnsureSchema is what creates it\");\n"
            "\n"
            "        // Named and ordered, not merely counted: the migrator's job is to\n"
            "        // apply every migration in sequence, and a count alone would pass on\n"
            "        // a shorter prefix of them applied twice. What a scaffolded service\n"
            "        // starts with is the schema, then §9.4's outbox table, §9.5's inbox,\n"
            "        // the index the retention purge deletes through, and §8.5's marker\n"
            "        // table with the database clock it is aged by and the rowversion the\n"
            "        // purge identifies one of its rows by — all of them wiring every\n"
            "        // service has rather than anything this one chose.\n"
            "        string[] applied = await fixture.AppliedMigrationsAsync();\n"
            "        applied.Length.ShouldBe(7);\n"
            "        applied[0].ShouldEndWith(\"_InitialCreate\");\n"
            "        applied[1].ShouldEndWith(\"_AddOutbox\");\n"
            "        applied[2].ShouldEndWith(\"_AddInbox\");\n"
            "        applied[3].ShouldEndWith(\"_AddOutboxRetentionIndex\");\n"
            "        applied[4].ShouldEndWith(\"_AddIdempotencyMarkers\");\n"
            "        applied[5].ShouldEndWith(\"_IdempotencyMarkerCommittedAtDefault\");\n"
            "        applied[6].ShouldEndWith(\"_AddIdempotencyMarkerRowVersion\");\n",
        ),
    ),
}

# Keyed on the file's shape rather than on its path, because the path carries
# Catalog's migration timestamp — and a PATCHES key that stopped matching would
# fail *open*, silently leaving the file unpatched. `require_once` still binds
# each anchor.
INITIAL_CREATE_PATCHES: tuple[tuple[str, str], ...] = (
    (
        "/// Catalog's first migration. EF generated an empty <c>Up</c>, because the\n"
        "/// model had no entity types until PR-10 — the schema below is hand-written,\n",
        "/// This service's first migration. EF generates an empty <c>Up</c> for a model\n"
        "/// with no entity types, so the schema below is hand-written,\n",
    ),
    (
        "/// The schema is the one piece of Catalog's shape that exists before its first\n"
        "/// table, and creating it here means PR-10's first <c>CREATE TABLE</c> lands in\n"
        "/// a schema that is already there rather than being ordered against it.\n",
        "/// The schema is the one piece of Catalog's shape that exists before its first\n"
        "/// table, and creating it here means the first <c>CREATE TABLE</c> lands in a\n"
        "/// schema that is already there rather than being ordered against it.\n",
    ),
    (
        # The second PR-10 in this file, and it went out unpatched: a
        # scaffolded service inherited "the input to PR-10's migrations add",
        # which is Catalog's history and false everywhere else. Found by a
        # reviewer reading Ordering's rendered copy, one patch below the one
        # that had already neutralised the *first* PR-10 three lines up —
        # a reminder that a file with a patch table is not therefore a file
        # whose references have all been checked.
        "/// the analysers, and are left exactly as the tool wrote them: the snapshot is\n"
        "/// the input to PR-10's <c>migrations add</c>, and an edited one produces a\n"
        "/// wrong migration two PRs later.\n",
        "/// the analysers, and are left exactly as the tool wrote them: the snapshot is\n"
        "/// the input to the next <c>migrations add</c>, and an edited one produces a\n"
        "/// wrong migration the moment one is run.\n",
    ),
)

# The outbox migration's twin, shape-keyed for the same reason: its path carries
# Catalog's timestamp. Only the prose is patched — the DDL below it is the
# tool's own output and is what gives a scaffolded service its outbox table.
OUTBOX_MIGRATION_PATCHES: tuple[tuple[str, str], ...] = (
    (
        "/// §9.4's outbox table, generated from <see cref=\"OutboxMessageConfiguration\"/>\n"
        "/// on AddProducts' terms — the configuration is the source of truth and only\n"
        "/// this file's dress is hand-authored (file-scoped namespace, this comment,\n"
        "/// the field CA1861 asks for). The <c>.Designer.cs</c> and the snapshot beside\n"
        "/// it are machine-owned and untouched.\n",
        "/// §9.4's outbox table, generated from <see cref=\"OutboxMessageConfiguration\"/>\n"
        "/// — the configuration is the source of truth and only this file's dress is\n"
        "/// hand-authored (file-scoped namespace, this comment, the field CA1861 asks\n"
        "/// for). The <c>.Designer.cs</c> and the snapshot beside it are machine-owned\n"
        "/// and untouched.\n",
    ),
)

# And the inbox migration's, for the same reason again. Its remark argues why
# Catalog carries a table it never writes to, which is a fact about Catalog;
# the scaffolded service's copy states the general rule the argument produced.
INBOX_MIGRATION_PATCHES: tuple[tuple[str, str], ...] = (
    (
        "/// §9.5's inbox table, generated from <see cref=\"InboxMessageConfiguration\"/>\n"
        "/// on <c>AddOutbox</c>'s terms — the configuration is the source of truth and\n"
        "/// only this file's dress is hand-authored (file-scoped namespace, this\n"
        "/// comment). The <c>.Designer.cs</c> and the snapshot beside it are\n"
        "/// machine-owned and untouched.\n",
        "/// §9.5's inbox table, generated from <see cref=\"InboxMessageConfiguration\"/>\n"
        "/// — the configuration is the source of truth and only this file's dress is\n"
        "/// hand-authored (file-scoped namespace, this comment). The\n"
        "/// <c>.Designer.cs</c> and the snapshot beside it are machine-owned and\n"
        "/// untouched.\n",
    ),
    (
        "/// <b>The table ships to every service, including the ones that consume\n"
        "/// nothing.</b> Catalog binds no receive endpoint yet (§3.2 gives it one\n"
        "/// Consumes cell, owned by a service that does not exist), so nothing writes a\n"
        "/// row here — but <c>RetentionPurgeService</c> runs from first boot and deletes\n"
        "/// from every table it was given, and a purge against a table that is not there\n"
        "/// logs a failure every pass. That is the same argument that keeps\n"
        "/// <c>AddOutbox</c> in the scaffold's output, inverted: the dispatcher would\n"
        "/// fail a claim, this would fail a delete.\n",
        "/// <b>The table arrives before the first consumer, deliberately.</b> A service\n"
        "/// that binds no receive endpoint writes no row here — but\n"
        "/// <c>RetentionPurgeService</c> runs from first boot and deletes from every\n"
        "/// table it was given, and a purge against a table that is not there logs a\n"
        "/// failure every pass. That is the same argument that keeps the outbox\n"
        "/// migration here, inverted: the dispatcher would fail a claim, this would fail\n"
        "/// a delete.\n",
    ),
)

# The retention index's, shape-keyed like the two above. What is dropped is the
# prose about how the gap was found — a scaffolded service inherits the index
# without inheriting the review that noticed its absence.
RETENTION_INDEX_MIGRATION_PATCHES: tuple[tuple[str, str], ...] = (
    (
        "/// The index §9.4's retention purge deletes through, generated from\n"
        "/// <see cref=\"OutboxMessageConfiguration\"/> on <c>AddInbox</c>'s terms — the\n"
        "/// configuration is the source of truth and only this file's dress is\n"
        "/// hand-authored. The <c>.Designer.cs</c> and the snapshot beside it are\n"
        "/// machine-owned and untouched.\n",
        "/// The index §9.4's retention purge deletes through, generated from\n"
        "/// <see cref=\"OutboxMessageConfiguration\"/> — the configuration is the source\n"
        "/// of truth and only this file's dress is hand-authored. The\n"
        "/// <c>.Designer.cs</c> and the snapshot beside it are machine-owned and\n"
        "/// untouched.\n",
    ),
    (
        "/// <para>\n"
        "/// The inbox got its <c>IX_Inbox_HandledAt</c> when its purge was written and\n"
        "/// this one did not, which is the asymmetry a review caught. Filtered the other\n"
        "/// way for the same reason its twin is filtered: the purge never reads an\n"
        "/// unprocessed row, so the index stays the size of the undeleted backlog rather\n"
        "/// than of the table.\n"
        "/// </para>\n",
        "/// <para>\n"
        "/// Filtered the other way for the same reason its twin is filtered: the purge\n"
        "/// never reads an unprocessed row, so the index stays the size of the undeleted\n"
        "/// backlog rather than of the table.\n"
        "/// </para>\n",
    ),
)

# §8.5's marker table, shape-keyed like the three above. What is dropped is the
# paragraph naming the defect the marker closes: a scaffolded service inherits
# the table without inheriting the history of the race that was open from PR-09
# until it was written.
IDEMPOTENCY_MIGRATION_PATCHES: tuple[tuple[str, str], ...] = (
    (
        "/// <b>This table is the one place in the schema where a missing row is a\n"
        "/// correctness failure rather than a lost record.</b> The outbox and the inbox\n"
        "/// hold delivery state; a row here says a command committed, and it is what\n"
        "/// refuses the retry of an attempt whose commit landed and whose acknowledgement\n"
        "/// was lost. Without it §8.5's guarantee carries the exception it carried from\n"
        "/// PR-09 to this migration — at most one commit per key, <em>except</em> across\n"
        "/// a lost acknowledgement.\n"
        "/// <para>\n",
        "/// <b>This table is the one place in the schema where a missing row is a\n"
        "/// correctness failure rather than a lost record.</b> The outbox and the inbox\n"
        "/// hold delivery state; a row here says a command committed, and it is what\n"
        "/// refuses the retry of an attempt whose commit landed and whose acknowledgement\n"
        "/// was lost.\n"
        "/// <para>\n",
    ),
)

# The one file with no counterpart in Catalog, and it is written to be deleted.
ASSEMBLY_MARKER = """namespace Catalog.Domain;

/// <summary>
/// The <c>typeof</c> anchor §4.2's architecture gates need, and nothing else.
/// A gate that reasons about an assembly has to name a type inside it, and
/// this project has none until its first aggregate.
/// </summary>
/// <remarks>
/// Written to be deleted. When that aggregate lands, re-anchor
/// <c>ArchitectureTests</c> in <c>Catalog.Domain.Tests</c> and
/// <c>Catalog.Application.Tests</c> on it and remove this file. A marker is
/// what a service has before it has a domain; leaving one in place after the
/// first aggregate arrives means the gates are judging an empty type instead
/// of the model they exist to constrain.
/// </remarks>
public sealed class AssemblyMarker;
"""

# Anything left in the rendered tree fails the run. `production` and EF's own
# `ProductVersion` annotation are the two benign substrings, and they are
# removed before the search rather than excused after it.
#
# **Two searches, at two different moments, and the split is load-bearing.**
# The template token is looked for *after* the rename, with the requested name
# masked out, because a service may legitimately contain it — `CatalogSearch`.
# The slice token is looked for *before* the rename, because masking cannot
# help there: a service called `Product` would mask away every real leftover
# along with its own name and the render would call itself domain-neutral.
# Before the rename a `Product` is unambiguous, since the rename maps the
# template's casings and never the slice's.
# Both case-insensitive, because the two halves have to hold to the same
# standard: `SLICE_TOKEN` was not, so `PRODUCT_ENDPOINT` in a copied file
# passed a guard that rejects `ProductEndpoint`.
BENIGN = re.compile(r"production|productversion", re.IGNORECASE)
TEMPLATE_TOKEN = re.compile(re.escape(TEMPLATE), re.IGNORECASE)
SLICE_TOKEN = re.compile(r"roduct", re.IGNORECASE)

# Every anchored pattern here is applied with `fullmatch`, never `match`.
# Python's `$` matches at the end of the string *or just before a trailing
# newline*, so `NAME.match("Zulu\n")` and `MIGRATION_ID.match("20260809120000\n")`
# both succeeded — and the newline then went into a directory name, a file
# name and a C# namespace. `fullmatch` is the whole fix, and it is worth
# stating because the patterns look exhaustive on their own.
NAME = re.compile(r"^[A-Z][A-Za-z0-9]*$")

# The three casings, matched in one pass. Distinct strings under a
# case-sensitive match, so alternation order carries no meaning.
CASINGS = re.compile("|".join((TEMPLATE, TEMPLATE.lower(), TEMPLATE.upper())))

# Windows reserves these as file and directory base names, with or without an
# extension — so neither `src/Services/Con` nor `Con.Domain.csproj` can be
# created there. They pass every other check in this file: PascalCase, no
# template token, no collision. Without this they fail *inside* `apply()`,
# which is the one place the script promises not to.
# The service name becomes a database name and a schema name, and SQL Server's
# `sysname` is nvarchar(128). Past that the projects render happily and the
# first migration is the thing that fails — late, on a machine with a database
# attached, which is the worst place for this script to be wrong.
SQL_IDENTIFIER_LIMIT = 128

# SQL Server's own. `Database=Master` points the migrator at a system database
# instead of an isolated one, and `Sys` collides with the reserved schema —
# both from a name that passes every other check and fails, if it fails at all,
# against a live server.
SQL_RESERVED = frozenset({"MASTER", "MODEL", "MSDB", "TEMPDB", "SYS"})

WINDOWS_RESERVED = frozenset(
    {"CON", "PRN", "AUX", "NUL"}
    | {f"COM{port}" for port in range(1, 10)}
    | {f"LPT{port}" for port in range(1, 10)}
)

# A migration id is the timestamp EF generates, and it reaches a path. Anything
# else is both invalid metadata and, with a `..` in it, a write outside the
# service tree — from a flag whose whole purpose is to make a test repeatable.
MIGRATION_ID = re.compile(r"^\d{14}$")

# The three shapes EF puts in a migrations directory. Anything else there is
# somebody's addition, and the scaffold refuses rather than dropping it.
INITIAL_CREATE = re.compile(r"^\d{14}_InitialCreate(\.Designer)?\.cs$")
# The outbox table is wiring, not slice: §9.4 gives every service one, and a
# scaffolded service that carried the dispatcher without the table would log a
# failed claim twice a second from its first boot. So this migration is copied
# with InitialCreate rather than dropped with Catalog's model changes.
OUTBOX_MIGRATION = re.compile(r"^\d{14}_AddOutbox(\.Designer)?\.cs$")
# The inbox table travels for the mirror of the outbox's reason: §9.5 gives
# every service one, the retention purge runs from first boot and deletes from
# both, and a service that carried the purge without the table would log a
# failed delete every pass. Consuming nothing does not exempt it — Catalog
# itself consumes nothing and has the table for exactly this.
INBOX_MIGRATION = re.compile(r"^\d{14}_AddInbox(\.Designer)?\.cs$")
# The purge's index, and it travels for the same reason the tables do: the
# claim's index is filtered `WHERE ProcessedAt IS NULL` and so excludes every
# row the purge deletes. A service scaffolded without this one scans its whole
# outbox table hourly from its first boot — the same class of silent cost as a
# dispatcher with no table, and invisible for exactly as long as the table is
# small.
RETENTION_INDEX_MIGRATION = re.compile(r"^\d{14}_AddOutboxRetentionIndex(\.Designer)?\.cs$")
# §8.5's durable marker, and it travels on a stronger version of the inbox's
# argument. A service that protects no command yet writes no row here — but
# `RetentionPurgeService` deletes from this table from first boot, and
# `EfIdempotencyMarkerStore` reads it on the first command that does opt in. A
# service scaffolded without it fails a purge every hour and then fails the
# first idempotent command it is ever given, both against a table that is
# simply not there.
IDEMPOTENCY_MIGRATION = re.compile(r"^\d{14}_AddIdempotencyMarkers(\.Designer)?\.cs$")

# The marker's `CommittedAt` default (#167). It travels for the reason the
# table itself does: the column default and the SQL cutoff that reads it are
# two halves of one guarantee, so a service scaffolded with the table and
# without the default ages its markers on the writing pod's clock while the
# purge ages them on the server's — which is the skew this migration exists to
# remove, shipped to every new service by omission.
COMMITTED_AT_DEFAULT_MIGRATION = re.compile(
    r"^\d{14}_IdempotencyMarkerCommittedAtDefault(\.Designer)?\.cs$"
)

# The marker's `rowversion` (#173). It travels for the same reason the default
# above does, and the failure it prevents is louder: `RetentionPurgeService`
# names this column in both of its marker statements, so a service scaffolded
# without the migration does not merely age its markers wrongly — its purge
# raises `Invalid column name 'RowVersion'` on the first pass and the table
# grows for ever. The column and the statements that read it are one mechanism,
# and half of it is not shippable.
ROW_VERSION_MIGRATION = re.compile(
    r"^\d{14}_AddIdempotencyMarkerRowVersion(\.Designer)?\.cs$"
)
LATER_MIGRATION = re.compile(r"^\d{14}_\w+(\.Designer)?\.cs$")

# The migrations a scaffolded service starts with, in the order they are
# applied — which is the order their ids have to be generated in. A tuple
# rather than one named constant each, because every place below that cares
# needs the position rather than the name: the id is the base plus the index in
# minutes, and the snapshot is derived from the last one's designer.
#
# No count anywhere in this comment, and that is deliberate. It has said two,
# then three, then four inside one pull request, and each stale sentence
# survived alongside its replacement. The tuple is the count.
TEMPLATE_MIGRATIONS = (
    INITIAL_CREATE,
    OUTBOX_MIGRATION,
    INBOX_MIGRATION,
    RETENTION_INDEX_MIGRATION,
    IDEMPOTENCY_MIGRATION,
    COMMITTED_AT_DEFAULT_MIGRATION,
    ROW_VERSION_MIGRATION,
)

# The name each shape above is known by in a diagnostic, in the same order and
# beside it rather than spelt out where `classify` pairs the two. There the
# pairing was `zip(..., strict=True)`, which is a real guard raising the wrong
# exception: a tuple grown without its label gave a bare `ValueError`, past
# `main`'s `except ScaffoldError` and out as a traceback, from a script whose
# stated contract is one line on stderr and exit 1. Declared here the two are
# read in one place, and `classify` says which of them is short.
MIGRATION_LABELS = (
    "InitialCreate",
    "AddOutbox",
    "AddInbox",
    "AddOutboxRetentionIndex",
    "AddIdempotencyMarkers",
    "IdempotencyMarkerCommittedAtDefault",
    "AddIdempotencyMarkerRowVersion",
)

# A service key in a Compose file, at the model's own indent, and the marker
# that bounds one service's block in `.env.example` — the one file that still
# accumulates a block per service.
SERVICE_KEY = re.compile(r"^  ([A-Za-z0-9][A-Za-z0-9_-]*):$")
ENV_MARKER = re.compile(r"^# ([A-Za-z0-9]+)'s two §7\.1 keys")

# §14.1's Compose model is an index and one file per deployable unit, so a
# service's environment is a file this script CREATES rather than a block it
# splices into a file every other service also owns. That is the whole of what
# changed here: the index gains one line, the unit file is written whole, and
# two services being scaffolded at once no longer meet in one file.
#
# The index's `include:` list is the anchor, and it is read rather than
# assumed: an entry is two spaces, a dash, a space and a path relative to the
# compose directory. A list this script cannot find is a template that has
# moved, which is a refusal and never a guess.
COMPOSE_DIR = "deploy/compose"
COMPOSE_INDEX = f"{COMPOSE_DIR}/docker-compose.yml"
COMPOSE_UNITS = "services"
COMPOSE_TEMPLATE_UNIT = f"{COMPOSE_UNITS}/{TEMPLATE.lower()}.yml"
INCLUDE_ENTRY = re.compile(r"^  - (\S+)$")

# One service's `environment:` mapping in that same file, and the keys inside
# it — read back off the block this script has just rendered rather than off
# the template it was lifted from, because the collision below is something the
# rename creates. Indent is the whole selector: a mapping opens at the
# service's own level and its keys sit one level inside it, so `build:`'s
# nested pair, `depends_on:`'s entries and every comment are excluded by
# position rather than by a list of names to skip past.
ENVIRONMENT_BLOCK = re.compile(r"^    environment:$")
ENVIRONMENT_KEY = re.compile(r"^      ([A-Za-z0-9_]+):(?:\s|$)")

# Docker publishes 1–65535 and nothing else. §14.1 allocates 51xx by
# convention, which is a decision rather than a rule, so the guard is the
# protocol's limit and the convention stays in the documentation.
PORTS = range(1, 65536)

# §14.1 publishes every mapping on loopback: the credentials in that file are
# deliberate development defaults, so the interface is the control standing in
# front of them, and a scaffolded service that bound 0.0.0.0 would reopen the
# hole one service at a time. LOOPBACK is what the render emits and what the
# template is required to carry; HOST_IP is deliberately wider, because the
# collision check asks whether a port is taken and a port taken on some other
# interface is taken all the same.
LOOPBACK = "127.0.0.1"
HOST_IP = r"\d+\.\d+\.\d+\.\d+"


# §15.1's secret scan reads the WORKING TREE, so the tree a render leaves
# behind is a tree that gate judges — and it refuses a credential-shaped
# literal nobody has written down a reason for. Catalog's literals all have
# one, added by hand the day Catalog landed; a rendered service carries the
# same literals under its own name and therefore under its own fingerprints,
# and nothing was writing those (#161). So the render ends by running the real
# scanner over what it has just built.
SCAN_GATE = ".github/secret-scan/secret_scan.py"
# The allow-list is a DIRECTORY, one file per tree, each declaring the prefix
# it may suppress. So this script no longer appends to one file: it appends to
# the file covering each entry's path, and refuses an entry no file covers
# rather than inventing a file for it — a new tree is a decision, exactly as a
# new credential-shaped literal is.
SCAN_ALLOW_LIST = ".github/secret-scan/allowed"

# The repository this script was checked out of, as opposed to the one it has
# been pointed at. They are the same thing whenever `--repo-root` is left alone,
# and the distinction only matters where trust does — `load_scan_gate` compares
# the gate it is about to execute against the copy here.
TOOL_ROOT = Path(__file__).resolve().parents[2]

# Everything this script calls on the module it loads. Named so a file that
# imports cleanly and is not the gate fails here, against the path the caller
# supplied, rather than three frames later against a symbol.
SCAN_GATE_MEMBERS = ("COVERS", "RULES", "covers_path", "read_allowed", "scan_text")

# The heading width the allow-list's own section headers are padded to.
SCAN_HEADING_WIDTH = 76

# One row per finding the render is known to produce: the file, the rule that
# reports it, a marker naming WHICH literal on that line it is, and the
# sentence the entry carries. Written in the template's own casings and put
# through `Names.rename` like every other string here, so `Catalog` below
# reaches the file as the service being rendered.
#
# A ROW IS NOT A SECOND MATCHER, and the distinction is the whole reason this
# imports the gate rather than reproducing it. The scanner decides what a
# finding is and what its fingerprint is; a marker only tells two findings of
# one rule in one file apart. The Compose file needs that much: a service's
# database default and its broker URI are both `credential-assignment` on
# adjacent lines, and they take different sentences because they record
# different decisions.
#
# A MARKER IS NEVER A TEST OF WHOSE FINDING IT IS, and it used to be read as
# one. Nothing in a row names the service being rendered, `"" in line` is true
# of every line, and the line the scanner reports for a password hash carries
# the value with the account name above it — so a render whose allow-list had
# lost a *previous* service's entry matched that service's finding, wrote a
# sentence naming the service being rendered, and cleared it. A suppression
# arriving for a credential the run is not writing is exactly what the
# allow-list's header says must never happen. `update_allowed_secrets`
# subtracts the findings the file already had, which settles ownership before
# a marker is consulted.
#
# EVERY MARKER IS NON-EMPTY, AND THAT IS ENFORCED RATHER THAN OBSERVED. Four
# of these rows carried `""` on the argument that the subtraction above made
# them safe, and that argument does not reach them: subtraction is defined
# over a path's ORIGINAL, so it protects the shared files in `updated` and
# nothing in `created`, which is where three of the four live. A rendered
# `HostSmokeTests.cs` gaining a SECOND `connection-string-password` literal
# would have been handed this row's sentence — a reason written about a
# different credential, which is the invented suppression the paragraph below
# refuses, arriving through the one path that skipped the refusal. So the
# emptiness is now a refusal of its own, because a row is a decision and `""`
# is the spelling of not having taken one.
#
# A NON-EMPTY MARKER IS STILL ONLY A NARROWING, so the run also checks what
# each row actually explained: one row that accounts for two findings with
# different fingerprints has not discriminated between them, whatever it
# matched on. That check is the subject-of-the-gate one — it reads what the
# table selected rather than trusting the table — and it is what catches a
# marker that is loose without being empty.
#
# THE COMPOSE MARKERS COME FROM THE VALUE, NOT THE KEY, and that is the second
# half of the same lesson. `ConnectionStrings__Catalog` renames to
# `ConnectionStrings__<Name>`, which is a substring of the broker line's
# `ConnectionStrings__RabbitMq` whenever the name is a prefix of `RabbitMq` —
# `R`, `Ra`, up to `RabbitMq` itself, eight legal PascalCase names that cleared
# every other precondition and then refused the run with a message blaming the
# template. A database name and a URI scheme are in the value, where no service
# name reaches them.
#
# A FINDING NO ROW EXPLAINS REFUSES THE RUN, on `classify`'s argument one
# function over. A reason this script invented would be a suppression nobody
# wrote, which is the one thing the allow-list's own header says it must never
# hold — so a new credential-shaped literal in the template is a decision this
# script forces, exactly as a new file there is.
SCAN_REASONS = (
    (
        "tests/Catalog.Api.Tests/HostSmokeTests.cs",
        "connection-string-password",
        # The value, on the compose rows' argument. It says which literal this
        # sentence is about, so a second password in this file refuses the run
        # rather than inheriting a reason written for this one.
        "not-a-real-password",
        "A deliberately unusable value in a host that must not reach a database.",
    ),
    (
        "tests/Catalog.Api.Tests/MessageTypeMapValidatorTests.cs",
        "connection-string-password",
        "not-a-real-password",
        "The same unusable fixture in the validator suite.",
    ),
    (
        "tests/Catalog.Api.Tests/MessagingRegistrationTests.cs",
        "credential-assignment",
        # The URI scheme, exactly as the compose broker row uses it.
        "amqp://",
        "A broker URI pointing at a hostname that does not resolve.",
    ),
    (
        # The service's own Compose unit, which a render CREATES. Every finding
        # in it is therefore new — there is no previous copy of this path for
        # `update_allowed_secrets` to subtract — so the rows below cover the
        # whole block and not merely the two literals the spliced version
        # added to a file that already had the rest.
        "deploy/compose/services/catalog.yml",
        "connection-string-password",
        # The keyword, not the value. A marker naming the password itself would
        # make this script a second place §14.1's local default is written, and
        # a credential in two places is a credential nobody can retire — the
        # allow-list's own header, applied to the table that feeds it.
        "Password=",
        "Section 14.1's local database default, nested inside this service's "
        "two connection defaults.",
    ),
    (
        "deploy/compose/services/catalog.yml",
        "credential-assignment",
        # The database segment of the connection string, which the migrator's
        # line and the API's both carry — one value, one fingerprint, one
        # entry, one sentence. Not the `ConnectionStrings__` key: that renames
        # into a substring of the broker line for eight legal service names.
        "Database=Catalog",
        "Catalog's local connection default, in-cluster hostname.",
    ),
    (
        "deploy/compose/services/catalog.yml",
        "credential-assignment",
        # The URI scheme, for the same reason. No service name renames into
        # it, and no connection string carries it.
        "amqp://",
        "Section 14.1's broker default for Catalog, the per-service account "
        "that replaced guest.",
    ),
    (
        "deploy/compose/services/catalog.yml",
        "credential-assignment",
        # The host, which appears in the value and never in the key: the key is
        # `ConnectionStrings__RedisCache`, with no hyphen and no colon.
        "redis-cache:",
        "A Redis cache endpoint. Host and port only, no credential in it.",
    ),
    (
        "deploy/compose/services/catalog.yml",
        "credential-assignment",
        "redis-coordination:",
        "A Redis coordination endpoint. Host and port only, no credential.",
    ),
    (
        "deploy/compose/rabbitmq/definitions.json",
        "credential-assignment",
        # The one row whose marker is a KEY rather than a value, and the
        # exception is narrow rather than a softening: the hash is computed per
        # service, so no literal this table could carry would match the line
        # the render writes. `password_hash` is safe where
        # `ConnectionStrings__Catalog` was not, because it carries no service
        # name for `rename` to turn into a prefix of a neighbouring key.
        "password_hash",
        "The Catalog service account's password hash, Section 14.1's local default.",
    ),
)


class ScaffoldError(Exception):
    """A precondition the script will not write over."""


@dataclass(frozen=True)
class Names:
    """The three casings every rename needs."""

    pascal: str

    @property
    def lower(self) -> str:
        return self.pascal.lower()

    @property
    def upper(self) -> str:
        return self.pascal.upper()

    def rename(self, text: str) -> str:
        """One pass over the three casings, never three passes.

        Chained `str.replace` calls feed each replacement to the next, and a
        name that contains a later casing of the template token is rewritten
        twice: `CATALOGSearch` turned a source `Catalog` into
        `CATALOGSEARCHSearch`, because pass one produced text that pass three
        then matched. A single alternation cannot re-enter its own output.
        """
        return CASINGS.sub(
            lambda match: {
                TEMPLATE: self.pascal,
                TEMPLATE.lower(): self.lower,
                TEMPLATE.upper(): self.upper,
            }[match.group(0)],
            text,
        )


@dataclass
class Plan:
    """Everything the run would write, before any of it is written."""

    created: dict[str, str] = field(default_factory=dict)
    updated: dict[str, str] = field(default_factory=dict)


def read(repo_root: Path, relative: str) -> tuple[str, str]:
    """The file with LF endings, and the endings it actually had.

    **The template does not have one line ending, and which files have which
    depends on the platform.** `.gitattributes` forces `*.cs text eol=crlf`, so
    C# is CRLF everywhere; every other file here — `.csproj`, `.slnx`, the
    Compose YAML, the Markdown, the Dockerfiles — carries no attribute, so it
    checks out CRLF on Windows and LF on the Ubuntu runner. Anchors written
    with CRLF therefore match on a developer machine and match nothing in CI,
    which is exactly how this was found. Every anchor in this file is spelt
    with LF and matched against normalised text; the file's own endings go back
    on by `restore` on the way out.
    """
    raw = (repo_root / relative).read_bytes()
    text = raw.decode("utf-8-sig")
    # Escaped, never the character itself: a U+FEFF sitting invisibly inside a
    # string literal is one "strip the BOM" editor command away from silently
    # changing what this script writes.
    if raw.startswith(b"\xef\xbb\xbf"):
        text = "\ufeff" + text

    newline = "\r\n" if "\r\n" in text else "\n"
    return text.replace("\r\n", "\n"), newline


def restore(text: str, newline: str) -> str:
    """Put a file's own line endings back on rendered text."""
    return text if newline == "\n" else text.replace("\n", newline)


def require_once(text: str, needle: str, where: str) -> None:
    """Assert an anchor is there exactly once, or refuse the whole run."""
    count = text.count(needle)
    if count != 1:
        first = needle.splitlines()[0] if needle else repr(needle)
        raise ScaffoldError(
            f"{where}: expected exactly one occurrence of\n"
            f"    {first}\n"
            f"  found {count}. The template has moved; reconcile "
            f"tools/new-service/new_service.py with it."
        )


def classify(repo_root: Path) -> list[str]:
    """The template files to copy, and a refusal if any is unclassified."""
    discovered: list[str] = []
    for root in COPY_ROOTS:
        for path in sorted((repo_root / root).rglob("*")):
            if not path.is_file():
                continue
            relative = PurePosixPath(path.relative_to(repo_root).as_posix())
            if "bin" in relative.parts or "obj" in relative.parts:
                continue
            discovered.append(str(relative))

    copied: list[str] = []
    for relative in discovered:
        if relative.startswith(MIGRATIONS + "/"):
            # Migration file names carry a timestamp, so they are classified by
            # shape rather than by name: a scaffolded service starts at
            # InitialCreate — the hand-written EnsureSchema of §7.4 — and every
            # later migration, and the snapshot, belongs to Catalog's model.
            #
            # Three shapes and no others. An unconditional `continue` here
            # treated *anything* in this directory as classified, so a helper
            # or a README added beside the migrations would be dropped without
            # the guard below ever seeing it — the one directory where the
            # scaffold's "it will not guess" promise silently did not hold.
            name = PurePosixPath(relative).name
            if any(shape.fullmatch(name) for shape in TEMPLATE_MIGRATIONS):
                copied.append(relative)
            elif LATER_MIGRATION.fullmatch(name) or name == f"{TEMPLATE}DbContextModelSnapshot.cs":
                pass
            else:
                raise ScaffoldError(
                    f"{relative} is not a migration, a designer file or the model snapshot. "
                    f"Classify it in new_service.py — the scaffold will not guess."
                )
            continue
        if relative in COPIED:
            copied.append(relative)
        elif relative not in OMITTED:
            raise ScaffoldError(
                f"{relative} is not classified. Add it to COPIED if every service "
                f"needs it, or to OMITTED if it belongs to Catalog's slice — "
                f"the scaffold will not guess."
            )

    # Each pair counted separately, one shape at a time. A single total over
    # the whole directory would be satisfied by duplicates of one migration and
    # none of another — which is precisely the state that ships a dispatcher
    # with no table behind it, or a purge with no index.
    #
    # The pairing is checked before it is used, and `strict=True` is what this
    # replaces: it caught the same mistake and raised `ValueError`, which
    # `main` does not catch, so growing TEMPLATE_MIGRATIONS without its label
    # ended the run in a traceback naming neither constant.
    if len(TEMPLATE_MIGRATIONS) != len(MIGRATION_LABELS):
        raise ScaffoldError(
            f"TEMPLATE_MIGRATIONS has {len(TEMPLATE_MIGRATIONS)} shape(s) and "
            f"MIGRATION_LABELS has {len(MIGRATION_LABELS)} name(s). A migration was "
            f"added to one and not the other; the shorter one is the edit that is "
            f"missing."
        )
    for shape, label in zip(TEMPLATE_MIGRATIONS, MIGRATION_LABELS, strict=True):
        pair = [p for p in copied if shape.fullmatch(PurePosixPath(p).name)]
        if len(pair) != 2:
            raise ScaffoldError(
                f"expected {label}.cs and {label}.Designer.cs under "
                f"{MIGRATIONS}, found {len(pair)}"
            )

    # The mirror of the check above, and the half that was missing. A file
    # *added* to Catalog stops the run; a file *deleted* from it did not — the
    # loop simply never saw it, so its patches never ran and `plan` succeeded
    # with a service missing a piece. That is the fail-open shape this whole
    # script is built to avoid, on the manifest itself.
    if (deleted := COPIED - set(discovered)):
        raise ScaffoldError(
            "the template no longer has: "
            + ", ".join(sorted(deleted))
            + ". Remove them from COPIED — with their PATCHES entries — or restore them."
        )

    # And no patch may be inert. A PATCHES key for a file that is not copied
    # never reaches `require_once`, so the anchor it guards would be unbound
    # while every other anchor still looked enforced.
    if (inert := set(PATCHES) - set(copied)):
        raise ScaffoldError(
            "PATCHES names files the scaffold does not copy: "
            + ", ".join(sorted(inert))
            + ". A patch that never runs is an anchor that guards nothing."
        )
    return copied


SLICE_ENTITY = f'            modelBuilder.Entity("{TEMPLATE}.Domain.Products.Product", b =>\n'
# The leading newline matters: without it this matches inside a nested block's
# deeper closer, because sixteen spaces then `});` is a substring of
# twenty-four spaces then `});`. The ComplexProperty block inside Catalog's
# aggregate is exactly that shape, so the removal stopped halfway and left the
# entity's own tail behind — caught by the check at the end of the function,
# which is the reason that check is there rather than trusted away.
ENTITY_END = "\n                });\n\n"


def without_slice_entity(designer: str) -> str:
    """The model body with Catalog's aggregate removed, and nothing else touched.

    A scaffolded service has the outbox entity and no aggregate, so the model
    EF would describe for it is exactly Catalog's minus one `Entity(...)`
    block. Removing that block is the one edit made to a machine-owned file
    here, and it is anchored at both ends rather than parsed: the opening line
    is exact and unique, and the closing `});` at that indent is the first one
    after it. Everything else — property order, annotations, the `using` block
    — stays byte-for-byte what the tool wrote.

    The alternative was to keep deriving from `InitialCreate.Designer.cs`,
    which describes an empty model. That stopped being the truth when the
    outbox joined the template: the snapshot would omit an entity the
    `DbContext` maps, and the first `migrations add` in a scaffolded service
    would generate a second `CreateTable` for a table its own InitialCreate had
    already created.
    """
    require_once(designer, SLICE_ENTITY, "the outbox migration's designer")
    start = designer.index(SLICE_ENTITY)

    end = designer.find(ENTITY_END, start)
    if end == -1:
        raise ScaffoldError(
            "the slice entity block in the designer has no closing `});` at its own indent"
        )

    stripped = designer[:start] + designer[end + len(ENTITY_END):]

    # The aggregate took a using with it. EF emits
    # `using System.Collections.Generic;` for a ComplexProperty mapped as a
    # Dictionary<string, object>, which is how §5.3's Money reaches the model —
    # so with the entity gone the using is unreferenced, and EF would not have
    # written it. Guarded rather than assumed: if any Dictionary< survives the
    # removal the using is still earning its place and stays.
    #
    # Found by diffing against the tool, which is the only way it could be:
    # the scaffolded service built and its migration produced an empty Up, and
    # the sole difference from EF's own rewritten snapshot was this line.
    dictionary_using = "using System.Collections.Generic;\n"
    if "Dictionary<" not in stripped and dictionary_using in stripped:
        stripped = stripped.replace(dictionary_using, "", 1)

    # Masked, like the render loop's own check: EF stamps a ProductVersion
    # annotation on every model it describes, and that token is nobody's
    # aggregate.
    if SLICE_TOKEN.search(BENIGN.sub("", stripped)) is not None:
        raise ScaffoldError(
            "the designer still names the slice after its entity block was removed — "
            "Catalog has gained a second entity, and the scaffold will not guess which"
        )
    return stripped


def snapshot_from_designer(designer: str, migration_id: str, migration: str) -> str:
    """The model snapshot, from the tool's own description of the same model.

    Catalog's snapshot cannot be copied — it describes `Product`, and the next
    `migrations add` in a service that has no such entity would generate a
    drop. Writing one by hand would break the rule that machine-owned files are
    left exactly as the tool wrote them. The *last* template migration's
    designer resolves both: it already holds EF's description of a model with
    both messaging entities in it, which is what a scaffolded service has once
    `without_slice_entity` has taken the aggregate out, so the class wrapper is
    rewritten and the model body is never retyped.

    The last one, and taking an earlier one would be wrong in a way with no
    symptom until the service's first `migrations add`: the outbox designer
    knows nothing of the inbox, so the snapshot would omit a table the
    `DbContext` maps and EF would generate a second `CreateTable` for one the
    scaffolded migrations had already created.

    The designer reaching here has already had the aggregate removed — the
    render loop does that before its slice check, so that a failed removal
    stops the run rather than reaching a shipped file. Stripping again here
    would find nothing and `require_once` would say so.

    `migration` is that last migration's class name, passed in rather than
    written here. It was a literal — `AddOutboxRetentionIndex` — until §8.5's
    marker table became the last entry in TEMPLATE_MIGRATIONS: the literal then
    named the second-to-last migration, every anchor below missed at once, and
    the run stopped. Loudly, which was luck.

    **What deriving it bought is narrow and worth stating narrowly**: this
    function no longer memorises a migration name, so it is not one of the
    places a sixth migration has to be edited. It is not the only place — the
    shape regex, TEMPLATE_MIGRATIONS, MIGRATION_LABELS, `without_slice_entity`
    and the patch dispatch all name the new one, and an earlier draft of this
    paragraph claimed the tuple was the whole edit. It was not, and a docstring
    that undercounts the work is how the next entry lands half-applied.
    """
    text = designer
    for needle, replacement in (
        ("using Microsoft.EntityFrameworkCore.Migrations;\n", ""),
        (f'    [Migration("{migration_id}_{migration}")]\n', ""),
        (
            f"    partial class {migration}\n",
            "    partial class CatalogDbContextModelSnapshot : ModelSnapshot\n",
        ),
        ("        /// <inheritdoc />\n", ""),
        (
            "        protected override void BuildTargetModel(ModelBuilder modelBuilder)\n",
            "        protected override void BuildModel(ModelBuilder modelBuilder)\n",
        ),
    ):
        require_once(text, needle, f"{migration}.Designer.cs")
        text = text.replace(needle, replacement)
    return text


def sort_usings(text: str) -> str:
    """Re-sort the leading `using` block, which the rename can reorder.

    EF writes the block sorted, and where the template's namespace sorts is not
    where the new service's does — `Catalog` comes before `Microsoft` and
    `Ordering` comes after it. Sorting after the rename is what keeps the file
    byte-identical to what the next `dotnet ef migrations add` in that service
    would write, so the first real migration produces no spurious diff. Checked
    against the tool rather than assumed: the difference is how it was found.
    """
    lines = text.splitlines(keepends=True)
    first = next((i for i, line in enumerate(lines) if line.startswith("using ")), None)
    if first is None:
        raise ScaffoldError("a machine-owned migration file with no using block")

    last = first
    while last < len(lines) and lines[last].startswith("using "):
        last += 1

    # Keyed on the namespace, not on the whole line: `;` sorts after `.`, so a
    # plain line sort puts Microsoft.EntityFrameworkCore.Infrastructure ahead
    # of Microsoft.EntityFrameworkCore and disagrees with the tool. Also found
    # by diffing against it.
    #
    # System first, which is the other half of the tool's order and did not
    # show until the outbox designer arrived: until then the only usings were
    # Microsoft.* and the service's own, and a plain sort happened to agree.
    # EF writes `using System;` and `using System.Collections.Generic;` above
    # everything else, so a service whose name sorts before `System` — every
    # one of them, since these are the only two — would otherwise get a block
    # the next `migrations add` immediately rewrites.
    def namespace(line: str) -> tuple[int, str]:
        name = line[len("using "):].strip().rstrip(";")
        return (0 if name == "System" or name.startswith("System.") else 1, name)

    lines[first:last] = sorted(lines[first:last], key=namespace)
    return "".join(lines)


def next_migration_id(migration_id: str, minutes: int = 1) -> str:
    """The id `minutes` after the given one, keeping EF's 14-digit shape.

    A plain `int(...) + 1` is wrong on every boundary the format has: second 59
    rolls into 60, and so do minute, hour and month. Parsed and re-formatted
    instead, which is the only arithmetic that is right for all of them.

    MIGRATION_ID accepts any fourteen digits, which is the right shape check
    and not a calendar one — `20261301000000` passes it and is month thirteen.
    `strptime` is what notices, and its ValueError is not a ScaffoldError, so
    without this the CLI printed a traceback where every other refusal prints
    one line. OverflowError joins it for the year-9999 end of the range, where
    adding a minute leaves what `datetime` can represent.
    """
    try:
        stamp = datetime.strptime(migration_id, "%Y%m%d%H%M%S") + timedelta(minutes=minutes)
    except (ValueError, OverflowError) as error:
        raise ScaffoldError(
            f"--migration-id {migration_id} is fourteen digits but not a timestamp: {error}"
        ) from error

    return stamp.strftime("%Y%m%d%H%M%S")


def render_projects(repo_root: Path, names: Names, migration_id: str) -> dict[str, str]:
    """The nine projects, the marker, the migration and its snapshot."""
    created: dict[str, str] = {}
    csharp_newline = ""

    for relative in classify(repo_root):
        text, newline = read(repo_root, relative)
        if relative.endswith(".cs"):
            csharp_newline = newline

        patches = PATCHES.get(relative, ())
        if PurePosixPath(relative).name.endswith("_InitialCreate.cs"):
            patches = (*patches, *INITIAL_CREATE_PATCHES)
        elif PurePosixPath(relative).name.endswith("_AddOutbox.cs"):
            patches = (*patches, *OUTBOX_MIGRATION_PATCHES)
        elif PurePosixPath(relative).name.endswith("_AddInbox.cs"):
            patches = (*patches, *INBOX_MIGRATION_PATCHES)
        elif PurePosixPath(relative).name.endswith("_AddOutboxRetentionIndex.cs"):
            patches = (*patches, *RETENTION_INDEX_MIGRATION_PATCHES)
        elif PurePosixPath(relative).name.endswith("_AddIdempotencyMarkers.cs"):
            patches = (*patches, *IDEMPOTENCY_MIGRATION_PATCHES)
        for needle, replacement in patches:
            require_once(text, needle, relative)
            text = text.replace(needle, replacement)

        # The outbox designer describes Catalog's whole model, aggregate
        # included. Stripped here rather than further down, because the slice
        # check immediately below is exactly the check that should see the
        # result — a Product block surviving the removal must stop the run, not
        # reach the file the service ships.
        # Every designer, not only the last one. Each describes the model as of
        # its own migration and each therefore carries Catalog's aggregate, so
        # leaving the earlier one alone would ship a service a designer that
        # claims a table it never creates — and would trip the slice check
        # below, which is the guard that made this obvious.
        if PurePosixPath(relative).name.endswith(
            (
                "_AddOutbox.Designer.cs",
                "_AddInbox.Designer.cs",
                "_AddOutboxRetentionIndex.Designer.cs",
                "_AddIdempotencyMarkers.Designer.cs",
                "_IdempotencyMarkerCommittedAtDefault.Designer.cs",
                "_AddIdempotencyMarkerRowVersion.Designer.cs",
            )):
            text = without_slice_entity(text)

        # Before the rename, where a slice token means only itself. Doing this
        # after it — with the requested name masked, as the template check
        # must be — would let a service called `Product` mask away the very
        # leftovers this looks for.
        if (slice_token := SLICE_TOKEN.search(BENIGN.sub("", text))) is not None:
            line = BENIGN.sub("", text)[: slice_token.start()].count("\n") + 1
            raise ScaffoldError(
                f"{relative}:{line}: the slice survived. This file names "
                f"Product somewhere the patches do not reach."
            )
        if SLICE_TOKEN.search(relative) is not None:
            raise ScaffoldError(f"{relative}: the path itself names the slice")

        target = relative
        rendered = names.rename(text)
        if relative.startswith(MIGRATIONS + "/"):
            name = PurePosixPath(relative).name
            template_id = name.split("_", 1)[0]

            # One id per template migration, and the order between them is the
            # order they are applied in — EF sorts by this prefix, so a service
            # whose outbox table were ordered before its schema would fail on
            # the first run. A minute apart, spaced by position in
            # TEMPLATE_MIGRATIONS rather than by name, so the next one added is
            # an entry in that tuple and no arithmetic here.
            #
            # No count in this comment on purpose. It has said two, then three,
            # then four inside one pull request, and the stale sentences stacked
            # rather than being replaced — three contradictory claims about the
            # same tuple, which is what a review caught. The tuple is the count.
            offset = next(
                index for index, shape in enumerate(TEMPLATE_MIGRATIONS) if shape.fullmatch(name)
            )
            new_id = next_migration_id(migration_id, offset) if offset else migration_id
            target = f"{MIGRATIONS}/{name.replace(template_id, new_id, 1)}"
            text = text.replace(template_id, new_id)
            rendered = names.rename(text)
            if name.endswith(".Designer.cs"):
                rendered = sort_usings(rendered)
                if offset == len(TEMPLATE_MIGRATIONS) - 1:
                    # Only the last migration's designer describes the model
                    # the service ends up with, and the snapshot is a
                    # description of exactly that.
                    snapshot = names.rename(
                        snapshot_from_designer(
                            text,
                            new_id,
                            name.split("_", 1)[1].removesuffix(".Designer.cs")))
                    created[names.rename(f"{MIGRATIONS}/{TEMPLATE}DbContextModelSnapshot.cs")] = (
                        restore(sort_usings(snapshot), newline)
                    )

        created[names.rename(target)] = restore(rendered, newline)

    # The marker is the only file with no template beside it to take endings
    # from, so it takes the ones the template's own C# has. Observed rather
    # than assumed: `.gitattributes` decides this, and reading it here means a
    # change to that rule carries into generated code without a second edit.
    if not csharp_newline:
        raise ScaffoldError("no C# file in the template to take line endings from")

    created[names.rename(f"src/Services/{TEMPLATE}/{TEMPLATE}.Domain/AssemblyMarker.cs")] = (
        restore(names.rename(ASSEMBLY_MARKER), csharp_newline)
    )
    return created


def update_solution(repo_root: Path, names: Names) -> str:
    """Five projects in their own solution folder, four test entries, alphabetical."""
    text, newline = read(repo_root, "Platform.slnx")
    lines = text.splitlines(keepends=True)

    folder = [
        f'  <Folder Name="/src/Services/{names.pascal}/">\n',
        *(
            f'    <Project Path="src/Services/{names.pascal}/{names.pascal}.{layer}'
            f'/{names.pascal}.{layer}.csproj" />\n'
            for layer in ("Api", "Application", "Domain", "Infrastructure", "Migrator")
        ),
        "  </Folder>\n",
    ]

    service_folder = re.compile(r'^  <Folder Name="/src/Services/([^/]+)/">')
    existing = [(i, m.group(1)) for i, line in enumerate(lines) if (m := service_folder.match(line))]
    if not existing:
        raise ScaffoldError("Platform.slnx has no /src/Services/<service>/ folder to insert beside")

    at = len(lines)
    for index, service in existing:
        if service > names.pascal:
            at = index
            break
    else:
        last, _ = existing[-1]
        at = lines.index("  </Folder>\n", last) + 1
    lines[at:at] = folder

    tests = [
        f'    <Project Path="tests/{names.pascal}.{suite}/{names.pascal}.{suite}.csproj" />\n'
        for suite in ("Api.Tests", "Application.Tests", "Domain.Tests", "TestSupport")
    ]
    entry = re.compile(r'^    <Project Path="tests/([^"]+)" />')
    positions = [(i, m.group(1)) for i, line in enumerate(lines) if (m := entry.match(line))]
    if not positions:
        raise ScaffoldError("Platform.slnx has no /tests/ project entries to insert beside")

    for line in reversed(tests):
        path = entry.match(line).group(1)
        at = next((i for i, existing_path in positions if existing_path > path), positions[-1][0] + 1)
        lines.insert(at, line)
        positions = [(i, m.group(1)) for i, l in enumerate(lines) if (m := entry.match(l))]

    return restore("".join(lines), newline)


def environment_keys(block: str) -> list[list[str]]:
    """The mapping keys of every `environment:` block, one list per mapping.

    **Per mapping and never one flat set**, because §14.1's pair rule renders
    two services and each declares its own: a key appearing in both is two
    containers agreeing about a variable, which is ordinary, while the same key
    twice in one mapping is a service saying one thing twice, which is the
    defect. Flattening the two would report the first as a collision and lose
    the second in the noise.

    **Returned rather than judged, so that what this reads is testable.** A
    duplicate check is only as good as the keys handed to it, and a pattern
    that stops matching the template's shape hands it nothing — over which
    every name there is passes. That is this repository's most-repeated
    failure, so the extraction is a value a test can assert about instead of a
    step buried inside the caller.
    """
    mappings: list[list[str]] = []
    keys: list[str] | None = None
    for line in block.split("\n"):
        if ENVIRONMENT_BLOCK.fullmatch(line):
            keys = []
            mappings.append(keys)
        elif keys is None:
            continue
        elif line.strip() and not line.startswith("      "):
            # Anything back out at the service's own level ends the mapping —
            # `ports:`, `depends_on:`, the next service. A blank line does not.
            keys = None
        elif (key := ENVIRONMENT_KEY.match(line)) is not None:
            keys.append(key.group(1))
    return mappings


def compose_unit(names: Names) -> str:
    """Where a service's own Compose file lives, repository-relative."""
    return f"{COMPOSE_DIR}/{COMPOSE_UNITS}/{names.lower}.yml"


def compose_included(repo_root: Path) -> list[tuple[int, str]]:
    """The index's include list: each entry's line number and its path.

    Read out of the index rather than globbed off the directory, because the
    index is what Compose obeys. A unit file sitting in `services/` that no
    line includes is not part of the model, and a port published in it is not
    a port that is taken — so globbing would refuse a free port on the strength
    of a file nothing reads.
    """
    text, _ = read(repo_root, COMPOSE_INDEX)
    entries = [
        (number, match.group(1))
        for number, line in enumerate(text.split("\n"))
        if (match := INCLUDE_ENTRY.fullmatch(line))
    ]
    if not entries:
        raise ScaffoldError(
            f"{COMPOSE_INDEX} declares no `include:` entry this script recognises "
            f"(two spaces, a dash, a space, a path). The template has moved; "
            f"reconcile tools/new-service/new_service.py with it."
        )
    return entries


def update_compose(repo_root: Path, names: Names, port: int) -> str:
    """The index gains one line, and nothing else in it moves.

    **The port collision check reads every included file, not this one.** The
    index publishes nothing at all now, so a check that kept reading it would
    have found no mapping anywhere and called every port free — a silent
    fail-open on the one guard that stops two services publishing the same
    port. It reads what the index includes instead, which is the same set of
    mappings the check has always been about.

    **Both port regexes carry the host-IP prefix, and the collision check is
    the one that fails quietly without it.** Every mapping in the model is
    published on `127.0.0.1` (§14.1), so `"5102:` no longer follows a quote
    and a pattern anchored on one matches nothing — which reads exactly like
    a free port, and would have published a second service on one already
    taken.
    """
    entries = compose_included(repo_root)

    for _, entry in entries:
        included, _ = read(repo_root, f"{COMPOSE_DIR}/{entry}")
        if re.search(rf'"(?:{HOST_IP}:)?{port}:\d+"', included):
            raise ScaffoldError(
                f"port {port} is already published in {COMPOSE_DIR}/{entry}"
            )

    text, newline = read(repo_root, COMPOSE_INDEX)
    lines = text.split("\n")

    unit = f"{COMPOSE_UNITS}/{names.lower}.yml"
    if unit in {entry for _, entry in entries}:
        raise ScaffoldError(f"{COMPOSE_INDEX} already includes {unit}")

    # The template's own entry is the anchor. Its absence means the layout this
    # script renders into is not the layout on disk, and inserting beside a
    # list whose shape is unknown is the guess this script does not make.
    units = [number for number, entry in entries if entry.startswith(f"{COMPOSE_UNITS}/")]
    if COMPOSE_TEMPLATE_UNIT not in {entry for _, entry in entries}:
        raise ScaffoldError(
            f"{COMPOSE_INDEX} does not include {COMPOSE_TEMPLATE_UNIT}, so there is "
            f"no template unit to render from (§14.1's pair rule lives in it)"
        )

    # After the last unit, so services accumulate in the order they were
    # created — the property the spliced block had, kept where it now lives.
    after = units[-1] + 1
    return restore("\n".join([*lines[:after], f"  - {unit}", *lines[after:]]), newline)


def render_service_compose(repo_root: Path, names: Names, port: int) -> str:
    """Catalog's own unit file, renamed, re-ported and re-headed.

    An extraction rather than a template: the pair's comments argue the
    inline-default rule and §7.1's two keys, and they travel with the copy.
    What does NOT travel is the header above `services:` — it is prose about
    the template, and a rename would turn true sentences about Catalog into
    false ones about the service being rendered. It is replaced rather than
    renamed, and the replacement names no template token, because the
    straggler check in `plan` reads what this returns.
    """
    text, newline = read(repo_root, f"{COMPOSE_DIR}/{COMPOSE_TEMPLATE_UNIT}")

    # §14.1's pair rule, asserted on the file that carries it — and asserted as
    # the WHOLE of it, which is what the split bought: a unit holds one
    # service's pair and nothing after it, so the check is an equality rather
    # than the two anchors and a bound that a spliced block needed. A template
    # that gained a third service would render one this script never saw.
    pair = [f"  {TEMPLATE.lower()}-migrator:", f"  {TEMPLATE.lower()}-api:"]
    declared = [line for line in text.split("\n") if SERVICE_KEY.fullmatch(line)]
    if declared != pair:
        raise ScaffoldError(
            f"{COMPOSE_DIR}/{COMPOSE_TEMPLATE_UNIT} declares {declared or 'no service'}; "
            f"§14.1's pair rule makes it exactly {pair}. The template has moved; "
            f"reconcile tools/new-service/new_service.py with it."
        )

    marker = "\nservices:\n"
    if marker not in text:
        raise ScaffoldError(
            f"{COMPOSE_DIR}/{COMPOSE_TEMPLATE_UNIT} has no `services:` key to render from"
        )
    block = names.rename(text[text.index(marker) + 1:])

    # The loopback prefix is REQUIRED of the template rather than copied from
    # it. Reading the prefix off Catalog would make the scaffold agree with
    # whatever Catalog does, so removing the bind there would silently publish
    # every service scaffolded afterwards on every interface — a gate that
    # follows its subject cannot catch its subject regressing. Anchored, the
    # same removal is a scaffold that refuses to run and says why.
    published = re.search(rf'ports: \[ "{re.escape(LOOPBACK)}:(\d+):8080" \]', block)
    if published is None:
        raise ScaffoldError(
            f"the template's api block publishes no {LOOPBACK}-bound port to substitute "
            f"(§14.1 binds every mapping to loopback)"
        )
    block = block.replace(published.group(0), f'ports: [ "{LOOPBACK}:{port}:8080" ]')

    # §7.1's runtime key is `ConnectionStrings__<Service>` and the rename is
    # what writes it, so a service named after one of §14.1's infrastructure
    # connections renders a key the api block already declares. Nothing above
    # can see it: the rename worked exactly as specified, the straggler check
    # finds no template token left, and duplicate keys leave the YAML well
    # formed — so the run reports success and the file quietly means one of the
    # two values.
    #
    # A predicate over the rendered block, never the three names it happens to
    # catch today. A list of names goes stale the moment §14.1 gives this block
    # a sixth `ConnectionStrings__*` key, and a gate that silently stops
    # covering the newest surface is this repository's most-repeated failure.
    #
    # **Compared casefolded, because the loader on the other side of this file
    # is.** §14.2 states it in the one line where it costs an Aspire resource
    # name: configuration is case-insensitive but not punctuation-insensitive.
    # So `ConnectionStrings__Rabbitmq` and `ConnectionStrings__RabbitMq` are
    # two YAML keys and one configuration key, and the first version of this
    # check — a case-sensitive `in seen` — saw two distinct strings and passed.
    # That handed the exact defect it was written for back to every spelling of
    # an infrastructure connection with different capitals, `RabbitMQ` (the
    # product's own) among them. The same predicate again, one level less
    # literal: a list of names would have gone stale, and so does an equality
    # that is stricter than the thing it is standing in for.
    #
    # `casefold` rather than `lower`, which is the spelling Python defines for
    # case-insensitive comparison — it folds what `lower` leaves alone, and the
    # cost of choosing the weaker one is a refusal that does not fire.
    for mapping in environment_keys(block):
        seen: dict[str, str] = {}
        for key in mapping:
            if (first := seen.get(key.casefold())) is not None:
                # Both spellings, and never one of them twice: the collision is
                # a fact about the configuration loader rather than about the
                # YAML, so a message quoting `ConnectionStrings__RabbitMq` and
                # blaming "the same key twice" reads as simply false to whoever
                # hit it having typed `Rabbitmq`.
                collision = (
                    f"renders {key} twice in one environment: mapping"
                    if first == key
                    else f"renders both {first} and {key} into one environment: mapping"
                )
                raise ScaffoldError(
                    f"'{names.pascal}' {collision}. .NET configuration keys are "
                    f"case-insensitive (§14.2), so those two collapse onto one another the "
                    f"moment configuration loads: the loader keeps one of the values and "
                    f"discards the other, and which one survives is its choice rather than "
                    f"this script's. This service's own §7.1 connection key takes the "
                    f"service's name, and §14.1 already declares an infrastructure "
                    f"connection under that name in the same block. Nothing else refuses "
                    f"it: the rename is correct, no template token is left, and two "
                    f"spellings are two valid YAML keys — so the file stays well formed and "
                    f"the run reports success. Give the service a different name."
                )
            seen[key.casefold()] = key

    header = (
        f"# {names.pascal}'s deployment (§14.1), included by {COMPOSE_INDEX}.\n"
        f"# Rendered by tools/new-service from the template service's unit file: the\n"
        f"# pair rule below belongs to the chapter, and the file boundary belongs to\n"
        f"# docs/change-locality.md, so a {names.pascal} PR edits this file and never\n"
        f"# another service's.\n"
        f"#\n"
        f"# `include` resolves a relative path against the directory of the file that\n"
        f"# declares it, so the repository root — the build context — is three levels up\n"
        f"# from here.\n"
    )
    return restore(header + block, newline)


def update_infra_only(repo_root: Path, names: Names) -> str:
    """Both halves of the pair join the excluded profile — §14.1's own rule."""
    text, newline = read(repo_root, "deploy/compose/docker-compose.infra-only.yml")

    # The anchor is the contiguous pair, and the pair is also what gets
    # written — one string, so the two cannot drift apart. Anchoring the API
    # half alone let a change to the migrator's entry through unnoticed while
    # this went on emitting the shape it used to have, which is the drift the
    # anchors exist to stop.
    pair = (
        f'  {TEMPLATE.lower()}-migrator:\n'
        f'    profiles: [ "excluded" ]\n'
        f'  {TEMPLATE.lower()}-api:\n'
        f'    profiles: [ "excluded" ]\n'
    )
    require_once(text, pair, "infra-only override")
    return restore(text + names.rename(pair), newline)


def update_env_example(repo_root: Path, names: Names) -> str:
    """Catalog's commented pair, extracted so its argument comes with it.

    Bounded by the next service's own marker rather than by the end of the
    file — the same defect as `update_compose`, and found the same way: to EOF
    is the template's block only until one service has been added, after which
    it drags that service's variables along and writes them twice.
    """
    text, newline = read(repo_root, "deploy/compose/.env.example")
    lines = text.split("\n")

    marks = [(i, m.group(1)) for i, line in enumerate(lines) if (m := ENV_MARKER.match(line))]
    template = [i for i, service in marks if service == TEMPLATE]
    if len(template) != 1:
        raise ScaffoldError(
            f".env.example: expected exactly one \"# {TEMPLATE}'s two §7.1 keys\" block, "
            f"found {len(template)}"
        )

    start = template[0]
    following = [i for i, _ in marks if i > start]
    stop = following[0] if following else len(lines)

    block = names.rename("\n".join(lines[start:stop]).rstrip("\n"))
    return restore(text.rstrip("\n") + "\n\n" + block + "\n", newline)


def update_broker_definitions(repo_root: Path, names: Names) -> str:
    """A broker account for the new service (#44).

    Since per-service identity, a service that reaches the broker as nobody in
    `definitions.json` cannot connect AT ALL — and the compose block this
    script already renders names `{service}-svc`. So the account is not an
    optional extra: without it the scaffolded service starts and then fails
    authentication against a broker that has never heard of it.

    Catalog's entry is the template, exactly as it is everywhere else here, so
    the permissions a new service gets are a PUBLISHER's: its own contracts,
    the framework's fault exchanges, and nothing of anybody else's. A service
    that grows a receive endpoint widens its own entry in the same change, and
    `deploy/compose/rabbitmq/check_permissions.py` is what says so — it derives
    what each service needs from the code and fails when the grant is short.

    The password is `local-dev-{service}`, on §14.1's terms for every other
    credential here, and the hash is computed rather than copied: RabbitMQ
    stores `base64(salt || sha256(salt || utf8(password)))`, so a copied hash
    would authenticate the template's password under the new name.
    """
    import base64
    import hashlib
    import json

    relative = "deploy/compose/rabbitmq/definitions.json"
    text, newline = read(repo_root, relative)
    definitions = json.loads(text)

    user = f"{names.lower}-svc"
    if any(entry["name"] == user for entry in definitions["users"]):
        raise ScaffoldError(f"{relative}: a broker user named {user} already exists")

    template_user = f"{TEMPLATE.lower()}-svc"
    template_permission = next(
        (entry for entry in definitions["permissions"] if entry["user"] == template_user),
        None)
    if template_permission is None:
        raise ScaffoldError(
            f"{relative}: no permissions for {template_user} to copy (§14.1, #44)")

    # A deterministic salt per service, so re-rendering the same name twice
    # produces the same file and a reviewer can recompute the hash.
    salt = hashlib.sha256(user.encode()).digest()[:4]
    digest = hashlib.sha256(salt + f"local-dev-{names.lower}".encode()).digest()

    definitions["users"].append({
        "name": user,
        "password_hash": base64.b64encode(salt + digest).decode(),
        "hashing_algorithm": "rabbit_password_hashing_sha256",
        "tags": [],
    })
    definitions["permissions"].append({
        "user": user,
        "vhost": template_permission["vhost"],
        "configure": names.rename(template_permission["configure"]),
        "write": names.rename(template_permission["write"]),
        "read": names.rename(template_permission["read"]),
    })

    return restore(json.dumps(definitions, indent=2) + "\n", newline)


def update_ports_readme(repo_root: Path, names: Names, port: int) -> str:
    """One row in the application-services table — the keyboard inventory (§14.1)."""
    text, newline = read(repo_root, "deploy/compose/README.md")
    header = "| Service | Host port(s) | Notes |\n"
    require_once(text, header, "deploy/compose/README.md")

    start = text.index(header)
    end = text.index("\n\n", start) + 1
    row = (
        f"| {names.pascal} API | http://localhost:{port} | "
        # The token note is not decoration: ADR-030 fallback policy covers
        # MapOpenApi, so a rendered service document answers 401 to an
        # anonymous request exactly as Catalog and Ordering do. A row
        # that omitted it would re-introduce the claim the README was
        # corrected to remove, once per scaffolded service.
        f"`/health/live`, `/health/ready`, "
        f"`/openapi/v1.json` (needs a token — see below) |\n"
    )
    return restore(text[:end] + row + text[end:], newline)


def load_scan_gate(repo_root: Path) -> ModuleType | None:
    """§15.1's secret scan, loaded out of the tree being rendered into, or None.

    Resolved from `repo_root` and never from this file's own location. The
    suite renders into temporary roots holding the template and the shared
    files and no `.github/` at all, so a gate resolved from `__file__` would
    read the real repository's rules and the real allow-list while writing
    into a tree that has neither — green, and about nothing.

    **`.github/` missing is the degradation; the gate missing is a refusal**,
    and the two used to be one test. A checkout with the directory and without
    one of its two files is a real tree, and returning None there rendered a
    service the scanner refuses without saying so — #161 back, quietly, from
    the code that closed it. It is also the only shared file whose absence was
    tolerated: the other six raise inside `read`.

    Loaded by path rather than by `import`, and the difference is not a
    formality: `import` caches by module name, so a second render against a
    second root would silently reuse the first root's gate, and the suite
    renders against several roots in one process. There is no `sys.path`
    manipulation anywhere in this repository and this does not add the first.
    `spec_from_file_location` is the form already in use here —
    `deploy/compose/rabbitmq/test_check_permissions.py` loads its subject that
    way, and so does `.claude/scripts/test_grok_helpers.py`; what is new is
    only the directory being crossed.

    **This executes a file chosen by `--repo-root`, and it is checked against
    the copy this script shipped with before it runs.** An earlier revision
    said the exposure "is not new" because the tool already reads the target's
    source and writes into its tree, and that was wrong in the way that
    matters: copying text is not running it, and a tool that renders a service
    into a checkout does not thereby earn the right to execute that checkout's
    Python with the developer's privileges.

    **Loading it from `TOOL_ROOT` instead was the obvious repair and it breaks
    a correctness invariant.** The entries written here carry the SCANNER'S
    fingerprints, and the scanner that will later verify them is the one in the
    tree being rendered into, because that is the checkout CI runs from. Two
    different implementations agreeing today is not the property needed; the
    entries have to be computed by the same code that checks them, or a
    fingerprint matching nothing becomes a stale entry and the build stops on
    it.

    So the two are reconciled by requiring them to be the SAME FILE rather than
    choosing between them: the target's gate is executed, and only after its
    bytes are established to be the ones here. Identical bytes make the
    invariant and the trust boundary the same statement. A target whose scanner
    differs is refused rather than run, which is also the honest answer for
    rendering at all — the template anchors this script matches are that
    repository's too.

    `secret_scan.py` is stdlib-only and guards its own entry point, so loading
    the file this repository ships runs no scan and touches nothing.
    """
    if not (repo_root / ".github").is_dir():
        return None

    gate = repo_root / SCAN_GATE
    if not gate.is_file():
        raise ScaffoldError(
            f"{repo_root} has a .github directory and no {SCAN_GATE}. §15.1's scan "
            f"is what says a rendered service may be committed; without it this "
            f"script cannot write the entries the gate would demand."
        )
    if not (repo_root / SCAN_ALLOW_LIST).is_dir():
        raise ScaffoldError(
            f"{SCAN_GATE} is here and {SCAN_ALLOW_LIST} is not. The scan reads that "
            f"directory to know what it may ignore, and this script appends to the "
            f"file in it that covers each entry's tree."
        )

    # Executed only if it is the file this script shipped with. Compared as
    # bytes rather than trusted, because the next statement runs it: `--repo-root`
    # is caller-supplied, and reading a checkout's source is not a reason to
    # execute it. Loading TOOL_ROOT's copy instead would be the wrong repair —
    # the fingerprints written here must come from the scanner that will later
    # verify them, which is the target's. Requiring one file satisfies both.
    trusted = TOOL_ROOT / SCAN_GATE
    if gate.resolve() != trusted.resolve():
        if not trusted.is_file():
            raise ScaffoldError(
                f"{TOOL_ROOT} carries no {SCAN_GATE}, so there is nothing to check "
                f"{repo_root}'s copy against, and this script will not execute an "
                f"unverified one."
            )
        if gate.read_bytes() != trusted.read_bytes():
            raise ScaffoldError(
                f"{repo_root}/{SCAN_GATE} is not the secret scan this script shipped "
                f"with, and this script executes it. Refusing rather than running a "
                f"copy it cannot vouch for — and the entries it would write carry that "
                f"scanner's fingerprints, so a different implementation is also the "
                f"wrong thing to compute them with. Render from a checkout whose gate "
                f"matches, or update this tool alongside it."
            )

    specification = importlib.util.spec_from_file_location("scaffold_secret_scan", gate)
    if specification is None or specification.loader is None:
        raise ScaffoldError(f"{SCAN_GATE} cannot be loaded as a Python module")

    module = importlib.util.module_from_spec(specification)
    try:
        specification.loader.exec_module(module)
    # Every exception class there is, because the subject is arbitrary
    # module-level code and nothing narrows the set. What matters is not which
    # one arrives but that it arrives as a ScaffoldError: `main` catches that
    # and nothing else, so an unwrapped SyntaxError here ends the run in a
    # traceback and breaks the one-line-on-stderr contract this script states.
    except Exception as error:
        raise ScaffoldError(f"{SCAN_GATE} did not load: {error}") from error

    # A module that loaded is not a module that is the gate. Checked because
    # the alternative is an AttributeError three frames later, naming a symbol
    # rather than the file the caller pointed at.
    if (absent := [name for name in SCAN_GATE_MEMBERS if not hasattr(module, name)]):
        raise ScaffoldError(
            f"{SCAN_GATE} declares no " + ", ".join(absent) + "; it is not the "
            f"secret scan this script knows how to drive."
        )
    return module


def scan_reason(rows: list[tuple[str, str, str, str]], finding: Any,
                line: str) -> tuple[int, str]:
    """The row explaining one finding and the sentence it carries, or a refusal.

    Exactly one row, never the first of several: two rows that both explain a
    finding is a table nobody can read, and no row at all is the template
    having gained a literal this script has never been shown. Both refuse,
    because the alternative is a suppression with a reason nobody wrote.

    **This decides WHICH sentence and never WHOSE finding it is.** Ownership is
    settled before anything reaches here, by the subtraction in
    `update_allowed_secrets`; a marker that had to carry it as well would be a
    second thing for the same string to be wrong about, which is how a row for
    one service came to be written over another service's credential.

    **The row's index comes back with its sentence so the caller can audit the
    selection**, which is the half a marker cannot do on its own: one row
    explaining two fingerprints has not told two credentials apart, however
    specific the substring it matched on looked.

    `finding` is typed `Any` because its class comes from a module loaded at
    run time out of `repo_root` — there is no name to annotate it with that
    this file could import.
    """
    matched = [
        (index, reason)
        for index, (path, rule, marker, reason) in enumerate(rows)
        if path == finding.path and rule == finding.rule.id and marker in line
    ]
    if len(matched) != 1:
        raise ScaffoldError(
            f"{finding.path}:{finding.line}: the secret scan reports "
            f"`{finding.rule.id}` here and SCAN_REASONS gives {len(matched)} "
            f"sentence(s) for it. The template has gained a credential-shaped "
            f"literal, or moved one; add the row saying why it is accepted. "
            f"A reason this script invented is a suppression nobody wrote."
        )
    return matched[0]


def allow_list_trees(repo_root: Path, gate: ModuleType) -> dict[str, str]:
    """Which allow-list file covers which tree, read out of the files.

    The prefixes are the gate's own `covers:` directives rather than a table
    here, for the reason every anchor in this script is read rather than
    restated: a second copy of the map is one that goes stale the day a tree is
    added, and the failure would be an entry written to a file that does not
    cover it — which the gate then reports against a service somebody has just
    scaffolded.
    """
    covers: dict[str, str] = {}
    for source in sorted((repo_root / SCAN_ALLOW_LIST).glob("*.txt")):
        for line in source.read_text(encoding="utf-8").splitlines():
            if (declaration := gate.COVERS.fullmatch(line.strip())) is not None:
                covers[declaration.group(1)] = f"{SCAN_ALLOW_LIST}/{source.name}"
                break
    return covers


def update_allowed_secrets(repo_root: Path, names: Names, created: dict[str, str],
                           updated: dict[str, str]) -> dict[str, str] | None:
    """One allow-list entry per finding THIS RENDER ADDS, or None (#161).

    **The return is a map now, because the allow-list is a directory.** An
    entry goes to the file whose `covers:` prefix its path starts with, so a
    render that writes both a Compose unit and a test fixture appends to two
    files — and two services being scaffolded at once meet in fewer of them
    than they used to.

    Two paths return None and both are ordinary. The tree has no `.github/`
    at all — the suite's synthetic roots, where a render has nothing to
    reconcile — or the render adds no finding, which is what a second run of a
    service already entered would produce. Silent in both cases: `main`
    reports what it wrote on stdout and nothing on stderr, and a warning about
    a directory the caller never asked for would be the tool complaining about
    its own fixture. A `.github/` that exists and is missing a piece is a
    refusal instead, and `load_scan_gate` argues that.

    **A finding the file already had is not this render's to explain.** Every
    path in `updated` has an original in the tree; both are scanned and only
    the keys the render INTRODUCED are eligible. Ownership is a property of
    the diff, not of the allow-list — the earlier version asked only whether a
    finding was already accepted, so a render whose allow-list had lost an
    entry for a service rendered earlier picked that finding up, wrote a
    sentence naming the wrong service, and cleared it. Paths in `created` did
    not exist a moment ago, so every finding in them is new by construction.

    The fingerprints are the SCANNER'S, taken from the findings it reported.
    A digest computed here would be a second implementation of which substring
    each rule matches, and being wrong at it is silent in the worst direction:
    an entry matching nothing is a stale entry, which is the failure the gate
    reports and the build stops on.
    """
    gate = load_scan_gate(repo_root)
    if gate is None:
        return None

    entries, problems = gate.read_allowed(
        repo_root / SCAN_ALLOW_LIST, {rule.id for rule in gate.RULES})
    if problems:
        raise ScaffoldError(
            f"{SCAN_ALLOW_LIST}: {problems[0]}. This script appends to those files "
            f"and will not append to a set that does not already parse."
        )

    covers = allow_list_trees(repo_root, gate)
    if not covers:
        raise ScaffoldError(
            f"{SCAN_ALLOW_LIST} declares no `covers:` prefix in any file, so there "
            f"is nowhere an entry could be filed."
        )

    rows = [
        (names.rename(path), rule, names.rename(marker), names.rename(reason))
        for path, rule, marker, reason in SCAN_REASONS
    ]

    # An empty marker matches every line of its file, so the row stops asking
    # which literal it is about and becomes a blanket acceptance of its rule
    # there. Refused here rather than trusted to the table, because the four
    # rows that carried one read as deliberate for as long as no second finding
    # existed to be swallowed.
    blank = [f"{path} | {rule}" for path, rule, marker, _ in rows if not marker]
    if blank:
        raise ScaffoldError(
            f"SCAN_REASONS: {len(blank)} row(s) carry an empty marker "
            f"({'; '.join(blank)}). A marker says WHICH literal on the line a "
            f"sentence explains, and an empty one matches every line — so the "
            f"next finding of that rule in that file would be allow-listed with "
            f"a reason written for a different credential."
        )

    # Deduplicated on the key the gate itself judges by. Two lines carrying one
    # value under one rule in one file are one accepted finding, and a second
    # entry for it is reported as duplicating the first — a failed build, from
    # the tool that was supposed to prevent one.
    accepted = {entry.key() for entry in entries}
    explained: dict[int, set[str]] = {}
    lines: dict[str, list[str]] = {}
    for relative, body in {**created, **updated}.items():
        before: set[tuple[str, str, str]] = set()
        if relative in updated:
            original, _ = read(repo_root, relative)
            before = {
                finding.key()
                for finding in gate.scan_text(relative, original, gate.RULES)
            }

        source = body.splitlines()
        for finding in gate.scan_text(relative, body, gate.RULES):
            if finding.key() in before or finding.key() in accepted:
                continue
            accepted.add(finding.key())
            index, reason = scan_reason(rows, finding, source[finding.line - 1])
            explained.setdefault(index, set()).add(finding.fingerprint)

            # The longest matching prefix, so a tree split further later takes
            # its own entries rather than leaving them with its parent. One
            # entry, one file: a finding no prefix covers is refused, because
            # the alternative is choosing a file for it, and which file a
            # suppression lives in is the whole of what makes it findable.
            #
            # `gate.covers_path` rather than `startswith`, because a prefix
            # without a trailing slash is one path and not a tree — and because
            # the gate is what will judge the entry this writes, so asking it
            # is the only way the two cannot disagree.
            home = max(
                (prefix for prefix in covers if gate.covers_path(prefix, finding.path)),
                key=len,
                default=None,
            )
            if home is None:
                raise ScaffoldError(
                    f"{finding.path}: no file under {SCAN_ALLOW_LIST} covers this "
                    f"path, so its entry has nowhere to go. Add the file that "
                    f"covers the tree, with its `covers:` line — this script will "
                    f"not create one, because a new tree is a decision."
                )
            lines.setdefault(covers[home], []).append(
                f"{finding.path} | {finding.rule.id} | {finding.fingerprint} | "
                f"{reason}"
            )

    # What each row actually selected, which is the question a non-empty marker
    # only narrows. A row accounting for two fingerprints has explained two
    # different credentials with one sentence — the empty marker's defect
    # surviving a marker that is merely too loose.
    loose = sorted(index for index, seen in explained.items() if len(seen) > 1)
    if loose:
        path, rule, marker, _ = rows[loose[0]]
        raise ScaffoldError(
            f"SCAN_REASONS: the row for {path} | {rule} with marker "
            f"'{marker}' explains {len(explained[loose[0]])} findings with "
            f"different fingerprints. One sentence cannot be the reason for two "
            f"credentials; narrow the marker, or add the row the second one owes."
        )

    if not lines:
        return None

    heading = f"# --- {names.pascal}, rendered by tools/new-service "
    heading += "-" * max(3, SCAN_HEADING_WIDTH - len(heading))

    written: dict[str, str] = {}
    for relative, block in sorted(lines.items()):
        text, newline = read(repo_root, relative)
        appended = "\n".join([
            "",
            "",
            heading,
            "#",
            "# One entry per finding the gate reported over this render, carrying the",
            "# fingerprints it computed rather than any this script worked out. The",
            "# equivalent literals for a hand-built service are entries above, written",
            "# the day that service landed; these are the same decision, taken by the",
            "# tool that rendered them (#161).",
            "",
        ] + block) + "\n"
        written[relative] = restore(text + appended, newline)

    return written


def plan(repo_root: Path, name: str, port: int, migration_id: str) -> Plan:
    """Everything the run would write, validated. Nothing is written here."""
    if not NAME.fullmatch(name):
        raise ScaffoldError(f"'{name}' is not a PascalCase service name")
    if len(name) > SQL_IDENTIFIER_LIMIT:
        raise ScaffoldError(
            f"'{name[:20]}…' is {len(name)} characters; the name becomes a SQL Server "
            f"database and schema, and sysname stops at {SQL_IDENTIFIER_LIMIT}. The "
            f"projects would render and the first migration would not run."
        )
    if name.upper() in SQL_RESERVED:
        raise ScaffoldError(
            f"'{name}' is a SQL Server system name: the service's database and schema "
            f"take this name, so the migrator would target the server's own."
        )
    if name.upper() in WINDOWS_RESERVED:
        raise ScaffoldError(
            f"'{name}' is a reserved device name on Windows: neither "
            f"src/Services/{name} nor {name}.Domain.csproj can be created there, and "
            f"a repository that half-renders on one platform is not portable."
        )
    # Case-insensitively, because the casings are what the rename keys on:
    # `CATALOG` passes an exact-match check, and its *lower* casing is still
    # `catalog`, so the Compose block it renders keeps the template's own
    # service keys and the file gains a duplicate pair. A case-sensitive
    # filesystem is where that lands — the collision check below hides it on
    # Windows and would not on the runner.
    if name.lower() == TEMPLATE.lower():
        raise ScaffoldError(
            f"{name} is the template under another casing; it cannot be its own copy"
        )

    if name.lower() in {service.lower() for service in WORKER_SERVICES}:
        raise ScaffoldError(
            f"§4.1 gives {name} a Worker in place of an Api, and this script renders the "
            f"Api shape. Worker mode joins with the PR that builds the first worker host; "
            f"until then a {name} scaffolded here would contradict the chapter."
        )

    # And the same test against every service already here, because the
    # template is only the first entry in that set. After Ordering exists,
    # `ORDERING` makes a distinct directory on a case-sensitive filesystem and
    # then renders `ordering-api` and `ordering-migrator` a second time — the
    # duplicate-key failure again, one service along.
    services = repo_root / "src" / "Services"
    taken = (
        {path.name.lower() for path in services.iterdir() if path.is_dir()}
        if services.is_dir()
        else set()
    )
    if name.lower() in taken:
        raise ScaffoldError(
            f"a service whose name differs from {name} only by casing already exists; "
            f"the two would share every lower-cased Compose key and connection variable"
        )
    if not MIGRATION_ID.fullmatch(migration_id):
        raise ScaffoldError(
            f"'{migration_id}' is not a 14-digit migration timestamp; it reaches a file path"
        )
    if port not in PORTS:
        raise ScaffoldError(f"port {port} is outside 1–65535 and Docker cannot publish it")
    if not (repo_root / COPY_ROOTS[0]).is_dir():
        raise ScaffoldError(f"{repo_root} does not look like the repository: no {COPY_ROOTS[0]}")

    names = Names(name)

    # Assembly identity first, because it is the more fundamental refusal:
    # "this name can never work here", ahead of "this service already exists".
    # `Common` renders Common.Domain and Common.Application beside the
    # building blocks of exactly those names, and a solution holding two
    # projects with one identity does not build. It was refused before this
    # check too — `tests/Common.Domain.Tests` exists, so the directory test
    # below fired — but by accident and with a message about the wrong thing,
    # and a name colliding on identity without colliding on disk sailed through.
    # Compared case-insensitively, because a .NET assembly simple name is.
    # `COMMON` clears every check above on a case-sensitive filesystem, and
    # `COMMON.Domain` does not intersect `Common.Domain` as a string — so the
    # first version of this check let through exactly the case it was written
    # to stop.
    generated = {f"{names.pascal}.{suffix}": suffix for suffix in PROJECT_SUFFIXES}
    existing = {
        path.stem.lower()
        for directory in ("src", "tests")
        if (repo_root / directory).is_dir()
        for path in (repo_root / directory).rglob("*.csproj")
    }
    if (clash := sorted(project for project in generated if project.lower() in existing)):
        raise ScaffoldError(
            "the solution already has " + ", ".join(clash) + ", give or take casing. Two "
            "projects with one assembly identity do not build, whatever directory each "
            "sits in and however each is spelt."
        )

    for root in COPY_ROOTS:
        target = repo_root / names.rename(root)
        if target.exists():
            raise ScaffoldError(f"{names.rename(root)} already exists; this script creates, never merges")

    # The new service's own name is masked before the search, or a legitimate
    # one that contains a template token — CatalogSearch, ProductReviews — is
    # rejected for the tokens it was asked for. What is left after masking is a
    # mention the rename did not reach, which is the only thing this check is
    # about.
    #
    # The mask is why the SLICE half of this check does not run here. Masking a
    # service called `Product` would strip every genuine `Product` leftover
    # along with its own name, and the render would report itself
    # domain-neutral while the slice survived in it. `render_projects` runs
    # that half before the rename instead, where a `Product` is unambiguous —
    # the rename maps the template's casings and never touches the slice's.
    mask = re.compile("|".join(re.escape(n) for n in (names.pascal, names.lower, names.upper)))
    created = render_projects(repo_root, names, migration_id)

    # The service's Compose unit is created rather than spliced, so it joins
    # `created` here — before the straggler loop below, which is exactly the
    # check a renamed file wants and the one the spliced block never got.
    #
    # **And it is refused if it is already there**, because `apply` opens every
    # created path with `w` and this one is the only created path outside
    # `COPY_ROOTS`, which is what the collision guard above reads. A unit left
    # behind by a partial run, or written by hand, would be truncated without a
    # word — against a script whose contract is that it creates and never
    # merges. `update_compose` refuses a unit the index already includes; this
    # is the other half, for a file the index has never heard of.
    unit = compose_unit(names)
    if (repo_root / unit).exists():
        raise ScaffoldError(
            f"{unit} already exists. This script creates it and would overwrite "
            f"what is there; remove it, or scaffold under a different name."
        )
    created[unit] = render_service_compose(repo_root, names, port)
    for relative, text in created.items():
        stripped = BENIGN.sub("", mask.sub("", text))
        if (left := TEMPLATE_TOKEN.search(stripped)) is not None:
            line = stripped[: left.start()].count("\n") + 1
            raise ScaffoldError(
                f"{relative}:{line}: '{left.group(0)}' survived the rename. "
                f"The file names Catalog somewhere this script does not patch."
            )
        if TEMPLATE_TOKEN.search(mask.sub("", relative)) is not None:
            raise ScaffoldError(f"{relative}: the path itself still names the template")

    updated = {
        "Platform.slnx": update_solution(repo_root, names),
        COMPOSE_INDEX: update_compose(repo_root, names, port),
        "deploy/compose/docker-compose.infra-only.yml": update_infra_only(repo_root, names),
        "deploy/compose/.env.example": update_env_example(repo_root, names),
        "deploy/compose/README.md": update_ports_readme(repo_root, names, port),
        "deploy/compose/rabbitmq/definitions.json":
            update_broker_definitions(repo_root, names),
    }

    # Last, because it is the only update whose input is every other one. The
    # gate runs over the whole render — the generated tree AND the shared files
    # as they will stand — so it can only be asked once both maps are complete,
    # and it is still asked before anything is written: the file it returns is
    # a value like every other, and `apply` remains the one thing that touches
    # disk.
    allowed = update_allowed_secrets(repo_root, names, created, updated)
    if allowed is not None:
        updated.update(allowed)

    return Plan(created=created, updated=updated)


def apply(repo_root: Path, rendered: Plan) -> None:
    """Write the plan.

    **This is not atomic, and the guarantee above is about validation only.**
    Every anchor, every classification and every straggler check runs before
    the first file is created, so a run this script *refuses* writes nothing —
    that much is a property. An I/O failure partway through this loop is not
    covered: some targets will exist and some shared files will be updated.

    Staging and rolling back was considered and declined. The target is a git
    checkout, the run is a developer typing one command, and `git status`
    already shows exactly what landed with `git restore` and one `rm -rf` to
    undo it — a bespoke transaction log would be a second, untested mechanism
    for something version control does better. Narrowing the claim is the
    honest half of that decision, and a Copilot review is what caught the
    claim being wider than the code.
    """
    for relative, text in {**rendered.created, **rendered.updated}.items():
        target = repo_root / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        with target.open("w", encoding="utf-8", newline="") as handle:
            handle.write(text)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="new_service.py",
        description="Render a new service from the Catalog template (Appendix C, PR-11).",
    )
    parser.add_argument("name", help="the service name, PascalCase — Ordering, Inventory, Payments")
    parser.add_argument(
        "--port",
        type=int,
        required=True,
        help=(
            "the host port the API publishes. Required, never derived: a port is an "
            "allocation recorded in §14.1 and deploy/compose/README.md, and a script "
            "that guessed one would quietly disagree with a printed chapter"
        ),
    )
    parser.add_argument(
        "--repo-root",
        type=Path,
        default=Path(__file__).resolve().parents[2],
        help="the repository root (default: inferred from this script's location)",
    )
    parser.add_argument(
        "--migration-id",
        default=None,
        help="the InitialCreate migration id (default: the current UTC timestamp)",
    )
    args = parser.parse_args(argv)

    migration_id = args.migration_id or datetime.now(timezone.utc).strftime("%Y%m%d%H%M%S")
    try:
        rendered = plan(args.repo_root, args.name, args.port, migration_id)
        apply(args.repo_root, rendered)
    except ScaffoldError as error:
        print(f"new_service.py: {error}", file=sys.stderr)
        return 1

    print(
        f"{args.name}: {len(rendered.created)} files created, "
        f"{len(rendered.updated)} updated, API on port {args.port}."
    )
    print("Next: dotnet restore Platform.slnx && dotnet build Platform.slnx")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
