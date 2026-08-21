using Microsoft.Xna.Framework;

namespace ARPG.Sim;

/// <summary>
/// Shared appearance vocabulary: palettes, hair styles, and the packed-RGB helpers
/// used by the save file, the wire protocol and the sprite baker alike. Colors are
/// FREE — the preset arrays are just the creation screen's quick-pick swatches; any
/// 24-bit color a player mixes is stored and replicated verbatim.
/// </summary>
public static class Appearance
{
    /// <summary>Quick-pick skin swatches (also the fallback for pre-color saves).</summary>
    public static readonly Color[] SkinTones =
    {
        new(244, 219, 190), new(229, 194, 160), new(205, 164, 126),
        new(176, 130, 90), new(141, 98, 62), new(102, 69, 43),
    };

    /// <summary>Quick-pick hair swatches: black, dark brown, brown, auburn, blonde, grey.</summary>
    public static readonly Color[] HairColors =
    {
        new(38, 32, 30), new(62, 46, 32), new(88, 58, 34),
        new(122, 54, 30), new(176, 142, 82), new(148, 146, 140),
    };

    public const byte HairShort = 0, HairLong = 1, HairBun = 2, HairBald = 3;
    public static readonly string[] HairStyleNames = { "Short", "Long", "Bun", "Bald" };

    /// <summary>Sentinel for saves made before hair styles existed: derive from body.</summary>
    public const byte HairAuto = 255;

    public static int Pack(Color c) => (c.R << 16) | (c.G << 8) | c.B;
    public static Color Unpack(int rgb) => new((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
}
