using ARPG.World;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using NumVec2 = System.Numerics.Vector2;
using Vector2 = Microsoft.Xna.Framework.Vector2;

namespace ARPG.Render;

/// <summary>
/// Client-side cosmetic weather: rain (slanted drops that burst into a crown-and-ring
/// splash where they land, ripples on water), snow (drifting flakes that SETTLE and
/// lie on the ground for a few seconds before melting, so a dusting builds up) and wind
/// (blown leaves + faint gust streaks). Purely visual and local — an Options toggle,
/// not zone data or network state.
///
/// Particles live in WORLD space around the camera, so shelter is real: drops aimed at
/// a tile the map reports sheltered (under a bridge deck, beneath a tree canopy) never
/// spawn, and everything lands on the column's actual surface — deck, wall top or
/// ground. Two draw passes: what's still in the AIR draws over the scene after the
/// world pass, while every GROUND mark (splash, ripple, settled flake) is queued into
/// the world's depth sort at its landing spot, so characters walk over settled snow
/// and splashes burst at their feet instead of on top of their heads.
/// </summary>
public class WeatherRenderer
{
    private struct Particle
    {
        public NumVec2 Pos;      // world tile position
        public float Land;       // surface height the particle lands on
        public float Air;        // height above Land (level units); <= 0 = landed
        public float Speed;      // fall (rain/snow) or travel (wind) rate
        public float Phase;      // per-particle sway/animation offset
        public float GroundT;    // wind: time alive
        public bool Alive;
        public byte Tint;        // wind: leaf color index
    }

    /// <summary>A mark left on the ground: a rain splash / water ripple, or a settled flake.</summary>
    private struct GroundMark
    {
        public NumVec2 Pos;
        public float Land;
        public float T;          // seconds since it landed
        public float Life;       // total seconds it lasts
        public bool Water;       // rain: landed on water (ripple instead of splash)
        public byte Size;        // snow: 2 or 3 px
        public byte Tone;        // snow: brightness variation
    }

    private const int RainCount = 240;
    private const int SnowCount = 320;
    private const int WindCount = 90;
    private const float SpawnRadius = 13f;   // world tiles around the camera center

    /// <summary>How long a rain splash plays (a water ripple runs longer).</summary>
    public const float SplashSeconds = 0.34f;
    public const float RippleSeconds = 0.6f;
    /// <summary>How long a settled flake lies on the ground before it has melted.</summary>
    public const float SnowSettleSeconds = 6f;
    private const float SnowMeltSeconds = 1.8f; // the fade at the end of the lie
    private const int MaxGroundMarks = 900;

    private Particle[] _parts = Array.Empty<Particle>();
    private readonly List<GroundMark> _ground = new();
    private string _mode = "off";
    private long _lastTick;
    private readonly Random _rng = new();

    /// <summary>Marks currently lying on the ground (splashes, ripples, settled flakes).</summary>
    public int GroundMarkCount => _ground.Count;
    /// <summary>Particles currently in the air.</summary>
    public int AirborneCount { get { int n = 0; foreach (var p in _parts) if (p.Alive) n++; return n; } }

    /// <summary>Wind travel direction in world space (rain slants along it too).</summary>
    private static readonly NumVec2 WindDir = NumVec2.Normalize(new NumVec2(1f, 0.45f));

    private static readonly Color[] LeafTints =
    {
        new(120, 150, 70), new(150, 160, 80), new(140, 110, 60), new(100, 130, 75),
    };

