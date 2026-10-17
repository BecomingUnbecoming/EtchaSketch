using EtchaSketch.Core.Privacy;
using EtchaSketch.Core.Profiles;
using EtchaSketch.Data.Profiles;
using EtchaSketch.Data.Storage;
using EtchaSketch.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace EtchaSketch.Tests.Profiles;

public sealed class HouseholdRepositoryTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("etchasketch-profiles-").FullName;
    private readonly InMemoryKeyStore _keys = new();

    private string DatabasePath => Path.Combine(_directory, "etchasketch.db");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private SqliteConnection Open()
    {
        var db = EncryptedDatabase.Open(DatabasePath, _keys);
        Schema.Apply(db);
        return db;
    }

    [Fact]
    public void Creating_a_household_makes_its_first_profile_the_owner()
    {
        using var db = Open();
        var repository = new HouseholdRepository(db);

        var household = repository.Create("Home", "Robin");

        var robin = Assert.Single(household.Members);
        Assert.Equal("Robin", robin.DisplayName);
        Assert.Equal(ProfileRole.Owner, robin.Role);
        Assert.Equal(household.Id, robin.HouseholdId);
    }

    [Fact]
    public void Members_can_be_added_and_survive_reopening_the_database()
    {
        Guid householdId;
        using (var db = Open())
        {
            var repository = new HouseholdRepository(db);
            householdId = repository.Create("Home", "Robin").Id;
            repository.AddMember(householdId, "Kai");
        }

        using (var db = Open())
        {
            var household = new HouseholdRepository(db).Get(householdId)!;

            Assert.Equal("Home", household.Name);
            Assert.Equal(["Robin", "Kai"], household.Members.Select(member => member.DisplayName));
            Assert.Equal(ProfileRole.Member, household.Members[1].Role);
        }
    }

    [Fact]
    public void Display_names_are_trimmed_and_required()
    {
        using var db = Open();
        var repository = new HouseholdRepository(db);

        var household = repository.Create("  Home ", "  Robin  ");

        Assert.Equal("Home", household.Name);
        Assert.Equal("Robin", household.Members[0].DisplayName);
        Assert.Throws<ArgumentException>(() => repository.AddMember(household.Id, "   "));
        Assert.Throws<ArgumentException>(() => repository.Create("Home", ""));
    }

    [Fact]
    public void Each_device_remembers_who_is_using_it()
    {
        Guid kai;
        using (var db = Open())
        {
            var repository = new HouseholdRepository(db);
            var household = repository.Create("Home", "Robin");
            kai = repository.AddMember(household.Id, "Kai").Id;

            Assert.Null(repository.CurrentProfileId());
            repository.SetCurrentProfile(kai);
        }

        using (var db = Open())
        {
            Assert.Equal(kai, new HouseholdRepository(db).CurrentProfileId());
        }
    }

    [Fact]
    public void Current_profile_must_exist()
    {
        using var db = Open();

        Assert.Throws<ArgumentException>(() => new HouseholdRepository(db).SetCurrentProfile(Guid.NewGuid()));
    }

    [Fact]
    public void Adding_to_a_missing_household_fails()
    {
        using var db = Open();

        Assert.Throws<ArgumentException>(() => new HouseholdRepository(db).AddMember(Guid.NewGuid(), "Kai"));
    }

    [Fact]
    public void Profiles_are_household_tier_so_streams_never_see_names()
    {
        using var db = Open();
        var household = new HouseholdRepository(db).Create("Home", "Robin");

        Assert.Null(PrivacyGateway.ForStudio(household.Members[0]));
        Assert.Null(PrivacyGateway.ForStudio(household));
        Assert.NotNull(PrivacyGateway.Project(household, Audience.ForProfile(household.Members[0].Id)));
    }

    [Fact]
    public void Schema_can_be_applied_more_than_once()
    {
        using var db = Open();

        Schema.Apply(db);
        Schema.Apply(db);

        Assert.Equal(Schema.Version, Schema.CurrentVersion(db));
    }
}
