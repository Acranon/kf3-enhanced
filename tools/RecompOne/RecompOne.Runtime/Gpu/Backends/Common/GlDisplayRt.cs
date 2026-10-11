using Silk.NET.OpenGL;

namespace RecompOne.Runtime.Hle;

public sealed class GlDisplayRt
{
    public int X, Y, W, H;
    public int Margin;
    public uint Tex, Fbo;
    // A texture rather than a renderbuffer, because the ambient-occlusion pass
    // reads the finished buffer back in a full-screen shader and a renderbuffer
    // cannot be sampled. Nothing else changes: it is still the same
    // DEPTH_COMPONENT24 attachment, still cleared at the head of a frame that
    // draws to this target, and ReadPixels off the FBO (the RECOMPONE_ZBUFFER_PROBE=2
    // census) reads it exactly as before.
    public uint Depth;
    // 0058. The occlusion pass's normals, and the geometry they are drawn from.
    // Both belong to the target rather than to the frame, for the same reason the
    // depth does: with two display buffers the target being presented was drawn a
    // frame ago. Created on demand, so a run with the pass off allocates neither.
    public uint Normal, NormalFbo;
    public int NormalW, NormalH;
    // 0067. The surface buffer, beside the normals in the same framebuffer: the
    // last surface drawn at each pixel, translucent water included, as an
    // octahedral normal, a depth and a material. Only while reflections are on.
    public uint Surface;
    public readonly AoGeometry Geo = new();
    // 0068. The scene from the camera mirrored in the water, drawn at this target's
    // size so the reflection pass indexes it exactly as it indexes this one. Its
    // own depth attachment is what says a texel was drawn. Serial and frame say
    // which capture it holds and whether this target's picture is that frame's;
    // the plane is the one the capture mirrored in, in that frame's view space.
    public GlDisplayRt? Planar;
    public bool IsPlanar;
    public int PlanarSerial = -1;
    public long PlanarFrame = -1;
    public readonly float[] PlanarPlane = new float[4];
    public readonly float[] ClipPlane = new float[4];
    // Anti-aliasing (GteDepth.Msaa). While these exist the game draws into them
    // and Tex/Depth are the resolved copy: MsNewer says the samples hold drawing
    // the copy has not had yet. Writes that land in Tex (VRAM syncs, fills,
    // restores) are copied into the samples over the rectangle they wrote.
    public uint MsFbo, MsColor, MsDepth;
    public int MsSamples;
    public bool MsNewer;
    public InternalFormat TexFormat;
    public int CreatedScale;
    public bool Dirty;
    public long Stamp;
    public long LastDrawFrame;
    // Whether this target's margin columns have ever carried a world's worth of
    // content: latched once a single display flip delivers enough game vertices
    // past the game's own draw edge, or a fill covers the target. Negative means
    // never. The density test is what keeps an oversized clear rect -- genuine
    // game output, but two vertices of it per flip -- from granting the margin
    // to a scene that never renders anything out there.
    public int MarginContentFlip = -1000;
    public int MarginVerts;
    public int MarginVertFlip = -1;
    public int ZGen = -1;
    // Upstream's own, kept beside ours rather than in place of it.
    public long LastMarginFrame = -1;

    public int Wide1x => W + Margin * 2;
    public int TexW => Wide1x * GlVram.Scale;
    public int TexH => H * GlVram.Scale;

    public bool Contains(int cx0, int cy0, int cx1, int cy1)
    {
        return cx0 >= X && cx1 <= X + W - 1 && cy0 >= Y && cy1 <= Y + H - 1;
    }

    public bool Covers(int cx0, int cy0, int cx1, int cy1)
    {
        return cx0 <= X && cx1 >= X + W - 1 && cy0 <= Y && cy1 >= Y + H - 1;
    }

    public bool Intersects(int rx, int ry, int rw, int rh)
    {
        return rx < X + W && X < rx + rw && ry < Y + H && Y < ry + rh;
    }

    public unsafe void Create(GL gl)
    {
        CreatedScale = GlVram.Scale;
        Tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, Tex);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        // True color keeps eight bits per channel so the shaded fog gradient does
        // not band; the default RGB5A1 matches the console's 15-bit VRAM and lets
        // the fragment shader's quant5 stand. The mask/STP bit rides the alpha
        // either way — 1-bit in 1555, the top of an 8-bit alpha in RGBA8, and both
        // read back as >= 0.5. The blits to and from VRAM (Rgb5A1) convert
        // automatically, so a true-color target still writes 15-bit content back.
        TexFormat = GteDepth.TrueColor ? InternalFormat.Rgba8 : InternalFormat.Rgb5A1;
        if (GteDepth.TrueColor)
            gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)TexW, (uint)TexH, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, new byte[TexW * TexH * 4].AsSpan());
        else
            gl.TexImage2D<ushort>(TextureTarget.Texture2D, 0, InternalFormat.Rgb5A1, (uint)TexW, (uint)TexH, 0,
                PixelFormat.Rgba, PixelType.UnsignedShort1555Rev, new ushort[TexW * TexH].AsSpan());

        Fbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, Fbo);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, Tex, 0);

        Depth = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, Depth);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        // Explicitly not a shadow sampler: with the compare mode left at whatever
        // the driver defaults to, an ordinary sampler2D read of this texture is
        // undefined rather than the stored depth.
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode, (int)GLEnum.None);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.DepthComponent24, (uint)TexW, (uint)TexH, 0,
            PixelFormat.DepthComponent, PixelType.Float, null);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            TextureTarget.Texture2D, Depth, 0);
        gl.BindTexture(TextureTarget.Texture2D, 0);

        gl.ClearColor(0f, 0f, 0f, 0f);
        gl.ClearDepth(1.0);
        gl.Disable(EnableCap.ScissorTest);
        gl.DepthMask(true);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
    }

    /// <summary>The multisampled colour and depth, in the resolved copy's
    /// formats (a resolve blit needs them identical). False if the driver
    /// refuses the combination.</summary>
    public bool CreateMs(GL gl, int samples)
    {
        MsColor = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, MsColor);
        gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, (uint)samples, TexFormat, (uint)TexW, (uint)TexH);
        MsDepth = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, MsDepth);
        gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, (uint)samples, InternalFormat.DepthComponent24, (uint)TexW, (uint)TexH);
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);
        MsFbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, MsFbo);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, MsColor);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, MsDepth);
        bool ok = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) == GLEnum.FramebufferComplete;
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        if (!ok) { DestroyMs(gl); return false; }
        MsSamples = samples;
        MsNewer = false;
        return true;
    }

    public void DestroyMs(GL gl)
    {
        if (MsFbo != 0) gl.DeleteFramebuffer(MsFbo);
        if (MsColor != 0) gl.DeleteRenderbuffer(MsColor);
        if (MsDepth != 0) gl.DeleteRenderbuffer(MsDepth);
        MsFbo = MsColor = MsDepth = 0;
        MsSamples = 0;
        MsNewer = false;
    }

    public void Destroy(GL gl)
    {
        DestroyMs(gl);
        if (Fbo != 0) gl.DeleteFramebuffer(Fbo);
        if (Tex != 0) gl.DeleteTexture(Tex);
        if (Depth != 0) gl.DeleteTexture(Depth);
        if (NormalFbo != 0) gl.DeleteFramebuffer(NormalFbo);
        if (Normal != 0) gl.DeleteTexture(Normal);
        if (Surface != 0) gl.DeleteTexture(Surface);
        Planar?.Destroy(gl);
        Planar = null;
        Fbo = Tex = Depth = Normal = NormalFbo = Surface = 0;
    }
}