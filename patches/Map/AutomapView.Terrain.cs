using System.Numerics;
using ImGuiNET;
using Kind = KfIII.Automap.Kind;

namespace KfIII;

// The floor plan: fills for floor, water, lava and chasm, a band in the wall colour for
// solid samples beside open ones, and a line along the edge of the area.
public static partial class AutomapView
{
    /// <summary>The floor's fill, shaded by its height against the player's in eight
    /// steps (so that a row's runs of one colour stay long).</summary>
    static uint FloorCol(short floor16, float alpha)
    {
        int d = floor16 * 16 - Automap.PlayerFloor;   // positive is lower
        int step = Math.Clamp((int)MathF.Round(d / 512f), -4, 4);
        int k = -step * 6;
        return Col(Math.Clamp(78 + k, 20, 140), Math.Clamp(70 + k, 18, 130), Math.Clamp(56 + k, 14, 110), 0.9f * alpha);
    }

    static bool Walkable(Kind k) => k is Kind.Floor or Kind.Water or Kind.Lava or Kind.Chasm;

    /// <summary>Samples x0..x1, z0..z1, with sample (cx, cz) at screen point o and s
    /// pixels a sample; north (+z) up.</summary>
    static void DrawCells(ImDrawListPtr dl, Automap.Area a, Vector2 o, float cx, float cz, float s, int x0, int z0, int x1, int z1, float alpha)
    {
        x0 = Math.Max(x0, 0); z0 = Math.Max(z0, 0);
        x1 = Math.Min(x1, Automap.Size - 1); z1 = Math.Min(z1, Automap.Size - 1);
        Vector2 At(float x, float z) => new(o.X + (x - cx) * s, o.Y - (z - cz) * s);
        uint wall = Fade(Palette.Wall, alpha);
        float lw = Math.Max(1f, s * 0.5f);

        bool Open(int x, int z) => (uint)x < Automap.Size && (uint)z < Automap.Size
                                   && a.Visible(z * Automap.Size + x) && Walkable(a.Shown(z * Automap.Size + x));

        // A wall is painted up to two samples into the solid, so walls up to about half a
        // tile thick read as one band, and thicker rock gets a border.
        bool NearOpen(int x, int z)
        {
            for (int dz = -2; dz <= 2; dz++)
                for (int dx = -2; dx <= 2; dx++)
                    if (Math.Abs(dx) + Math.Abs(dz) <= 3 && Open(x + dx, z + dz)) return true;
            return false;
        }

        for (int z = z0; z <= z1; z++)
        {
            int row = z * Automap.Size;
            // Fills, as runs of one colour.
            int run = -1; uint runCol = 0;
            for (int x = x0; x <= x1 + 1; x++)
            {
                uint col = 0;
                if (x <= x1)
                {
                    int i = row + x;
                    if (a.Visible(i))
                        col = a.Shown(i) switch
                        {
                            Kind.Floor => FloorCol(a.Floor[i], alpha),
                            Kind.Water => Fade(Palette.Water, alpha),
                            Kind.Lava => Fade(Palette.Lava, alpha),
                            Kind.Chasm => Fade(Palette.Chasm, alpha),
                            Kind.Solid when NearOpen(x, z) => wall,
                            _ => 0,
                        };
                }
                if (col != runCol || x > x1)
                {
                    if (runCol != 0 && run >= 0) dl.AddRectFilled(At(run, z + 1), At(x, z), runCol);
                    run = x; runCol = col;
                }
            }
            // Chasm hatching, once a sample is big enough to show it.
            if (s >= 4f)
                for (int x = x0; x <= x1; x++)
                {
                    int i = row + x;
                    if (a.Visible(i) && a.Shown(i) == Kind.Chasm && ((x + z) & 3) == 0)
                        dl.AddLine(At(x, z), At(x + 1, z + 1), Fade(Palette.Hatch, alpha), Math.Max(1f, s * 0.25f));
                }
        }

        // Edges against the outside (no tile at all), where there is no solid sample to
        // paint: a line, drawn as runs along each row and column.
        bool Outside(int x, int z)
        {
            if ((uint)x >= Automap.Size || (uint)z >= Automap.Size) return true;
            return a.Shown(z * Automap.Size + x) == Kind.Outside;
        }

        for (int z = z0; z <= z1 + 1; z++)
        {
            // The edge between rows z - 1 and z.
            int start = -1;
            for (int x = x0; x <= x1 + 1; x++)
            {
                bool edge = x <= x1 && ((Open(x, z) && Outside(x, z - 1)) || (Open(x, z - 1) && Outside(x, z)));
                if (edge && start < 0) start = x;
                if (!edge && start >= 0) { dl.AddLine(At(start, z), At(x, z), wall, lw); start = -1; }
            }
        }
        for (int x = x0; x <= x1 + 1; x++)
        {
            int start = -1;
            for (int z = z0; z <= z1 + 1; z++)
            {
                bool edge = z <= z1 && ((Open(x, z) && Outside(x - 1, z)) || (Open(x - 1, z) && Outside(x, z)));
                if (edge && start < 0) start = z;
                if (!edge && start >= 0) { dl.AddLine(At(x, start), At(x, z), wall, lw); start = -1; }
            }
        }
    }
}
