using System.Text;
using EtchaSketch.Data.Storage;
using Microsoft.Data.Sqlite;

namespace EtchaSketch.Tests.Storage;

public sealed class EncryptedDatabaseTests : IDisposable
{
    private const string Secret = "my door code is 4821";

    private readonly string _directory = Directory.CreateTempSubdirectory("etchasketch-tests-").FullName;
    private readonly InMemoryKeyStore _keys = new();

    private string DatabasePath => Path.Combine(_directory, "etchasketch.db");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private void WriteSecret(IKeyStore keys)
    {
        using var db = EncryptedDatabase.Open(DatabasePath, keys);
        Execute(db, "CREATE TABLE notes (body TEXT NOT NULL)");
        Execute(db, $"INSERT INTO notes (body) VALUES ('{Secret}')");
    }

    private static void Execute(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    [Fact]
    public void Data_reads_back_with_the_same_key()
    {
        WriteSecret(_keys);

        using var db = EncryptedDatabase.Open(DatabasePath, _keys);
        Assert.Equal(Secret, Scalar(db, "SELECT body FROM notes"));
    }

    [Fact]
    public void The_file_on_disk_contains_nothing_readable()
    {
        WriteSecret(_keys);

        var bytes = File.ReadAllBytes(DatabasePath);
        var text = Encoding.Latin1.GetString(bytes);
        Assert.DoesNotContain(Secret, text);
        Assert.DoesNotContain("notes", text);
        Assert.DoesNotContain("SQLite format 3", text);
    }

    [Fact]
    public void A_copied_file_cannot_be_opened_with_another_key()
    {
        WriteSecret(_keys);

        var otherMachine = new InMemoryKeyStore();
        otherMachine.Save(EncryptedDatabase.KeyName, new byte[32]);

        Assert.Throws<SqliteException>(() => EncryptedDatabase.Open(DatabasePath, otherMachine));
    }

    [Fact]
    public void A_copied_file_cannot_be_read_without_any_key()
    {
        WriteSecret(_keys);

        using var plain = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadOnly;Pooling=False");
        plain.Open();
        Assert.Throws<SqliteException>(() => Scalar(plain, "SELECT body FROM notes"));
    }

    [Fact]
    public void An_existing_database_with_no_key_is_refused_instead_of_given_a_new_key()
    {
        WriteSecret(_keys);

        var emptyStore = new InMemoryKeyStore();
        Assert.Throws<DatabaseKeyMissingException>(() => EncryptedDatabase.Open(DatabasePath, emptyStore));
        Assert.Null(emptyStore.Load(EncryptedDatabase.KeyName));
    }

    [Fact]
    public void A_new_database_gets_a_random_256_bit_key_that_is_reused()
    {
        WriteSecret(_keys);
        var first = _keys.Load(EncryptedDatabase.KeyName);

        using (EncryptedDatabase.Open(DatabasePath, _keys)) { }

        Assert.NotNull(first);
        Assert.Equal(32, first.Length);
        Assert.Equal(first, _keys.Load(EncryptedDatabase.KeyName));
        Assert.NotEqual(new byte[32], first);
    }

    [Fact]
    public void Two_new_databases_get_different_keys()
    {
        var otherKeys = new InMemoryKeyStore();
        using (EncryptedDatabase.Open(DatabasePath, _keys)) { }
        using (EncryptedDatabase.Open(Path.Combine(_directory, "other.db"), otherKeys)) { }

        Assert.NotEqual(_keys.Load(EncryptedDatabase.KeyName), otherKeys.Load(EncryptedDatabase.KeyName));
    }

    [Fact]
    public void The_connection_is_backed_by_sqlcipher()
    {
        using var db = EncryptedDatabase.Open(DatabasePath, _keys);

        Assert.False(string.IsNullOrEmpty(Scalar(db, "PRAGMA cipher_version") as string));
    }

    private sealed class InMemoryKeyStore : IKeyStore
    {
        private readonly Dictionary<string, byte[]> _keys = [];

        public byte[]? Load(string name) => _keys.TryGetValue(name, out var key) ? key.ToArray() : null;

        public void Save(string name, byte[] key) => _keys[name] = key.ToArray();

        public void Delete(string name) => _keys.Remove(name);
    }
}
