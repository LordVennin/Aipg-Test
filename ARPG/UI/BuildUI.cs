using ARPG.Core;
using ARPG.Net;
using ARPG.Render;
using ARPG.Sim;
using ARPG.World;
using FontStashSharp;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ARPG.UI;

/// <summary>
/// The workbench: the defense arena's build menu. Pick a structure (or a hired
/// mercenary), then click a spot on the ground to place it (right-click cancels).
/// Prices are the shared DefenseBalance rules — the server re-validates everything
/// anyway. Also hosts the "call the wave" ready button.
/// </summary>
public class BuildUI
{
    public bool Open;
    /// <summary>The structure kind being placed (play screen runs the placement),
    /// or null when not placing.</summary>
    public StructureKind? PendingKind;
    /// <summary>The mercenary being deployed (play screen runs the placement).</summary>
    public MercData PendingMerc;
    /// <summary>Mercs this CLIENT deployed on the current map (local bookkeeping —
    /// the server is the real gate; cleared on every map change).</summary>
    public readonly HashSet<string> DeployedLocal = new();

    private readonly GameClient _client;
    private Rectangle _panelRect;
    private Point _lastMouse;
    private int _mercScroll;
    private const int RowH = 44;
    private const int MercRowH = 26;
    private const int MercVisible = 4;

    public readonly WindowDrag Window = new();

    private static readonly (StructureKind Kind, string Name, string Desc)[] Rows =
    {
        (StructureKind.CrossbowTurret, "Crossbow Turret", "shoots bolts at whatever comes close"),
        (StructureKind.SpikedBarrier, "Spiked Barrier", "a wall of stakes the horde must break through"),
        (StructureKind.FlameTurret, "Flamethrower", "sprays close attackers with fire"),
    };

    public BuildUI(GameClient client) => _client = client;

    private List<MercData> Mercs() => _client.World.MyCharacter?.Mercs ?? new List<MercData>();

    public void Layout(Point screen)
    {
        int mercRows = Math.Min(MercVisible, Math.Max(1, Mercs().Count == 0 ? 1 : Mercs().Count));
        int h = 118 + Rows.Length * RowH + 24 + mercRows * MercRowH + 8 + 38;
        _panelRect = Window.Place(new Rectangle(screen.X / 2 - 190, 48, 380, h), screen);
    }

    /// <summary>Supplies to patch every damaged structure (shared DefenseBalance rule —
    /// the client can price it from the replicated health bars).</summary>
    private int RepairCostNow() => DefenseBalance.RepairCost(
        _client.World.Structures.Values.Where(s => s.Kind != 4).Sum(s => s.MaxHealth - s.Health));

    /// <summary>Build capacity held by standing structures (priced from replication).</summary>
    private int CapUsed() => _client.World.Structures.Values
        .Where(s => DefenseBalance.PlayerBuildable((StructureKind)s.Kind))
        .Sum(s => DefenseBalance.CapacityCost((StructureKind)s.Kind));

    private int CapMax() => DefenseBalance.BuildCapBase +
        DefenseBalance.BuildCapPerExtraPlayer * Math.Max(0, _client.World.Players.Count - 1);

    private Rectangle RepairRect() =>
        new(_panelRect.X + 10, _panelRect.Bottom - 84, _panelRect.Width - 20, 30);

    public bool Contains(Point p) => Open && _panelRect.Contains(p);

    private Rectangle RowRect(int i) =>
        new(_panelRect.X + 10, _panelRect.Y + 62 + i * RowH, _panelRect.Width - 20, RowH - 6);

    private Rectangle MercHeaderPos() =>
        new(_panelRect.X + 10, _panelRect.Y + 62 + Rows.Length * RowH + 2, _panelRect.Width - 20, 18);

    private Rectangle MercRowRect(int i) =>
        new(_panelRect.X + 10, MercHeaderPos().Bottom + 2 + i * MercRowH, _panelRect.Width - 20, MercRowH - 3);

    private Rectangle ReadyRect() =>
        new(_panelRect.X + 10, _panelRect.Bottom - 46, _panelRect.Width - 20, 34);

