using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Common.TestSupport;
using MassTransit;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Notifications.TestSupport;
using Testcontainers.MsSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Settings;
using Xunit;
using CatalogFixture = Catalog.TestSupport.ServiceFixture;
using InventoryFixture = Inventory.TestSupport.ServiceFixture;
using NotificationsFixture = Notifications.TestSupport.ServiceFixture;
using OrderingFixture = Ordering.TestSupport.ServiceFixture;
using PaymentsFixture = Payments.TestSupport.ServiceFixture;
using PaymentsMappings = Payments.TestSupport.SimulatorMappings;
using Response = WireMock.ResponseBuilders.Response;
using ShippingFixture = Shipping.TestSupport.ServiceFixture;
using ShippingMappings = Shipping.TestSupport.SimulatorMappings;

namespace Platform.IntegrationTests.Journey;

/// <summary>An order a scenario placed, with what it needs to read the order back across the services.</summary>
public sealed record JourneyOrder(Guid Id, Guid Customer, Guid Product, int Quantity, decimal Total)
{
    /// <summary>Where the customer's notices go: new to the order, so the shared relay holds only its own.</summary>
    public string Mailbox => MailboxOf(Customer);

    public static string MailboxOf(Guid customer) => $"customer-{customer:N}@example.test";
}

/// <summary>
/// Six services over one SQL Server, one broker and one Redis pair, each as its own host, each under the account the
/// broker's definitions give it (§12.1's journey level).
/// </summary>
/// <remarks>
/// Nothing is widened for the harness: a service that publishes or binds what its shipped grant refuses fails here,
/// which is the cross-service proof a per-service suite cannot give. The edges are the simulators Compose mounts and
/// Ordering's address read is Ordering's own service, not a stand-in for it. Every order, product and customer is new
/// to its scenario, so one scenario's rows are no part of another's assertions and nothing is reset between them.
/// </remarks>
public abstract class JourneyWorld(Jurisdiction jurisdiction) : IAsyncLifetime
{
    /// <summary>The services of §3.2's path, which are also the databases' names (§7.1).</summary>
    public const string Catalog = "Catalog";

    public const string Ordering = "Ordering";
    public const string Inventory = "Inventory";
    public const string Payments = "Payments";
    public const string Shipping = "Shipping";
    public const string Notifications = "Notifications";

    private readonly MsSqlContainer _sql = new MsSqlBuilder().WithImage(ComposeImage.Of("sql")).Build();

    private readonly RedisContainer _cache = new RedisBuilder()
        .WithImage(ComposeImage.Of("redis-cache"))
        .WithCommand("--maxmemory-policy", "allkeys-lru")
        .Build();

    private readonly RedisContainer _coordination = new RedisBuilder()
        .WithImage(ComposeImage.Of("redis-coordination"))
        .WithCommand("--maxmemory-policy", "noeviction")
        .Build();

    private readonly Dictionary<string, OutboxGate> _gates = new()
    {
        [Catalog] = new OutboxGate(),
        [Ordering] = new OutboxGate(),
        [Inventory] = new OutboxGate(),
        [Payments] = new OutboxGate(),
        [Shipping] = new OutboxGate()
    };

    private RabbitMqContainer? _broker;

    private string _sqlConnection = null!;

    /// <summary>The deployment's answers and the address its customers live at.</summary>
    public Jurisdiction Jurisdiction { get; } = jurisdiction;

    /// <summary>The relay every notice leaves by, read back through its API.</summary>
    public Mailpit Relay { get; } = Mailpit.Plain();

    /// <summary>The payment provider, serving the simulator's mappings (§14.1).</summary>
    public WireMockServer Provider { get; private set; } = null!;

    /// <summary>The carrier, serving the simulator's mappings (§14.1).</summary>
    public WireMockServer Carrier { get; private set; } = null!;

    /// <summary>Keycloak's admin API, the owner ADR-052's contact read asks.</summary>
    public WireMockServer Contacts { get; private set; } = null!;

