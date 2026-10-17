using System.Text.RegularExpressions;
using EtchaSketch.Core.Sync;

namespace EtchaSketch.Server;

/// <summary>The sync server's HTTP endpoints. Devices authenticate to a stream with a bearer token.</summary>
public static partial class SyncApi
{
    public static WebApplication MapSyncApi(this WebApplication app)
    {
        app.MapGet("/health", () => "ok");

        app.MapPost("/v1/streams/{streamId}/changes", (string streamId, PushRequest body, HttpRequest request, SyncStore store, SyncAccess access, HttpResponse response) =>
        {
            if (!StreamId().IsMatch(streamId))
            {
                return Results.BadRequest();
            }

            if (BearerToken(request) is not { } token)
            {
                return Results.Unauthorized();
            }

            return store.Push(streamId, token, body.Changes, mayCreateStream: access.Allows(request)) switch
            {
                PushResult.NeedsJoinToken => NeedsJoinToken(response),
                PushResult.Stored => Results.NoContent(),
                PushResult.Denied => Results.StatusCode(StatusCodes.Status403Forbidden),
                PushResult.TooLarge => Results.StatusCode(StatusCodes.Status413PayloadTooLarge),
                _ => Results.StatusCode(StatusCodes.Status507InsufficientStorage),
            };
        }).RequireRateLimiting(SyncRateLimits.General);

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
        }).RequireRateLimiting(SyncRateLimits.General);

        app.MapPut("/v1/pairings/{pairingId}", (string pairingId, PairingRequest body, HttpRequest request, HttpResponse response, SyncStore store, SyncAccess access) =>
        {
            if (!PairingId().IsMatch(pairingId))
            {
                return Results.BadRequest();
            }

            if (!access.Allows(request))
            {
                return NeedsJoinToken(response);
            }

            return store.PutPairing(pairingId, body.Data) switch
            {
                PairingResult.Stored => Results.NoContent(),
                PairingResult.Taken => Results.Conflict(),
                PairingResult.TooLarge => Results.StatusCode(StatusCodes.Status413PayloadTooLarge),
                _ => Results.StatusCode(StatusCodes.Status507InsufficientStorage),
            };
        }).RequireRateLimiting(SyncRateLimits.Pairing);

        app.MapPost("/v1/pairings/{pairingId}/claim", (string pairingId, SyncStore store) =>
        {
            if (!PairingId().IsMatch(pairingId))
            {
                return Results.BadRequest();
            }

            return store.ClaimPairing(pairingId) is { } data ? Results.Ok(new PairingRequest(data)) : Results.NotFound();
        }).RequireRateLimiting(SyncRateLimits.Pairing);

        return app;
    }

    private static IResult NeedsJoinToken(HttpResponse response)
    {
        response.Headers.WWWAuthenticate = SyncAccess.Challenge;
        return Results.StatusCode(StatusCodes.Status401Unauthorized);
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
