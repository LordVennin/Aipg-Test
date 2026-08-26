using ARPG.Core;
using ARPG.Net;
using ARPG.Render;
using FontStashSharp;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ARPG.UI;

/// <summary>
/// Odessa's desk: spend Mercenary Contracts on RANDOMIZED hires (the roll is the
/// server's — kind, name and power), hand over the Flamethrower Blueprint to unlock
/// the third turret, and review the roster of everyone hired so far.
/// </summary>
public class ResearcherUI
{
    public bool Open;
    private readonly GameClient _client;
    private Rectangle _panelRect, _listRect;
    private Point _lastMouse;
    private int _scroll;
    private const int RowH = 24;

    public readonly WindowDrag Window = new();

    public ResearcherUI(GameClient client) => _client = client;

    public void Layout(Point screen)
    {
        _panelRect = Window.Place(new Rectangle(screen.X / 2 - 200, 52, 400, 430), screen);
        _listRect = new Rectangle(_panelRect.X + 10, _panelRect.Y + 176, _panelRect.Width - 20,
            _panelRect.Height - 220);
    }

    public bool Contains(Point p) => Open && _panelRect.Contains(p);

    private int BagCount(string baseId) =>
        _client.World.MyCharacter?.Inventory.Items
            .Where(pl => pl.Item.BaseItemId == baseId)
            .Sum(pl => Math.Max(1, pl.Item.StackCount)) ?? 0;

    private Rectangle HireRect() =>
        new(_panelRect.X + 10, _panelRect.Y + 82, _panelRect.Width - 20, 34);

    private Rectangle BlueprintRect() =>
        new(_panelRect.X + 10, _panelRect.Y + 124, _panelRect.Width - 20, 34);

    public void Update(InputManager input, bool mouseBlocked = false)
    {
        if (!Open || mouseBlocked) return;
        _lastMouse = input.MousePosition;
        if (CloseButton.Handle(input, _panelRect)) { Open = false; return; }
        if (Window.HandleBar(input, WindowDrag.BarFor(_panelRect))) return;
        if (!_panelRect.Contains(_lastMouse)) return;
        input.MouseCapturedByUI = true;

        var character = _client.World.MyCharacter;
        if (character == null) return;
        int mercCount = character.Mercs.Count;
        int maxScroll = Math.Max(0, mercCount - _listRect.Height / RowH);
        if (input.ScrollDelta != 0)
            _scroll = Math.Clamp(_scroll - Math.Sign(input.ScrollDelta) * 3, 0, maxScroll);
        _scroll = Math.Clamp(_scroll, 0, maxScroll);

        if (input.MouseLeftPressed)
        {
            if (HireRect().Contains(_lastMouse) && BagCount("merc_contract") > 0)
                _client.RequestResearch(0);
            else if (BlueprintRect().Contains(_lastMouse) &&
                     !character.FlamethrowerUnlocked && BagCount("flamethrower_blueprint") > 0)
                _client.RequestResearch(1);
        }
    }