    /// <summary>Advance the weather by real time (no drawing; headless-safe). Call once
    /// per frame before queuing the ground marks. Pass a dt to override the wall clock.</summary>
    public void Update(GameMap map, NumVec2 center, string mode, float? fixedDt = null)
    {
        mode ??= "off";
        long now = Environment.TickCount64;
        float dt = fixedDt ?? Math.Clamp((now - _lastTick) / 1000f, 0f, 0.1f);
        _lastTick = now;
        if (mode != _mode)
        {
            _mode = mode;
            int want = mode switch { "rain" => RainCount, "snow" => SnowCount, "wind" => WindCount, _ => 0 };
            _parts = new Particle[want];
            _ground.Clear();
        }
        if (_parts.Length == 0 || map == null) { _ground.Clear(); return; }

        float clock = now * 0.001f;
        for (int i = 0; i < _parts.Length; i++)
        {
            ref var p = ref _parts[i];
            if (!p.Alive) { TrySpawn(ref p, map, center); continue; }
            // Recycle anything the camera left far behind.
            if (MathF.Abs(p.Pos.X - center.X) > SpawnRadius + 4f ||
                MathF.Abs(p.Pos.Y - center.Y) > SpawnRadius + 4f) { p.Alive = false; continue; }

            switch (_mode)
            {
                case "rain":
                    p.Air -= p.Speed * dt;
                    p.Pos += WindDir * (0.55f * dt); // storm slant
                    if (p.Air <= 0f)
                    {
                        // The drop is spent: it becomes a splash on the ground (or a ripple
                        // on water) and the slot refills so the downpour stays dense.
                        bool water = map.IsWater((int)MathF.Floor(p.Pos.X), (int)MathF.Floor(p.Pos.Y));
                        AddGround(new GroundMark
                        {
                            Pos = p.Pos, Land = p.Land, Water = water,
                            Life = water ? RippleSeconds : SplashSeconds,
                        });
                        p.Alive = false;
                    }
                    break;

                case "snow":
                    p.Air -= p.Speed * dt;
                    p.Pos.X += MathF.Sin(clock * 1.4f + p.Phase) * 0.45f * dt;
                    p.Pos.Y += MathF.Cos(clock * 1.1f + p.Phase * 1.7f) * 0.25f * dt;
                    if (p.Air <= 0f)
                    {
                        // Settle and lie there (flakes that hit water just vanish).
                        if (!map.IsWater((int)MathF.Floor(p.Pos.X), (int)MathF.Floor(p.Pos.Y)))
                            AddGround(new GroundMark
                            {
                                Pos = p.Pos, Land = p.Land, Life = SnowSettleSeconds,
                                Size = (byte)(_rng.Next(3) == 0 ? 3 : 2), Tone = (byte)_rng.Next(3),
                            });
                        p.Alive = false;
                    }
                    break;

                case "wind":
                    p.Pos += WindDir * (p.Speed * dt);
                    p.Air = MathF.Max(0.08f, p.Air + MathF.Sin(clock * 2.2f + p.Phase) * 0.5f * dt);
                    p.GroundT += dt;
                    if (p.GroundT > 5f) p.Alive = false;
                    break;
            }
        }

        // Ground marks age out; anything the camera left far behind goes too.
        for (int i = _ground.Count - 1; i >= 0; i--)
        {
            var g = _ground[i];
            g.T += dt;
            if (g.T >= g.Life ||
                MathF.Abs(g.Pos.X - center.X) > SpawnRadius + 4f ||
                MathF.Abs(g.Pos.Y - center.Y) > SpawnRadius + 4f)
            {
                _ground.RemoveAt(i);
                continue;
            }
            _ground[i] = g;
        }
    }

    private void AddGround(GroundMark mark)
    {
        if (_ground.Count >= MaxGroundMarks) _ground.RemoveAt(0); // oldest melts first
        _ground.Add(mark);
    }

    /// <summary>Queue every ground mark into the world's depth sort at its landing spot
    /// (a hair above the floor), so entities standing past it draw over it.</summary>
    public void QueueGround(IsoCamera camera, Action<NumVec2, float, Action<SpriteBatch>> enqueue)
    {
        if (_mode is not ("rain" or "snow")) return;
        foreach (var g in _ground)
        {
            var mark = g;
            var s = camera.WorldToScreen(mark.Pos, mark.Land);
            if (_mode == "rain")
                enqueue(mark.Pos, mark.Land, sb => { if (mark.Water) DrawRipple(sb, s, mark); else DrawSplash(sb, s, mark); });
            else
                enqueue(mark.Pos, mark.Land, sb => DrawSettledFlake(sb, s, mark));
        }
    }

    /// <summary>Draw what's still in the air, over the scene.</summary>
    public void DrawAir(SpriteBatch sb, IsoCamera camera, GameMap map)
    {
        if (_parts.Length == 0 || map == null) return;
        float clock = Environment.TickCount64 * 0.001f;
        for (int i = 0; i < _parts.Length; i++)
        {
            ref var p = ref _parts[i];
            if (!p.Alive) continue;
            switch (_mode)
            {
                case "rain": DrawRainDrop(sb, camera, p); break;
                case "snow": DrawFlake(sb, camera, p, 0.85f); break;
                case "wind": DrawLeaf(sb, camera, p, clock); break;
            }
        }
    }

    private void TrySpawn(ref Particle p, GameMap map, NumVec2 center)
    {
        var pos = center + new NumVec2(
            ((float)_rng.NextDouble() * 2f - 1f) * SpawnRadius,
            ((float)_rng.NextDouble() * 2f - 1f) * SpawnRadius);
        int tx = (int)MathF.Floor(pos.X), ty = (int)MathF.Floor(pos.Y);
        if (tx < 0 || ty < 0 || tx >= map.Width || ty >= map.Height) return;
        float land = map.WeatherLandHeight(tx, ty);
        // Shelter check at the LANDING surface: under-deck and under-canopy spots
        // never get a particle (wind doesn't care — leaves blow through).
        if (_mode != "wind" && map.IsSheltered(pos, land)) return;

        p.Pos = pos;
        p.Land = land;
        p.Phase = (float)_rng.NextDouble() * MathF.Tau;
        p.GroundT = 0f;
        p.Tint = (byte)_rng.Next(LeafTints.Length);
        switch (_mode)
        {
            case "rain": p.Air = 3.5f + (float)_rng.NextDouble() * 4f; p.Speed = 9f + (float)_rng.NextDouble() * 3f; break;
            case "snow": p.Air = 4f + (float)_rng.NextDouble() * 4f; p.Speed = 0.9f + (float)_rng.NextDouble() * 0.6f; break;
            default: p.Air = 0.1f + (float)_rng.NextDouble() * 1.3f; p.Speed = 4.5f + (float)_rng.NextDouble() * 3.5f; break;
        }
        p.Alive = true;
    }

