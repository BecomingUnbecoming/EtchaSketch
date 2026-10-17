using System;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Hardware.Biometrics;
using EtchaSketch.UI.Services;

namespace EtchaSketch.Android;

/// <summary>
/// The phone's fingerprint, face or screen lock, through Android's own BiometricPrompt. It is part
/// of Android itself, so it works on GrapheneOS without Google Play Services. Needs Android 11.
/// </summary>
public sealed class AndroidDeviceUnlock(Context context) : IDeviceUnlock
{
    private const int Authenticators = (int)(BiometricManagerAuthenticators.BiometricStrong | BiometricManagerAuthenticators.DeviceCredential);

    /// <summary>The activity on screen, which the prompt is shown over. Set by <see cref="MainActivity"/>.</summary>
    public static Activity? CurrentActivity { get; set; }

    public bool IsAvailable =>
        OperatingSystem.IsAndroidVersionAtLeast(30)
        && context.GetSystemService(Context.BiometricService) is BiometricManager manager
        && manager.CanAuthenticate(Authenticators) == BiometricCode.Success;

    public Task<bool> ConfirmAsync(string reason, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(30) || CurrentActivity is not { } activity)
        {
            return Task.FromResult(false);
        }

        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancel = new global::Android.OS.CancellationSignal();
        cancellationToken.Register(cancel.Cancel);

        var prompt = new BiometricPrompt.Builder(activity)
            .SetTitle("EtchaSketch")
            .SetSubtitle(reason)
            .SetAllowedAuthenticators(Authenticators)
            .Build();
        prompt.Authenticate(cancel, activity.MainExecutor!, new Callback(result));
        return result.Task;
    }

    private sealed class Callback(TaskCompletionSource<bool> result) : BiometricPrompt.AuthenticationCallback
    {
        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult? authenticationResult) =>
            result.TrySetResult(true);

        // Cancelled, too many tries, or no way to check. One unrecognised finger is not an error:
        // the prompt stays up for another try.
        public override void OnAuthenticationError(BiometricErrorCode errorCode, Java.Lang.ICharSequence? errString) =>
            result.TrySetResult(false);
    }
}
