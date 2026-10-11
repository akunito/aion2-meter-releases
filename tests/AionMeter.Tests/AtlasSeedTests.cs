using AionMeter.Core.Atlas;
using AionMeter.Core.GameDb;

namespace AionMeter.Tests;

/// <summary>A2M-37: the shipped atlas data (build.sh copies atlas/maps/* into data/atlas, data/pets.json into data/gamedb) after the SkeeveAN import.</summary>
public class AtlasSeedTests
{
    private static string Find(string rel, string probe)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, rel);
            if (File.Exists(Path.Combine(p, probe))) return p;
        }
        Assert.Fail($"{rel} not found: run build/build.sh (it copies the atlas data from the project root)");
        return "";
    }

    private static string AtlasDir() => Find(Path.Combine("data", "atlas"), Path.Combine("1010", "calibration.json"));

    [Theory]
    // boss-list packet world xy → the boss icon pixel on the 4096 px official map (200 cm/px, −409,600): kept over SkeeveAN's fit (docs/28)
    [InlineData(1010, -57517f, -85173f, 1760.415, 1622.135)]    // Neikel of the East
    [InlineData(1010, 18723f, 95025f, 2141.615, 2523.125)]      // Heretic Layla
    [InlineData(1010, 117274f, -96444f, 2634.37, 1565.78)]      // Harvest Manager Moshav
    public void Calibration_round_trips_known_points(int map, float x, float y, double px, double py)
    {
        var cal = MapCalibration.Load(AtlasDir(), map)!;
        var (u, v) = cal.ToPixel(x, y);
        Assert.Equal(px, u, 2);
        Assert.Equal(py, v, 2);
        // inverse of the affine (no rotation): back to the same world point within 1 cm
        Assert.Equal(x, (u - 2048) / cal.Scale, 0);
        Assert.Equal(y, (v - 2048) / cal.Scale, 0);
        var b = cal.Bosses.First(t => Math.Abs(t.Px - px) < 0.5 && Math.Abs(t.Py - py) < 0.5);
        Assert.True(b.Code > 2_100_000);
    }

    [Theory]
    [InlineData(1010, 637, 14158, 210)]
    [InlineData(1110, 659, 17845, 151)]
    [InlineData(20, 78, 1704, 29)]
    public void Monster_seeds_load_with_npc_ids(int map, int templates, int points, int withIds)
    {
        var list = MonsterSpawns.Load(AtlasDir(), map);
        Assert.Equal(templates, list.Count);
        Assert.Equal(points, list.Sum(t => t.Points.Length));
        Assert.Equal(withIds, list.Count(t => t.Ids.Length > 0));
    }

    [Fact]
    public void Node_seeds_load_per_map()
    {
        var store = new NodeStore(Path.Combine(Path.GetTempPath(), "aionmeter-atlas-seed-tests", Guid.NewGuid().ToString("N")));
        Assert.Equal(2734 + 2695 + 240, store.LoadSeeds(AtlasDir()));   // 1010 has two seeds in one 1 m key
        Assert.Equal(2734, store.Spots(1010).Count);
        Assert.Equal(2695, store.Spots(1110).Count);
        Assert.Equal(240, store.Spots(20).Count);
        Assert.Contains(store.Spots(1110), s => s.Name == "Conide");   // a SkeeveAN-only gathering spot
    }

    [Theory]
    [InlineData(1010, 200)]
    [InlineData(1110, 200)]
    [InlineData(20, 100)]
    public void Imported_points_keep_5_m_from_every_point_of_their_template(int map, int shugoGridCm)
    {
        // shugo points sit on its pixel grid (k·cm − offset); a point off the grid came from SkeeveAN and must be > 5 m from all others
        foreach (var t in MonsterSpawns.Load(AtlasDir(), map))
        {
            var p = t.Points;
            for (var i = 0; i < p.Length; i++)
            {
                var iShugo = p[i].X % shugoGridCm == 0 && p[i].Y % shugoGridCm == 0;
                for (var j = i + 1; j < p.Length; j++)
                {
                    if (iShugo && p[j].X % shugoGridCm == 0 && p[j].Y % shugoGridCm == 0) continue;
                    long dx = p[i].X - p[j].X, dy = p[i].Y - p[j].Y;
                    Assert.True(dx * dx + dy * dy > 500L * 500L, $"{t.Name}: {p[i]} and {p[j]} closer than 5 m");
                }
            }
        }
    }

    [Fact]
    public void Pet_droppers_match_by_npc_id_where_the_names_differ()
    {
        var pets = PetBook.Load(Path.Combine(Find(Path.Combine("data", "gamedb"), "pets.json"), "pets.json"));
        var worker = pets.All.First(p => p.Id == 1104);   // Manduri Trebuchet Worker: aion2hub calls dropper 2110507 "Crimson Manduri Trebuchet Worker"
        var slinger = MonsterSpawns.Load(AtlasDir(), 1010).First(t => t.Name == "Crimson Manduri Slinger");   // the client's name for 2110507
        Assert.True(slinger.HasId(2110507));
        Assert.True(MonsterSpawns.Drops(worker, slinger));
        Assert.False(MonsterSpawns.Drops(worker, slinger with { Ids = [] }));   // name-only matching (before A2M-37) missed it
        var notDropper = MonsterSpawns.Load(AtlasDir(), 1010).First(t => t.Ids.Length > 0 && !worker.DropperIds.Any(t.HasId) && !worker.Droppers.Contains(t.Name, StringComparer.OrdinalIgnoreCase));
        Assert.False(MonsterSpawns.Drops(worker, notDropper));
    }
}
