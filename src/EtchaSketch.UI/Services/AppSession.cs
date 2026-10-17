using System;
using System.IO;
using EtchaSketch.Data.Profiles;
using EtchaSketch.Data.Storage;
using EtchaSketch.Data.Tasks;
using Microsoft.Data.Sqlite;

namespace EtchaSketch.UI.Services;

/// <summary>The open, unlocked database and everything the screens read and write through it.</summary>
public sealed class AppSession : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TimeProvider _clock;

    private AppSession(SqliteConnection connection, TimeProvider clock)
    {
        _connection = connection;
        _clock = clock;
        Households = new HouseholdRepository(connection);
        Tasks = new TaskRepository(connection);
    }

    public HouseholdRepository Households { get; }

    public TaskRepository Tasks { get; }

    public DateTimeOffset Now => _clock.GetLocalNow();

    public DateOnly Today => DateOnly.FromDateTime(Now.DateTime);

    public static AppSession Open(AppEnvironment environment)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(environment.DatabasePath))!);
        var connection = EncryptedDatabase.Open(environment.DatabasePath, environment.KeyStore);
        try
        {
            Schema.Apply(connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        return new AppSession(connection, environment.Clock ?? TimeProvider.System);
    }

    public void Dispose() => _connection.Dispose();
}
