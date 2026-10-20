using System;
using EtchaSketch.Data.Storage;
using EtchaSketch.Data.Sync;
using EtchaSketch.UI.Services;

namespace EtchaSketch.UI;

/// <summary>What each platform's app supplies before starting: where data lives and how keys are kept.</summary>
/// <param name="Clock">The clock to read "now" from. Null means the system clock; tests pass a fixed one.</param>
/// <param name="SyncServerFactory">Connects to a sync server at an address. Null means over HTTP; tests pass an in-memory server.</param>
/// <param name="DeviceUnlock">The device's fingerprint or screen lock, where the platform offers one. Null means PIN only.</param>
/// <param name="ReminderScheduler">The device's alarm clock for reminders. Null means reminders show only inside the app.</param>
public sealed record AppEnvironment(
    string DatabasePath,
    IKeyStore KeyStore,
    TimeProvider? Clock = null,
    Func<Uri, ISyncServer>? SyncServerFactory = null,
    IDeviceUnlock? DeviceUnlock = null,
    IReminderScheduler? ReminderScheduler = null);
