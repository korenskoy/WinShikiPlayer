namespace WinShikiPlayer.Core;

public sealed record AppPalette(string Id, string Name, bool Dark, uint Background, uint Card, uint Foreground, uint Accent)
{
    public static IReadOnlyList<AppPalette> All { get; } =
    [
        new("midnight", "Midnight Otaku", true, 0x0B0A0D, 0x17151B, 0xF5F1EA, 0xFF4D5E),
        new("plum", "Neon Plum", true, 0x0E0B16, 0x150E24, 0xF0EAFF, 0x00E3FF),
        new("paper", "Daylight Otaku", false, 0xFBFAF7, 0xFFFFFF, 0x0B0A0D, 0xFF4D5E),
        new("slate", "Daylight Plum", false, 0xF6F3FB, 0xFDFBFF, 0x1A1230, 0x0099B3)
    ];
    public static AppPalette Resolve(string id, bool systemDark)
    {
        id = id switch { "Dark" => "midnight", "Light" => "paper", "System" or "auto.otaku" => systemDark ? "midnight" : "paper", "auto.plum" => systemDark ? "plum" : "slate", _ => id };
        return All.FirstOrDefault(x => x.Id == id) ?? All[0];
    }
}
