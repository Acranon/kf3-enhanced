using System.Numerics;
using ImGuiNET;
using MarkerKind = KfIII.Automap.MarkerKind;

namespace KfIII;

// The markers' icons and the player's arrow.
public static partial class AutomapView
{
    /// <summary>The markers (Automap.Markers, already only what is to be shown), with
    /// sample (cx, cz) at screen point o and s pixels a sample, clipped to c0..c1. The
    /// full map draws them bigger and labels the exits.</summary>
    static void DrawMarkers(ImDrawListPtr dl, Vector2 o, float cx, float cz, float s, Vector2 c0, Vector2 c1, bool full)
    {
        Vector2 At(float x, float z) => new(o.X + (x - cx) * s, o.Y - (z - cz) * s);
        float r = Math.Max(full ? 7f : 3.5f, s * Automap.PerTile * 0.3f);
        var list = SnapshotMarkers();
        bool Off(Vector2 p, float pad) => p.X < c0.X - pad || p.Y < c0.Y - pad || p.X > c1.X + pad || p.Y > c1.Y + pad;
        // Area exits under everything: a white hatched strip, and on the full map the
        // area it leads to written beside it.
        foreach (var mk in list)
        {
            if (mk.Kind != MarkerKind.Exit) continue;
            Vector2 a = At(mk.X, mk.Z + mk.H), b = At(mk.X + mk.W, mk.Z);
            if (b.X < c0.X || a.X > c1.X || b.Y < c0.Y || a.Y > c1.Y) continue;
            dl.AddRectFilled(a, b, Palette.Exit & 0x55FFFFFF);
            float step = Math.Max(4f, s * Automap.PerTile * 0.35f);
            dl.PushClipRect(Vector2.Max(a, c0), Vector2.Min(b, c1), true);
            for (float t = -(b.Y - a.Y); t < b.X - a.X; t += step)
                dl.AddLine(new Vector2(a.X + t, b.Y), new Vector2(a.X + t + (b.Y - a.Y), a.Y), Palette.Exit & 0xAAFFFFFF, 1.5f);
            dl.PopClipRect();
            dl.AddRect(a, b, Palette.Exit, 0f, ImDrawFlags.None, Math.Max(1.5f, s * 0.6f));
            if (full)
            {
                float ts = Math.Max(14f, (c1.Y - c0.Y) / 38f);   // the hover label's size
                var text = "To " + mk.Name;
                var ext = ImGui.GetFont().CalcTextSizeA(ts, float.MaxValue, 0f, text);
                // Below a wide strip, beside a tall one; on the left if it would run off.
                var at = new Vector2((a.X + b.X - ext.X) * 0.5f, b.Y + 3f);
                if (b.X - a.X < b.Y - a.Y) at = new Vector2(b.X + 4f, (a.Y + b.Y - ext.Y) * 0.5f);
                if (at.X + ext.X > c1.X) at.X = a.X - 4f - ext.X;
                dl.AddText(ImGui.GetFont(), ts, at + new Vector2(1, 1), Col(0, 0, 0, 0.9f), text);
                dl.AddText(ImGui.GetFont(), ts, at, Palette.Exit, text);
            }
        }
        // Trees under the rest.
        foreach (var mk in list)
        {
            if (mk.Kind != MarkerKind.Tree) continue;
            var p = At(mk.X, mk.Z);
            if (Off(p, r)) continue;
            dl.AddCircleFilled(p, r * 0.5f, Palette.Tree, 10);
            dl.AddCircle(p, r * 0.5f, Col(30, 60, 25, 0.9f), 10, 1f);
        }
        foreach (var mk in list)
        {
            var p = At(mk.X, mk.Z);
            if (Off(p, 4f * r + mk.Length * s)) continue;
            DrawIcon(dl, mk, p, At(mk.X + mk.DirX * mk.Length, mk.Z + mk.DirZ * mk.Length), r, s);
            // Contents cheat: a gold ring round a container with something in it, or a
            // monster certain to drop something.
            if (Automap.ShowContents && mk.Item >= 0 && !mk.Done && mk.Kind is not MarkerKind.Item
                && (mk.Kind != MarkerKind.Enemy || mk.Chance >= 256))
                dl.AddCircle(p, r * 1.15f, Palette.Item, 16, Math.Max(1.5f, r * 0.22f));
            // Traps cheat: a warning badge on a trapped chest still shut (green for poison gas).
            if (Automap.ShowTraps && mk.Trapped && !mk.Done)
            {
                var bp = p + new Vector2(r * 0.95f, -r * 0.95f);
                float br = Math.Max(3.5f, r * 0.55f);
                dl.AddCircleFilled(bp, br, mk.TrapEffect == 0x1A ? Palette.Gas : Col(220, 50, 40), 12);
                dl.AddCircle(bp, br, Palette.Outline, 12, 1f);
                float t = Math.Max(1f, br * 0.28f);
                dl.AddLine(bp + new Vector2(0, -br * 0.55f), bp + new Vector2(0, br * 0.15f), Col(20, 20, 20), t);
                dl.AddCircleFilled(bp + new Vector2(0, br * 0.5f), t * 0.6f, Col(20, 20, 20), 6);
            }
        }
    }