    internal JourneyCatalogFactory CatalogHost { get; private set; } = null!;

    internal JourneyOrderingFactory OrderingHost { get; private set; } = null!;

    internal JourneyInventoryFactory InventoryHost { get; private set; } = null!;

    internal JourneyPaymentsFactory PaymentsHost { get; private set; } = null!;

    internal JourneyShippingFactory ShippingHost { get; private set; } = null!;

    internal JourneyNotificationsFactory NotificationsHost { get; private set; } = null!;

    /// <summary>One service's outbox, which a scenario holds to park an order where it needs it.</summary>
    public OutboxGate OutboxOf(string service) => _gates[service];

    public async ValueTask InitializeAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        IFutureDockerImage image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(BrokerContextPath())
            .WithDockerfile("Dockerfile")
            // A name of this suite's own: Testcontainers writes the build context to a file named after the image.
            .WithName($"ashamray-test-broker-journey-{Jurisdiction.Name}:4.1-delayed")
            .WithCleanUp(false)
            .Build();

        // The first account only names the container's default user; every other login comes from the definitions.
        _broker = new RabbitMqBuilder()
            .WithUsername("ordering-svc")
            .WithPassword("local-dev-ordering")
            .WithImage(image)
            .Build();

        await DaemonRetry.CreateAsync(image, ct);
        await Task.WhenAll(
            DaemonRetry.StartAsync(_sql, ct),
            DaemonRetry.StartAsync(_broker, ct),
            DaemonRetry.StartAsync(_cache, ct),
            DaemonRetry.StartAsync(_coordination, ct),
            Relay.StartAsync(ct));

        _sqlConnection = _sql.GetConnectionString();

        Provider = Simulator(PaymentsMappings.Directory());
        Carrier = Simulator(ShippingMappings.Directory());
        Contacts = WireMockServer.Start(new WireMockServerSettings { Urls = ["http://127.0.0.1:0"] });

        // The migrators are the shipped §7.4 jobs, one per database.
        await Task.WhenAll(
            Migrated(CatalogFixture.RunMigratorAsync(Connection(Catalog)), Catalog),
            Migrated(OrderingFixture.RunMigratorAsync(Connection(Ordering)), Ordering),
            Migrated(InventoryFixture.RunMigratorAsync(Connection(Inventory)), Inventory),
            Migrated(PaymentsFixture.RunMigratorAsync(Connection(Payments)), Payments),
            Migrated(ShippingFixture.RunMigratorAsync(Connection(Shipping)), Shipping),
            Migrated(NotificationsFixture.RunMigratorAsync(Connection(Notifications)), Notifications));

        string cache = _cache.GetConnectionString();
        string coordination = _coordination.GetConnectionString();

        CatalogHost = new JourneyCatalogFactory(
            Connection(Catalog),
            Account("catalog"),
            cache,
            coordination,
            _gates[Catalog]);
        OrderingHost = new JourneyOrderingFactory(
            Connection(Ordering),
            Account("ordering"),
            cache,
            coordination,
            _gates[Ordering]);
        InventoryHost = new JourneyInventoryFactory(
            Connection(Inventory),
            Account("inventory"),
            cache,
            coordination,
            _gates[Inventory]);
        PaymentsHost = new JourneyPaymentsFactory(
            Connection(Payments),
            Account("payments"),
            Provider.Urls[0] + "/",
            _gates[Payments]);
        ShippingHost = new JourneyShippingFactory(
            Connection(Shipping),
            Account("shipping"),
            Carrier.Urls[0] + "/",
            OrderingHost.Server,
            _gates[Shipping],
            Jurisdiction);
        NotificationsHost = new JourneyNotificationsFactory(
            Connection(Notifications),
            Account("notifications"),
            Relay.Host,
            Relay.Port,
            new Uri(Contacts.Urls[0] + "/"),
            Jurisdiction);

