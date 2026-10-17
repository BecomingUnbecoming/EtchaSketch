using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EtchaSketch.Core.Sync;

namespace EtchaSketch.Data.Sync;

/// <summary>Talks to an EtchaSketch sync server over HTTP. The client's base address is the server's address.</summary>
public sealed class HttpSyncServer(HttpClient http) : ISyncServer
{
    public async Task PushAsync(string streamId, string accessToken, IReadOnlyList<byte[]> changes, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"v1/streams/{streamId}/changes")
        {
            Content = JsonContent.Create(new PushRequest(changes)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
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
        using var response = await http.PutAsJsonAsync($"v1/pairings/{pairingId}", new PairingRequest(sealedKeys), cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<byte[]?> ClaimPairingAsync(string pairingId, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync($"v1/pairings/{pairingId}/claim", content: null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PairingRequest>(cancellationToken))?.Data;
    }

    private static void EnsureAllowed(HttpResponseMessage response, string streamId)
    {
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new SyncAccessDeniedException(streamId);
        }

        response.EnsureSuccessStatusCode();
    }
}
