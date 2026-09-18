using ARPG.Core;
using ARPG.Data;
using ARPG.Render;
using ARPG.World;
using FontStashSharp;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using NumVec2 = System.Numerics.Vector2;

namespace ARPG.UI;

/// <summary>
/// The scripted-scene player: letterbox bars, a camera focus the play screen lerps
/// toward, and timed dialogue lines. Scenes are identified by id (broadcast by the
/// server so everyone watches together) and their text lives in Data/Cutscenes —
/// anchored to the current map's authored spots, no sync traffic beyond the one id. Click or
/// SPACE advances a line early; ENTER skips the whole scene. The world keeps
/// simulating underneath — a cutscene is a view, never a pause.
/// </summary>
public class CutscenePlayer
{
    public readonly record struct Step(NumVec2 Focus, string Speaker, string Line, float Duration);

    public bool Active { get; private set; }
    private readonly List<Step> _steps = new();
    private int _index;
    private float _timer;

    /// <summary>Where the camera should look right now (world space).</summary>
    public NumVec2 Focus => Active ? _steps[_index].Focus : default;

    public void Start(string id, GameMap map, GameData data)
    {
        _steps.Clear();
        _index = 0;
        _timer = 0f;
        Build(id, map, data, _steps);
        Active = _steps.Count > 0;
    }

    /// <summary>The scenes come from Data/Cutscenes/*.json; each line's anchor names a
    /// spot of the current map (see CutsceneDefinition), so the same text plays
    /// correctly on any map that has the spot.</summary>
    private static void Build(string id, GameMap map, GameData data, List<Step> steps)
    {
        if (map == null || data == null || !data.Cutscenes.TryGetValue(id, out var scene)) return;
        foreach (var st in scene.Steps)
            steps.Add(new Step(ResolveAnchor(st.Anchor, map), st.Speaker ?? "", st.Line ?? "", Math.Max(0.5f, st.Duration)));
    }

    public static NumVec2 ResolveAnchor(string anchor, GameMap map)
    {
        string key = string.IsNullOrWhiteSpace(anchor) ? "camp" : anchor.Trim();
        var offset = NumVec2.Zero;
        int plus = key.IndexOf('+');
        if (plus >= 0)
        {
            var parts = key[(plus + 1)..].Split(',');
            if (parts.Length == 2 &&
                float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float dx) &&
                float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float dy))
                offset = new NumVec2(dx, dy);
            key = key[..plus];
        }
        NumVec2 at;
        if (key.StartsWith("npc:") && int.TryParse(key[4..], out int npcIndex) && npcIndex >= 0 && npcIndex < map.NpcSpots.Count)
            at = map.NpcSpots[npcIndex];
        else
            at = key switch
            {
                "gate" or "exit" => map.ExitDoor,
                "boss" => map.BossSpot,
                "podium" => map.PodiumSpot,
                "spawn" => map.PlayerSpawn,
                "fountain" => map.FountainSpot,
                _ => map.WagonSpot,
            };
        if (at == NumVec2.Zero) at = map.WagonSpot != NumVec2.Zero ? map.WagonSpot : map.PlayerSpawn;
        return at + offset;
    }

    public void Update(float dt, InputManager input)
    {
        if (!Active) return;
        if (input.WasKeyPressed(Keys.Enter))
        {
            Active = false;
            return;
        }
        _timer += dt;
        bool advance = _timer >= _steps[_index].Duration ||
                       input.MouseLeftPressed || input.WasKeyPressed(Keys.Space);
        if (!advance) return;
        _timer = 0f;
        _index++;
        if (_index >= _steps.Count) Active = false;
    }

    public void Draw(SpriteBatch sb, Point screen)
    {
        if (!Active) return;
        int bar = Math.Max(56, (int)(screen.Y * 0.12f));
        sb.Draw(TextureGen.Pixel, new Rectangle(0, 0, screen.X, bar), new Color(0, 0, 0, 235));
        sb.Draw(TextureGen.Pixel, new Rectangle(0, screen.Y - bar, screen.X, bar), new Color(0, 0, 0, 235));

        var step = _steps[_index];
        var nameFont = FontManager.GetBold(17);
        var lineFont = FontManager.Get(16);
        string speaker = step.Speaker + ":";
        var sSize = nameFont.MeasureString(speaker);
        var lSize = lineFont.MeasureString(step.Line);
        float totalW = sSize.X + 10f + lSize.X;
        float x = screen.X / 2f - totalW / 2f;
        float y = screen.Y - bar / 2f - lSize.Y / 2f;
        sb.DrawString(nameFont, speaker, new Vector2(x, y - 1), new Color(240, 200, 110));
        sb.DrawString(lineFont, step.Line, new Vector2(x + sSize.X + 10f, y), new Color(232, 228, 216));

        string hint = "click to continue  ·  ENTER skips";
        var hFont = FontManager.Get(11);
        var hSize = hFont.MeasureString(hint);
        sb.DrawString(hFont, hint, new Vector2(screen.X - hSize.X - 14, screen.Y - 18),
            new Color(126, 122, 112));
    }
}
