using System;
using EtchaSketch.Data.Storage;

namespace EtchaSketch.UI;

/// <summary>What each platform's app supplies before starting: where data lives and how keys are kept.</summary>
/// <param name="Clock">The clock to read "now" from. Null means the system clock; tests pass a fixed one.</param>
public sealed record AppEnvironment(string DatabasePath, IKeyStore KeyStore, TimeProvider? Clock = null);
