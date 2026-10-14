using Common.Web;
using FluentValidation;
using Notifications.Devices.V1;
using Web.Bff.Devices;
using DevicesGrpc = Notifications.Devices.V1.Devices;

namespace Web.Bff.Endpoints;

/// <summary>§10.10's device registration: one hop to Notifications, under the caller's own token (ADR-117).</summary>
public static class DeviceEndpoints
{
    /// <summary>The 409's code, which tells an app to set its counter one past the reply's sequence (§10.5).</summary>
    public const string StaleSequenceCode = "device.stale_sequence";

    public static void MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app
            .MapGroup("/v1/devices")
            .WithTags("Devices")
            // Fail closed at the group (§11.4); the subject is the forwarded token's, never the request's.
            .RequireAuthorization();

        group
            .MapPut(
                "/{installationId:guid}",
                async (
                    Guid installationId,
                    DeviceRegistration request,
                    IValidator<DeviceRegistration> validator,
                    DevicesGrpc.DevicesClient devices,
                    CancellationToken ct) =>
                {
                    await validator.ValidateAndThrowAsync(request, ct);

                    DeviceReply reply = await devices.RegisterAsync(
                        new RegisterRequest
                        {
                            // "D", the canonical form devices.proto names, whatever form the route bound.
                            InstallationId = installationId.ToString("D"),
                            Platform = request.Platform,
                            AppId = request.AppId,
                            Token = request.Token,
                            Sequence = request.Sequence
                        },
                        cancellationToken: ct);

                    return Answer(reply, request.Sequence);
                })
            // Ordered by the installation's sequence, so a repeat is answered as the first was (ADR-117).
            .RetrySafe(RetrySafety.Convergent)
            .WithName("PutDevice");

        group
            .MapDelete(
                "/{installationId:guid}",
                async (
                    Guid installationId,
                    long sequence,
                    IValidator<DeviceSignOut> validator,
                    DevicesGrpc.DevicesClient devices,
                    CancellationToken ct) =>
                {
                    await validator.ValidateAndThrowAsync(new DeviceSignOut(sequence), ct);

                    DeviceReply reply = await devices.SignOutAsync(
                        new SignOutRequest { InstallationId = installationId.ToString("D"), Sequence = sequence },
                        cancellationToken: ct);

                    return Answer(reply, sequence);
                })
            .RetrySafe(RetrySafety.Convergent)
            .WithName("DeleteDevice");
    }

    /// <summary>204 for a write applied or repeated, 409 with the row's sequence for a stale one (ADR-117).</summary>
    /// <remarks>Checked rather than trusted, as §9.7's rule 6 has every reply; a violation is a 500.</remarks>
    private static IResult Answer(DeviceReply reply, long sequence)
    {
        switch (reply.Outcome)
        {
            case DeviceReply.Types.Outcome.Applied or DeviceReply.Types.Outcome.Repeated
                when reply.Sequence == sequence:
                return Results.NoContent();

            // A stale write names a row, and a row's sequence is one some request carried, so it is positive.
            case DeviceReply.Types.Outcome.Stale when reply.Sequence > 0:
                return Results.Problem(
                    detail: "The installation's sequence is out of order. Send the present state again with a " +
                            "sequence one past the one this reply carries.",
                    statusCode: StatusCodes.Status409Conflict,
                    extensions: new Dictionary<string, object?>
                    {
                        ["code"] = StaleSequenceCode,
                        ["sequence"] = reply.Sequence
                    });

            default:
                throw new InvalidOperationException(
                    $"Notifications answered a device write of sequence {sequence} with outcome " +
                    $"{reply.Outcome} and sequence {reply.Sequence}. devices.proto gives an applied or repeated " +
                    "write the request's own sequence and a stale one the row's, so this is a contract " +
                    "violation rather than an answer.");
        }
    }
}