    public void Draw(SpriteBatch sb)
    {
        if (!Open) return;
        var character = _client.World.MyCharacter;
        if (character == null) return;

        sb.Draw(TextureGen.Pixel, _panelRect, new Color(22, 24, 32, 242));
        Border(sb, _panelRect, new Color(110, 140, 190));
        WindowDrag.DrawBar(sb, _panelRect, _lastMouse);
        CloseButton.Draw(sb, _panelRect, _lastMouse);

        int x = _panelRect.X + 12;
        sb.DrawString(FontManager.GetBold(19), "Odessa the Researcher",
            new Vector2(x, _panelRect.Y + 8), new Color(150, 190, 240));
        int contracts = BagCount("merc_contract");
        sb.DrawString(FontManager.Get(13),
            $"contracts in your bag: {contracts}",
            new Vector2(x, _panelRect.Y + 36), new Color(200, 190, 160));
        sb.DrawString(FontManager.Get(12),
            "each contract hires a RANDOM sword or bow — fate signs the name",
            new Vector2(x, _panelRect.Y + 56), new Color(150, 145, 132));

        // Hire button.
        var hire = HireRect();
        bool canHire = contracts > 0;
        sb.Draw(TextureGen.Pixel, hire,
            canHire && hire.Contains(_lastMouse) ? new Color(50, 72, 104, 235)
            : canHire ? new Color(38, 54, 78, 235) : new Color(34, 36, 44, 210));
        Border(sb, hire, canHire ? new Color(120, 160, 210) : new Color(70, 74, 86));
        string hireLabel = canHire ? "Hire a mercenary  ·  1 contract" : "No contracts to spend";
        var hSize = FontManager.GetBold(14).MeasureString(hireLabel);
        sb.DrawString(FontManager.GetBold(14), hireLabel,
            new Vector2(hire.Center.X - hSize.X / 2, hire.Y + 8),
            canHire ? new Color(220, 230, 245) : new Color(130, 130, 140));

        // Blueprint line.
        var bp = BlueprintRect();
        bool hasBp = BagCount("flamethrower_blueprint") > 0;
        bool canHand = !character.FlamethrowerUnlocked && hasBp;
        sb.Draw(TextureGen.Pixel, bp,
            canHand && bp.Contains(_lastMouse) ? new Color(96, 62, 34, 235)
            : canHand ? new Color(72, 48, 28, 235) : new Color(34, 36, 44, 210));
        Border(sb, bp, canHand ? new Color(220, 150, 70) : new Color(70, 74, 86));
        string bpLabel = character.FlamethrowerUnlocked
            ? "Flamethrower: RESEARCHED — build it at any workbench"
            : hasBp ? "Hand over the flamethrower blueprint"
            : "Flamethrower: blueprint not yet found";
        var bSize = FontManager.GetBold(13).MeasureString(bpLabel);
        sb.DrawString(FontManager.GetBold(13), bpLabel,
            new Vector2(bp.Center.X - bSize.X / 2, bp.Y + 9),
            character.FlamethrowerUnlocked ? new Color(150, 220, 150)
            : canHand ? new Color(240, 200, 130) : new Color(130, 130, 140));

        // Roster.
        sb.DrawString(FontManager.GetBold(14), $"Your roster ({character.Mercs.Count})",
            new Vector2(x, _listRect.Y - 16), new Color(210, 205, 190));
        sb.Draw(TextureGen.Pixel, _listRect, new Color(14, 14, 20, 235));
        int visible = _listRect.Height / RowH;
        var rowFont = FontManager.Get(13);
        var subFont = FontManager.Get(11);
        for (int i = 0; i < visible; i++)
        {
            int idx = i + _scroll;
            if (idx >= character.Mercs.Count) break;
            var m = character.Mercs[idx];
            var row = new Rectangle(_listRect.X, _listRect.Y + i * RowH, _listRect.Width, RowH);
            var mercTex = SpriteGen.GetSummonSprite("merc_" + m.Kind);
            if (mercTex != null)
                sb.Draw(mercTex, new Rectangle(row.X + 4, row.Y + 2, 15, 20), Color.White);
            sb.DrawString(rowFont, m.Name, new Vector2(row.X + 26, row.Y + 4), new Color(228, 222, 206));
            string tag = $"{m.Kind} · power {m.Power}";
            var tSize = subFont.MeasureString(tag);
            sb.DrawString(subFont, tag,
                new Vector2(row.Right - tSize.X - 8, row.Y + 6), new Color(150, 145, 132));
        }
        if (character.Mercs.Count > visible)
            sb.DrawString(subFont,
                $"scroll · {_scroll + 1}-{Math.Min(character.Mercs.Count, _scroll + visible)} of {character.Mercs.Count}",
                new Vector2(_listRect.X, _listRect.Bottom + 4), new Color(140, 132, 120));
        sb.DrawString(FontManager.Get(12), "deploy hired mercs from the defense workbench",
            new Vector2(x, _panelRect.Bottom - 20), new Color(150, 140, 120));
    }

    private static void Border(SpriteBatch sb, Rectangle r, Color c)
    {
        sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Y, r.Width, 2), c);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Bottom - 2, r.Width, 2), c);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Y, 2, r.Height), c);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.Right - 2, r.Y, 2, r.Height), c);
    }
}
