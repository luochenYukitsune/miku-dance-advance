using System.Collections.ObjectModel;
using Mmd.Modern.Application;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility;

/// <summary>Original pre-model background order and the corresponding native MED1 kind.</summary>
public enum D3D9ClassicMediaKind { Picture = 1, Video = 2 }

/// <summary>Original PMM capture mode; mode3 samples the one global AVI texture.</summary>
public enum D3D9ClassicScreenCaptureMode { Disabled = 0, FullFrame = 1, CropFourByThree = 2, BackgroundRefresh = 3 }

/// <summary>Original PMM media placement, expressed in the original 1280-wide reference coordinates.</summary>
public sealed record D3D9ClassicMediaPlacement(int OffsetX, int OffsetY, float Scale)
{
    /// <summary>Original main-view rectangle for a full render viewport. Both product preview and
    /// capture render this same viewport; editor chrome and the original recording-window scaling
    /// are not additional coordinate systems in the product's ViewOutput frame.</summary>
    public D3D9ClassicMediaRectangle Resolve(int mediaWidth, int mediaHeight, int outputWidth, int outputHeight)
    {
        if (mediaWidth <= 0 || mediaHeight <= 0 || outputWidth <= 0 || outputHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(mediaWidth));
        if (!float.IsFinite(Scale)) throw new ArgumentException("Classic media scale must be finite.", nameof(Scale));
        // MikuDanceStudio MIT pin bfb6297: ui_viewport_refresh.cpp:138 and
        // bg_overlay.cpp:70..116. Preserve the float store of viewScale, the
        // double /k arithmetic and the original -1/+1 pixel boundaries.
        float viewScale = (float)((double)outputWidth * 1.2 / 1280.0);
        const double divisor = 1.2000000476837158;
        double left = OffsetX * (double)viewScale / divisor - 1.0;
        double top = OffsetY * (double)viewScale / divisor - 1.0;
        return new((float)left, (float)top,
            (float)(mediaWidth * (double)Scale * viewScale / divisor + left + 1.0),
            (float)(mediaHeight * (double)Scale * viewScale / divisor + top + 1.0));
    }
}

/// <summary>Pretransformed pixel bounds. Native drawing generates the original six vertices:
/// RT,RB,LT / LT,RB,LB, with z=0, rhw=1, diffuse=white and UV 0..1.</summary>
public sealed record D3D9ClassicMediaRectangle(float Left, float Top, float Right, float Bottom)
{
    internal void Validate()
    {
        if (!float.IsFinite(Left) || !float.IsFinite(Top) || !float.IsFinite(Right) || !float.IsFinite(Bottom))
            throw new ArgumentException("Classic media rectangle must be finite.");
    }
}

/// <summary>One immutable original-size, top-to-bottom RGBA8 background. Values remain in the
/// decoder's encoded RGB domain, matching Classic's non-sRGB fixed texture path; no modern
/// color conversion or CPU resize is applied. Video alpha is always opaque, as in original X8R8G8B8.</summary>
public sealed class D3D9ClassicMediaLayer
{
    private readonly byte[] rgba;
    public D3D9ClassicMediaKind Kind { get; }
    public int Width { get; }
    public int Height { get; }
    public D3D9ClassicMediaRectangle Rectangle { get; }
    public bool DrawBackground { get; }
    public ReadOnlyMemory<byte> Rgba => rgba;

    public D3D9ClassicMediaLayer(D3D9ClassicMediaKind kind, int width, int height,
        D3D9ClassicMediaRectangle rectangle, ReadOnlySpan<byte> rgba, bool drawBackground = true)
    {
        ArgumentNullException.ThrowIfNull(rectangle); rectangle.Validate();
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (width is < 1 or > 32768 || height is < 1 or > 32768
            || (long)width * height * 4 > 256 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(width), "Classic background exceeds the decoded frame budget.");
        if (rgba.Length != checked(width * height * 4))
            throw new ArgumentException("Classic background RGBA size must match its original dimensions.", nameof(rgba));
        if (!drawBackground && kind != D3D9ClassicMediaKind.Video)
            throw new ArgumentException("Only the global AVI texture can be supplied without a background quad.", nameof(drawBackground));
        Kind = kind; Width = width; Height = height; Rectangle = rectangle; DrawBackground = drawBackground; this.rgba = rgba.ToArray();
        if (kind == D3D9ClassicMediaKind.Video)
            for (int i = 3; i < this.rgba.Length; i += 4) this.rgba[i] = 255;
    }
}

/// <summary>Frozen decoded background inputs tied by identity to one accepted geometry sample.
/// They do not contain an animation/physics owner, decoder, original path or playback clock.</summary>
public sealed class D3D9ClassicMediaInputs
{
    internal OfflineFrame Frame { get; }
    public IReadOnlyList<D3D9ClassicMediaLayer> Layers { get; }
    public D3D9ClassicScreenCaptureMode? ScreenCaptureMode { get; }

    public D3D9ClassicMediaInputs(OfflineFrame frame, IEnumerable<D3D9ClassicMediaLayer> layers,
        D3D9ClassicScreenCaptureMode? screenCaptureMode = null)
    {
        ArgumentNullException.ThrowIfNull(frame); ArgumentNullException.ThrowIfNull(layers);
        if (screenCaptureMode is { } mode && !Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(screenCaptureMode));
        ScreenCaptureMode = screenCaptureMode;
        var frozen = layers.ToArray();
        if (frozen.Length > 2 || frozen.Any(value => value is null)
            || frozen.Select(value => value.Kind).Distinct().Count() != frozen.Length
            || !frozen.Select(value => value.Kind).SequenceEqual(frozen.Select(value => value.Kind).Order()))
            throw new ArgumentException("Classic backgrounds require at most one picture followed by at most one video.", nameof(layers));
        if (screenCaptureMode is null && frozen.Any(value => !value.DrawBackground))
            throw new ArgumentException("A hidden global AVI layer requires explicit original screen metadata.", nameof(screenCaptureMode));
        Frame = frame; Layers = new ReadOnlyCollection<D3D9ClassicMediaLayer>(frozen);
    }
}

/// <summary>Captured track bytes plus its original PMM layout; no source path is reopened by rendering.</summary>
public sealed record D3D9ClassicMediaSource(SceneMediaTrack Track, D3D9ClassicMediaPlacement Placement,
    bool DrawBackground = true)
{
    internal D3D9ClassicMediaKind Kind => Track.Kind switch
    {
        SceneMediaKind.BackgroundImage => D3D9ClassicMediaKind.Picture,
        SceneMediaKind.Video => D3D9ClassicMediaKind.Video,
        _ => throw new NotSupportedException("Classic background inputs do not implement audio playback or mux.")
    };

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Track); ArgumentNullException.ThrowIfNull(Placement); Track.Validate();
        _ = Kind;
        if (!DrawBackground && Kind != D3D9ClassicMediaKind.Video)
            throw new ArgumentException("Only the global AVI source can be hidden independently of decoding.");
        if (!float.IsFinite(Placement.Scale)) throw new ArgumentException("Classic media scale must be finite.");
    }
}
