using Common.Infrastructure.Transport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>ADR-079's rule, connection by connection, and the start-up check that applies it.</summary>
public sealed class TransportSecurityTests
{
    private const string Secret = "s3cret-value";

    private static string? Refusal(string environment, params (string Key, string? Value)[] settings)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        return TransportSecurity.Refusal(configuration, new TestEnvironment("t") { EnvironmentName = environment });
    }

    private static string? InProduction(string name, string value) =>
        Refusal(Environments.Production, ($"ConnectionStrings:{name}", value));

    [Theory]
    [InlineData("RabbitMq", $"amqp://svc:{Secret}@broker:5672")]
    [InlineData("RedisCache", $"redis-cache:6379,user=svc,password={Secret}")]
    [InlineData("RedisCoordination", $"redis-coordination:6379,ssl=false,password={Secret}")]
    [InlineData("Ordering", $"Server=sql;Database=Ordering;User Id=svc;Password={Secret};Encrypt=False")]
    [InlineData("Ordering", $"Server=sql;Database=Ordering;Password={Secret};Encrypt=Optional")]
    [InlineData("OrderingMigrator", $"Server=sql;Database=Ordering;Password={Secret};TrustServerCertificate=True")]
    [InlineData("Ordering", $"Server=sql;Password={Secret};Trust Server Certificate=yes")]
    [InlineData("Ordering", "Server=sql;Password=\"unterminated")]
    public void A_plaintext_or_unverified_hop_is_refused_outside_development(string name, string value)
    {
        string? refusal = InProduction(name, value);

        refusal.ShouldNotBeNull();
        refusal.ShouldStartWith($"ConnectionStrings:{name} ");
        refusal.ShouldContain(TransportSecurity.PlaintextKey);
        refusal.ShouldNotContain(Secret, Case.Insensitive, "a refusal is logged, and the value carries the credential");
    }

    [Theory]
    [InlineData("RabbitMq", "amqps://svc:p@broker:5671")]
    [InlineData("RedisCache", "redis-cache:6380,ssl=true,user=svc,password=p")]
    [InlineData("Ordering", "Server=sql;Database=Ordering;User Id=svc;Password=p")]
    [InlineData("Ordering", "Server=sql;Database=Ordering;Password=p;Encrypt=Strict")]
    [InlineData("Ordering", "Server=sql;Database=Ordering;Password=p;Encrypt=True;TrustServerCertificate=False")]
    public void An_encrypted_and_verified_hop_is_accepted(string name, string value) =>
        InProduction(name, value).ShouldBeNull();

    [Fact]
    public void Development_accepts_the_compose_strings()
    {
        Refusal(
                Environments.Development,
                ("ConnectionStrings:RabbitMq", "amqp://svc:p@rabbitmq:5672"),
                ("ConnectionStrings:Ordering", "Server=sql;Password=p;TrustServerCertificate=True"))
            .ShouldBeNull();
    }

    [Fact]
    public void A_connection_named_as_plaintext_is_accepted_and_no_other_is()
    {
        (string, string?)[] settings =
        [
            ("ConnectionStrings:RabbitMq", "amqp://svc:p@rabbitmq:5672"),
            ("ConnectionStrings:Ordering", "Server=sql;Password=p;TrustServerCertificate=True"),
            ($"{TransportSecurity.PlaintextKey}:0", "RabbitMq"),
        ];

        string? refusal = Refusal(Environments.Production, settings);

        refusal.ShouldNotBeNull();
        refusal.ShouldStartWith("ConnectionStrings:Ordering ");
        Refusal(Environments.Production, [.. settings, ($"{TransportSecurity.PlaintextKey}:1", "Ordering")])
            .ShouldBeNull();
    }

    [Fact]
    public void Staging_is_not_development()
    {
        Refusal("Staging", ("ConnectionStrings:RabbitMq", "amqp://svc:p@broker:5672")).ShouldNotBeNull();
    }

    [Fact]
    public async Task The_check_stops_the_host_before_any_hosted_service_starts()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings { EnvironmentName = Environments.Production });
        builder.Configuration["ConnectionStrings:RabbitMq"] = "amqp://svc:p@broker:5672";
        builder.Services.AddTransportSecurity();
        builder.Services.AddTransportSecurity();
        RecordingService recorder = new();
        builder.Services.AddSingleton<IHostedService>(recorder);
        using IHost host = builder.Build();

        OptionsValidationException refused = await Should.ThrowAsync<OptionsValidationException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));

        refused.Message.ShouldContain("ConnectionStrings:RabbitMq is not an amqps:// address");
        recorder.Started.ShouldBeFalse("a hosted service that connects would already have sent the credential");
    }

    [Fact]
    public void Every_project_that_reads_a_connection_string_applies_the_check()
    {
        // The subject is the tree: a host added later is held to the rule by this, not by a list beside it.
        string root = RepositoryRoot();
        string[] projects =
        [
            .. Directory.GetDirectories(Path.Combine(root, "src", "Services"), "*", SearchOption.TopDirectoryOnly)
                .SelectMany(Directory.GetDirectories),
            .. Directory.GetDirectories(Path.Combine(root, "src", "BFF")),
        ];
        string[] reading = [.. projects.Where(p => Sources(p).Any(s => s.Contains("GetConnectionString(")))];

        reading.ShouldNotBeEmpty("a scan that found no reader would pass every tree");
        // A migrator reaches the check through its own Infrastructure, the one path §4.2 leaves it; a wrapper
        // defined in a project is not the project applying it, so each kind is held to its own call.
        reading.Where(p => !Sources(p).Any(s => s.Contains(p.EndsWith(".Migrator", StringComparison.Ordinal)
                ? "MigratorTransport.Refusal("
                : "services.AddTransportSecurity()")))
            .Select(p => Path.GetRelativePath(root, p))
            .ShouldBeEmpty();
    }

    private static IEnumerable<string> Sources(string project) =>
        Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(File.ReadAllText);

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Platform.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException($"No Platform.slnx above {AppContext.BaseDirectory}.");
    }

    private sealed class RecordingService : IHostedService
    {
        public bool Started { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
