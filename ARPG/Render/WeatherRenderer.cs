using ARPG.World;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using NumVec2 = System.Numerics.Vector2;
using Vector2 = Microsoft.Xna.Framework.Vector2;

namespace ARPG.Render;

/// <summary>
/// Client-side cosmetic weather: rain (slanted drops + ground splashes), snow (drifting
/// flakes that settle and fade) and wind (blown leaves + faint gust streaks). Purely
/// visual and local — an Options toggle, not zone data or network state. Particles live
/// in WORLD space around the camera, so shelter is real: drops aimed at a tile the map
/// reports sheltered (under a bridge deck, beneath a tree canopy, future interiors)
/// never spawn, and everything lands on the column's actual surface — deck, wall top or
/// ground. Drawn inside the world pass, so zone lighting dims it like everything else.
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
        public float GroundT;    // time since landing (splash / settle fade)
        public bool Alive;
        public byte Tint;        // wind: leaf color index
    }

    private const int RainCount = 240;
    private const int SnowCount = 170;
    private const int WindCount = 90;
    private const float SpawnRadius = 13f;   // world tiles around the camera center

    private Particle[] _parts = Array.Empty<Particle>();
    private string _mode = "off";
    private long _lastTick;
    private readonly Random _rng = new();

    /// <summary>Wind travel direction in world space (rain slants along it too).</summary>
    private static readonly NumVec2 WindDir = NumVec2.Normalize(new NumVec2(1f, 0.45f));

    private static readonly Color[] LeafTints =
    {
        new(120, 150, 70), new(150, 160, 80), new(140, 110, 60), new(100, 130, 75),
    };

    public void Draw(SpriteBatch sb, IsoCamera camera, GameMap map, NumVec2 center, string mode)
    {
        mode ??= "off";
        long now = Environment.TickCount64;
        float dt = Math.Clamp((now - _lastTick) / 1000f, 0f, 0.1f);
        _lastTick = now;
        if (mode != _mode)
        {
            _mode = mode;
            int want = mode switch { "rain" => RainCount, "snow" => SnowCount, "wind" => WindCount, _ => 0 };
            _parts = new Particle[want];
        }
        if (_parts.Length == 0 || map == null) return;

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
                    if (p.Air > 0f)
                    {
                        p.Air -= p.Speed * dt;
                        p.Pos += WindDir * (0.55f * dt); // storm slant
                        if (p.Air <= 0f) { p.Air = 0f; p.GroundT = 0f; }
                        DrawRainDrop(sb, camera, p);
                    }
                    else
                    {
                        p.GroundT += dt;
                        if (p.GroundT > 0.28f) { p.Alive = false; break; }
                        DrawSplash(sb, camera, p);
                    }
                    break;

                case "snow":
                    if (p.Air > 0f)
                    {
                        p.Air -= p.Speed * dt;
                        p.Pos.X += MathF.Sin(clock * 1.4f + p.Phase) * 0.45f * dt;
                        p.Pos.Y += MathF.Cos(clock * 1.1f + p.Phase * 1.7f) * 0.25f * dt;
                        if (p.Air <= 0f) { p.Air = 0f; p.GroundT = 0f; }
                        DrawFlake(sb, camera, p, 0.85f);
                    }
                    else
                    {
                        p.GroundT += dt; // settled: linger, then melt away
                        if (p.GroundT > 1.1f) { p.Alive = false; break; }
                        DrawFlake(sb, camera, p, 0.75f * (1f - p.GroundT / 1.1f));
                    }
                    break;

                case "wind":
                    p.Pos += WindDir * (p.Speed * dt);
                    p.Air = MathF.Max(0.08f, p.Air + MathF.Sin(clock * 2.2f + p.Phase) * 0.5f * dt);
                    p.GroundT += dt;
                    if (p.GroundT > 5f) { p.Alive = false; break; }
                    DrawLeaf(sb, camera, map, p, clock);
                    break;
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

    private static void DrawSplash(SpriteBatch sb, IsoCamera camera, in Particle p)
    {
        var s = camera.WorldToScreen(p.Pos, p.Land);
        float t = p.GroundT / 0.28f;
        float r = 2f + t * 5f;
        var c = new Color(180, 205, 230) * (0.5f * (1f - t));
        // Four pixels widening into a flat ellipse — reads as a splash ring in iso.
        sb.Draw(TextureGen.Pixel, new Rectangle((int)(s.X - r), (int)s.Y, 2, 1), c);
        sb.Draw(TextureGen.Pixel, new Rectangle((int)(s.X + r), (int)s.Y, 2, 1), c);
        sb.Draw(TextureGen.Pixel, new Rectangle((int)s.X, (int)(s.Y - r * 0.5f), 1, 1), c);
        sb.Draw(TextureGen.Pixel, new Rectangle((int)s.X, (int)(s.Y + r * 0.5f), 1, 1), c);
    }

    private static void DrawFlake(SpriteBatch sb, IsoCamera camera, in Particle p, float alpha)
    {
        var s = camera.WorldToScreen(p.Pos, p.Land + p.Air);
        sb.Draw(TextureGen.Pixel, new Rectangle((int)s.X, (int)s.Y, 2, 2), Color.White * alpha);
    }

    private void DrawLeaf(SpriteBatch sb, IsoCamera camera, GameMap map, in Particle p, float clock)
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