    public void Update(InputManager input, bool mouseBlocked = false)
    {
        if (!Open || mouseBlocked) return;
        _lastMouse = input.MousePosition;
        if (CloseButton.Handle(input, _panelRect)) { Open = false; return; }
        if (Window.HandleBar(input, WindowDrag.BarFor(_panelRect))) return;
        if (!_panelRect.Contains(_lastMouse)) return;
        input.MouseCapturedByUI = true;

        var mercs = Mercs();
        int maxScroll = Math.Max(0, mercs.Count - MercVisible);
        if (input.ScrollDelta != 0 && _lastMouse.Y >= MercHeaderPos().Y &&
            _lastMouse.Y < ReadyRect().Y)
            _mercScroll = Math.Clamp(_mercScroll - Math.Sign(input.ScrollDelta), 0, maxScroll);
        _mercScroll = Math.Clamp(_mercScroll, 0, maxScroll);

        bool buildPhase = _client.World.DefensePhase == 0;
        if (input.MouseLeftPressed)
        {
            var character = _client.World.MyCharacter;
            for (int i = 0; i < Rows.Length; i++)
            {
                var row = RowRect(i);
                if (!row.Contains(_lastMouse)) continue;
                var (kind, _, _) = Rows[i];
                if (kind == StructureKind.FlameTurret &&
                    character?.FlamethrowerUnlocked != true) break;
                if (!buildPhase) break;
                if (_client.World.MySupplies < DefenseBalance.Cost(kind)) break;
                if (CapUsed() + DefenseBalance.CapacityCost(kind) > CapMax()) break;
                PendingKind = kind;   // the play screen takes over placement
                PendingMerc = null;
                Open = false;
                return;
            }
            for (int i = 0; i < MercVisible; i++)
            {
                int idx = i + _mercScroll;
                if (idx >= mercs.Count) break;
                if (!MercRowRect(i).Contains(_lastMouse)) continue;
                if (!buildPhase || DeployedLocal.Contains(mercs[idx].Id)) break;
                PendingMerc = mercs[idx];
                PendingKind = null;
                Open = false;
                return;
            }
            if (RepairRect().Contains(_lastMouse) && buildPhase && RepairCostNow() > 0 &&
                _client.World.MySupplies >= RepairCostNow())
            {
                _client.RequestRepair();
                return;
            }
            if (ReadyRect().Contains(_lastMouse) && buildPhase)
            {
                _client.RequestDoorReady();
                Open = false;
            }
        }
    }

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
        // Supplies are the arena's own currency (kills and cleared waves pay out);
        // capacity is the party's shared placement budget.
        int capUsed = CapUsed(), capMax = CapMax();
        sb.DrawString(FontManager.Get(14), $"Supplies: {_client.World.MySupplies}",
            new Vector2(x, _panelRect.Y + 36), new Color(240, 200, 90));
        string capText = $"Capacity: {capUsed}/{capMax}";
        var capSize = FontManager.Get(14).MeasureString(capText);
        sb.DrawString(FontManager.Get(14), capText,
            new Vector2(_panelRect.Right - capSize.X - 12, _panelRect.Y + 36),
            capUsed >= capMax ? new Color(220, 120, 90) : new Color(170, 190, 220));

