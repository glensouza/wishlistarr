using Wishlistarr.Data;

namespace Wishlistarr.Api.Kids;

public static class KidDeviceEndpoints
{
    public static void MapParentKidDevices(this RouteGroupBuilder parent)
    {
        RouteGroupBuilder devices = parent.MapGroup("/kid-devices").WithTags("Kid devices");

        devices.MapGet("/", async (HttpContext context, KidDevices service, CancellationToken ct) =>
        {
            KidDevice? current = await service.FindCurrentAsync(context, ct);
            return await service.ListAsync(current?.Id, ct);
        })
            .WithName("ListKidDevices")
            .WithSummary("Lists kid devices with their assigned profiles and pairing state. Tokens and codes are never returned.")
            .Produces<IReadOnlyList<KidDeviceSummary>>();

        devices.MapPost("/", async (KidDeviceRequest? request, KidDevices service, CancellationToken ct) =>
        {
            if (request is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "A device is required.");
            }

            DeviceOutcome outcome = await service.CreateAsync(request, ct);
            return outcome is { Device: KidDeviceSummary device, Code: PairingCode code }
                ? Results.Created($"/api/v1/kid-devices/{device.Id}", new KidDevicePairing(device, code.Code, code.ExpiresAt))
                : Results.Problem(statusCode: outcome.Status, title: outcome.Error);
        })
            .WithName("CreateKidDevice")
            .WithSummary("Adds a kid device with its profiles and returns a short-lived, single-use pairing code. The code is shown only once.")
            .Produces<KidDevicePairing>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        devices.MapPut("/{id}", async (int id, KidDeviceRequest? request, HttpContext context, KidDevices service, CancellationToken ct) =>
        {
            if (request is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "A device is required.");
            }

            KidDevice? current = await service.FindCurrentAsync(context, ct);
            DeviceOutcome outcome = await service.UpdateAsync(id, request, current?.Id, ct);
            return outcome.Device is null
                ? Results.Problem(statusCode: outcome.Status, title: outcome.Error)
                : Results.Ok(outcome.Device);
        })
            .WithName("UpdateKidDevice")
            .WithSummary("Renames a kid device and replaces its assigned profiles. Kid sessions for removed profiles end.")
            .Produces<KidDeviceSummary>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        devices.MapPost("/{id}/pairing-code", async (int id, KidDevices service, CancellationToken ct) =>
            await service.IssueCodeAsync(id, ct) is PairingCode code
                ? Results.Ok(code)
                : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Device not found."))
            .WithName("CreateKidDevicePairingCode")
            .WithSummary("Issues a new pairing code for a device, replacing any earlier code. Redeeming it replaces the device's token.")
            .Produces<PairingCode>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        devices.MapDelete("/{id}", async (int id, KidDevices service, CancellationToken ct) =>
            await service.RevokeAsync(id, ct)
                ? Results.NoContent()
                : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Device not found."))
            .WithName("RevokeKidDevice")
            .WithSummary("Revokes a kid device. Its token and any kid sessions on it stop working immediately.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }
}
