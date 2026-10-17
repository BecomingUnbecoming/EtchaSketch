using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EtchaSketch.Core.Sync;

namespace EtchaSketch.Data.Sync;

/// <summary>Talks to an EtchaSketch sync server over HTTP. The client's base address is the server's address.</summary>
public sealed class HttpSyncServer(HttpClient http) : ISyncServer
{
    private const string JoinTokenHeader = "X-Join-Token";

    public string? JoinToken { get; set; }

    public async Task PushAsync(string streamId, string accessToken, IReadOnlyList<byte[]> changes, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"v1/streams/{streamId}/changes")
        {
            Content = JsonContent.Create(new PushRequest(changes)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        AddJoinToken(request);
        using var response = await http.SendAsync(request, cancellationToken);
        EnsureAllowed(response, streamId);
    }

    public async Task<PullResponse> PullAsync(string streamId, string accessToken, long after, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"v1/streams/{streamId}/changes?after={after}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await http.SendAsync(request, cancellationToken);
        EnsureAllowed(response, streamId);
        return await response.Content.ReadFromJsonAsync<PullResponse>(cancellationToken)
               ?? throw new InvalidDataException("The sync server sent an empty reply.");
    }

    public async Task PutPairingAsync(string pairingId, byte[] sealedKeys, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"v1/pairings/{pairingId}")
        {
            Content = JsonContent.Create(new PairingRequest(sealedKeys)),
        };
        AddJoinToken(request);
        using var response = await http.SendAsync(request, cancellationToken);
        EnsureNotRefused(response);
        response.EnsureSuccessStatusCode();
    }

    public async Task<byte[]?> ClaimPairingAsync(string pairingId, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync($"v1/pairings/{pairingId}/claim", content: null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        EnsureNotRefused(response);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PairingRequest>(cancellationToken))?.Data;
    }

    private static void EnsureAllowed(HttpResponseMessage response, string streamId)
    {
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new SyncAccessDeniedException(streamId);
        }

        EnsureNotRefused(response);
        response.EnsureSuccessStatusCode();
    }

    private void AddJoinToken(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(JoinToken))
        {
            request.Headers.TryAddWithoutValidation(JoinTokenHeader, JoinToken);
        }
    }

    private static void EnsureNotRefused(HttpResponseMessage response)
    {
        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized when response.Headers.WwwAuthenticate.Any(challenge => challenge.Scheme == "JoinToken"):
                throw new SyncServerRefusedException("This sync server asks for a join token, and this device's is missing or wrong. Enter the server's join token in Sync settings.");
            case HttpStatusCode.TooManyRequests:
                throw new SyncServerRefusedException("The sync server is getting too many requests from this device. Wait a minute and try again.");
            case HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.InsufficientStorage:
                throw new SyncServerRefusedException("The sync server has no room for this. Its limits are set in its settings; see docs/sync-server.md.");
        }
    }
}
