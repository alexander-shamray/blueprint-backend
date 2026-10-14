using Common.Application;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Notifications.Application.Devices;
using Notifications.Application.Devices.RegisterDevice;
using Notifications.Application.Devices.SignOutDevice;
using Notifications.Devices.V1;
using DevicesGrpc = Notifications.Devices.V1.Devices;

namespace Notifications.Api.Grpc;

/// <summary>ADR-117's registration and sign-out, a transport adapter under §4.2's gate.</summary>
/// <remarks>
/// Under the caller's own token, which the BFF forwards, so no credential the BFF holds can register a device to
/// anyone else; no permission, since any signed-in subject registers its own devices (ADR-117, §11.5).
/// </remarks>
[Authorize]
internal sealed class DevicesService(IDispatcher dispatcher, ICurrentUser user) : DevicesGrpc.DevicesBase
{
    public override async Task<DeviceReply> Register(RegisterRequest request, ServerCallContext context)
    {
        Result<DeviceWrite> written = await dispatcher.SendAsync(
            new RegisterDeviceCommand(
                user.Id,
                Installation(request.InstallationId),
                request.Platform,
                request.AppId,
                request.Token,
                request.Sequence),
            context.CancellationToken);

        return Reply(written.Value);
    }

    public override async Task<DeviceReply> SignOut(SignOutRequest request, ServerCallContext context)
    {
        Result<DeviceWrite> written = await dispatcher.SendAsync(
            new SignOutDeviceCommand(user.Id, Installation(request.InstallationId), request.Sequence),
            context.CancellationToken);

        return Reply(written.Value);
    }

    // Parsed here, as a wire concern; "D" only, since the contract says canonical text form.
    private static Guid Installation(string text) =>
        Guid.TryParseExact(text, "D", out Guid id)
            ? id
            : throw new RpcException(new Status(StatusCode.InvalidArgument, "installation_id is not a GUID."));

    private static DeviceReply Reply(DeviceWrite write) =>
        new()
        {
            Outcome = write.Outcome switch
            {
                DeviceWriteOutcome.Applied => DeviceReply.Types.Outcome.Applied,
                DeviceWriteOutcome.Repeated => DeviceReply.Types.Outcome.Repeated,
                _ => DeviceReply.Types.Outcome.Stale
            },
            Sequence = write.Sequence
        };
}
