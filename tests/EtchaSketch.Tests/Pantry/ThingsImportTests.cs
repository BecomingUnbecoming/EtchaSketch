using System.Globalization;
using EtchaSketch.Core.Pantry;
using EtchaSketch.Data.Import;
using EtchaSketch.Data.Pantry;
using EtchaSketch.Data.Profiles;
using EtchaSketch.Data.Storage;
using EtchaSketch.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace EtchaSketch.Tests.Pantry;

public sealed class ThingsImportTests : IDisposable
{
    // As Homebox writes it: its own column order, an empty date as 0001-01-01, a note with a line break and a comma.
    private const string HomeboxExport =
        "HB.purchase_date,HB.warranty_expires,HB.sold_date,HB.import_ref,HB.parent_import_ref,HB.url,HB.name,HB.description,HB.notes,HB.purchase_from,HB.manufacturer,HB.model_number,HB.serial_number,HB.warranty_details,HB.sold_to,HB.sold_notes,HB.location,HB.tags,HB.asset_id,HB.quantity,HB.purchase_price,HB.sold_price,HB.archived,HB.insured,HB.lifetime_warranty,HB.field.Colour\n" +
        "2024-03-02,2027-03-02,0001-01-01,,,,Cordless drill,18V,\"Two batteries,\nkeep charged\",Toolstation,DeWalt,DCD778,DW-123,,,,Home / Garage / Shelf 2,Tools; Power,000-001,1,89.99,0,false,false,false,Yellow\n" +
        "0001-01-01,0001-01-01,0001-01-01,,,,Blender,,,,,,,,,,Home / Kitchen,,000-002,1,0,0,false,false,true,\n" +
        "0001-01-01,0001-01-01,0001-01-01,,,,Old phone,,,,,,,,,,Home,,000-003,1,0,0,true,false,false,\n" +
        "0001-01-01,0001-01-01,2025-01-05,,,,Bike,,,,,,,,Sam,,Home / Shed,,000-004,1,0,0,false,false,false,\n" +
        "0001-01-01,0001-01-01,0001-01-01,,,,,,,,,,,,,,Home,,000-005,1,0,0,false,false,false,\n";

    private static readonly DateOnly Today = new(2026, 10, 21);

    private readonly string _directory = Directory.CreateTempSubdirectory("etchasketch-things-import-").FullName;
    private readonly SqliteConnection _db;
    private readonly InventoryRepository _inventory;
    private readonly UpkeepRepository _upkeep;
    private readonly Guid _home;

    public ThingsImportTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        _db = EncryptedDatabase.Open(Path.Combine(_directory, "etchasketch.db"), new InMemoryKeyStore());
        Schema.Apply(_db);
        _home = new HouseholdRepository(_db).Create("Home", "Robin").Id;
        _inventory = new InventoryRepository(_db);
        _upkeep = new UpkeepRepository(_db, _inventory);
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Csv_handles_quotes_line_breaks_tabs_and_blank_lines()
    {
        Assert.Equal([["a", "b,c", "d\"e"], ["1", "two\nlines", ""]], Csv.Read("a,\"b,c\",\"d\"\"e\"\r\n\r\n1,\"two\nlines\",\n"));
        Assert.Equal([["a", "b,c"], ["1", "2"]], Csv.Read("﻿a\tb,c\n1\t2"));
    }

    [Fact]
    public void A_homebox_export_brings_in_what_the_household_still_has()
    {
        var preview = HomeboxImport.Apply(_inventory, _home, HomeboxExport, dryRun: true);
        Assert.Equal(2, preview.Added);
        Assert.Empty(_inventory.Items(_home));

        var result = HomeboxImport.Apply(_inventory, _home, HomeboxExport);

        Assert.Equal((2, 0), (result.Added, result.AlreadyThere));
        Assert.Equal(["Row 4: Old phone is archived in Homebox", "Row 5: Bike was sold", "Row 6: no name"], result.Skipped);

        var drill = _inventory.Items(_home).Single(item => item.Name == "Cordless drill");
        Assert.Equal(("Home / Garage / Shelf 2", "Tools", "DW-123"), (drill.Location, drill.Category, drill.Serial));
        Assert.Equal((new DateOnly(2024, 3, 2), 89.99m, new DateOnly(2027, 3, 2)), (drill.PurchasedOn, drill.Price, drill.WarrantyEnds));
        Assert.Equal("18V\nTwo batteries,\nkeep charged\nMake: DeWalt DCD778\nBought from: Toolstation\nColour: Yellow\nImported from Homebox (asset 000-001)", drill.Notes);

        var blender = _inventory.Items(_home).Single(item => item.Name == "Blender");
        Assert.Equal((null, null, null), (blender.PurchasedOn, blender.Price, blender.WarrantyEnds));
        Assert.Equal("Lifetime warranty\nImported from Homebox (asset 000-002)", blender.Notes);
    }

