using System.Text.RegularExpressions;
using EtchaSketch.Core.Sync;

namespace EtchaSketch.Server;

/// <summary>The sync server's HTTP endpoints. Devices authenticate to a stream with a bearer token.</summary>
public static partial class SyncApi
{
    public static WebApplication MapSyncApi(this WebApplication app)
    {
        app.MapPost("/v1/streams/{streamId}/changes", (string streamId, PushRequest body, HttpRequest request, SyncStore store) =>
        {
            if (!StreamId().IsMatch(streamId))
            {
                return Results.BadRequest();
            }

            if (BearerToken(request) is not { } token)
            {
                return Results.Unauthorized();
            }

            return store.Push(streamId, token, body.Changes) ? Results.NoContent() : Results.StatusCode(StatusCodes.Status403Forbidden);
        });

        app.MapGet("/v1/streams/{streamId}/changes", (string streamId, long? after, HttpRequest request, SyncStore store) =>
        {
            if (!StreamId().IsMatch(streamId))
            {
                return Results.BadRequest();
            }

            if (BearerToken(request) is not { } token)
            {
                return Results.Unauthorized();
            }

            return store.Pull(streamId, token, after ?? 0) is { } page
                ? Results.Ok(page)
                : Results.StatusCode(StatusCodes.Status403Forbidden);
        });

        app.MapPut("/v1/pairings/{pairingId}", (string pairingId, PairingRequest body, SyncStore store) =>
        {
            if (!PairingId().IsMatch(pairingId))
            {
                return Results.BadRequest();
            }

            return store.PutPairing(pairingId, body.Data) ? Results.NoContent() : Results.Conflict();
        });

        app.MapPost("/v1/pairings/{pairingId}/claim", (string pairingId, SyncStore store) =>
        {
            if (!PairingId().IsMatch(pairingId))
            {
                return Results.BadRequest();
            }

            return store.ClaimPairing(pairingId) is { } data ? Results.Ok(new PairingRequest(data)) : Results.NotFound();
        });

        return app;
    }

    private static string? BearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.Ordinal) && header.Length > "Bearer ".Length
            ? header["Bearer ".Length..]
            : null;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{16,64}$")]
    private static partial Regex StreamId();

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex PairingId();
}
