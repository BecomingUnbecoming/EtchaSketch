using System.Threading;
using System.Threading.Tasks;

namespace EtchaSketch.UI.Services;

/// <summary>The device's own way of checking it's you, like a fingerprint or the phone's screen lock.</summary>
public interface IDeviceUnlock
{
    /// <summary>Whether this device has one set up right now.</summary>
    bool IsAvailable { get; }

    /// <summary>Asks the device to check. True only when it confirmed; false when it failed or was cancelled.</summary>
    Task<bool> ConfirmAsync(string reason, CancellationToken cancellationToken = default);
}
