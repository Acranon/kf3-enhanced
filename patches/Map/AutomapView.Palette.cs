namespace KfIII;

// The map's colours, after kf1-enhanced's map. ImGui colours are uints 0xAABBGGRR.
public static partial class AutomapView
{
    static uint Col(int r, int g, int b, float a = 1f) => (uint)(r | g << 8 | b << 16 | (int)(Math.Clamp(a, 0f, 1f) * 255) << 24);

    /// <summary>The colour with its alpha scaled (the minimap's opacity).</summary>
    static uint Fade(uint c, float alpha) => alpha >= 0.999f ? c : (c & 0x00FFFFFFu) | ((uint)((c >> 24) * alpha) << 24);

    static class Palette
    {
        // Terrain.
        public static readonly uint Wall = Col(215, 200, 160), Water = Col(50, 88, 140, 0.92f), Lava = Col(215, 75, 20, 0.95f);
        public static readonly uint Chasm = Col(46, 46, 50, 0.92f), Hatch = Col(78, 78, 84, 0.9f);

        // Markers.
        public static readonly uint Door = Col(190, 120, 50), LockedDoor = Col(210, 60, 50), Chest = Col(230, 170, 60);
        // People are hot pink with a white ring: no other marker is near that colour,
        // and trees (small, dark green) must never be mistaken for them.
        public static readonly uint Tree = Col(60, 120, 52), Person = Col(255, 60, 170), Outline = Col(0, 0, 0, 0.8f);
        /// <summary>One colour per portal pair, with the name the hover text uses.</summary>
        public static readonly (uint Col, string Name)[] Portals =
        [
            (Col(150, 90, 255), "violet"), (Col(60, 200, 90), "green"), (Col(240, 150, 40), "orange"),
            (Col(60, 170, 255), "blue"), (Col(240, 70, 140), "pink"), (Col(235, 225, 70), "yellow"),
            (Col(70, 225, 215), "teal"), (Col(220, 60, 50), "red"),
        ];
        public static readonly uint ArchStone = Col(200, 185, 160), Warp = Col(205, 150, 255);
        public static readonly uint SavePoint = Col(80, 220, 230), Gas = Col(150, 225, 60), Exit = Col(250, 250, 255);
        public static readonly uint Secret = Col(170, 90, 220), Item = Col(250, 220, 90), Enemy = Col(235, 50, 50);
        public static readonly uint Lamp = Col(70, 78, 92), LampLit = Col(255, 140, 40), LampRim = Col(200, 205, 215);
        public static readonly uint Stump = Col(105, 70, 38), StumpRing = Col(200, 165, 110);
        public static readonly uint Barrel = Col(150, 95, 50), BarrelRim = Col(225, 190, 140);
    }
}
