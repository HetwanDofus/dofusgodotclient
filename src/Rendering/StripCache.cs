using Godot;
using DofusRetroFuture.Core;

namespace DofusRetroFuture.Rendering;

public record StripData(
    Texture2D StripTexture,
    AtlasTexture[] Frames,
    float AnchorX,
    float AnchorY,
    int FrameCount,
    int Fps,
    byte[]? AlphaMap,
    int AlphaWidth,
    int AlphaHeight,
    int GridCols
);

/// <summary>
/// Cache of rendered animation strips. Each unique visual state (gfx+anim+colors+acc)
/// gets one strip texture with all frames in a grid.
/// </summary>
public class StripCache
{
    private readonly System.Collections.Generic.Dictionary<string, StripData> _cache = new();
    private readonly VelloRenderer _vello;
    private float _resolution;

    public int Count => _cache.Count;

    public StripCache(VelloRenderer vello, float resolution)
    {
        _vello = vello;
        _resolution = resolution;
    }

    public static string MakeKey(int gfxId, string animName, int[] colors, int[] accInfo)
    {
        var key = $"{gfxId}:{animName}:{colors[0]}_{colors[1]}_{colors[2]}";
        if (accInfo.Length >= 2)
        {
            var parts = new System.Text.StringBuilder();
            for (int i = 0; i < accInfo.Length; i += 2)
                parts.Append($"{accInfo[i]}-{accInfo[i + 1]},");
            key += $":{parts}";
        }
        return key;
    }

    public StripData? Get(string key)
    {
        return _cache.TryGetValue(key, out var data) ? data : null;
    }

    public StripData? GetOrRender(int gfxId, string animName, int[] colors, int[] accInfo)
    {
        var key = MakeKey(gfxId, animName, colors, accInfo);
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        var assetId = _vello.GetSpriteAssetId(gfxId);
        if (assetId is null) return null;

        var strip = _vello.RenderAnimationStrip(assetId.Value, animName, _resolution, colors, accInfo);
        if (!strip.ContainsKey("texture")) return null;

        int frameCount = strip.ContainsKey("frameCount") ? strip["frameCount"].AsInt32() : 0;
        int gridCols = strip.ContainsKey("gridCols") ? strip["gridCols"].AsInt32() : frameCount;
        float fw = strip.ContainsKey("frameWidth") ? strip["frameWidth"].AsInt32() : 1;
        float fh = strip.ContainsKey("frameHeight") ? strip["frameHeight"].AsInt32() : 1;
        float anchorX = strip.ContainsKey("anchorX") ? (float)strip["anchorX"].AsDouble() : 0;
        float anchorY = strip.ContainsKey("anchorY") ? (float)strip["anchorY"].AsDouble() : 0;
        int fps = strip.ContainsKey("fps") ? strip["fps"].AsInt32() : Constants.GetAnimFps(animName);

        var baseTexture = strip["texture"].As<Texture2D>();
        var frames = new AtlasTexture[frameCount];
        for (int i = 0; i < frameCount; i++)
        {
            int col = i % gridCols;
            int row = i / gridCols;
            var atlas = new AtlasTexture();
            atlas.Atlas = baseTexture;
            atlas.Region = new Rect2(col * fw, row * fh, fw, fh);
            frames[i] = atlas;
        }

        // Extract alpha map for pixel-perfect picking
        byte[]? alphaMap = null;
        int alphaW = 0, alphaH = 0;
        if (strip.ContainsKey("alphaMap"))
        {
            var packed = strip["alphaMap"].AsByteArray();
            alphaMap = new byte[packed.Length];
            for (int i = 0; i < packed.Length; i++)
                alphaMap[i] = packed[i];
            alphaW = strip.ContainsKey("alphaWidth") ? strip["alphaWidth"].AsInt32() : 0;
            alphaH = strip.ContainsKey("alphaHeight") ? strip["alphaHeight"].AsInt32() : 0;
        }

        var data = new StripData(baseTexture, frames, anchorX, anchorY, frameCount, fps, alphaMap, alphaW, alphaH, gridCols);
        _cache[key] = data;
        return data;
    }

    public void Clear()
    {
        _cache.Clear();
    }

    public void ClearAndUpdateResolution(float resolution)
    {
        _cache.Clear();
        _resolution = resolution;
    }
}
