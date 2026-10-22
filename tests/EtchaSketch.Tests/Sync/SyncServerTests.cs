using System.Net;
using System.Net.Http.Json;
using EtchaSketch.Core.Sync;
using EtchaSketch.Data.Sync;
using EtchaSketch.Server;

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
        for (var batch = 0; batch < 3; batch++)
        {
            await client.PushAsync(Stream, Token, Enumerable.Range(0, 200).Select(i => new[] { (byte)i }).ToList());
        }

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

    private static Task<SyncServerFixture> WithLimits(SyncLimits limits) => SyncServerFixture.StartAsync(limits: limits);

    private static string StreamNamed(int number) => $"stream-number-{number:D4}-padding";

    private static byte[] Bytes(int length) => new byte[length];

    [Fact]
    public async Task One_oversized_change_refuses_the_whole_push_and_stores_nothing()
    {
        await using var server = await WithLimits(new SyncLimits { MaxChangeBytes = 100 });
        var client = server.Client();

        var refused = await Assert.ThrowsAsync<SyncServerRefusedException>(() => client.PushAsync(Stream, Token, [Bytes(10), Bytes(101)]));

        Assert.Contains("no room", refused.Message);
        Assert.Empty((await client.PullAsync(Stream, Token, after: 0)).Changes);
    }

    [Fact]
    public async Task A_push_with_too_many_changes_is_refused()
    {
        await using var server = await WithLimits(new SyncLimits { MaxChangesPerPush = 3 });
        var client = server.Client();

        await client.PushAsync(Stream, Token, [[1], [2], [3]]);
        await Assert.ThrowsAsync<SyncServerRefusedException>(() => client.PushAsync(Stream, Token, [[1], [2], [3], [4]]));
    }

    [Fact]
    public async Task A_stream_stops_taking_changes_at_its_quota_but_still_reads()
    {
        await using var server = await WithLimits(new SyncLimits { MaxStreamBytes = 100 });
        var client = server.Client();
        await client.PushAsync(Stream, Token, [Bytes(60)]);

        await Assert.ThrowsAsync<SyncServerRefusedException>(() => client.PushAsync(Stream, Token, [Bytes(41)]));
        await client.PushAsync(Stream, Token, [Bytes(40)]);

        Assert.Equal(2, (await client.PullAsync(Stream, Token, after: 0)).Changes.Count);
    }

    [Fact]
    public async Task The_server_stops_creating_streams_at_its_limit_but_existing_ones_keep_working()
    {
        await using var server = await WithLimits(new SyncLimits { MaxStreams = 2 });
        var client = server.Client();
        await client.PushAsync(StreamNamed(1), Token, [[1]]);
        await client.PushAsync(StreamNamed(2), Token, [[1]]);

        await Assert.ThrowsAsync<SyncServerRefusedException>(() => client.PushAsync(StreamNamed(3), Token, [[1]]));
        await client.PushAsync(StreamNamed(1), Token, [[2]]);
        Assert.Empty((await client.PullAsync(StreamNamed(3), Token, after: 0)).Changes);
    }

    [Fact]
    public async Task Only_so_many_new_device_codes_can_wait_at_once_and_claiming_makes_room()
    {
        await using var server = await WithLimits(new SyncLimits { MaxPendingPairings = 2 });
        var client = server.Client();
        await client.PutPairingAsync("00000000000000000000000000000001", [1]);
        await client.PutPairingAsync("00000000000000000000000000000002", [1]);

        await Assert.ThrowsAsync<SyncServerRefusedException>(() => client.PutPairingAsync("00000000000000000000000000000003", [1]));
        await client.ClaimPairingAsync("00000000000000000000000000000001");
        await client.PutPairingAsync("00000000000000000000000000000003", [1]);
    }

    [Fact]
    public async Task Too_many_requests_from_one_address_are_turned_away()
    {
        await using var server = await WithLimits(new SyncLimits { RequestsPerMinute = 3 });
        var client = server.Client();

        for (var request = 0; request < 3; request++)
        {
            await client.PullAsync(Stream, Token, after: 0);
        }

        var refused = await Assert.ThrowsAsync<SyncServerRefusedException>(() => client.PullAsync(Stream, Token, after: 0));
        Assert.Contains("too many requests", refused.Message);
    }

    [Fact]
    public async Task Health_checks_are_not_rate_limited()
    {
        await using var server = await WithLimits(new SyncLimits { RequestsPerMinute = 1 });
        var http = server.HttpClient();

        for (var request = 0; request < 5; request++)
        {
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health")).StatusCode);
        }
    }

    [Fact]
    public async Task With_a_join_token_set_a_stranger_cannot_start_a_stream_or_leave_a_code()
    {
        await using var server = await SyncServerFixture.StartAsync(joinToken: "the-join-token");
        var stranger = server.Client();

        var refusedPush = await Assert.ThrowsAsync<SyncServerRefusedException>(() => stranger.PushAsync(Stream, Token, [[1]]));
        var refusedCode = await Assert.ThrowsAsync<SyncServerRefusedException>(() => stranger.PutPairingAsync("0123456789abcdef0123456789abcdef", [1]));

        Assert.Contains("join token", refusedPush.Message);
        Assert.Contains("join token", refusedCode.Message);
        Assert.Empty((await stranger.PullAsync(Stream, Token, after: 0)).Changes);
    }

    [Fact]
    public async Task A_wrong_join_token_is_turned_away_like_none()
    {
        await using var server = await SyncServerFixture.StartAsync(joinToken: "the-join-token");
        var client = server.Client();
        client.JoinToken = "a-guess";

        await Assert.ThrowsAsync<SyncServerRefusedException>(() => client.PushAsync(Stream, Token, [[1]]));
    }

    [Fact]
    public async Task The_right_join_token_lets_a_device_start_things_and_existing_streams_need_no_token()
    {
        await using var server = await SyncServerFixture.StartAsync(joinToken: "the-join-token");
        var member = server.Client();
        member.JoinToken = "the-join-token";
        await member.PushAsync(Stream, Token, [[1]]);
        await member.PutPairingAsync("0123456789abcdef0123456789abcdef", [9]);

        var newDevice = server.Client();
        Assert.Equal([9], await newDevice.ClaimPairingAsync("0123456789abcdef0123456789abcdef"));
        await newDevice.PushAsync(Stream, Token, [[2]]);
        Assert.Equal(2, (await newDevice.PullAsync(Stream, Token, after: 0)).Changes.Count);
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Once_a_device_is_removed_only_registered_devices_get_in()
    {
        var client = _server.Client();
        await client.PushAsync(Stream, Token, [[1]]);
        Assert.True(await client.SupportsDevicesAsync());
        Assert.Equal(DeviceOutcome.Done, await client.RegisterDeviceAsync(Stream, Token, "desktop-0001", "desktop-token-000001"));
        Assert.Equal(DeviceOutcome.Done, await client.RegisterDeviceAsync(Stream, Token, "lost-phone-01", "phone-token-0000001"));

        Assert.Equal(DeviceOutcome.Done, await client.RemoveDeviceAsync(Stream, "desktop-token-000001", "lost-phone-01"));

        await Assert.ThrowsAsync<SyncAccessDeniedException>(() => client.PullAsync(Stream, "phone-token-0000001", after: 0));
        await Assert.ThrowsAsync<SyncAccessDeniedException>(() => client.PullAsync(Stream, Token, after: 0));
        Assert.Equal(DeviceOutcome.Denied, await client.RegisterDeviceAsync(Stream, Token, "lost-phone-01", "phone-token-0000002"));
        Assert.Single((await client.PullAsync(Stream, "desktop-token-000001", after: 0)).Changes);
    }

    [Fact]
    public async Task A_one_time_pass_lets_one_new_device_in_once()
    {
        var client = _server.Client();
        await client.PushAsync(Stream, Token, [[1]]);
        await client.RegisterDeviceAsync(Stream, Token, "desktop-0001", "desktop-token-000001");
        await client.RegisterDeviceAsync(Stream, Token, "old-phone-001", "old-phone-token-0001");
        await client.RemoveDeviceAsync(Stream, "desktop-token-000001", "old-phone-001");

        Assert.Equal(DeviceOutcome.Done, await client.AddEnrolmentAsync(Stream, "desktop-token-000001", "a-one-time-pass-0001"));
        Assert.Equal(DeviceOutcome.Done, await client.RegisterDeviceAsync(Stream, "a-one-time-pass-0001", "new-phone-001", "new-phone-token-0001"));
        Assert.Equal(DeviceOutcome.Denied, await client.RegisterDeviceAsync(Stream, "a-one-time-pass-0001", "sneaky-00001", "sneaky-token-000001"));
        Assert.Equal(DeviceOutcome.Denied, await client.AddEnrolmentAsync(Stream, "old-phone-token-0001", "another-pass-00001"));
        Assert.Single((await client.PullAsync(Stream, "new-phone-token-0001", after: 0)).Changes);
    }

    [Fact]
    public async Task The_last_device_on_a_stream_cant_be_removed()
    {
        var client = _server.Client();
        await client.PushAsync(Stream, Token, [[1]]);
        await client.RegisterDeviceAsync(Stream, Token, "only-device-1", "only-device-token-01");

        Assert.Equal(DeviceOutcome.LastDevice, await client.RemoveDeviceAsync(Stream, "only-device-token-01", "only-device-1"));
        Assert.Single((await client.PullAsync(Stream, Token, after: 0)).Changes);
    }
}
