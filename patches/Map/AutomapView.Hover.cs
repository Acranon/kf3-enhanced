using System.Numerics;
using ImGuiNET;
using MarkerKind = KfIII.Automap.MarkerKind;

namespace KfIII;

// The full map's hover label: what the marker under the mouse is, and with the cheats
// on what it holds and what opens it.
public static partial class AutomapView
{
    /// <summary>The marker nearest the mouse within reach, labelled beside the mouse;
    /// for a portal, also a line to where it sends you.</summary>
    static void DrawHover(ImDrawListPtr dl, Vector2 o, float cx, float cz, float s, Vector2 mouse, Vector2 g0, Vector2 g1, float ts)
    {
        Vector2 At(float x, float z) => new(o.X + (x - cx) * s, o.Y - (z - cz) * s);
        float reach = Math.Max(10f, s * Automap.PerTile * 0.45f);
        var list = SnapshotMarkers();
        float best = reach * reach; int bi = -1;
        for (int i = 0; i < list.Length; i++)
        {
            var mk = list[i];
            var p = At(mk.X, mk.Z);
            if (mk.Length > 0)   // a door: the middle of its leaf
                p = At(mk.X + mk.DirX * mk.Length * 0.5f, mk.Z + mk.DirZ * mk.Length * 0.5f);
            float d = Vector2.DistanceSquared(p, mouse);
            if (mk.Kind == MarkerKind.Exit)
            {
                // An exit strip: anywhere over it counts, ranked just behind a marker on top.
                Vector2 a = At(mk.X, mk.Z + mk.H), b = At(mk.X + mk.W, mk.Z);
                bool over = mouse.X >= a.X && mouse.X <= b.X && mouse.Y >= a.Y && mouse.Y <= b.Y;
                d = over ? best * 0.99f : float.MaxValue;
            }
            if (d < best) { best = d; bi = i; }
        }
        if (bi < 0) return;
        // A portal: a line to its partner arch, else to the tile it sends you to.
        if (list[bi].Kind == MarkerKind.Portal)
        {
            var pm = list[bi];
            var far = At(pm.DestX, pm.DestZ);
            foreach (var q in list)
                if (q.Kind == MarkerKind.Portal && q.Pair == pm.Pair && pm.Pair >= 0 && (q.X != pm.X || q.Z != pm.Z)) far = At(q.X, q.Z);
            uint pc = Palette.Portals[Math.Max(0, pm.Pair) % Palette.Portals.Length].Col;
            var near = At(pm.X, pm.Z);
            float lw = Math.Max(2f, s * 0.6f);
            dl.AddLine(near, far, Col(0, 0, 0, 0.85f), lw + 3f);
            dl.AddLine(near, far, pc, lw);
            dl.AddCircle(far, Math.Max(8f, s * Automap.PerTile * 0.7f), pc, 20, lw);
        }
        string text = Describe(list[bi]);
        var font = ImGui.GetFont();
        var ext = font.CalcTextSizeA(ts, float.MaxValue, 0f, text);
        float pad = ts * 0.4f;
        var at = new Vector2(mouse.X + ts, mouse.Y + ts * 0.6f);
        if (at.X + ext.X + pad > g1.X) at.X = mouse.X - ts - ext.X - pad;
        if (at.Y + ext.Y + pad > g1.Y) at.Y = mouse.Y - ext.Y - pad * 2;
        dl.AddRectFilled(at - new Vector2(pad, pad), at + ext + new Vector2(pad, pad), Col(20, 17, 12, 0.92f), pad);
        dl.AddRect(at - new Vector2(pad, pad), at + ext + new Vector2(pad, pad), Col(200, 180, 130), pad);
        dl.AddText(font, ts, at, Col(240, 230, 210), text);
    }

