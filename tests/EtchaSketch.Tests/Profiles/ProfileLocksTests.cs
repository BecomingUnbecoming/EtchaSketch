using System.Text;
using EtchaSketch.Data.Profiles;
using EtchaSketch.Data.Storage;
using EtchaSketch.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace EtchaSketch.Tests.Profiles;

/// <summary>A person's PIN on this device: how it's kept, checked, and slowed down after wrong guesses.</summary>
public sealed class ProfileLocksTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Directory.CreateTempSubdirectory("etchasketch-locks-").FullName;
    private readonly InMemoryKeyStore _keys = new();
    private readonly List<SqliteConnection> _connections = [];

    public void Dispose()
    {
        foreach (var connection in _connections)
        {
            connection.Dispose();
        }

        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    private SqliteConnection Open()
    {
        var db = EncryptedDatabase.Open(Path.Combine(_directory, "etchasketch.db"), _keys);
        Schema.Apply(db);
        _connections.Add(db);
        return db;
    }

    // Few rounds keep the tests quick; the app uses ProfileLocks.DefaultIterations.
    private static ProfileLocks Locks(SqliteConnection db) => new(db, iterations: 1_000);

    private static Guid Person(SqliteConnection db, string name = "Robin")
    {
        var households = new HouseholdRepository(db);
        return households.All() is [var home, ..]
            ? households.AddMember(home.Id, name).Id
            : households.Create("Home", name).Members[0].Id;
    }

    private static void GuessWrong(ProfileLocks locks, Guid person, int times, DateTimeOffset at)
    {
        for (var i = 0; i < times; i++)
        {
            Assert.Equal(PinOutcome.Wrong, locks.Check(person, "0000", at).Outcome);
        }
    }

    [Fact]
    public void A_person_without_a_PIN_is_not_locked()
    {
        var db = Open();
        var robin = Person(db);

        Assert.False(Locks(db).HasPin(robin));
    }

    [Fact]
    public void The_right_PIN_unlocks_and_a_wrong_one_does_not()
    {
        var db = Open();
        var robin = Person(db);
        var locks = Locks(db);

        locks.SetPin(robin, "4821");

        Assert.True(locks.HasPin(robin));
        Assert.Equal(PinOutcome.Correct, locks.Check(robin, "4821", Now).Outcome);
        Assert.Equal(PinOutcome.Wrong, locks.Check(robin, "4822", Now).Outcome);
    }

    [Theory]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("12a4")]
    [InlineData("1234567890123")]
    public void A_PIN_is_4_to_12_digits(string pin)
    {
        var db = Open();
        var robin = Person(db);

        var error = Assert.Throws<ArgumentException>(() => Locks(db).SetPin(robin, pin));
        Assert.StartsWith("A PIN is 4 to 12 digits.", error.Message);
    }

    [Fact]
    public void The_PIN_itself_is_never_stored()
    {
        var db = Open();
        var robin = Person(db);
        var kai = Person(db, "Kai");
        var locks = Locks(db);

        locks.SetPin(robin, "482193");
        locks.SetPin(kai, "482193");

        using var command = db.CreateCommand();
        command.CommandText = "SELECT * FROM profile_locks";
        using var reader = command.ExecuteReader();
        var hashes = new List<byte[]>();
        while (reader.Read())
        {
            for (var column = 0; column < reader.FieldCount; column++)
            {
                var value = reader.GetValue(column);
                var text = value is byte[] bytes ? Encoding.UTF8.GetString(bytes) + Convert.ToHexString(bytes) : Convert.ToString(value);
                Assert.DoesNotContain("482193", text);
            }

            hashes.Add((byte[])reader["pin_hash"]);
        }

        // Each person's PIN is salted, so the same PIN doesn't give the same hash.
        Assert.Equal(2, hashes.Count);
        Assert.NotEqual(hashes[0], hashes[1]);
    }

    [Fact]
    public void Five_wrong_guesses_mean_a_wait_even_for_the_right_PIN()
    {
        var db = Open();
        var robin = Person(db);
        var locks = Locks(db);
        locks.SetPin(robin, "4821");

        GuessWrong(locks, robin, 4, Now);
        var fifth = locks.Check(robin, "0000", Now);
        Assert.Equal(PinOutcome.Wait, fifth.Outcome);
        Assert.Equal(Now.AddSeconds(30), fifth.TryAgainAt);

        var waiting = locks.Check(robin, "4821", Now.AddSeconds(10));
        Assert.Equal(PinOutcome.Wait, waiting.Outcome);
        Assert.Equal(Now.AddSeconds(30), waiting.TryAgainAt);

        Assert.Equal(PinOutcome.Correct, locks.Check(robin, "4821", Now.AddSeconds(30)).Outcome);
    }

    [Fact]
    public void The_wait_doubles_with_each_wrong_guess_after_that()
    {
        var db = Open();
        var robin = Person(db);
        var locks = Locks(db);
        locks.SetPin(robin, "4821");

        GuessWrong(locks, robin, 4, Now);
        Assert.Equal(Now.AddSeconds(30), locks.Check(robin, "0000", Now).TryAgainAt);

        var later = Now.AddSeconds(30);
        Assert.Equal(later.AddSeconds(60), locks.Check(robin, "0000", later).TryAgainAt);
    }

    [Fact]
    public void The_right_PIN_clears_earlier_wrong_guesses()
    {
        var db = Open();
        var robin = Person(db);
        var locks = Locks(db);
        locks.SetPin(robin, "4821");

        GuessWrong(locks, robin, 4, Now);
        Assert.Equal(PinOutcome.Correct, locks.Check(robin, "4821", Now).Outcome);

        GuessWrong(locks, robin, 4, Now);
        Assert.Equal(PinOutcome.Correct, locks.Check(robin, "4821", Now).Outcome);
    }

    [Fact]
    public void Restarting_the_app_does_not_reset_the_wait()
    {
        var db = Open();
        var robin = Person(db);
        var locks = Locks(db);
        locks.SetPin(robin, "4821");
        GuessWrong(locks, robin, 4, Now);
        locks.Check(robin, "0000", Now);
        db.Dispose();
        SqliteConnection.ClearAllPools();

        Assert.Equal(PinOutcome.Wait, Locks(Open()).Check(robin, "4821", Now.AddSeconds(5)).Outcome);
    }

    [Fact]
    public void Removing_the_PIN_unlocks_the_profile_and_turns_off_device_unlock()
    {
        var db = Open();
        var robin = Person(db);
        var locks = Locks(db);
        locks.SetPin(robin, "4821");
        locks.SetDeviceUnlock(robin, true);

        locks.RemovePin(robin);

        Assert.False(locks.HasPin(robin));
        Assert.False(locks.UsesDeviceUnlock(robin));
    }

    [Fact]
    public void Device_unlock_needs_a_PIN_to_fall_back_on()
    {
        var db = Open();
        var robin = Person(db);
        var locks = Locks(db);

        Assert.Throws<InvalidOperationException>(() => locks.SetDeviceUnlock(robin, true));

        locks.SetPin(robin, "4821");
        locks.SetDeviceUnlock(robin, true);
        Assert.True(locks.UsesDeviceUnlock(robin));
    }

    [Fact]
    public void Unlocking_another_way_clears_wrong_guesses()
    {
        var db = Open();
        var robin = Person(db);
        var locks = Locks(db);
        locks.SetPin(robin, "4821");
        GuessWrong(locks, robin, 4, Now);
        locks.Check(robin, "0000", Now);

        locks.UnlockedAnotherWay(robin);

        Assert.Equal(PinOutcome.Correct, locks.Check(robin, "4821", Now).Outcome);
    }
}
