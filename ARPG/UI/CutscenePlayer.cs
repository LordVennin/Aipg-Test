using ARPG.Core;
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
/// server so everyone watches together) and DEFINED here against the current map's
/// authored spots — no data files, no sync traffic beyond the one id. Click or
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

    public void Start(string id, GameMap map)
    {
        _steps.Clear();
        _index = 0;
        _timer = 0f;
        Build(id, map, _steps);
        Active = _steps.Count > 0;
    }

    /// <summary>The scenes themselves, staged against the map's authored landmarks.</summary>
    private static void Build(string id, GameMap map, List<Step> steps)
    {
        if (map == null) return;
        var camp = map.WagonSpot;
        var gate = map.ExitDoor;
        var boss = map.BossSpot;
        switch (id)
        {
            case "tut_intro":
                steps.Add(new Step(camp, "Brakka",
                    "Road's washed out, and the dead don't much care for company.", 3.8f));
                steps.Add(new Step(camp + new NumVec2(9f, -1f), "Brakka",
                    "Clear us a path east to those ruins and we'll make camp somewhere dry.", 3.8f));
                steps.Add(new Step(camp, "Odessa",
                    "Mind the pools, dear. Things live in them. Well... 'live'.", 3.4f));
                break;
            case "tut_clearway":
                steps.Add(new Step(boss, "Brakka",
                    "There's the gate... and THAT is exactly why we hired you.", 3.8f));
                steps.Add(new Step(gate, "Brakka",
                    "Clear the way. We'll bring the wagon up behind you.", 3.2f));
                break;
            case "tut_victory":
                steps.Add(new Step(boss, "Brakka",
                    "HA! Not bad. Not bad at all.", 2.8f));
                steps.Add(new Step(gate, "Odessa",
                    "Dry, defensible, and riddled with things to study. The ruins will do nicely.", 4.0f));
                steps.Add(new Step(gate, "Brakka",
                    "We'll bring the wagon through and get a roof up — come along when you're ready.", 3.4f));
                break;
        }
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
