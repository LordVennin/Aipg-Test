using System.Numerics;
using ARPG.Items;
using ARPG.World;

namespace ARPG.Server;

/// <summary>What a sealed warp scroll describes, read off its modifiers when it goes
/// on the podium: the destination's kind, theme, weather, level and the dials that
/// shape its population and loot.</summary>
public class ScrollRun
{
    public string Title = "a sealed zone";
    public bool Defense;
    public bool Survival;
    public string ThemeId = "forest";
    public string Weather;          // null = the theme's default
    public int EnemyLevel = 1;
    public float DensityPct;        // more packs
    public float ElitePct;          // more magic/rare leaders
    public bool Boss;
    public float LootPct;           // more drops, rarer finds
    public int Seed;
    public const int SurvivalWaves = 5;

    public static ScrollRun Read(ItemInstance scroll, Data.GameData data, int runSeed)
    {
        var run = new ScrollRun
        {
            EnemyLevel = Math.Max(1, scroll.ItemLevel),
            Seed = unchecked(runSeed * 31 + scroll.InstanceId.GetHashCode()),
        };
        string zoneName = "Mirewood";
        string modeName = "";
        foreach (var roll in scroll.Modifiers)
        {
            var def = data.Modifiers.GetValueOrDefault(roll.ModifierId);
            if (def == null || string.IsNullOrEmpty(def.ModifierGroup)) continue;
            switch (def.ModifierGroup)
            {
                case "warp_mode":
                    run.Defense = def.Id == "warp_mode_defense";
                    run.Survival = def.Id == "warp_mode_survival";
                    modeName = run.Defense ? "Caravan Stand" : "Last Stand";
                    break;
                case "warp_zone":
                    (run.ThemeId, zoneName) = def.Id switch
                    {
                        "warp_zone_swamp" => ("graveyard", "the Fen"),
                        "warp_zone_ruins" => ("tomb", "the Sunken Halls"),
                        _ => ("forest", "the Mirewood"),
                    };
                    break;
                case "warp_weather":
                    run.Weather = def.Id switch
                    {
                        "warp_weather_rain" => "rain",
                        "warp_weather_snow" => "snow",
                        "warp_weather_wind" => "wind",
                        _ => "",
                    };
                    break;
                case "warp_level": run.EnemyLevel += (int)roll.Value; break;
                case "warp_density": run.DensityPct += roll.Value; break;
                case "warp_elites": run.ElitePct += roll.Value; break;
                case "warp_boss": run.Boss = true; break;
                case "warp_loot": run.LootPct += roll.Value; break;
            }
        }
        run.Title = modeName.Length > 0 ? $"{zoneName} — {modeName}" : zoneName;
        return run;
    }
}

/// <summary>The portal loop (ServerWorld partial): a sealed scroll goes on the ruins'
/// podium, the portal opens, the party steps through into the zone the scroll
/// describes, and the zone's exit leads home. Also the "Last Stand" survival mode:
/// waves that hunt the party wherever it stands.</summary>
public partial class ServerWorld
{
    public const int ScrollMapIndex = -4;

    private ItemInstance _portalScroll;
    private ScrollRun _scrollRun;
    private readonly HashSet<int> _readyAtPortal = new();
    private readonly HashSet<int> _readyAtRoad = new();

    /// <summary>The hub's portal stands open (a scroll was placed; nobody has gone yet).</summary>
    public bool PortalOpen => _portalScroll != null && Map.Kind == MapKind.RuinsHub;
    public string PortalTitle => PortalOpen ? _portalRun?.Title ?? "" : "";
    private ScrollRun _portalRun;
    /// <summary>The zone's banner title: the scroll's destination on a scroll map.</summary>
    public string ZoneTitle => MapIndex == ScrollMapIndex ? _scrollRun?.Title ?? "a sealed zone"
        : MapIndex == StoryRoadIndex && _roadReturn ? "the road back" : "";
    /// <summary>Enemy level for the zone being played: the scroll's on a scroll map,
    /// the campaign loop's everywhere else.</summary>
    public int ZoneEnemyLevel => MapIndex == ScrollMapIndex && _scrollRun != null ? _scrollRun.EnemyLevel : CampaignEnemyLevel;
    /// <summary>Drop multiplier from the scroll's "of Plenty" seals (1 = none).</summary>
    public float ZoneLootMultiplier => MapIndex == ScrollMapIndex && _scrollRun != null ? 1f + _scrollRun.LootPct / 100f : 1f;

    // Survival ("Last Stand"): waves that spawn around the party.
    public int SurvivalWave { get; private set; }
    public int SurvivalTotal => _scrollRun?.Survival == true ? ScrollRun.SurvivalWaves : 0;
    public bool SurvivalDone { get; private set; }
    private float _nextSurvivalAt;
    private bool _survivalWaveLive;

