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
        _tutorialIntroAt = _roadReturn ? 0f : Time + 1.2f;
        _tutClearwayPlayed = _roadReturn;

        // Back from the ruins: the caravan has moved in, so the road is just the road.
        // The dead are re-placed at the campaign's level, the boss holds the FAR end
        // (where the camp stood), and the doorway home stays open.
        if (_roadReturn)
        {
            float rcr = Map.Height / 2 + 0.5f;
            float x0r = Map.Kind == MapKind.StoryRoad ? 8f : 0f;
            float sxr = (Map.Width - x0r) / 84f;
            int lvl = Math.Max(1, CampaignEnemyLevel);
            foreach (var (kind, x84, y) in new (string, float, float)[]
            {
                ("grunt", 17.5f, rcr), ("grunt", 18.5f, rcr + 1f), ("spitter", 25.5f, rcr - 2f),
                ("grunt", 36.5f, rcr), ("grunt", 38.5f, rcr + 1f), ("shambler", 37.5f, rcr - 1f),
                ("crypt_leaper", 46.5f, rcr), ("grunt", 56.5f, rcr), ("shambler", 58.5f, rcr + 1f),
                ("grunt", 66.5f, rcr - 1f), ("spitter", 70.5f, rcr + 2f),
            })
                if (Data.Enemies.ContainsKey(kind))
                    SpawnEnemy(kind, new Vector2(x0r + x84 * sxr, y), level: lvl, buried: x84 < 30f);
            if (Data.Enemies.ContainsKey("barrowlord"))
            {
                var boss = SpawnEnemy("barrowlord", new Vector2(x0r + 6.5f, rcr), EliteAffix.Boss, level: lvl);
                _bossEnemyId = boss.Id;
            }
            return;
        }

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
        // Authored for the 84-wide road; the story road is longer and starts past a
        // stretch of road behind the camp (8 tiles).
        float x0 = Map.Kind == MapKind.StoryRoad ? 8f : 0f;
        float sx = (Map.Width - x0) / 84f;
        foreach (var (kind, x84, y) in new (string, float, float)[]
        {
            ("grunt", 17.5f, rc), ("grunt", 18.5f, rc + 1f),
            ("spitter", 25.5f, rc - 2f),
            ("grunt", 36.5f, rc), ("grunt", 38.5f, rc + 1f), ("shambler", 37.5f, rc - 1f),
            ("crypt_leaper", 46.5f, rc),
            ("grunt", 56.5f, rc), ("shambler", 58.5f, rc + 1f), // the high ground is held too
        })
            if (Data.Enemies.ContainsKey(kind))
                // The first pair and the high-ground guard lie buried — the road's
                // introduction to the dead rising; the middle groups stand in plain view.
                SpawnEnemy(kind, new Vector2(x0 + x84 * sx, y), level: 1, buried: x84 < 20f || x84 > 50f);

        // The gate boss: the Barrow Lord — the Gravelord's tutorial cousin, who
        // raises melee zombies instead of spitters — already weathered: the tutorial
        // wants a real boss fight, not a wall (its bar reads part-worn on purpose).
        if (Data.Enemies.ContainsKey("barrowlord"))
        {
            // A BOSS, affix and all: the big body, the boss bar, half stun/freeze.
            var boss = SpawnEnemy("barrowlord", Map.BossSpot, EliteAffix.Boss, level: 1);
            boss.MaxHealth *= 0.3f;
            boss.Health = boss.MaxHealth;
            _bossEnemyId = boss.Id;
            _events.EnemyHealthChanged(boss);
        }
    }

    private float _ruinsIntroAt;
    private bool _ruinsIntroPlayed;

    /// <summary>Furnish the story hub: the caravan parked by the entrance, the crew at
    /// their stations (the peddler and the lorekeeper open for business; the sellsword
    /// and the gambler still unpacking), the standing torches, barrels and urns.</summary>
    private void SetupRuinsHub()
    {
        Spawners.Clear();
        Packs.Clear();
        Npcs.Clear();
        Chests.Clear();
        _bossEnemyId = -1;
        void Npc(int id, string typeId, int spot)
        {
            if (!Data.Npcs.ContainsKey(typeId) || Map.NpcSpots.Count <= spot) return;
            Npcs.Add(new ServerNpc
            {
                Id = id, TypeId = typeId, Position = Map.NpcSpots[spot],
                Height = Map.GroundHeightAt(Map.NpcSpots[spot]),
            });
        }
        Npc(1, "merchant", 0);
        Npc(2, "skill_trainer", 1);
        Npc(7, "mercenary", 2);
        Npc(3, "gambler", 3);
        if (!_ruinsIntroPlayed) _ruinsIntroAt = Time + 1.6f; // the cart, torches and urns: FurnishHub
    }

    /// <summary>The camp's first evening: once the party arrives in the ruins, the crew
    /// points them at the stairs — "see what's down there".</summary>
    private void TickRuinsHub()
    {
        if (!Story || Map.Kind != MapKind.RuinsHub || _ruinsIntroPlayed || _ruinsIntroAt <= 0f) return;
        if (Time < _ruinsIntroAt) return;
        _ruinsIntroPlayed = true;
        _ruinsIntroAt = 0f;
        PlayScene("hub_arrival");
        foreach (var pl in Players.Values)
            _events.MessageFor(pl, "Odessa: \"Head down below and see if you can find anything useful.\"");
    }

    /// <summary>Scripted beats: the arrival cutscene shortly after the map opens, and
    /// the "clear the way" scene when anyone reaches the ruins' edge.</summary>
    private void TickTutorial()
    {
        if (!Campaign || !Map.IsRoad) return;
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
