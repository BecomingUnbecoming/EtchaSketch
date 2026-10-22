namespace EtchaSketch.Tests.TestSupport;

/// <summary>
/// Tests that hand the app something from outside, like "Done" on a notification or a scanned
/// label. Those go through one queue shared by every open app, so these tests run with no other
/// test's app open; otherwise that app could take the request first.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AppScreens
{
    public const string Name = "App screens";
}