    /// <summary>One marker's icon at p, r pixels across give or take (exits and trees are
    /// drawn first, by DrawMarkers). leafEnd is the far end of a door's leaf.</summary>
    static void DrawIcon(ImDrawListPtr dl, Automap.Marker mk, Vector2 p, Vector2 leafEnd, float r, float s)
    {
        switch (mk.Kind)
        {
            case MarkerKind.Door:
            case MarkerKind.LockedDoor:
            case MarkerKind.SecretDoor:
                {
                    // The leaf, from its hinge along its angle.
                    uint col = mk.Kind switch { MarkerKind.Door => Palette.Door, MarkerKind.SecretDoor => Palette.Secret, _ => Palette.LockedDoor };
                    float w = Math.Max(3f, s * 2.2f);
                    dl.AddLine(p, leafEnd, Palette.Outline, w + 2f);
                    dl.AddLine(p, leafEnd, col, w);
                    break;
                }
            case MarkerKind.Chest:
                {
                    var h = new Vector2(r * 0.8f, r * 0.6f);
                    if (mk.Done)
                    {
                        // Opened: an empty box in the chest colour.
                        dl.AddRectFilled(p - h, p + h, Col(20, 17, 12, 0.85f));
                        dl.AddRect(p - h, p + h, Palette.Chest, 0f, ImDrawFlags.None, Math.Max(1.5f, r * 0.25f));
                    }
                    else
                    {
                        dl.AddRectFilled(p - h, p + h, Palette.Chest);
                        dl.AddRect(p - h, p + h, Palette.Outline, 0f, ImDrawFlags.None, 1f);
                    }
                    break;
                }
            case MarkerKind.Remains:
                {
                    // A skull: the jaw, the cranium, two eyes.
                    float k = r * 0.75f;
                    uint bone = Col(225, 220, 200), dark = Col(25, 20, 15);
                    dl.AddRectFilled(p + new Vector2(-k * 0.45f, k * 0.25f), p + new Vector2(k * 0.45f, k * 0.85f), bone, k * 0.15f);
                    dl.AddCircleFilled(p, k * 0.75f, bone, 14);
                    dl.AddCircle(p, k * 0.75f, Palette.Outline, 14, 1f);
                    dl.AddCircleFilled(p + new Vector2(-k * 0.3f, k * 0.05f), k * 0.2f, dark, 8);
                    dl.AddCircleFilled(p + new Vector2(k * 0.3f, k * 0.05f), k * 0.2f, dark, 8);
                    break;
                }
            case MarkerKind.Grave:
                {
                    // A headstone: rounded top on a slab.
                    float k = r * 0.75f;
                    uint stone = Col(170, 170, 175);
                    dl.AddRectFilled(p + new Vector2(-k * 0.5f, -k * 0.2f), p + new Vector2(k * 0.5f, k * 0.8f), stone);
                    dl.AddCircleFilled(p + new Vector2(0, -k * 0.2f), k * 0.5f, stone, 12);
                    dl.AddLine(p + new Vector2(-k * 0.5f, k * 0.8f), p + new Vector2(k * 0.5f, k * 0.8f), Palette.Outline, 1.5f);
                    break;
                }
            case MarkerKind.SavePoint:
                {
                    // kf1-enhanced's cyan diamond.
                    float k = r * 1.0f;
                    var t = p + new Vector2(0, -k); var rt = p + new Vector2(k * 0.7f, 0); var b = p + new Vector2(0, k); var lf = p + new Vector2(-k * 0.7f, 0);
                    dl.AddQuadFilled(t, rt, b, lf, Palette.SavePoint);
                    dl.AddQuad(t, rt, b, lf, Palette.Outline, 1.2f);
                    break;
                }
            case MarkerKind.Pedestal:
                {
                    // A key: ring and bit.
                    float k = r * 0.7f;
                    uint gold = Col(240, 200, 80);
                    dl.AddCircle(p + new Vector2(-k * 0.45f, 0), k * 0.4f, gold, 10, Math.Max(1.5f, k * 0.25f));
                    dl.AddLine(p + new Vector2(-k * 0.05f, 0), p + new Vector2(k * 0.9f, 0), gold, Math.Max(1.5f, k * 0.25f));
                    dl.AddLine(p + new Vector2(k * 0.6f, 0), p + new Vector2(k * 0.6f, k * 0.35f), gold, Math.Max(1.5f, k * 0.25f));
                    break;
                }
            case MarkerKind.Trap:
                {
                    // A spike: a red triangle.
                    float k = r * 0.8f;
                    var a = p + new Vector2(0, -k); var b = p + new Vector2(k * 0.85f, k * 0.6f); var c = p + new Vector2(-k * 0.85f, k * 0.6f);
                    dl.AddTriangleFilled(a, b, c, Col(220, 50, 40));
                    dl.AddTriangle(a, b, c, Palette.Outline, 1f);
                    break;
                }
            case MarkerKind.Portal:
                {
                    // An arch filled with its pair's colour: the two arches that send you to
                    // each other share it. At least a tile wide, so the shape reads.
                    float k = Math.Max(r * 1.4f, s * Automap.PerTile * 0.55f);
                    uint fill = Palette.Portals[Math.Max(0, mk.Pair) % Palette.Portals.Length].Col;
                    var b0 = p + new Vector2(-k * 0.75f, -k * 0.15f); var b1 = p + new Vector2(k * 0.75f, k * 1.0f);
                    dl.AddRectFilled(b0, b1, Palette.ArchStone);
                    dl.AddCircleFilled(p + new Vector2(0, -k * 0.15f), k * 0.75f, Palette.ArchStone, 16);
                    dl.AddRectFilled(p + new Vector2(-k * 0.45f, -k * 0.15f), p + new Vector2(k * 0.45f, k * 1.0f), fill);
                    dl.AddCircleFilled(p + new Vector2(0, -k * 0.15f), k * 0.45f, fill, 14);
                    dl.AddCircle(p + new Vector2(0, -k * 0.15f), k * 0.75f, Palette.Outline, 16, 1.2f);
                    break;
                }
            case MarkerKind.Warp:
                {
                    // A glyph on the floor: a glowing ring with a six-pointed star in it (unlike
                    // an exit's strip, this is a spot to step on).
                    float k = Math.Max(r * 1.3f, s * Automap.PerTile * 0.5f);
                    dl.AddCircleFilled(p, k, Col(40, 20, 70, 0.9f), 20);
                    dl.AddCircle(p, k, Palette.Warp, 20, Math.Max(1.5f, k * 0.14f));
                    for (int t = 0; t < 2; t++)
                    {
                        float a0 = t * MathF.PI / 3f - MathF.PI / 2f;
                        Vector2 V(int n) => p + new Vector2(MathF.Cos(a0 + n * MathF.PI * 2f / 3f), MathF.Sin(a0 + n * MathF.PI * 2f / 3f)) * k * 0.7f;
                        dl.AddTriangle(V(0), V(1), V(2), Palette.Warp, Math.Max(1f, k * 0.09f));
                    }
                    break;
                }
            case MarkerKind.Throne:
                {
                    // A chair from the front: tall back, seat, legs; gold, its back glowing
                    // blue once Ichrius' Key is in it.
                    float k = Math.Max(r * 1.3f, s * Automap.PerTile * 0.5f);
                    uint gold = Col(225, 185, 70);
                    dl.AddRectFilled(p + new Vector2(-k * 0.55f, -k * 1.0f), p + new Vector2(k * 0.55f, k * 0.2f), gold, k * 0.2f);
                    dl.AddRectFilled(p + new Vector2(-k * 0.75f, k * 0.1f), p + new Vector2(k * 0.75f, k * 0.45f), gold);
                    dl.AddRectFilled(p + new Vector2(-k * 0.7f, k * 0.45f), p + new Vector2(-k * 0.45f, k * 1.0f), gold);
                    dl.AddRectFilled(p + new Vector2(k * 0.45f, k * 0.45f), p + new Vector2(k * 0.7f, k * 1.0f), gold);
                    if (mk.Done) dl.AddCircleFilled(p + new Vector2(0, -k * 0.4f), k * 0.28f, Col(120, 170, 255), 10);
                    dl.AddRect(p + new Vector2(-k * 0.55f, -k * 1.0f), p + new Vector2(k * 0.55f, k * 0.2f), Palette.Outline, k * 0.2f, ImDrawFlags.None, 1.2f);
                    break;
                }
            case MarkerKind.Lamp:
                {
                    // A quest object, not a creature: a tall diamond (a crystal), flame-coloured
                    // when lit, dark with a pale rim when not (the save point is a cyan one).
                    float k = r * 0.95f;
                    var top = p + new Vector2(0, -k * 1.25f); var rt = p + new Vector2(k * 0.7f, 0);
                    var bot = p + new Vector2(0, k * 1.25f); var lf = p + new Vector2(-k * 0.7f, 0);
                    uint fill = mk.Lit == true ? Palette.LampLit : Palette.Lamp;
                    dl.AddQuadFilled(top, rt, bot, lf, fill);
                    if (mk.Lit == true) dl.AddCircleFilled(p, k * 0.35f, Col(255, 240, 160), 10);
                    dl.AddQuad(top, rt, bot, lf, mk.Lit == true ? Palette.Outline : Palette.LampRim, 1.5f);
                    break;
                }
            case MarkerKind.Stump:
                {
                    // A cut trunk from above: bark brown with pale rings. Big, as it blocks the
                    // way: nearly a corridor across, never smaller than 1.5 r.
                    float sr = Math.Max(r * 1.5f, s * Automap.PerTile * 0.85f);
                    dl.AddCircleFilled(p, sr, Palette.Stump, 20);
                    dl.AddCircle(p, sr * 0.6f, Palette.StumpRing, 18, Math.Max(1.5f, sr * 0.12f));
                    dl.AddCircle(p, sr * 0.3f, Palette.StumpRing, 12, Math.Max(1f, sr * 0.08f));
                    dl.AddCircle(p, sr, Palette.Outline, 20, 1.5f);
                    break;
                }
            case MarkerKind.Well:
                {
                    dl.AddCircleFilled(p, r * 0.7f, Col(60, 110, 170), 14);
                    dl.AddCircle(p, r * 0.7f, Col(170, 165, 150), 14, Math.Max(1.5f, r * 0.25f));
                    break;
                }
            case MarkerKind.Barrel:
                {
                    float br = r * 0.6f;
                    dl.AddCircleFilled(p, br, Palette.Barrel, 10);
                    dl.AddCircle(p, br, Palette.BarrelRim, 10, Math.Max(1f, br * 0.3f));
                    break;
                }
            case MarkerKind.Person:
                dl.AddCircleFilled(p, r * 0.95f, Palette.Person, 16);
                dl.AddCircle(p, r * 0.95f, Col(255, 255, 255), 16, Math.Max(1.5f, r * 0.2f));
                dl.AddCircle(p, r * 0.95f + Math.Max(1.5f, r * 0.2f) * 0.5f + 0.5f, Palette.Outline, 16, 1f);
                break;
            case MarkerKind.Hidden:
                {
                    // A gold sparkle (four points): there is nothing to see there in the game.
                    float k = r * 0.75f, q = k * 0.28f;
                    var pts = new[] { p + new Vector2(0, -k), p + new Vector2(q, -q), p + new Vector2(k, 0), p + new Vector2(q, q),
                                  p + new Vector2(0, k), p + new Vector2(-q, q), p + new Vector2(-k, 0), p + new Vector2(-q, -q) };
                    for (int j = 0; j < 8; j += 2) dl.AddTriangleFilled(pts[j], pts[(j + 1) % 8], pts[(j + 7) % 8], Palette.Item);
                    dl.AddQuadFilled(pts[1], pts[3], pts[5], pts[7], Palette.Item);
                    dl.AddPolyline(ref pts[0], 8, Palette.Outline, ImDrawFlags.Closed, 1f);
                    break;
                }
            case MarkerKind.Item:
                dl.AddCircleFilled(p, r * 0.5f, Palette.Item, 10);
                dl.AddCircle(p, r * 0.5f, Palette.Outline, 10, 1f);
                break;
            case MarkerKind.Enemy:
                dl.AddCircleFilled(p, r * 0.65f, Palette.Enemy, 12);
                dl.AddCircle(p, r * 0.65f, Palette.Outline, 12, 1f);
                break;
        }
    }

    /// <summary>The player's arrow, kf1-enhanced's: yaw 0 faces north (+z), increasing turns left.</summary>
    static void DrawPlayer(ImDrawListPtr dl, Vector2 p, float size)
    {
        float yaw = Automap.PlayerYaw;
        var fwd = new Vector2(-MathF.Sin(yaw), -MathF.Cos(yaw));   // on screen +y is south
        var side = new Vector2(-fwd.Y, fwd.X);
        var tip = p + fwd * size;
        var back = p - fwd * (size * 0.45f);
        var left = p - fwd * (size * 0.7f) + side * (size * 0.65f);
        var right = p - fwd * (size * 0.7f) - side * (size * 0.65f);
        dl.AddQuadFilled(tip, left, back, right, Col(255, 250, 220));
        dl.AddQuad(tip, left, back, right, Col(0, 0, 0), 1.5f);
    }
}
