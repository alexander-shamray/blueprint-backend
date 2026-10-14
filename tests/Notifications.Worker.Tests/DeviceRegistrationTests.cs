using Common.Application;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Devices;
using Notifications.Application.Devices.RegisterDevice;
using Notifications.Application.Devices.SignOutDevice;
using Notifications.Application.Privacy.ErasePersonalData;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>
/// ADR-117's device rows against a real database, through the shared store both hosts compose; the API host's own
/// suite holds the transport above it. The worker's suite has the migrated database, so the rows are proved here.
/// </summary>
[Collection(nameof(IntegrationCollection))]
public sealed class DeviceRegistrationTests(ServiceFixture fixture) : IAsyncLifetime
{
    private const string Platform = "fcm";
    private const string App = "com.example.shop";

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<DeviceWrite> SendAsync(ICommand<Result<DeviceWrite>> command)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        Result<DeviceWrite> written = await dispatcher.SendAsync(command, TestContext.Current.CancellationToken);

        return written.Value;
    }

    private Task<DeviceWrite> RegisterAsync(Guid subject, Guid installation, string token, long sequence) =>
        SendAsync(new RegisterDeviceCommand(subject, installation, Platform, App, token, sequence));

    private Task<DeviceWrite> SignOutAsync(Guid subject, Guid installation, long sequence) =>
        SendAsync(new SignOutDeviceCommand(subject, installation, sequence));

    private Task<string?> TokenAsync(Guid installation) =>
        fixture.ScalarAsync<string?>(
            "SELECT Value = Token FROM notifications.DeviceInstallations WHERE InstallationId = {0}", installation);

    private Task<Guid?> SubjectAsync(Guid installation) =>
        fixture.ScalarAsync<Guid?>(
            "SELECT Value = SubjectId FROM notifications.DeviceInstallations WHERE InstallationId = {0}", installation);

    private Task<long> SequenceAsync(Guid installation) =>
        fixture.ScalarAsync<long>(
            "SELECT Value = Sequence FROM notifications.DeviceInstallations WHERE InstallationId = {0}", installation);

    [Fact]
    public async Task A_first_registration_writes_the_row_and_its_repeat_is_answered_as_it_was()
    {
        Guid subject = Guid.CreateVersion7();
        Guid installation = Guid.CreateVersion7();

        (await RegisterAsync(subject, installation, "token-a", 1))
            .ShouldBe(new DeviceWrite(DeviceWriteOutcome.Applied, 1));
        (await RegisterAsync(subject, installation, "token-a", 1))
            .ShouldBe(new DeviceWrite(DeviceWriteOutcome.Repeated, 1), "a repeat asks for what the row holds");

        (await TokenAsync(installation)).ShouldBe("token-a");
        (await SubjectAsync(installation)).ShouldBe(subject);
    }

    [Fact]
    public async Task A_rotation_replaces_the_token_in_the_same_row()
    {
        Guid subject = Guid.CreateVersion7();
        Guid installation = Guid.CreateVersion7();
        await RegisterAsync(subject, installation, "token-a", 1);

        (await RegisterAsync(subject, installation, "token-b", 2)).Outcome.ShouldBe(DeviceWriteOutcome.Applied);

        (await TokenAsync(installation)).ShouldBe("token-b");
        (await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM notifications.DeviceInstallations"))
            .ShouldBe(1, "keyed by the installation, so a rotation leaves no row sending the old token");
    }

    [Theory]
    [InlineData(4, "token-a", "below the row's")]
    [InlineData(5, "token-b", "equal to the row's but asking for something else")]
    [InlineData(1006, "token-b", "past the step above the row's")]
    public async Task A_stale_registration_changes_nothing_and_is_told_the_rows_sequence(
        long sequence, string token, string because)
    {
        Guid subject = Guid.CreateVersion7();
        Guid installation = Guid.CreateVersion7();
        await RegisterAsync(subject, installation, "token-a", 5);

        (await RegisterAsync(subject, installation, token, sequence))
            .ShouldBe(new DeviceWrite(DeviceWriteOutcome.Stale, 5), because);

        (await TokenAsync(installation)).ShouldBe("token-a");
        (await SequenceAsync(installation)).ShouldBe(5);
    }

    [Fact]
    public async Task A_second_subject_signing_in_takes_the_device()
    {
        // The device shows whoever signed in last and must never show the one before (ADR-117).
        Guid before = Guid.CreateVersion7();
        Guid after = Guid.CreateVersion7();
        Guid installation = Guid.CreateVersion7();
        await RegisterAsync(before, installation, "token-a", 1);

        (await RegisterAsync(after, installation, "token-a", 2)).Outcome.ShouldBe(DeviceWriteOutcome.Applied);

        (await SubjectAsync(installation)).ShouldBe(after);
    }

    [Fact]
    public async Task A_token_that_reappears_under_a_new_installation_leaves_the_old_one()
    {
        Guid subject = Guid.CreateVersion7();
        Guid reinstalled = Guid.CreateVersion7();
        Guid old = Guid.CreateVersion7();
        await RegisterAsync(subject, old, "token-a", 3);

        (await RegisterAsync(subject, reinstalled, "token-a", 1)).Outcome.ShouldBe(DeviceWriteOutcome.Applied);

        (await TokenAsync(old)).ShouldBeNull("the platform, app id and token are unique across the table");
        (await SequenceAsync(old)).ShouldBe(3, "cleared as a sign-out clears, so a stale retry is still refused");
        (await TokenAsync(reinstalled)).ShouldBe("token-a");
    }

    [Fact]
    public async Task A_registration_past_the_cap_clears_the_row_seen_least_recently()
    {
        Guid subject = Guid.CreateVersion7();
        Guid[] installations = [.. Enumerable.Range(0, 10).Select(_ => Guid.CreateVersion7())];
        for (int i = 0; i < installations.Length; i++)
        {
            await RegisterAsync(subject, installations[i], $"token-{i}", 1);

            // Spread, so least recently seen is one row and not a tie: the first is the oldest by i hours.
            await fixture.ExecuteAsync(
                "UPDATE notifications.DeviceInstallations SET LastSeenAt = DATEADD(hour, {0}, LastSeenAt) " +
                "WHERE InstallationId = {1}",
                i - installations.Length,
                installations[i]);
        }

        await RegisterAsync(subject, Guid.CreateVersion7(), "token-10", 1);

        (await TokenAsync(installations[0])).ShouldBeNull("ten is DeviceTokenOptions.MaxPerSubject's default");
        (await TokenAsync(installations[1])).ShouldBe("token-1");
        (await fixture.ScalarAsync<int>(
                "SELECT Value = COUNT(*) FROM notifications.DeviceInstallations WHERE SubjectId = {0}", subject))
            .ShouldBe(10);
    }

    [Fact]
    public async Task A_sign_out_clears_the_row_to_its_id_and_sequence_and_its_repeat_is_answered_as_it_was()
    {
        Guid subject = Guid.CreateVersion7();
        Guid installation = Guid.CreateVersion7();
        await RegisterAsync(subject, installation, "token-a", 1);

        (await SignOutAsync(subject, installation, 2)).ShouldBe(new DeviceWrite(DeviceWriteOutcome.Applied, 2));
        (await SignOutAsync(subject, installation, 2)).ShouldBe(new DeviceWrite(DeviceWriteOutcome.Repeated, 2));

        (await TokenAsync(installation)).ShouldBeNull();
        (await SubjectAsync(installation)).ShouldBeNull();
        (await SequenceAsync(installation)).ShouldBe(2);

        // A registration retried from before the sign-out carries a lower sequence and cannot undo it (ADR-117).
        (await RegisterAsync(subject, installation, "token-a", 1)).Outcome.ShouldBe(DeviceWriteOutcome.Stale);
    }

    [Fact]
    public async Task A_sign_out_touches_only_a_row_of_the_callers_own()
    {
        Guid owner = Guid.CreateVersion7();
        Guid installation = Guid.CreateVersion7();
        await RegisterAsync(owner, installation, "token-a", 1);

        (await SignOutAsync(Guid.CreateVersion7(), installation, 2)).Outcome.ShouldBe(DeviceWriteOutcome.Applied);
        (await SignOutAsync(owner, Guid.CreateVersion7(), 1))
            .Outcome.ShouldBe(DeviceWriteOutcome.Applied, "no row is answered as one cleared would be");

        (await TokenAsync(installation)).ShouldBe("token-a");
        (await SequenceAsync(installation)).ShouldBe(1);

        // Nor a cleared row, which is nobody's: its sequence stays where the owner's sign-out left it.
        await SignOutAsync(owner, installation, 2);
        (await SignOutAsync(Guid.CreateVersion7(), installation, 3)).Outcome.ShouldBe(DeviceWriteOutcome.Applied);
        (await SequenceAsync(installation)).ShouldBe(2);
    }

    [Fact]
    public async Task A_sign_out_is_ordered_against_a_cleared_row_as_against_any_other()
    {
        Guid subject = Guid.CreateVersion7();
        Guid installation = Guid.CreateVersion7();
        await RegisterAsync(subject, installation, "token-a", 1);
        await SignOutAsync(subject, installation, 5);

        (await SignOutAsync(subject, installation, 4)).ShouldBe(new DeviceWrite(DeviceWriteOutcome.Stale, 5));
        (await SignOutAsync(Guid.CreateVersion7(), installation, 4))
            .ShouldBe(new DeviceWrite(DeviceWriteOutcome.Stale, 5), "a cleared row is nobody's, so anyone is ordered");
        (await SignOutAsync(subject, installation, 5 + 1001)).ShouldBe(new DeviceWrite(DeviceWriteOutcome.Stale, 5));
        (await SignOutAsync(subject, installation, 6)).ShouldBe(new DeviceWrite(DeviceWriteOutcome.Applied, 6));

        (await SequenceAsync(installation)).ShouldBe(5, "an applied sign-out of a cleared row writes nothing");
    }

    [Fact]
    public async Task An_erasure_clears_the_subjects_rows_and_keeps_their_sequences()
    {
        Guid subject = Guid.CreateVersion7();
        Guid installation = Guid.CreateVersion7();
        await RegisterAsync(subject, installation, "token-a", 4);

        await using (AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope())
        {
            Result<int> erased = await scope.ServiceProvider.GetRequiredService<IDispatcher>().SendAsync(
                new ErasePersonalDataCommand(Guid.CreateVersion7(), subject),
                TestContext.Current.CancellationToken);

            erased.Value.ShouldBe(1);
        }

        (await TokenAsync(installation)).ShouldBeNull();
        (await SubjectAsync(installation)).ShouldBeNull();
        (await SequenceAsync(installation))
            .ShouldBe(4, "so a retry of a registration applied before the erasure changes nothing");
        (await RegisterAsync(subject, installation, "token-a", 4)).Outcome.ShouldBe(DeviceWriteOutcome.Stale);
    }

    [Fact]
    public async Task The_retention_pass_deletes_a_row_unseen_for_the_stale_window_and_keeps_a_fresher_one()
    {
        Guid stale = Guid.CreateVersion7();
        Guid fresher = Guid.CreateVersion7();
        await RegisterAsync(Guid.CreateVersion7(), stale, "token-a", 1);
        await RegisterAsync(Guid.CreateVersion7(), fresher, "token-b", 1);
        await fixture.ExecuteAsync(
            "UPDATE notifications.DeviceInstallations SET LastSeenAt = DATEADD(day, -31, LastSeenAt) " +
            "WHERE InstallationId = {0}",
            stale);
        await fixture.ExecuteAsync(
            "UPDATE notifications.DeviceInstallations SET LastSeenAt = DATEADD(day, -29, LastSeenAt) " +
            "WHERE InstallationId = {0}",
            fresher);

        (_, _, _, int devices) = await fixture.PurgeNotificationsRetentionAsync();

        devices.ShouldBe(1, "thirty days is DeviceTokenOptions.StaleAfter's default");
        (await fixture.ScalarAsync<int>(
                "SELECT Value = COUNT(*) FROM notifications.DeviceInstallations WHERE InstallationId = {0}", stale))
            .ShouldBe(0);
        (await TokenAsync(fresher)).ShouldBe("token-b");
    }

    [Fact]
    public async Task No_log_line_holds_a_token()
    {
        const string marker = "value-that-must-never-be-logged";
        fixture.CapturedLogs.Clear();

        await RegisterAsync(Guid.CreateVersion7(), Guid.CreateVersion7(), marker, 1);

        fixture.CapturedLogs.Everything.ShouldNotContain(line => line.Contains(marker), "§13.4, ADR-117");
        fixture.CapturedLogs.Everything.ShouldContain(line => line.Contains(nameof(RegisterDeviceCommand)));
    }
}
