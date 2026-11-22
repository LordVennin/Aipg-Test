using FontStashSharp;
using ARPG.Core;
using ARPG.Data;
using ARPG.Net;
using ARPG.Render;
using ARPG.Sim;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ARPG.UI;

/// <summary>
/// Quest display. The data all comes from the character the server sends
/// (CharacterData.Quests) plus each party member's broadcast quest step:
///  - the TRACKER: a compact top-right block with the active quest's current step and
///    progress ("Goblins slain 7/12"), the side quests under it, and a "helping
///    &lt;name&gt;" line when a party member is on a step you have already done;
///  - the QUEST LOG panel (default key J): every active quest with its steps ticked
///    off, and the completed ones.
/// The text model is static and UI-free so the headless test can read it.
/// </summary>
public class QuestUI
{
    public bool Open;
    private readonly GameData _data;
    private readonly GameClient _client;
    private Rectangle _panelRect;
    private Point _lastMouse;
    private int _scroll;
    public readonly WindowDrag Window = new();

    public QuestUI(GameData data, GameClient client)
    {
        _data = data;
        _client = client;
    }

    // ------------------------------------------------------------------ text model

    /// <summary>"Goblins slain 7/12" / "Speak to the skill trainer in the camp".</summary>
    public static string StepText(QuestStep step, int count) =>
        step.Count > 1 ? $"{step.Text} {Math.Min(count, step.Count)}/{step.Count}" : step.Text;

    /// <summary>The quest the tracker leads with: the first active main-line quest,
    /// else the first active side quest (the same rule the server broadcasts).</summary>
    public static QuestProgress Tracked(GameData data, CharacterData c) =>
        c?.Quests?.Active.FirstOrDefault(q => data.GetQuest(q.QuestId) is { Side: false }) ?? c?.Quests?.Active.FirstOrDefault();

    /// <summary>One tracker line: Kind is "title", "step", "side" or "helping".</summary>
    public readonly record struct TrackerLine(string Kind, string Text);

    public static List<TrackerLine> TrackerLines(GameData data, CharacterData c, IEnumerable<ClientPlayer> others)
    {
        var lines = new List<TrackerLine>();
        if (c?.Quests == null) return lines;
        var tracked = Tracked(data, c);
        if (tracked != null && data.GetQuest(tracked.QuestId) is { } def && tracked.Step < def.Steps.Count)
        {
            lines.Add(new TrackerLine("title", def.Name));
            lines.Add(new TrackerLine("step", StepText(def.Steps[tracked.Step], tracked.Count)));
        }
        foreach (var q in c.Quests.Active)
        {
            if (q == tracked || lines.Count >= 6) continue;
            if (data.GetQuest(q.QuestId) is { } sideDef && q.Step < sideDef.Steps.Count)
                lines.Add(new TrackerLine("side", $"{sideDef.Name}: {StepText(sideDef.Steps[q.Step], q.Count)}"));
        }
        // A party member still on a step you've finished: you're helping them.
        foreach (var other in others ?? Enumerable.Empty<ClientPlayer>())
        {
            if (other == null || other.IsLocal || string.IsNullOrEmpty(other.QuestId)) continue;
            var theirs = data.GetQuest(other.QuestId);
            if (theirs == null || other.QuestStep < 0 || other.QuestStep >= theirs.Steps.Count) continue;
            var mine = c.Quests.Get(other.QuestId);
            bool ahead = c.Quests.IsCompleted(other.QuestId) || (mine != null && mine.Step > other.QuestStep);
            if (ahead) lines.Add(new TrackerLine("helping", $"helping {other.Name}: {theirs.Steps[other.QuestStep].Text}"));
        }
        return lines;
    }

    // ------------------------------------------------------------------ tracker HUD

    public void DrawTracker(SpriteBatch sb, Point screen)
    {
        var c = _client.World.MyCharacter;
        var lines = TrackerLines(_data, c, _client.World.Players.Values);
        if (lines.Count == 0) return;
        const int w = 300;
        int x = screen.X - w - 14, y = 14;
        var titleFont = FontManager.GetBold(15);
        var font = FontManager.Get(13);
        int h = 8;
        foreach (var line in lines) h += line.Kind == "title" ? 20 : 17;
        sb.Draw(TextureGen.Pixel, new Rectangle(x - 8, y - 4, w + 16, h + 4), new Color(12, 12, 18, 150));
        sb.Draw(TextureGen.Pixel, new Rectangle(x - 8, y - 4, 2, h + 4), new Color(214, 184, 110, 200));
        foreach (var line in lines)
        {
            switch (line.Kind)
            {
                case "title":
                    sb.DrawString(titleFont, line.Text, new Vector2(x, y), new Color(236, 212, 150));
                    y += 20;
                    break;
                case "step":
                    sb.DrawString(font, "- " + Fit(font, line.Text, w - 12), new Vector2(x + 4, y), Color.White);
                    y += 17;
                    break;
                case "side":
                    sb.DrawString(font, Fit(font, line.Text, w - 4), new Vector2(x, y), new Color(170, 176, 190));
                    y += 17;
                    break;
                default:
                    sb.DrawString(font, Fit(font, line.Text, w - 4), new Vector2(x, y), new Color(130, 210, 150));
                    y += 17;
                    break;
            }
        }
    }

