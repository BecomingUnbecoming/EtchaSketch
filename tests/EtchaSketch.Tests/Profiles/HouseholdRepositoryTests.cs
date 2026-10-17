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

        var household = repository.Create("Home", "Lacy");

        var lacy = Assert.Single(household.Members);
        Assert.Equal("Lacy", lacy.DisplayName);
        Assert.Equal(ProfileRole.Owner, lacy.Role);
        Assert.Equal(household.Id, lacy.HouseholdId);
    }

    [Fact]
    public void Members_can_be_added_and_survive_reopening_the_database()
    {
        Guid householdId;
        using (var db = Open())
        {
            var repository = new HouseholdRepository(db);
            householdId = repository.Create("Home", "Lacy").Id;
            repository.AddMember(householdId, "Jordy");
        }

        using (var db = Open())
        {
            var household = new HouseholdRepository(db).Get(householdId)!;

            Assert.Equal("Home", household.Name);
            Assert.Equal(["Lacy", "Jordy"], household.Members.Select(member => member.DisplayName));
            Assert.Equal(ProfileRole.Member, household.Members[1].Role);
        }
    }

    [Fact]
    public void Display_names_are_trimmed_and_required()
    {
        using var db = Open();
        var repository = new HouseholdRepository(db);

        var household = repository.Create("  Home ", "  Lacy  ");

        Assert.Equal("Home", household.Name);
        Assert.Equal("Lacy", household.Members[0].DisplayName);
        Assert.Throws<ArgumentException>(() => repository.AddMember(household.Id, "   "));
        Assert.Throws<ArgumentException>(() => repository.Create("Home", ""));
    }

    [Fact]
    public void Each_device_remembers_who_is_using_it()
    {
        Guid jordy;
        using (var db = Open())
        {
            var repository = new HouseholdRepository(db);
            var household = repository.Create("Home", "Lacy");
            jordy = repository.AddMember(household.Id, "Jordy").Id;

            Assert.Null(repository.CurrentProfileId());
            repository.SetCurrentProfile(jordy);
        }

        using (var db = Open())
        {
            Assert.Equal(jordy, new HouseholdRepository(db).CurrentProfileId());
        }
    }

    [Fact]
    public void Households_on_this_device_can_be_listed_and_found_from_a_member()
    {
        using var db = Open();
        var repository = new HouseholdRepository(db);
        Assert.Empty(repository.All());

        var home = repository.Create("Home", "Lacy");
        var jordy = repository.AddMember(home.Id, "Jordy");

        Assert.Equal([home.Id], repository.All().Select(household => household.Id));
        Assert.Equal(home.Id, repository.HouseholdOf(jordy.Id)!.Id);
        Assert.Null(repository.HouseholdOf(Guid.NewGuid()));
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

        Assert.Throws<ArgumentException>(() => new HouseholdRepository(db).AddMember(Guid.NewGuid(), "Jordy"));
    }

    [Fact]
    public void Profiles_are_household_tier_so_streams_never_see_names()
    {
        using var db = Open();
        var household = new HouseholdRepository(db).Create("Home", "Lacy");

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