        // Every host is up, and so every queue bound, before the first order is placed: a publish to an exchange
        // nothing is bound to yet is dropped by the broker, not held.
        _ = (CatalogHost.Services, OrderingHost.Services, InventoryHost.Services, PaymentsHost.Services);
        _ = (ShippingHost.Services, NotificationsHost.Services);
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);

        foreach (IDisposable? host in new IDisposable?[]
        {
            NotificationsHost, ShippingHost, PaymentsHost, InventoryHost, OrderingHost, CatalogHost
        })
        {
            host?.Dispose();
        }

        foreach (OutboxGate gate in _gates.Values)
            gate.Dispose();

        Provider?.Stop();
        Carrier?.Stop();
        Contacts?.Stop();

        await Relay.DisposeAsync();
        await _coordination.DisposeAsync();
        await _cache.DisposeAsync();

        if (_broker is not null)
            await _broker.DisposeAsync();

        await _sql.DisposeAsync();
    }

    /// <summary>A product Catalog publishes, waited for until Ordering can price it.</summary>
    public async Task<Guid> PublishProductAsync(decimal amount)
    {
        using HttpClient catalog = ClientFor(CatalogHost, Guid.CreateVersion7(), "catalog:write");
        HttpResponseMessage published = await catalog.PostAsJsonAsync(
            "/v1/catalog/products",
            new
            {
                commandId = Guid.CreateVersion7(),
                name = "Walnut desk lamp",
                thumbnailUrl = (string?)null,
                amount,
                currency = Jurisdiction.Currency
            },
            TestContext.Current.CancellationToken);

        published.EnsureSuccessStatusCode();
        Guid product = await published.Content.ReadFromJsonAsync<Guid>(TestContext.Current.CancellationToken);

        await Convergence.UntilAsync(
            () => AnyAsync(Ordering, "SELECT 1 FROM ordering.ProductPrices WHERE ProductId = @p0", product),
            Deadlines.Projected,
            "the product's price reaching Ordering");

        return product;
    }

    /// <summary>Sets what Inventory holds on hand, as its administrator does.</summary>
    public async Task StockAsync(Guid product, int onHand)
    {
        using HttpClient inventory = ClientFor(InventoryHost, Guid.CreateVersion7(), "inventory:admin");
        HttpResponseMessage stocked = await inventory.PutAsJsonAsync(
            $"/v1/inventory/stock/{product}",
            new { onHand },
            TestContext.Current.CancellationToken);

        stocked.EnsureSuccessStatusCode();
    }

    /// <summary>A customer with a mailbox placing an order for one product, through Ordering as a person does.</summary>
    public async Task<JourneyOrder> PlaceAsync(
        Guid product,
        int quantity,
        decimal unitPrice,
        string? postalCode = null)
    {
        Guid customer = Guid.CreateVersion7();
        ContactAnswers(customer, JourneyOrder.MailboxOf(customer), Jurisdiction.Locale);

        using HttpClient ordering = ClientFor(OrderingHost, customer, "orders:write");
        HttpResponseMessage placed = await ordering.PostAsJsonAsync(
            "/v1/orders",
            new
            {
                commandId = Guid.CreateVersion7(),
                items = new[] { new { productId = product, quantity } },
                shippingAddress = new
                {
                    line1 = $"{customer:N} Journey Street",
                    line2 = (string?)null,
                    city = Jurisdiction.City,
                    postalCode = postalCode ?? Jurisdiction.PostalCode,
                    country = Jurisdiction.Country
                },
                currency = Jurisdiction.Currency
            },
            TestContext.Current.CancellationToken);

        placed.EnsureSuccessStatusCode();
        Guid id = await placed.Content.ReadFromJsonAsync<Guid>(TestContext.Current.CancellationToken);

        return new JourneyOrder(id, customer, product, quantity, unitPrice * quantity);
    }

    /// <summary>The customer cancelling, through the endpoint that is theirs to call (§11.4).</summary>
    public async Task<HttpResponseMessage> CancelAsync(JourneyOrder order)
    {
        using HttpClient ordering = ClientFor(OrderingHost, order.Customer, "orders:cancel");

        return await ordering.PostAsJsonAsync(
            $"/v1/orders/{order.Id}/cancel",
            new { reason = "customer_request" },
            TestContext.Current.CancellationToken);
    }

    /// <summary>The queues that hold a message nobody consumed: errors and skips, with what each holds.</summary>
    /// <remarks>
    /// A message lands there only when a consumer gave up on it (§9.8), so a scenario that meant nothing to fail
    /// finds the list empty. Read from the broker itself, as an operator would.
    /// </remarks>
    public async Task<IReadOnlyList<string>> DeadLettersAsync()
    {
        ExecResult listing = await _broker!.ExecAsync(
            ["rabbitmqctl", "list_queues", "--quiet", "--no-table-headers", "name", "messages"],
            TestContext.Current.CancellationToken);

        if (listing.ExitCode != 0)
            throw new InvalidOperationException($"Could not list the broker's queues: {listing.Stderr}");

        return
        [
            .. listing.Stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split('\t', StringSplitOptions.TrimEntries))
                .Where(columns => columns.Length == 2 &&
                    (columns[0].EndsWith("_error", StringComparison.Ordinal) ||
                        columns[0].EndsWith("_skipped", StringComparison.Ordinal)) &&
                    columns[1] != "0")
                .Select(columns => $"{columns[0]}: {columns[1]}")
        ];
    }

    /// <summary>The carrier reporting no collection for any parcel, until the returned scope is disposed.</summary>
    /// <remarks>
    /// The simulator holds no state, so its feed reports every booked parcel collected and delivered at the first
    /// poll (§14.1). A cancellation that has to meet a booked parcel needs one nobody has collected yet.
    /// </remarks>
    public IDisposable CarrierHoldsParcels()
    {
        Carrier
            .Given(Request.Create().WithPath("/v1/shipments/*/events").UsingGet())
            .AtPriority(0)
            .RespondWith(
                Response
                    .Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody("""{"events":[]}"""));

        return new CarrierRestore(this);
    }

    private sealed class CarrierRestore(JourneyWorld world) : IDisposable
    {
        public void Dispose()
        {
            world.Carrier.ResetMappings();
            world.Carrier.ReadStaticMappings(ShippingMappings.Directory());
        }
    }

    /// <summary>
    /// A wait expiring, delivered as the delayed exchange would deliver it, from Ordering's own bus under Ordering's
    /// own grant (ADR-021).
    /// </summary>
    /// <remarks>
    /// The waits are minutes and days (§9.6), so a scenario that is about what follows one cannot let it lapse. The
    /// message is the one the saga schedules, to the queue that binds it, so the saga cannot tell it from the clock.
    /// </remarks>
    public async Task ExpireAsync<T>(T expiry)
        where T : class
    {
        IBus bus = OrderingHost.Services.GetRequiredService<IBus>();

        await bus.Publish(expiry, c => c.MessageId = Guid.CreateVersion7(), TestContext.Current.CancellationToken);
    }

    /// <summary>Keycloak answering one user, with the mailbox the notices go to.</summary>
    public void ContactAnswers(Guid customer, string email, string locale)
    {
        JsonObject user = new()
        {
            ["id"] = customer.ToString("D"),
            ["username"] = $"customer-{customer:N}",
            ["firstName"] = "Aigerim",
            ["enabled"] = true,
            ["email"] = email,
            ["emailVerified"] = true,
            ["attributes"] = new JsonObject { ["locale"] = new JsonArray(locale) }
        };

        Contacts
            .Given(Request.Create().WithPath(NotificationsFixture.UserPath(customer)).UsingGet())
            .RespondWith(
                Response
                    .Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody(user.ToJsonString()));
    }

    /// <summary>A client for one host, as a person holding <paramref name="permissions"/> is.</summary>
    public static HttpClient ClientFor<T>(WebApplicationFactory<T> host, Guid user, string permissions)
        where T : class
    {
        HttpClient client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", user.ToString("D"));
        client.DefaultRequestHeaders.Add("X-Test-Permissions", permissions);

        return client;
    }

    public async Task<T> ScalarAsync<T>(string service, string sql, params object[] arguments)
    {
        await using SqlConnection connection = new(Connection(service));
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using SqlCommand command = new(sql, connection);
        for (int i = 0; i < arguments.Length; i++)
            command.Parameters.AddWithValue($"@p{i.ToString(CultureInfo.InvariantCulture)}", arguments[i]);

        object? value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);

        return value is null or DBNull ? default! : (T)value;
    }

    /// <summary>The first column of every row a query returns, as text.</summary>
    public async Task<IReadOnlyList<string>> ColumnAsync(string service, string sql, params object[] arguments)
    {
        await using SqlConnection connection = new(Connection(service));
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using SqlCommand command = new(sql, connection);
        for (int i = 0; i < arguments.Length; i++)
            command.Parameters.AddWithValue($"@p{i.ToString(CultureInfo.InvariantCulture)}", arguments[i]);

        List<string> values = [];
        await using SqlDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            values.Add(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!);

        return values;
    }

    /// <summary>Whether a query returned any row, which is every convergence predicate's shape.</summary>
    public async Task<bool> AnyAsync(string service, string sql, params object[] arguments) =>
        await ScalarAsync<int>(service, $"SELECT CASE WHEN EXISTS ({sql}) THEN 1 ELSE 0 END", arguments) == 1;

    private string Connection(string service)
    {
        SqlConnectionStringBuilder builder = new(_sqlConnection) { InitialCatalog = service };

        return builder.ConnectionString;
    }

    /// <summary>The broker as one service's account reaches it, under §14.1's local-development password.</summary>
    private string Account(string service)
    {
        UriBuilder uri = new(_broker!.GetConnectionString())
        {
            UserName = $"{service}-svc",
            Password = $"local-dev-{service}"
        };

        return uri.Uri.ToString();
    }

    private static async Task Migrated(Task<int> run, string service)
    {
        int exit = await run;
        if (exit != 0)
            throw new InvalidOperationException($"{service}'s migrator exited {exit}.");
    }

    private static WireMockServer Simulator(string mappings)
    {
        // Loopback, not every interface, which a workstation firewall stops to ask about.
        WireMockServer server = WireMockServer.Start(new WireMockServerSettings { Urls = ["http://127.0.0.1:0"] });
        server.ReadStaticMappings(mappings);

        return server;
    }

    private static string BrokerContextPath()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string context = Path.Combine(dir.FullName, "deploy", "compose", "rabbitmq");
            if (File.Exists(Path.Combine(dir.FullName, "Platform.slnx")) &&
                File.Exists(Path.Combine(context, "Dockerfile")))
            {
                return context;
            }
        }

        throw new InvalidOperationException(
            $"No Platform.slnx above {AppContext.BaseDirectory}; the broker image cannot be built.");
    }
}

/// <summary>The journey under the first deployment's answers (§12.1).</summary>
public sealed class FirstJurisdictionWorld() : JourneyWorld(Jurisdiction.First);

/// <summary>The journey under a second set, which agrees with the first on nothing a host reads (§12.1).</summary>
public sealed class SecondJurisdictionWorld() : JourneyWorld(Jurisdiction.Second);

/// <summary>One world for the journey suite, and a category every member class inherits (§12.4).</summary>
[CollectionDefinition(nameof(JourneyCollection), DisableParallelization = true)]
[Trait("Category", "Integration")]
public sealed class JourneyCollection : ICollectionFixture<FirstJurisdictionWorld>;

/// <summary>The second world, serial with the first so two sets of containers are never up at once.</summary>
[CollectionDefinition(nameof(SecondJurisdictionCollection), DisableParallelization = true)]
[Trait("Category", "Integration")]
public sealed class SecondJurisdictionCollection : ICollectionFixture<SecondJurisdictionWorld>;