    /// <summary>Place a sealed warp scroll on the podium: it is consumed and the portal
    /// opens to the zone it describes. Ruins hub only, within reach of the podium.</summary>
    public void UsePodium(int playerId, Guid itemId)
    {
        if (!Players.TryGetValue(playerId, out var p) || !p.Alive) return;
        if (Map.Kind != MapKind.RuinsHub || Map.PodiumSpot == Vector2.Zero) return;
        if (Vector2.Distance(p.Position, Map.PodiumSpot) > 2.6f) return;
        if (_portalScroll != null)
        {
            _events.MessageFor(p, "The portal already stands open — step through, or let it close behind the party.");
            return;
        }
        var placed = p.Character.Inventory.FindByInstance(itemId);
        var scrollBase = placed?.Item.GetBase(Data);
        if (placed == null || scrollBase?.Category != ItemCategory.WarpScroll)
        {
            _events.MessageFor(p, "The podium wants a sealed warp scroll.");
            return;
        }
        p.Character.Inventory.Remove(itemId);
        _events.CharacterChanged(p);
        OpenPortal(placed.Item);
    }

    /// <summary>The seals burn: the portal opens onto the scroll's zone.</summary>
    private void OpenPortal(ItemInstance scroll)
    {
        _portalScroll = scroll;
        _portalRun = ScrollRun.Read(scroll, Data, _runSeed);
        foreach (var pl in Players.Values)
            _events.MessageFor(pl, $"The seals burn away. The portal opens onto {_portalRun.Title} (level {_portalRun.EnemyLevel}).");
        _events.WorldEffect("darkburst", Map.PortalSpot, 1.2f, 0.8f, Map.GroundHeightAt(Map.PortalSpot));
        _events.ZoneStateChanged(this);
    }

    /// <summary>Step through: the scroll's zone becomes the run (called from the door
    /// flow once everyone alive is ready at the portal).</summary>
    private void EnterPortal()
    {
        if (_portalRun == null) return;
        _scrollRun = _portalRun;
        _portalRun = null;
        _portalScroll = null;
        TransitionTo(ScrollMapIndex);
    }

    private void SetupSurvival()
    {
        SurvivalWave = 0;
        SurvivalDone = false;
        _survivalWaveLive = false;
        _nextSurvivalAt = Time + 8f;
        foreach (var pl in Players.Values)
            _events.MessageFor(pl, "Something has caught your scent. Stand together — they come in waves.");
    }

    private void TickSurvival()
    {
        if (MapIndex != ScrollMapIndex || _scrollRun?.Survival != true || SurvivalDone) return;
        if (_survivalWaveLive)
        {
            if (Enemies.Values.Any(e => !e.Dead)) return;
            _survivalWaveLive = false;
            if (SurvivalWave >= ScrollRun.SurvivalWaves)
            {
                SurvivalDone = true;
                foreach (var pl in Players.Values)
                    _events.MessageFor(pl, "The last of them is down. The way out is open.");
                _events.ZoneStateChanged(this);
                return;
            }
            _nextSurvivalAt = Time + 6f;
            foreach (var pl in Players.Values)
                _events.MessageFor(pl, $"Wave {SurvivalWave} beaten — the next is close.");
            return;
        }
        if (Time < _nextSurvivalAt) return;
        SurvivalWave++;
        _survivalWaveLive = true;
        int players = Math.Max(1, Players.Values.Count(pl => pl.Alive));
        int count = 5 + 2 * SurvivalWave + 2 * (players - 1);
        int level = ZoneEnemyLevel + (SurvivalWave - 1);
        var anchors = Players.Values.Where(pl => pl.Alive).Select(pl => pl.Position).ToList();
        if (anchors.Count == 0) anchors.Add(Map.PlayerSpawn);
        int spawned = 0;
        for (int i = 0; i < count * 6 && spawned < count; i++)
        {
            var anchor = anchors[i % anchors.Count];
            float ang = (float)(_rng.NextDouble() * Math.PI * 2);
            float dist = 6.5f + (float)_rng.NextDouble() * 3.5f;
            var pos = anchor + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * dist;
            if (pos.X < 1 || pos.Y < 1 || pos.X >= Map.Width - 1 || pos.Y >= Map.Height - 1 || Map.CircleHitsWall(pos, 0.45f) || Map.IsWater((int)pos.X, (int)pos.Y)) continue;
            string kind = RollWaveEnemy(Math.Min(5, SurvivalWave));
            if (!Data.Enemies.ContainsKey(kind)) kind = "grunt";
            var e = SpawnEnemy(kind, pos, level: level);
            e.State = EnemyState.Chase;
            e.Hunting = true; // has the scent: no aggro range, no leash
            _events.WorldEffect("darkburst", pos, 0.8f, 0.5f, e.Height);
            spawned++;
        }
        foreach (var pl in Players.Values)
            _events.MessageFor(pl, $"Wave {SurvivalWave} of {ScrollRun.SurvivalWaves} — they've found you!");
        _events.ZoneStateChanged(this);
    }
}
