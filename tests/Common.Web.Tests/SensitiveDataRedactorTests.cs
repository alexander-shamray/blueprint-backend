using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

public class SensitiveDataRedactorTests
{
    // LoggerMessage.Define, because CA1848 binds test projects too (ADR-019).
    private static readonly Action<ILogger, string, string, Exception?> Login =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(1, nameof(Login)),
            "Login for {User} with {Password}");

    private static readonly Action<ILogger, string, Exception?> Failed =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(7, nameof(Failed)),
            "Login failed with {Password}");

    private static readonly Action<ILogger, string, Exception?> Plain =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(2, nameof(Plain)),
            "Customer {Customer} signed in");

    private static LogRecord EmitRecord(Action<ILogger> write)
    {
        List<LogRecord> exported = [];

        // Built as AddObservability builds it (§13.2), IncludeFormattedMessage included, so this is the host's seam.
        using (ILoggerFactory factory = LoggerFactory.Create(b =>
            b.AddOpenTelemetry(o =>
            {
                o.IncludeFormattedMessage = true;
                o.AddProcessor(new SensitiveDataRedactor());
                o.AddInMemoryExporter(exported);
            })))
        {
            write(factory.CreateLogger("test"));
        }

        return exported.Single();
    }

    private static IReadOnlyList<KeyValuePair<string, object?>> Emit(Action<ILogger> write) =>
        EmitRecord(write).Attributes!;

    [Fact]
    public void Sensitive_attributes_are_redacted()
    {
        IReadOnlyList<KeyValuePair<string, object?>> attributes =
            Emit(logger => Login(logger, "ada", "hunter2", null));

        attributes.Single(a => a.Key == "Password").Value.ShouldBe("[redacted]");

        // Everything not on the deny list survives intact.
        attributes.Single(a => a.Key == "User").Value.ShouldBe("ada");
    }

    [Fact]
    public void A_redacted_record_does_not_export_the_rendered_secret()
    {
        // The exporter sends FormattedMessage as the body (§13.2), so the rendered secret must go too.
        LogRecord record = EmitRecord(logger => Login(logger, "ada", "hunter2", null));

        record.FormattedMessage.ShouldNotBeNull();
        record.FormattedMessage.ShouldNotContain("hunter2");

        // The template keeps the record readable, and the safe values stay as attributes.
        record.FormattedMessage.ShouldBe("Login for {User} with {Password}");
    }

    [Fact]
    public void A_record_with_nothing_sensitive_keeps_its_formatted_message()
    {
        // The control: a processor that rewrote every message would pass the test above.
        LogRecord record = EmitRecord(logger => Plain(logger, "ada", null));

        record.FormattedMessage.ShouldBe("Customer ada signed in");
    }

    [Fact]
    public void Matching_is_by_substring_and_ignores_case()
    {
        // An explicit state, because CA1727 forbids `card_number` as a placeholder.
        KeyValuePair<string, object?>[] state =
        [
            new("NewPassword", "a"),
            new("card_number", "b"),
            new("Authorization", "c"),
            new("Customer", "ada")
        ];

        IReadOnlyList<KeyValuePair<string, object?>> attributes = Emit(logger =>
            logger.Log(LogLevel.Information, new EventId(3), state, null, (_, _) => "signed in"));

        attributes.Single(a => a.Key == "NewPassword").Value.ShouldBe("[redacted]");
        attributes.Single(a => a.Key == "card_number").Value.ShouldBe("[redacted]");
        attributes.Single(a => a.Key == "Authorization").Value.ShouldBe("[redacted]");
        attributes.Single(a => a.Key == "Customer").Value.ShouldBe("ada");
    }

    [Fact]
    public void A_connection_string_is_redacted_whatever_its_key_is_called()
    {
        // The value half: no term in the vocabulary is a substring of "Dsn".
        KeyValuePair<string, object?>[] state =
        [
            // Spaced separator, which ADO.NET accepts and a literal "password=" check does not see.
            new("Dsn", "Server=sql,1433;Database=Catalog;User Id=sa;Password = hunter2"),
            new("Customer", "ada")
        ];

        IReadOnlyList<KeyValuePair<string, object?>> attributes = Emit(logger =>
            logger.Log(LogLevel.Error, new EventId(4), state, null, (_, _) => "cannot reach it"));

        attributes.Single(a => a.Key == "Dsn").Value.ShouldBe("[redacted]");
        attributes.Single(a => a.Key == "Customer").Value.ShouldBe("ada");
    }

    [Fact]
    public void The_template_is_not_redacted_by_its_own_text()
    {
        // {OriginalFormat} is exempt from the value check, since the template is the rewrite's fallback.
        KeyValuePair<string, object?>[] state =
        [
            new("Host", "sql"),
            new("{OriginalFormat}", "connecting with password= from {Host}")
        ];

        LogRecord record = EmitRecord(logger =>
            logger.Log(
                LogLevel.Information,
                new EventId(5),
                state,
                null,
                (_, _) => "connecting with password= from sql"));

        record.Attributes!.Single(a => a.Key == "{OriginalFormat}").Value
            .ShouldBe("connecting with password= from {Host}");
        record.FormattedMessage.ShouldBe("connecting with password= from sql");
    }

    [Fact]
    public void A_record_with_no_attributes_at_all_is_left_alone()
    {
        // The guard clause, reachable through ILogger with a null state.
        List<LogRecord> exported = [];

        using (ILoggerFactory factory = LoggerFactory.Create(b =>
            b.AddOpenTelemetry(o =>
            {
                o.AddProcessor(new SensitiveDataRedactor());
                o.AddInMemoryExporter(exported);
            })))
        {
            factory.CreateLogger("test").Log<object?>(
                LogLevel.Information,
                new EventId(4),
                null,
                null,
                (_, _) => "no state");
        }

        exported.Single().Attributes.ShouldBeNull();
    }

    // Capturing processors either side, with no exporter, because the in-memory exporter copies every record.
    private static (object? Before, object? After) AttributesEitherSideOf(Action<ILogger> write)
    {
        object? before = null;
        object? after = null;

        using (ILoggerFactory factory = LoggerFactory.Create(b =>
            b.AddOpenTelemetry(o =>
            {
                o.AddProcessor(new CapturingProcessor(r => before = r.Attributes));
                o.AddProcessor(new SensitiveDataRedactor());
                o.AddProcessor(new CapturingProcessor(r => after = r.Attributes));
            })))
        {
            write(factory.CreateLogger("test"));
        }

        return (before, after);
    }

    [Fact]
    public void A_record_with_nothing_sensitive_is_not_copied()
    {
        (object? before, object? after) =
            AttributesEitherSideOf(logger => Plain(logger, "ada", null));

        // Same instance, not merely an equal one: the processor did not allocate.
        after.ShouldBeSameAs(before);
    }

    [Fact]
    public void A_record_with_something_sensitive_is_copied()
    {
        // The control, without which a redactor that never redacts passes the test above.
        (object? before, object? after) =
            AttributesEitherSideOf(logger => Login(logger, "ada", "hunter2", null));

        after.ShouldNotBeSameAs(before);
    }

    [Fact]
    public void A_state_with_no_template_loses_its_message_rather_than_re_exporting_it()
    {
        // Without {OriginalFormat} the SDK fills Body with rendered output, so Body is no safe fallback.
        KeyValuePair<string, object?>[] state = [new("Password", "hunter2")];

        LogRecord record = EmitRecord(logger =>
            logger.Log(LogLevel.Information, new EventId(6), state, null, (s, _) => $"password is {s[0].Value}"));

        record.Attributes!.Single(a => a.Key == "Password").Value.ShouldBe("[redacted]");
        record.FormattedMessage.ShouldBe("[redacted]");
    }

    [Fact]
    public void An_exception_repeating_a_redacted_value_is_dropped()
    {
        // OTLP serialises the exception as a third channel beside Attributes and FormattedMessage.
        LogRecord record = EmitRecord(logger =>
            Failed(logger, "hunter2", new InvalidOperationException("auth rejected token hunter2")));

        record.Attributes!.Single(a => a.Key == "Password").Value.ShouldBe("[redacted]");
        record.Exception.ShouldBeNull();
    }

    [Fact]
    public void An_exception_that_reveals_nothing_is_kept()
    {
        // The control: an unrelated failure keeps its stack trace beside a redacted attribute.
        InvalidOperationException failure = new("connection reset by peer");

        LogRecord record = EmitRecord(logger => Failed(logger, "hunter2", failure));

        record.Attributes!.Single(a => a.Key == "Password").Value.ShouldBe("[redacted]");
        record.Exception.ShouldBeSameAs(failure);
    }

    [Fact]
    public void No_other_logging_provider_survives_to_see_the_rendered_secret()
    {
        // A provider outside the pipeline would ship the secret, so AddObservability clears them (§13.4).
        HostApplicationBuilder builder = TelemetryHost.Builder();
        CapturingProvider console = new();
        builder.Logging.AddProvider(console);

        builder.AddObservability();

        using IHost host = builder.Build();
        Login(host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("test"), "ada", "hunter2", null);

        console.Messages.ShouldBeEmpty();
    }

    private sealed class CapturingProvider : ILoggerProvider
    {
        internal readonly ConcurrentQueue<string> Messages = new();

        public ILogger CreateLogger(string categoryName) => new Sink(Messages);

        public void Dispose()
        {
        }

        private sealed class Sink(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception));
        }
    }

    private sealed class CapturingProcessor(Action<LogRecord> capture) : BaseProcessor<LogRecord>
    {
        public override void OnEnd(LogRecord record) => capture(record);
    }
}
