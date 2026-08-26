using ARPG.Core;
using ARPG.Net;
using ARPG.Render;
using ARPG.World;
using FontStashSharp;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ARPG.UI;

/// <summary>
/// The workbench: the defense arena's build menu. Pick a structure, then click a
/// spot on the ground to place it (right-click cancels). Prices are the shared
/// DefenseBalance rules — the server re-validates everything anyway. Also hosts
/// the "call the wave" ready button.
/// </summary>
public class BuildUI
{
    public bool Open;
    /// <summary>The structure kind being placed (play screen runs the placement),
    /// or null when not placing.</summary>
    public StructureKind? PendingKind;

    private readonly GameClient _client;
    private Rectangle _panelRect;
    private Point _lastMouse;
    private const int RowH = 44;

    public readonly WindowDrag Window = new();

    private static readonly (StructureKind Kind, string Name, string Desc)[] Rows =
    {
        (StructureKind.CrossbowTurret, "Crossbow Turret", "shoots bolts at whatever comes close"),
        (StructureKind.SpikedBarrier, "Spiked Barrier", "a wall of stakes the horde must break through"),
        (StructureKind.FlameTurret, "Flamethrower", "requires a blueprint nobody has found yet"),
    };

    public BuildUI(GameClient client) => _client = client;

    public void Layout(Point screen) =>
        _panelRect = Window.Place(new Rectangle(screen.X / 2 - 190, 60, 380, 118 + Rows.Length * RowH), screen);

    public bool Contains(Point p) => Open && _panelRect.Contains(p);

    public void Update(InputManager input, bool mouseBlocked = false)
    {
        if (!Open || mouseBlocked) return;
        _lastMouse = input.MousePosition;
        if (CloseButton.Handle(input, _panelRect)) { Open = false; return; }
        if (Window.HandleBar(input, WindowDrag.BarFor(_panelRect))) return;
        if (!_panelRect.Contains(_lastMouse)) return;
        input.MouseCapturedByUI = true;

        bool buildPhase = _client.World.DefensePhase == 0;
        if (input.MouseLeftPressed)
        {
            for (int i = 0; i < Rows.Length; i++)
            {
                var row = RowRect(i);
                if (!row.Contains(_lastMouse)) continue;
                var (kind, _, _) = Rows[i];
                if (kind == StructureKind.FlameTurret) break; // batch 48: blueprint unlock
                if (!buildPhase) break;
                if ((_client.World.MyCharacter?.Gold ?? 0) < DefenseBalance.Cost(kind)) break;
                PendingKind = kind;   // the play screen takes over placement
                Open = false;
                return;
            }
            if (ReadyRect().Contains(_lastMouse) && buildPhase)
            {
                _client.RequestDoorReady();
                Open = false;
            }
        }
    }

    private Rectangle RowRect(int i) =>
        new(_panelRect.X + 10, _panelRect.Y + 62 + i * RowH, _panelRect.Width - 20, RowH - 6);

    private Rectangle ReadyRect() =>
        new(_panelRect.X + 10, _panelRect.Bottom - 46, _panelRect.Width - 20, 34);

    public void Draw(SpriteBatch sb)
    {
        if (!Open) return;
        var character = _client.World.MyCharacter;
        if (character == null) return;

        sb.Draw(TextureGen.Pixel, _panelRect, new Color(24, 20, 30, 242));
        Border(sb, _panelRect, new Color(150, 120, 70));
        WindowDrag.DrawBar(sb, _panelRect, _lastMouse);
        CloseButton.Draw(sb, _panelRect, _lastMouse);

        int x = _panelRect.X + 12;
        sb.DrawString(FontManager.GetBold(19), "Workbench",
            new Vector2(x, _panelRect.Y + 8), new Color(240, 200, 110));
        sb.DrawString(FontManager.Get(14), $"Your gold: {character.Gold}",
            new Vector2(x, _panelRect.Y + 36), new Color(240, 200, 90));

        bool buildPhase = _client.World.DefensePhase == 0;
        var nameFont = FontManager.Get(15);
        var subFont = FontManager.Get(11);
        for (int i = 0; i < Rows.Length; i++)
        {
            var (kind, name, desc) = Rows[i];
            var row = RowRect(i);
            bool locked = kind == StructureKind.FlameTurret;
            int cost = DefenseBalance.Cost(kind);
            bool afford = !locked && buildPhase && character.Gold >= cost;
            bool hover = row.Contains(_lastMouse);
            sb.Draw(TextureGen.Pixel, row,
                hover && afford ? new Color(60, 50, 36, 220) : new Color(14, 12, 18, 235));
            var tex = SpriteGen.GetStructureSprite((byte)kind);
            if (tex != null)
                sb.Draw(tex, new Rectangle(row.X + 4, row.Y + row.Height / 2 - tex.Height / 2 - 2,
                    tex.Width, tex.Height), locked ? new Color(90, 90, 90) : Color.White);
            sb.DrawString(nameFont, name, new Vector2(row.X + 36, row.Y + 4),
                afford ? new Color(230, 224, 210) : new Color(130, 124, 112));
            sb.DrawString(subFont, desc, new Vector2(row.X + 36, row.Y + 23),
                new Color(140, 134, 122));
            string price = locked ? "locked" : $"{cost} g";
            var pSize = nameFont.MeasureString(price);
            sb.DrawString(nameFont, price,
                new Vector2(row.Right - pSize.X - 8, row.Y + 4),
                locked ? new Color(150, 90, 80) : afford ? new Color(240, 200, 90) : new Color(150, 110, 80));
        }

        var ready = ReadyRect();
        bool readyHover = ready.Contains(_lastMouse);
        sb.Draw(TextureGen.Pixel, ready,
            !buildPhase ? new Color(40, 36, 44, 200)
            : readyHover ? new Color(96, 70, 36, 235) : new Color(70, 54, 30, 235));
        Border(sb, ready, new Color(170, 130, 60));
        string label = buildPhase
            ? $"Ready for wave {_client.World.DefenseWave} / {_client.World.DefenseWavesTotal}"
            : "The wave is already running";
        var lSize = FontManager.GetBold(15).MeasureString(label);
        sb.DrawString(FontManager.GetBold(15), label,
            new Vector2(ready.Center.X - lSize.X / 2, ready.Y + 8),
            buildPhase ? new Color(240, 210, 140) : new Color(150, 140, 120));
    }

    private static void Border(SpriteBatch sb, Rectangle r, Color c)
    {
        sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Y, r.Width, 2), c);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Bottom - 2, r.Width, 2), c);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Y, 2, r.Height), c);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.Right - 2, r.Y, 2, r.Height), c);
    }
}