        bool buildPhase = _client.World.DefensePhase == 0;
        var nameFont = FontManager.Get(15);
        var subFont = FontManager.Get(11);
        for (int i = 0; i < Rows.Length; i++)
        {
            var (kind, name, desc) = Rows[i];
            var row = RowRect(i);
            bool locked = kind == StructureKind.FlameTurret && !character.FlamethrowerUnlocked;
            int cost = DefenseBalance.Cost(kind);
            int capCost = DefenseBalance.CapacityCost(kind);
            bool capFits = capUsed + capCost <= capMax;
            bool afford = !locked && buildPhase && _client.World.MySupplies >= cost && capFits;
            bool hover = row.Contains(_lastMouse);
            sb.Draw(TextureGen.Pixel, row,
                hover && afford ? new Color(60, 50, 36, 220) : new Color(14, 12, 18, 235));
            var tex = SpriteGen.GetStructureSprite((byte)kind);
            if (tex != null)
                sb.Draw(tex, new Rectangle(row.X + 4, row.Y + row.Height / 2 - tex.Height / 2 - 2,
                    tex.Width, tex.Height), locked ? new Color(90, 90, 90) : Color.White);
            sb.DrawString(nameFont, name, new Vector2(row.X + 36, row.Y + 4),
                afford ? new Color(230, 224, 210) : new Color(130, 124, 112));
            sb.DrawString(subFont, locked ? "requires the researched blueprint" : desc,
                new Vector2(row.X + 36, row.Y + 23), new Color(140, 134, 122));
            string price = locked ? "locked" : $"{cost} sup";
            var pSize = nameFont.MeasureString(price);
            sb.DrawString(nameFont, price,
                new Vector2(row.Right - pSize.X - 8, row.Y + 4),
                locked ? new Color(150, 90, 80) : afford ? new Color(240, 200, 90) : new Color(150, 110, 80));
            if (!locked)
            {
                string capTag = $"{capCost} cap";
                var cSize = subFont.MeasureString(capTag);
                sb.DrawString(subFont, capTag,
                    new Vector2(row.Right - cSize.X - 8, row.Y + 24),
                    capFits ? new Color(150, 170, 200) : new Color(210, 110, 90));
            }
        }

        // Mercenaries: free to field, one outing each per run.
        var mercs = Mercs();
        var header = MercHeaderPos();
        sb.DrawString(FontManager.GetBold(13),
            mercs.Count == 0 ? "Mercenaries — none hired (see the researcher)" : "Mercenaries — deploy free, once per run",
            new Vector2(header.X + 2, header.Y), new Color(190, 180, 160));
        for (int i = 0; i < MercVisible; i++)
        {
            int idx = i + _mercScroll;
            if (idx >= mercs.Count) break;
            var m = mercs[idx];
            var row = MercRowRect(i);
            bool spent = DeployedLocal.Contains(m.Id);
            bool usable = buildPhase && !spent;
            bool hover = row.Contains(_lastMouse);
            sb.Draw(TextureGen.Pixel, row,
                hover && usable ? new Color(46, 58, 46, 225) : new Color(14, 16, 14, 235));
            var mercTex = SpriteGen.GetSummonSprite("merc_" + m.Kind);
            if (mercTex != null)
                sb.Draw(mercTex, new Rectangle(row.X + 4, row.Y + 2, 14, 19),
                    usable ? Color.White : new Color(100, 100, 100));
            sb.DrawString(FontManager.Get(13), m.Name, new Vector2(row.X + 24, row.Y + 4),
                usable ? new Color(214, 226, 208) : new Color(120, 124, 116));
            string tag = spent ? "fielded" : $"{m.Kind} · p{m.Power}";
            var tSize = subFont.MeasureString(tag);
            sb.DrawString(subFont, tag,
                new Vector2(row.Right - tSize.X - 8, row.Y + 6),
                spent ? new Color(150, 120, 90) : new Color(150, 158, 144));
        }

        // Repairs: one cheap sweep patches everything (the wagon included).
        var repair = RepairRect();
        int repairCost = RepairCostNow();
        bool canRepair = buildPhase && repairCost > 0 && _client.World.MySupplies >= repairCost;
        sb.Draw(TextureGen.Pixel, repair,
            canRepair && repair.Contains(_lastMouse) ? new Color(44, 66, 44, 235)
            : canRepair ? new Color(32, 50, 32, 235) : new Color(34, 36, 44, 210));
        Border(sb, repair, canRepair ? new Color(120, 190, 120) : new Color(70, 74, 86));
        string repairLabel = repairCost <= 0 ? "Nothing needs repair"
            : $"Repair all  ·  {repairCost} sup";
        var rSize = FontManager.GetBold(14).MeasureString(repairLabel);
        sb.DrawString(FontManager.GetBold(14), repairLabel,
            new Vector2(repair.Center.X - rSize.X / 2, repair.Y + 6),
            canRepair ? new Color(190, 235, 190) : new Color(130, 130, 140));

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
