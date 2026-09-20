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
            "        // Explicit rather than scanned, beside the dispatcher it serves —\n"
            "        // §4.2's registration sample is the shape. §7.5's real dispatcher,\n"
            "        // and no null one beside it: a dispatcher that drops every domain\n"
            "        // event is deleted rather than disabled, so nothing can register it\n"
            "        // back by accident.\n",
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
            "    table's second row. Contracts arrives with the §9.3 mapper, which is the\n"
            "    only type here that names one: the allow-list turns a domain event into a\n"
            "    public record, so the layer that owns the allow-list is the layer that\n"
            "    pays for the reference. §4.3's one assembly that crosses a service\n"
            "    boundary, and it crosses at the mapper.\n",
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
            "        // These are the registrations §6.2's scan produces, so the scan itself\n"
            "        // is testable. Every slice adds a row here — the scan is public-only,\n"
            "        // and a handler it misses registers as nothing at all rather than as\n"
            "        // something wrong.\n"
            "        ServiceCollection services = new();\n"
            "\n"
            "        services.AddCatalogApplication();\n"
            "\n"
            "        services.ShouldContain(d =>\n"
            "            d.ServiceType == typeof(ICommandHandler<PublishProductCommand, Result<Guid>>));\n"
            "        services.ShouldContain(d =>\n"
            "            d.ServiceType == typeof(IQueryHandler<GetProductsQuery, CursorPage<ProductSummaryDto>>));\n"
            "\n"
            "        // The pricing slice. The scan is public-only (§6.2), so an internal\n"
            "        // handler, a rename or a missed IQueryHandler<,> registers as nothing\n"
            "        // and fails on the first gRPC call rather than at startup —\n"
            "        // ValidateOnBuild never constructs the dispatcher's handler map, and\n"
            "        // the suites that would catch it need a Docker daemon.\n"
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
        # A rendered service starts with no consumer and no receive endpoint,
        # so it never needs the harness-only broker widening below: that
        # widening belongs with a service's first consumer, and arrives with
        # it rather than with the scaffold.
        (
            "    /// <summary>\n"
            "    /// The harness publishes a peer's contract under this service's own\n"
            "    /// account, which the deployed grant refuses: a consumer reads a peer's\n"
            "    /// exchange and never writes it. Only the test container's write moves.\n"
            "    /// </summary>\n"
            "    /// <remarks>\n"
            "    /// <c>configure</c> and <c>read</c> are read back out of the definitions\n"
            "    /// the container imports rather than restated, so the topology this suite\n"
            "    /// judges is judged by the scope that deploys.\n"
            "    /// </remarks>\n"
            "    private async Task WidenWriteForTheHarnessAsync()\n"
            "    {\n"
            "        const string user = \"catalog-svc\";\n"
            "        const string write = \"^(catalog-|Common\\\\.Contracts|MassTransit:)\";\n"
            "\n"
            "        (string configure, string read) = ImportedGrant();\n"
            "\n"
            "        ExecResult result = await _rabbit!.ExecAsync(\n"
            "            [\"rabbitmqctl\", \"set_permissions\", \"-p\", \"/\", user, configure, write, read],\n"
            "            TestContext.Current.CancellationToken);\n"
            "\n"
            "        // A silent failure here would surface as every endpoint test retrying\n"
            "        // a refused publish until its budget ran out, naming a message rather\n"
            "        // than a permission.\n"
            "        if (result.ExitCode != 0)\n"
            "        {\n"
            "            throw new InvalidOperationException(\n"
            "                $\"Could not widen {user}'s broker permissions for the harness \"\n"
            "                + $\"(exit {result.ExitCode}). stdout: {result.Stdout} stderr: {result.Stderr}\");\n"
            "        }\n"
            "\n"
            "        // The mapped file rather than the container, because it is the same\n"
            "        // text the broker imported and it can be read before anything starts.\n"
            "        static (string Configure, string Read) ImportedGrant()\n"
            "        {\n"
            "            string path = Path.Combine(BrokerContextPath(), \"definitions.json\");\n"
            "            using JsonDocument definitions = JsonDocument.Parse(File.ReadAllText(path));\n"
            "\n"
            "            foreach (JsonElement entry in definitions.RootElement"
            ".GetProperty(\"permissions\").EnumerateArray())\n"
            "            {\n"
            "                if (entry.GetProperty(\"user\").GetString() != user "
            "|| entry.GetProperty(\"vhost\").GetString() != \"/\")\n"
            "                    continue;\n"
            "\n"
            "                return (entry.GetProperty(\"configure\").GetString()!, "
            "entry.GetProperty(\"read\").GetString()!);\n"
            "            }\n"
            "\n"
            "            throw new InvalidOperationException(\n"
            "                $\"{path} grants {user} nothing on the default vhost, "
            "so there is no scope to preserve.\");\n"
            "        }\n"
            "    }\n"
            "\n",
            "",
        ),
        (
            "\n"
            "        await WidenWriteForTheHarnessAsync();\n"
            "\n",
            "\n",
        ),
        ("using DotNet.Testcontainers.Containers;\n", ""),
        # The widening above is the fixture's one JSON reader, so its using
        # leaves with it.
        ("using System.Text.Json;\n", ""),
    ),
    "tests/Catalog.Api.Tests/Catalog.Api.Tests.csproj": (
        # The gRPC client package is Catalog's, because the pricing RPC is,
        # and it leaves with the suite that calls it. A reference nothing in
        # the rendered project uses is the unused-dependency claim CLAUDE.md
        # rules out, one file type over.
        (
            "    <!-- GrpcChannel and Grpc.Core's StatusCode, for calling the gRPC server\n"
            "         over loopback. Carried transitively through Catalog.Api; named here\n"
            "         on the register's honesty rule, because a project that names a type\n"
            "         declares the package rather than relying on a production csproj it\n"
            "         does not control. -->\n"
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
            "    <!-- The consumer-driven contract, linked rather than referenced — the\n"
            "         same relationship pricing.proto already has, one level up. The .proto\n"
            "         is Catalog's because Catalog serves the RPC; this file is Web.Bff's\n"
            "         because only a consumer can say what it needs, and it is compiled\n"
            "         into this suite so the provider can be held to it. A file and not an\n"
            "         assembly, so no project dependency is created and §4.3 is untouched:\n"
            "         Common.Contracts is still the only assembly that crosses a service\n"
            "         boundary, and a test helper is expressly not it. The cost is a\n"
            "         build-time path into another suite's tree, paid once — no Dockerfile\n"
            "         builds a test project, so there is no COPY line to keep in step. -->\n"
            "    <Compile Include=\"..\\Web.Bff.TestSupport\\PricingContract.cs\" Link=\"Contract\\PricingContract.cs\" />\n"
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
    ),
    "tests/Catalog.TestSupport/CatalogApiFactory.cs": (
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
            "        // Named and ordered, not merely counted: the migrator's job is to\n"
            "        // apply every migration in sequence, and a count alone would pass on\n"
            "        // a shorter prefix of them applied twice.\n"
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

# And the inbox migration's, for the same reason again: the template names the
# outbox migration whose dress it follows, and a scaffolded service's copy
# states the convention without the cross-reference.
INBOX_MIGRATION_PATCHES: tuple[tuple[str, str], ...] = (
    (
        "/// §9.5's inbox table, generated from <see cref=\"InboxMessageConfiguration\"/>\n"
        "/// on <c>AddOutbox</c>'s terms: the configuration is the source of truth, and\n"
        "/// the <c>.Designer.cs</c> and snapshot beside it are machine-owned.\n",
        "/// §9.5's inbox table, generated from <see cref=\"InboxMessageConfiguration\"/>:\n"
        "/// the configuration is the source of truth, and the <c>.Designer.cs</c> and\n"
        "/// snapshot beside it are machine-owned.\n",
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
