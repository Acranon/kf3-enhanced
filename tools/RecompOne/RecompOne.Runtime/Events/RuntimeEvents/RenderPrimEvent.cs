namespace RecompOne.Runtime.Events;

/// <summary>fires for each primitive before it is drawns</summary>
public sealed class RenderPrimEvent : GameEvent
{
    public int Count;
    public readonly int[] X = new int[4];
    public readonly int[] Y = new int[4];
    public int DrawLeft, DrawRight, DrawTop, DrawBottom;
    public bool Textured, SemiTransparent, Gouraud, Raw;
    public int Clut, TexPage;
    public bool Skip;
    /// <summary>Set by a listener: draw this primitive in painter's order, outside
    /// the depth test and the depth the occlusion pass reads (a 3D HUD element,
    /// such as a compass, that the GTE projected).</summary>
    public bool NoDepth;
}