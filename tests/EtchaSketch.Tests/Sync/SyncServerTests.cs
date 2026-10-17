using System.Net;
using System.Net.Http.Json;
using EtchaSketch.Core.Sync;

namespace EtchaSketch.Tests.Sync;

public sealed class SyncServerTests : IAsyncLifetime
{
    private const string Stream = "c3RyZWFtLWZvci10ZXN0cw";
    private const string Token = "a-long-random-access-token";

    private SyncServerFixture _server = null!;

    public async Task InitializeAsync() => _server = await SyncServerFixture.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task Changes_come_back_in_order_after_a_position()
    {
        var client = _server.Client();
        await client.PushAsync(Stream, Token, [[1], [2], [3]]);

        var all = await client.PullAsync(Stream, Token, after: 0);
        var later = await client.PullAsync(Stream, Token, after: all.Changes[0].Seq);

        Assert.Equal([[1], [2], [3]], all.Changes.Select(change => change.Data));
        Assert.Equal([[2], [3]], later.Changes.Select(change => change.Data));
    }

    [Fact]
    public async Task A_stream_belongs_to_whoever_wrote_it_first()
    {
        var client = _server.Client();
        await client.PushAsync(Stream, Token, [[1]]);

        await Assert.ThrowsAsync<SyncAccessDeniedException>(() => client.PushAsync(Stream, "someone-else", [[2]]));
        await Assert.ThrowsAsync<SyncAccessDeniedException>(() => client.PullAsync(Stream, "someone-else", after: 0));
    }

    [Fact]
    public async Task An_unknown_stream_is_simply_empty()
    {
        Assert.Empty((await _server.Client().PullAsync(Stream, Token, after: 0)).Changes);
    }

    [Fact]
    public async Task Requests_without_a_token_are_refused()
    {
        var response = await _server.HttpClient().GetAsync($"/v1/streams/{Stream}/changes?after=0");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Malformed_stream_ids_are_refused()
    {
        var http = _server.HttpClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", Token);

        var response = await http.PostAsJsonAsync("/v1/streams/no!/changes", new PushRequest([]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Long_streams_come_back_in_pages()
    {
        var client = _server.Client();
        await client.PushAsync(Stream, Token, Enumerable.Range(0, 600).Select(i => new[] { (byte)i }).ToList());

        var first = await client.PullAsync(Stream, Token, after: 0);

        Assert.True(first.More);
        Assert.Equal(500, first.Changes.Count);
        Assert.Equal(100, (await client.PullAsync(Stream, Token, first.Changes[^1].Seq)).Changes.Count);
    }

    [Fact]
    public async Task A_pairing_can_be_claimed_only_once()
    {
        var client = _server.Client();
        await client.PutPairingAsync("0123456789abcdef0123456789abcdef", [9, 9]);

        Assert.Equal([9, 9], await client.ClaimPairingAsync("0123456789abcdef0123456789abcdef"));
        Assert.Null(await client.ClaimPairingAsync("0123456789abcdef0123456789abcdef"));
    }

    [Fact]
    public async Task A_pairing_expires_after_fifteen_minutes()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        await using var server = await SyncServerFixture.StartAsync(clock);
        var client = server.Client();
        await client.PutPairingAsync("0123456789abcdef0123456789abcdef", [9]);

        clock.Now += TimeSpan.FromMinutes(16);

        Assert.Null(await client.ClaimPairingAsync("0123456789abcdef0123456789abcdef"));
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
