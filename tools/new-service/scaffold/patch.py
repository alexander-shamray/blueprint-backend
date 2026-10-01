"""The anchored edits a render makes to the template's own text.

A table and nothing else. `render` applies it, `require_once` binds each
anchor, and `classify` refuses a key for a file the render does not copy.
"""

from __future__ import annotations

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
            "        // §4.2's sample line. IValidator<T> is not in PluggableInterfaces.All\n"
            "        // because it is FluentValidation's contract, not one of ours — its own\n"
            "        // scanner knows its own conventions (Include* filters, internal\n"
            "        // validators) and a second scan would drift from it.\n"
            "        services.AddValidatorsFromAssemblyContaining<PublishProductValidator>();\n",
            "        // §4.2's sample line, over the assembly until the first validator gives it a type.\n"
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
            "    // Empty until this service publishes a contract: translation is opt-in (§9.3).\n"            "    private static readonly Dictionary<Type, Func<IDomainEvent, object>> Registry = [];\n",
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
            "    Domain, Common.Application and Common.Contracts, §4.2's second row. Contracts arrives with the §9.3\n"
            "    mapper, whose allow-list turns a domain event into a public record (§4.3).\n",
            "    Domain and Common.Application, §4.2's second row; Common.Contracts joins with the §9.3 mapper's first entry.\n",
        ),
        (
            "  <ItemGroup>\n"
            "    <!-- The read side of §6.5: query handlers use Dapper directly, never EF —\n"
            "         the architecture gate in Catalog.Application.Tests holds that line. -->\n"
            "    <PackageReference Include=\"Dapper\" />\n",
            "  <ItemGroup>\n"
            "    <!-- Dapper joins with the first query handler (§6.5). -->\n",
        ),
    ),
    "src/Services/Catalog/Catalog.Infrastructure/Catalog.Infrastructure.csproj": (
        (
            "    <!-- typeof(ProductPublished).Assembly, the Broker lane's half of\n"
            "         MessageTypeSource. Transitive through Catalog.Application, named\n"
            "         directly because this file names the type. -->\n",
            "    <!-- MessageTypeSource's Broker half, through IIntegrationEvent until this service has a contract (§9.4). -->\n",
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
            "        // IIntegrationEvent and AssemblyMarker stand in for §9.4's two anchors until the service has its own.\n"            "        services.AddSingleton(\n"
            "            new MessageTypeSource(typeof(IIntegrationEvent).Assembly, typeof(AssemblyMarker).Assembly));\n",
        ),
        (
            "        // The payload format (§9.4), and the converters that make this service's value objects part of it.\n"
            "        // Without MoneyJsonConverter a Money round-trips silently to zero and a null currency.\n"
            "        services.AddSingleton<JsonConverter, MoneyJsonConverter>();\n"
            "        services.AddSingleton<OutboxJson>();\n",
            "        // The payload format (§9.4); a value object on a domain event registers its converter here (§12.4).\n"
            "        services.AddSingleton<OutboxJson>();\n",
        ),
        ("using System.Text.Json.Serialization;\n", ""),
    ),
    "src/Services/Catalog/Catalog.Api/Catalog.Api.csproj": (
        (
            "    <!-- The server half of §9.7's pricing hop. Grpc.AspNetCore brings\n"
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
            "    <!-- Catalog owns the contract because Catalog serves it; Web.Bff compiles this file as a Client, by link.\n"
            "         Both halves, because this project's suite needs a client, and generating one there would put every\n"
            "         message type in a compilation twice, where CS0436 is an error under ADR-019. -->\n"
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
            "// 500 rather than the caller's 400.\n"
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
            "// Catalog's permission policies (§11.4). Deliberately not inside either helper above: Application knows nothing\n"
            "// about HTTP, and Common.Web must not know Catalog's names.\n"
            "builder.Services\n"
            "    .AddAuthorizationBuilder()\n"
            "    .AddPolicy(CatalogPermissions.Write, p => p.RequirePermission(CatalogPermissions.Write));\n"
            "\n",
            "// This service registers no permission policy until an endpoint names one (§11.4).\n"
            "\n",
        ),
        (
            "app.MapOpenApi();\n"
            "app.MapProductEndpoints();        // §11.4\n",
            "app.MapOpenApi();\n"
            "\n"
            "// This service maps no endpoint of its own yet. The first one goes behind RequireAuthorization at the group (§11.4).\n",
        ),
    ),
    "tests/Catalog.Domain.Tests/ArchitectureTests.cs": (
        ("using Catalog.Domain.Products;\n", ""),
        (
            "        // An exact allow-list, as §4.2's table is: a System.* prefix would pass System.Data.SqlClient.\n"
            "        // System.Collections carries generated record equality through EqualityComparer<T>, and System.Linq is\n"
            "        // Money's currency check.\n"
            "        string[] allowed = [\"Common.Domain\", \"System.Runtime\", \"System.Collections\", \"System.Linq\"];\n"
            "\n"
            "        IEnumerable<string> referenced = typeof(Product).Assembly\n",
            "        // An exact allow-list, as §4.2's table is: a System.* prefix would pass System.Data.SqlClient.\n"
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
        # Money is Catalog's, and a rendered service has no value object of its own yet.
        (
            '                "no converters. Money has a private constructor, so it '
            'round-trips to a zero " +\n'
            '                "amount and a null currency and nothing says so (§4.2) — '
            'an idempotent command " +\n'
            '                "returns a primitive, a Guid or a DTO, never a domain '
            'value object.");\n',
            '                "no converters. A domain value object need not survive '
            'that round trip, and " +\n'
            '                "nothing says so (§4.2) — an idempotent command returns '
            'a primitive, a Guid " +\n'
            '                "or a DTO, never a domain value object.");\n',
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
            "    <!-- ServiceCollection itself; the abstractions package has no container to build. -->\n"
            "    <PackageReference Include=\"Microsoft.Extensions.DependencyInjection\" />\n"
            "    <!-- The handler tests seed and assert through the real CatalogDbContext (§12.4). -->\n"
            "    <PackageReference Include=\"Microsoft.EntityFrameworkCore.SqlServer\" />\n",
            "    <!-- ServiceCollection itself; the abstractions package has no container to build. -->\n"
            "    <PackageReference Include=\"Microsoft.Extensions.DependencyInjection\" />\n",
        ),
        (
            "    <ProjectReference Include=\"..\\..\\src\\Services\\Catalog\\Catalog.Application\\Catalog.Application.csproj\" />\n"
            "    <!-- §12.1's handler tests run against real containers, from a fixture project of their own (§4.1). -->\n"
            "    <ProjectReference Include=\"..\\..\\tests\\Catalog.TestSupport\\Catalog.TestSupport.csproj\" />\n"
            "    <!-- CatalogDbContext by name; §4.2's gate binds Catalog.Application, not its tests. -->\n"
            "    <ProjectReference Include=\"..\\..\\src\\Services\\Catalog\\Catalog.Infrastructure\\Catalog.Infrastructure.csproj\" />\n",
            "    <ProjectReference Include=\"..\\..\\src\\Services\\Catalog\\Catalog.Application\\Catalog.Application.csproj\" />\n"
            "    <!-- TestSupport, Infrastructure and the EF provider return with the first handler test (§12.1). -->\n",
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
            "        // ValidationBehavior takes IEnumerable<IValidator<T>>, so a lost scan validates nothing and fails nowhere.\n"
            "        ServiceCollection services = new();\n"
            "\n"
            "        services.AddCatalogApplication();\n"
            "\n"
            "        services.ShouldContain(\n"
            "            d => d.ServiceType == typeof(FluentValidation.IValidator<PublishProductCommand>),\n"
            "            \"AddValidatorsFromAssemblyContaining is §4.2's line, and losing it fails silently\");\n"
            "\n"
            "        // A query's validator too, since ValidationBehavior is unconstrained (§6.3) and this is GetPrices' bound.\n"
            "        services.ShouldContain(\n"
            "            d => d.ServiceType == typeof(FluentValidation.IValidator<GetPricesQuery>));\n"
            "    }\n"
            "\n"
            "    [Fact]\n"
            "    public void AddCatalogApplication_registers_the_slice_handlers()\n"
            "    {\n"
            "        // The scan is public-only (§6.2), and a handler it misses registers as nothing.\n"
            "        ServiceCollection services = new();\n"
            "\n"
            "        services.AddCatalogApplication();\n"
            "\n"
            "        services.ShouldContain(d =>\n"
            "            d.ServiceType == typeof(ICommandHandler<PublishProductCommand, Result<Guid>>));\n"
            "        services.ShouldContain(d =>\n"
            "            d.ServiceType == typeof(IQueryHandler<GetProductsQuery, CursorPage<ProductSummaryDto>>));\n"
            "\n"
            "        services.ShouldContain(d =>\n"
            "            d.ServiceType == typeof(IQueryHandler<GetPricesQuery, IReadOnlyList<ProductPriceDto>>));\n"
            "    }\n"
            "}\n",
            "\n"
            "    // The first handler of either kind and the first validator each bring back their own registration test (§6.2).\n"
            "}\n",
        ),
    ),
    "tests/Catalog.TestSupport/Outbox/OutboxRows.cs": (
        ("using Common.Contracts.Catalog.V1;\n", ""),
        (
            "    /// <summary>A Broker-lane row carrying a real contract.</summary>\n"
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
            "    // The Broker-lane builder returns with this service's first contract (§9.3).\n"
            "\n",
        ),
    ),
    "tests/Catalog.Api.Tests/OutboxDispatcherTests.cs": (
        (
            "\n"
            "    [Fact]\n"
            "    public async Task A_broker_row_is_published_and_completed()\n"
            "    {\n"
            "        // The Broker half of DeliverAsync against the fixture's real broker, asserted by the row's completion.\n"
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
            "    // The Broker-lane tests return with this service's first contract and OutboxRows.Broker (§9.1).\n",
        ),
        (
            "\n"
            "    [Fact]\n"
            "    public async Task An_integration_event_on_the_local_lane_never_reaches_a_projection()\n"
            "    {\n"
            "        // The mirror: ProjectionInvoker is unconstrained, so without the guard a contract would be marked processed.\n"
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
        # A rendered service starts with no consumer and no receive endpoint,
        # so it keeps the imported grant and never needs the harness's wider
        # write: that override belongs with a service's first consumer, and
        # arrives with it rather than with the scaffold.
        (
            "\n"
            "    /// <summary>Widens <c>catalog-svc</c>'s write so the harness can publish a peer's contract, "
            "past ADR-036.</summary>\n"
            "    protected override string? HarnessWrite(string granted) => "
            "\"^(catalog-|Common\\\\.Contracts|MassTransit:)\";\n",
            "",
        ),
    ),
    "tests/Catalog.Api.Tests/Catalog.Api.Tests.csproj": (
        # The gRPC client package is Catalog's, because the pricing RPC is,
        # and it leaves with the suite that calls it. A reference nothing in
        # the rendered project uses is the unused-dependency claim CLAUDE.md
        # rules out, one file type over.
        (
            "    <!-- GrpcChannel and Grpc.Core's StatusCode, named although Catalog.Api carries them transitively. -->\n"
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
            "    <!-- Web.Bff's consumer-driven contract, linked rather than referenced (ADR-023). -->\n"
            "    <Compile Include=\"..\\Web.Bff.TestSupport\\PricingContract.cs\" Link=\"Contract\\PricingContract.cs\" />\n"
            "  </ItemGroup>\n"
            "\n",
            "",
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
    ),
    # StockLevelConsumer.cs is OMITTED: a rendered service subscribes to
    # nothing, so these registrations of it are removed and the rest of the
    # file is otherwise byte-for-byte the template's.
    "src/Services/Catalog/Catalog.Infrastructure/Messaging/DependencyInjection.cs": (
        (
            "            x.DisableUsageTelemetry();\n"
            "\n"
            "            x.AddStockLevelConsumer();\n",
            "            x.DisableUsageTelemetry();\n",
        ),
        (
            "                cfg.Host(new Uri(connectionString));\n"
            "\n"
            "                cfg.ConfigureStockLevelEndpoint(context);\n",
            "                cfg.Host(new Uri(connectionString));\n",
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
            "        schema.ShouldBe(1, \"InitialCreate's hand-written EnsureSchema creates it; "
            "AddProducts' is a no-op after it\");\n"
            "\n"
            "        // Named and ordered, since a count passes on a shorter prefix applied twice.\n"
            "        string[] applied = await fixture.AppliedMigrationsAsync();\n"
            "        applied.Length.ShouldBe(9);\n"
            "        applied[0].ShouldEndWith(\"_InitialCreate\");\n"
            "        applied[1].ShouldEndWith(\"_AddProducts\");\n"
            "        applied[2].ShouldEndWith(\"_AddOutbox\");\n"
            "        applied[3].ShouldEndWith(\"_AddInbox\");\n"
            "        applied[4].ShouldEndWith(\"_AddOutboxRetentionIndex\");\n"
            "        applied[5].ShouldEndWith(\"_AddIdempotencyMarkers\");\n"
            "        applied[6].ShouldEndWith(\"_IdempotencyMarkerCommittedAtDefault\");\n"
            "        applied[7].ShouldEndWith(\"_AddIdempotencyMarkerRowVersion\");\n"
            "        applied[8].ShouldEndWith(\"_AddStockLevels\");\n",
            "        schema.ShouldBe(1, \"InitialCreate's hand-written EnsureSchema is what creates it\");\n"
            "\n"
            "        // Named and ordered, since a count passes on a shorter prefix applied twice.\n"
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

# The edits a WORKER render makes on top of PATCHES, and the order is
# load-bearing: these are appended to a file's PATCHES tuple, so each anchor is
# matched against the text the earlier ones already produced. Two of the three
# Program.cs entries anchor on a PATCHES replacement for exactly that reason.
#
# §3.2 gives a worker no API, and §15.3 keeps Kestrel bound for §13.5's health
# endpoint — so what leaves is the OpenAPI document, the endpoint guidance and
# the smoke tests that ask for the document, and what stays is the host, the
# middleware §11.2 requires of every service, and the probes.
WORKER_PATCHES: dict[str, tuple[tuple[str, str], ...]] = {
    "src/Services/Catalog/Catalog.Api/Catalog.Api.csproj": (
        (
            "  <!-- The service's one web host; Infrastructure is referenced for Program.cs alone, the composition root (§4.2). -->\n",
            "  <!-- The Web SDK for §13.5's health endpoint, on a host §3.2 gives no API (§15.3). -->\n",
        ),
        (
            "  <ItemGroup>\n"
            "    <!-- Appendix C's OpenAPI deliverable: document only, no UI. -->\n"
            "    <PackageReference Include=\"Microsoft.AspNetCore.OpenApi\" />\n"
            "  </ItemGroup>\n"
            "\n",
            "",
        ),
    ),
    "src/Services/Catalog/Catalog.Api/Program.cs": (
        (
            "\n"
            "// Appendix C's OpenAPI deliverable: document only, no UI.\n"
            "builder.Services.AddOpenApi();\n",
            "",
        ),
        (
            "// This service registers no permission policy until an endpoint names one (§11.4).\n",
            "// A worker names no endpoint, so it registers no permission policy (§3.2); the token middleware below stays (§11.2).\n",
        ),
        (
            "app.MapCommonHealthEndpoints();   // §13.5 — anonymous; kubelet carries no token\n"
            "app.MapOpenApi();\n"
            "\n"
            "// This service maps no endpoint of its own yet. The first one goes behind RequireAuthorization at the group (§11.4).\n",
            "// §13.5's probes are all this host serves, on a port the kubelet reaches directly (§15.3).\n"
            "app.MapCommonHealthEndpoints();   // §13.5 — anonymous; kubelet carries no token\n",
        ),
    ),
    # The smoke suite asks the host for the document with a caller and expects
    # 200. A host that maps no document answers 404 to that caller — the
    # suite's own unknown-path test says so — so the two tests that name the
    # document leave, and the suite's summary stops describing it. The
    # authenticated factory stays: the unknown-path test is its other user.
    "tests/Catalog.Api.Tests/HostSmokeTests.cs": (
        (
            "/// <summary>The host builds under <c>ValidateOnBuild</c> and serves its probes (§13.5) and OpenAPI "
            "document.</summary>\n",
            "/// <summary>The host builds under <c>ValidateOnBuild</c> and serves its probes (§13.5).</summary>\n",
        ),
        (
            "\n"
            "    [Fact]\n"
            "    public async Task OpenApi_document_is_not_anonymous()\n"
            "    {\n"
            "        // MapOpenApi has no authorization metadata, so AddCommonWebDefaults' fallback policy reaches it (§11.4).\n"
            "        using HttpClient client = factory.CreateClient();\n"
            "\n"
            "        HttpResponseMessage response =\n"
            "            await client.GetAsync(\"/openapi/v1.json\", TestContext.Current.CancellationToken);\n"
            "\n"
            "        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);\n"
            "    }\n"
            "\n"
            "    [Fact]\n"
            "    public async Task OpenApi_document_is_served_to_a_caller()\n"
            "    {\n"
            "        // The half a 401 cannot show: that the document still generates.\n"
            "        using AuthenticatedUnreachableFactory authenticated = new();\n"
            "        using HttpClient client = authenticated.CreateClient();\n"
            "\n"
            "        using HttpRequestMessage request = new(HttpMethod.Get, \"/openapi/v1.json\");\n"
            "        request.Headers.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());\n"
            "\n"
            "        HttpResponseMessage response =\n"
            "            await client.SendAsync(request, TestContext.Current.CancellationToken);\n"
            "\n"
            "        response.StatusCode.ShouldBe(HttpStatusCode.OK);\n"
            "        response.Content.Headers.ContentType!.MediaType.ShouldBe(\"application/json\");\n"
            "    }\n",
            "",
        ),
    ),
}
