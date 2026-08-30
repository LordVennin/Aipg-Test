using System.Numerics;
using ARPG.World;

namespace ARPG.Server;

/// <summary>
/// The tutorial introduction (ServerWorld partial): the caravan's authored push
/// through the graveyard to the ruins. Behind the hub's south door, never forced.
/// Owns the scripted beats — the arrival cutscene, the "clear the way" cutscene at
/// the ruins' edge, the victory scene when the gate boss falls — plus the tutorial's
/// forgiving death rule: the caravan drags you back to camp and ribs you for it.
/// </summary>
public partial class ServerWorld
{
    private float _tutorialIntroAt;
    private bool _tutClearwayPlayed;
    private int _tutorialQuip;

    private static readonly string[] TutorialDeathQuips =
    {
        "Brakka: \"Glad we hired more than one of you.\"",
        "Brakka: \"Back on your feet — the mud's taken uglier things than you.\"",
        "Odessa: \"Fascinating. Try dying LESS, perhaps?\"",
        "Brakka: \"You get one of those for free. The next one's coming out of your pay.\"",
    };

    /// <summary>Furnish the introduction: the parked caravan, the crew, the authored
    /// enemy placements, and the weakened Gravelord holding the ruins gate.</summary>
    private void SetupTutorial()
    {
        _tutorialIntroAt = Time + 1.2f;
        _tutClearwayPlayed = false;

        // The caravan, parked at camp (indestructible scenery here — no defense rules).
        AddStructure(StructureKind.Wagon, Map.WagonSpot, 1_000_000f, ownerId: -1, radius: 0.85f);

        if (Data.Npcs.ContainsKey("mercenary") && Map.NpcSpots.Count > 0)
        {
            var brakka = new ServerNpc
            {
                Id = 7, TypeId = "mercenary", Position = Map.NpcSpots[0],
                Height = Map.GroundHeightAt(Map.NpcSpots[0]),
            };
            Npcs.Add(brakka);
            _events.NpcAdded(brakka);
        }
        if (Data.Npcs.ContainsKey("researcher") && Map.NpcSpots.Count > 1)
        {
            var odessa = new ServerNpc
            {
                Id = 8, TypeId = "researcher", Position = Map.NpcSpots[1],
                Height = Map.GroundHeightAt(Map.NpcSpots[1]),
            };
            Npcs.Add(odessa);
            _events.NpcAdded(odessa);
        }

        // The dead along the road — authored placements, gentle levels (the road
        // band sits on the map's center row).
        float rc = Map.Height / 2 + 0.5f;
        foreach (var (kind, x, y) in new (string, float, float)[]
        {
            ("grunt", 17.5f, rc), ("grunt", 18.5f, rc + 1f),
            ("spitter", 25.5f, rc - 2f),
            ("grunt", 36.5f, rc), ("grunt", 38.5f, rc + 1f), ("shambler", 37.5f, rc - 1f),
            ("crypt_leaper", 46.5f, rc),
            ("grunt", 56.5f, rc), ("shambler", 58.5f, rc + 1f), // the high ground is held too
        })
            if (Data.Enemies.ContainsKey(kind))
                SpawnEnemy(kind, new Vector2(x, y), level: 1);

        // The gate boss: a Gravelord, already weathered — the tutorial wants a real
        // boss fight, not a wall (its bar reads part-worn on purpose).
        if (Data.Enemies.ContainsKey("gravelord"))
        {
            var boss = SpawnEnemy("gravelord", Map.BossSpot, level: 1);
            boss.MaxHealth *= 0.45f;
            boss.Health = boss.MaxHealth;
            _bossEnemyId = boss.Id;
            _events.EnemyHealthChanged(boss);
        }
    }

    /// <summary>Scripted beats: the arrival cutscene shortly after the map opens, and
    /// the "clear the way" scene when anyone reaches the ruins' edge.</summary>
    private void TickTutorial()
    {
        if (!Campaign || Map.Kind != MapKind.Tutorial) return;
        if (_tutorialIntroAt > 0f && Time >= _tutorialIntroAt)
        {
            _tutorialIntroAt = 0f;
            _events.CutscenePlayed("tut_intro");
        }
        if (!_tutClearwayPlayed &&
            Players.Values.Any(pl => pl.Alive && pl.Position.X > Map.Width - 20))
        {
            _tutClearwayPlayed = true;
            _events.CutscenePlayed("tut_clearway");
        }
    }
}