    /// <summary>A marker's hover label: what it is, then a line each for what the cheats
    /// tell about it.</summary>
    static string Describe(Automap.Marker mk)
    {
        string what = mk.Kind switch
        {
            MarkerKind.Door => "Door",
            MarkerKind.LockedDoor => "Locked door",
            MarkerKind.SecretDoor => "Secret wall",
            MarkerKind.Pedestal => "Key pedestal",
            MarkerKind.SavePoint => "Save point",
            MarkerKind.Chest => (mk.Label ?? "Chest") + (mk.Done ? " (opened)" : ""),
            MarkerKind.Barrel => mk.Label ?? "Barrel",
            MarkerKind.Remains => mk.Label ?? "Remains",
            MarkerKind.Hidden => mk.Label ?? "Hidden item",
            MarkerKind.Grave => "Grave",
            MarkerKind.Well => "Well",
            MarkerKind.Tree => "Tree",
            MarkerKind.Stump => "Giant tree stump",
            MarkerKind.Lamp => mk.Lit switch { true => "Crystal lamp (lit)", false => "Crystal lamp (unlit: Fire Ball lights it)", _ => "Crystal lamp (Fire Ball lights it)" },
            MarkerKind.Person => mk.Name ?? "Person",
            MarkerKind.Trap => "Trap",
            MarkerKind.Item => mk.Name ?? "Item",
            MarkerKind.Enemy => mk.Who ?? "Enemy",
            MarkerKind.Exit => $"Exit to {mk.Name}",
            MarkerKind.Portal => mk.Pair >= 0 ? $"Portal ({Palette.Portals[mk.Pair % Palette.Portals.Length].Name})" : "Portal",
            MarkerKind.Throne => mk.Label ?? "Throne",
            MarkerKind.Warp => mk.Name != null ? $"Warp glyph: to {mk.Name}" : "Warp glyph (within this area)",
            _ => "",
        };
        var lines = new List<string> { what };
        if (mk.Kind == MarkerKind.Portal)
            lines.Add(mk.Pair >= 0 ? $"Leads to the other {Palette.Portals[mk.Pair % Palette.Portals.Length].Name} arch" : "Leads somewhere");
        if (mk.Kind == MarkerKind.Throne)
            lines.Add(mk.Done ? "Ichrius' Key is in it: the portals are open" : "Put Ichrius' Key in it to open the portals (they stay open)");
        if (mk.Kind == MarkerKind.Lamp && Automap.LampsLit is int lit)
            lines.Add(lit >= 12 ? "All 12 lamps lit: the palace's chests are unlocked" : $"{lit} of 12 lamps lit (all 12 unlock the palace's chests)");
        bool container = mk.Kind is MarkerKind.Chest or MarkerKind.Barrel or MarkerKind.Remains
                                 or MarkerKind.Grave or MarkerKind.Well or MarkerKind.Hidden;
        if (Automap.ShowContents && container && !mk.Done)
            lines.Add(mk.Item >= 0 ? $"Holds: {mk.Name}" : "Empty");
        if (Automap.ShowContents && mk.Kind == MarkerKind.Enemy && mk.Item >= 0)
            lines.Add(mk.Chance >= 256 ? $"Drops: {mk.Name}" : $"May drop: {mk.Name} ({Math.Max(1, mk.Chance * 100 / 256)}%)");
        if (Automap.ShowTraps && mk.Trapped && !mk.Done)
            lines.Add(mk.TrapEffect == 0x1A ? "Trapped: poison gas" : "Trapped");
        // A hand-written guide note (Automap.Guide.cs) replaces the lock line it explains.
        if (Automap.ShowKeys && mk.Kind == MarkerKind.Chest && !mk.Done)
            lines.Add(mk.Hint ?? LockText(mk.Key));
        if (Automap.ShowKeys && mk.Kind == MarkerKind.LockedDoor)
            lines.Add(mk.Hint ?? LockText(mk.Key));
        return string.Join("\n", lines);
    }

    /// <summary>The keys cheat's line for a lock (Marker.Key, see Automap.LockOf).</summary>
    static string LockText(int key) => key switch
    {
        -1 => "Not locked",
        -2 => "Sealed",
        -3 => "Opened by an event elsewhere",
        -4 => "Key: not known yet",
        -5 => "Sealed: opened by a switch or event",
        0x88 => "Needs: Orladin's Key (it keeps the key)",
        _ => $"Needs: {Automap.KeyName(key)}",
    };
}