    private static string Fit(SpriteFontBase font, string text, int width)
    {
        if (font.MeasureString(text).X <= width) return text;
        while (text.Length > 4 && font.MeasureString(text + "...").X > width) text = text[..^1];
        return text + "...";
    }

    // ------------------------------------------------------------------ quest log panel

    public void Layout(Point screen)
    {
        _panelRect = Window.Place(new Rectangle(screen.X / 2 - 240, 50, 480, Math.Min(520, screen.Y - 100)), screen);
    }

    public bool Contains(Point p) => Open && _panelRect.Contains(p);

    public void Update(InputManager input, bool mouseBlocked = false)
    {
        if (!Open || mouseBlocked) return;
        _lastMouse = input.MousePosition;
        if (CloseButton.Handle(input, _panelRect)) { Open = false; return; }
        if (Window.HandleBar(input, WindowDrag.BarFor(_panelRect))) return;
        if (!_panelRect.Contains(_lastMouse)) return;
        input.MouseCapturedByUI = true;
        if (input.ScrollDelta != 0) _scroll = Math.Max(0, _scroll - Math.Sign(input.ScrollDelta) * 40);
    }

    public void Draw(SpriteBatch sb)
    {
        if (!Open) return;
        var c = _client.World.MyCharacter;
        if (c?.Quests == null) return;
        sb.Draw(TextureGen.Pixel, _panelRect, new Color(22, 22, 28, 244));
        Border(sb, _panelRect, new Color(170, 146, 90));
        WindowDrag.DrawBar(sb, _panelRect, _lastMouse);
        CloseButton.Draw(sb, _panelRect, _lastMouse);
        int x = _panelRect.X + 14;
        sb.DrawString(FontManager.GetBold(19), "Quest Log", new Vector2(x, _panelRect.Y + 8), new Color(236, 212, 150));
        var small = FontManager.Get(12);
        sb.DrawString(small, "your own log — a step only counts if you were there when it happened",
            new Vector2(x, _panelRect.Y + 34), new Color(150, 146, 134));

        var body = new Rectangle(_panelRect.X + 8, _panelRect.Y + 56, _panelRect.Width - 16, _panelRect.Height - 66);
        var nameFont = FontManager.GetBold(15);
        var font = FontManager.Get(13);
        int y = body.Y - _scroll;
        bool Row(int height) => y + height > body.Y && y + height <= body.Bottom + height && y >= body.Y - 2 && y + height <= body.Bottom;
        void Text(SpriteFontBase f, string text, int indent, Color color, int height)
        {
            if (Row(height)) sb.DrawString(f, Fit(f, text, body.Width - indent - 8), new Vector2(x + indent, y), color);
            y += height;
        }
        if (c.Quests.Active.Count == 0 && c.Quests.Completed.Count == 0)
            Text(font, "No quests yet. (Quests follow the story — start a Story game.)", 0, new Color(170, 166, 150), 20);
        foreach (var q in c.Quests.Active)
        {
            var def = _data.GetQuest(q.QuestId);
            if (def == null) continue;
            Text(nameFont, def.Name + (def.Side ? "   (side quest)" : ""), 0, new Color(236, 212, 150), 21);
            foreach (var descLine in ItemTooltip.WrapText(def.Description ?? "", 64))
                Text(small, descLine, 4, new Color(160, 156, 144), 15);
            for (int i = 0; i < def.Steps.Count; i++)
            {
                bool done = i < q.Step, current = i == q.Step;
                string mark = done ? "[x] " : current ? "[ ] " : "    ";
                Text(font, mark + (current ? StepText(def.Steps[i], q.Count) : def.Steps[i].Text), 8,
                    done ? new Color(120, 170, 120) : current ? Color.White : new Color(120, 118, 112), 18);
            }
            var rewards = new List<string>();
            if (def.RewardGold > 0) rewards.Add($"{def.RewardGold} gold");
            if (def.RewardXp > 0) rewards.Add($"{def.RewardXp:0} xp");
            if (!string.IsNullOrEmpty(def.RewardItem) && _data.Items.TryGetValue(def.RewardItem, out var ri)) rewards.Add(ri.Name);
            if (rewards.Count > 0) Text(small, "Reward: " + string.Join(", ", rewards), 8, new Color(200, 180, 110), 16);
            y += 8;
        }
        if (c.Quests.Completed.Count > 0)
        {
            Text(nameFont, "Completed", 0, new Color(150, 190, 150), 22);
            foreach (var id in c.Quests.Completed)
                Text(font, "[x] " + (_data.GetQuest(id)?.Name ?? id), 8, new Color(120, 170, 120), 18);
        }
        int contentHeight = y + _scroll - body.Y;
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, contentHeight - body.Height));
    }

    private static void Border(SpriteBatch sb, Rectangle r, Color c)
    {
        sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Y, r.Width, 2), c);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Bottom - 2, r.Width, 2), c);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Y, 2, r.Height), c);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.Right - 2, r.Y, 2, r.Height), c);
    }
}
