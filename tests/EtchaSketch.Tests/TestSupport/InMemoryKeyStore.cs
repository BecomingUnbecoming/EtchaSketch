using EtchaSketch.Data.Storage;

namespace EtchaSketch.Tests.TestSupport;

/// <summary>A key store for tests that keeps keys in memory instead of the system's secure storage.</summary>
internal sealed class InMemoryKeyStore : IKeyStore
{
    private readonly Dictionary<string, byte[]> _keys = [];

    public byte[]? Load(string name) => _keys.TryGetValue(name, out var key) ? key.ToArray() : null;

    public void Save(string name, byte[] key) => _keys[name] = key.ToArray();

    public void Delete(string name) => _keys.Remove(name);
}