    private static void DrawRainDrop(SpriteBatch sb, IsoCamera camera, in Particle p)
    {
        var s = camera.WorldToScreen(p.Pos, p.Land + p.Air);
        // A short slanted streak: rotation matches the storm's screen-space lean.
        sb.Draw(TextureGen.Pixel, s, null, new Color(165, 190, 225) * 0.55f,
            0.22f, Vector2.Zero, new Vector2(1.6f, 11f), SpriteEffects.None, 0f);
    }

    /// <summary>A drop bursting on a hard surface: a crown of three droplets that jump
    /// up and fall back over the first third, and a flat ring that widens and fades
    /// underneath — the splash you can actually see from the iso camera.</summary>
    private static void DrawSplash(SpriteBatch sb, Vector2 s, in GroundMark g)
    {
        float t = g.T / g.Life;
        // Ring: a flat ellipse outline, 8 pixels around it, widening as it fades.
        float r = 2f + t * 7f;
        var ring = new Color(200, 224, 248) * (0.78f * (1f - t));
        for (int k = 0; k < 8; k++)
        {
            float a = k * (MathF.Tau / 8f);
            sb.Draw(TextureGen.Pixel,
                new Rectangle((int)(s.X + MathF.Cos(a) * r), (int)(s.Y + MathF.Sin(a) * r * 0.5f), 1, 1), ring);
        }
        // Crown: three droplets thrown up (centre highest), back down by a third in.
        if (t < 0.36f)
        {
            float u = t / 0.36f;
            float lift = 4f * u * (1f - u);
            var drop = new Color(224, 238, 255) * (0.85f * (1f - u * 0.5f));
            sb.Draw(TextureGen.Pixel, new Rectangle((int)s.X, (int)(s.Y - 1 - lift * 5f), 1, 2), drop);
            sb.Draw(TextureGen.Pixel, new Rectangle((int)s.X - 2 - (int)(u * 2f), (int)(s.Y - lift * 3f), 1, 1), drop);
            sb.Draw(TextureGen.Pixel, new Rectangle((int)s.X + 2 + (int)(u * 2f), (int)(s.Y - lift * 3f), 1, 1), drop);
        }
    }

    /// <summary>A drop on water: two concentric ripple rings spreading out, no crown.</summary>
    private static void DrawRipple(SpriteBatch sb, Vector2 s, in GroundMark g)
    {
        float t = g.T / g.Life;
        for (int ringIdx = 0; ringIdx < 2; ringIdx++)
        {
            float tt = t - ringIdx * 0.25f;
            if (tt < 0f) continue;
            float r = 2f + tt * 12f;
            var ring = new Color(210, 232, 250) * (0.5f * (1f - tt));
            for (int k = 0; k < 10; k++)
            {
                float a = k * (MathF.Tau / 10f);
                sb.Draw(TextureGen.Pixel,
                    new Rectangle((int)(s.X + MathF.Cos(a) * r), (int)(s.Y + MathF.Sin(a) * r * 0.5f), 1, 1), ring);
            }
        }
    }

    private static void DrawFlake(SpriteBatch sb, IsoCamera camera, in Particle p, float alpha)
    {
        var s = camera.WorldToScreen(p.Pos, p.Land + p.Air);
        sb.Draw(TextureGen.Pixel, new Rectangle((int)s.X, (int)s.Y, 2, 2), Color.White * alpha);
    }

    /// <summary>A flake lying where it fell: a 2-3 px fleck of white, full for most of
    /// its stay, then melting away over the last stretch.</summary>
    private static void DrawSettledFlake(SpriteBatch sb, Vector2 s, in GroundMark g)
    {
        float left = g.Life - g.T;
        float alpha = MathF.Min(1f, left / SnowMeltSeconds) * 0.82f;
        var tone = g.Tone switch { 0 => new Color(246, 250, 255), 1 => new Color(228, 238, 250), _ => new Color(255, 255, 255) };
        int w = g.Size, h = g.Size == 3 ? 2 : 2;
        sb.Draw(TextureGen.Pixel, new Rectangle((int)s.X, (int)s.Y, w, h), tone * alpha);
    }

    private void DrawLeaf(SpriteBatch sb, IsoCamera camera, in Particle p, float clock)
    {
        var s = camera.WorldToScreen(p.Pos, p.Land + p.Air);
        float tumble = clock * 4f + p.Phase;
        sb.Draw(TextureGen.Pixel, s, null, LeafTints[p.Tint] * 0.8f,
            tumble, new Vector2(0.5f, 0.5f), new Vector2(3f, 2f), SpriteEffects.None, 0f);
        // Faint gust streak trailing a few of the leaves sells the wind itself.
        if (p.Tint == 0)
            sb.Draw(TextureGen.Pixel, s - new Vector2(14, 2), null, Color.White * 0.07f,
                0.1f, Vector2.Zero, new Vector2(16f, 1f), SpriteEffects.None, 0f);
    }
}