    [Fact]
    public void Importing_the_same_homebox_file_again_adds_nothing()
    {
        HomeboxImport.Apply(_inventory, _home, HomeboxExport);

        var again = HomeboxImport.Apply(_inventory, _home, HomeboxExport);

        Assert.Equal((0, 2), (again.Added, again.AlreadyThere));
        Assert.Equal(2, _inventory.Items(_home).Count);
    }

    [Fact]
    public void A_file_that_is_not_from_homebox_is_explained()
    {
        var problem = Assert.Throws<FormatException>(() => HomeboxImport.Apply(_inventory, _home, "name,place\nDrill,Garage\n"));
        Assert.StartsWith("That isn't a Homebox export", problem.Message, StringComparison.Ordinal);
    }

    private InventoryItem Car() => _inventory.Add(new InventoryItem { HouseholdId = _home, Name = "Corolla", DistanceUnit = "km" });

    [Fact]
    public void A_lubelogger_fuel_export_becomes_the_vehicles_log()
    {
        var car = Car();
        const string fuel =
            "Date,Odometer,FuelConsumed,Cost,FuelEconomy,IsFillToFull,MissedFuelUp,Notes,Tags\n" +
            "10/2/2026,\"39,800\",41.5,$68.20,6.1,True,False,,\n" +
            "10/18/2026,40250,38.25,\"$61.05\",6.0,False,True,Motorway trip,holiday\n" +
            "13/45/2026,40300,10,15,,True,False,,\n";

        var result = LubeLoggerImport.Apply(_upkeep, car, LogKind.Service, fuel);

        Assert.Equal((2, 0), (result.Added, result.AlreadyThere));
        Assert.Equal(["Row 4: can't read the date \"13/45/2026\""], result.Skipped);
        Assert.Equal("Dates were read as month/day/year.", result.Note);
        var log = _upkeep.LogFor(car.Id);
        Assert.All(log, entry => Assert.Equal(LogKind.Fuel, entry.Kind));
        Assert.Equal((new DateOnly(2026, 10, 18), 40_250, 38.25m, 61.05m), (log[0].On, log[0].Distance, log[0].Amount, log[0].Cost));
        Assert.Equal("Motorway trip · partial fill · missed a fill-up before this · tags: holiday", log[0].Note);
        Assert.Equal(39_800, log[1].Distance);
        Assert.Equal(40_250, _upkeep.DistanceNow(car.Id));

        Assert.Equal((0, 2), (LubeLoggerImport.Apply(_upkeep, car, LogKind.Fuel, fuel).Added, LubeLoggerImport.Apply(_upkeep, car, LogKind.Fuel, fuel).AlreadyThere));
    }

    [Fact]
    public void Service_records_with_day_first_dates_and_european_money()
    {
        var car = Car();
        const string services =
            "Date\tOdometer\tDescription\tCost\tNotes\tTags\n" +
            "21.10.2026\t40300\tOil change\t\"1.234,50 €\"\tSynthetic 5W-30\t\n" +
            "03.04.2026\t31000\tTyres\t480\t\t\n";

        var result = LubeLoggerImport.Apply(_upkeep, car, LogKind.Service, services);

        Assert.Equal("Dates were read as day/month/year.", result.Note);
        var log = _upkeep.LogFor(car.Id);
        Assert.Equal((Today, LogKind.Service, 1234.50m, "Oil change · Synthetic 5W-30"), (log[0].On, log[0].Kind, log[0].Cost, log[0].Note));
        Assert.Equal(new DateOnly(2026, 4, 3), log[1].On);
    }

    [Fact]
    public void Lubelogger_files_are_checked_before_anything_goes_in()
    {
        var kettle = _inventory.Add(new InventoryItem { HouseholdId = _home, Name = "Kettle" });
        Assert.Throws<FormatException>(() => LubeLoggerImport.Apply(_upkeep, kettle, LogKind.Service, "Date,Odometer\n2026-10-21,5\n"));
        Assert.Throws<FormatException>(() => LubeLoggerImport.Apply(_upkeep, Car(), LogKind.Fuel, "Date,Odometer,Description\n2026-10-21,5,Oil\n"));
        Assert.Throws<FormatException>(() => LubeLoggerImport.Apply(_upkeep, Car(), LogKind.Service, "When,Odometer\n2026-10-21,5\n"));
    }

    [Theory]
    [InlineData("$1,234.50", "1234.50")]
    [InlineData("1.234,50 €", "1234.50")]
    [InlineData("61.2", "61.2")]
    [InlineData("61,2", "61.2")]
    [InlineData("1,234", "1234")]
    [InlineData("£15", "15")]
    public void Money_reads_either_way_of_writing_it(string text, string expected) =>
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), LubeLoggerImport.Money(text));
}
