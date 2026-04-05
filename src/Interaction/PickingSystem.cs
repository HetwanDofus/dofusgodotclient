using Godot;
using System.Collections.Generic;
using DofusRetroFuture.Rendering;

namespace DofusRetroFuture.Interaction;

public record PickableObject(
    int Id, Sprite2D Sprite, string Type, string Name, int CellId, int ObjectType = 0,
    string? StripKey = null,
    byte[]? AlphaMap = null, int AlphaWidth = 0, int AlphaHeight = 0
);

public class PickingSystem
{
    private readonly Dictionary<int, PickableObject> _objects = new();
    private int _nextId;
    private StripCache? _stripCache;

    private const int AlphaThreshold = 10; // Matches Pixi's alpha > 10

    public void SetStripCache(StripCache cache) => _stripCache = cache;

    public int Register(Sprite2D sprite, string type, string name = "", int cellId = -1, int objectType = 0, string? stripKey = null, byte[]? alphaMap = null, int alphaWidth = 0, int alphaHeight = 0)
    {
        int id = _nextId++;
        _objects[id] = new PickableObject(id, sprite, type, name, cellId, objectType, stripKey, alphaMap, alphaWidth, alphaHeight);
        return id;
    }

    public void UpdateStripKey(int id, string stripKey)
    {
        if (_objects.TryGetValue(id, out var obj))
            _objects[id] = obj with { StripKey = stripKey };
    }

    public void Unregister(int id) => _objects.Remove(id);
    public void Clear() => _objects.Clear();

    public PickableObject? Pick(Vector2 worldPos)
    {
        PickableObject? best = null;
        int bestZ = int.MinValue;
        List<int>? stale = null;

        foreach (var kv in _objects)
        {
            var obj = kv.Value;
            if (!GodotObject.IsInstanceValid(obj.Sprite))
            {
                stale ??= new List<int>();
                stale.Add(kv.Key);
                continue;
            }
            if (!obj.Sprite.Visible) continue;

            if (HitTest(worldPos, obj) && obj.Sprite.ZIndex > bestZ)
            {
                bestZ = obj.Sprite.ZIndex;
                best = obj;
            }
        }

        if (stale is not null)
            foreach (int id in stale)
                _objects.Remove(id);

        return best;
    }

    private bool HitTest(Vector2 worldPos, PickableObject obj)
    {
        var sprite = obj.Sprite;
        var tex = sprite.Texture;
        if (tex is null) return false;

        // Convert world pos to sprite-local coordinates
        var local = sprite.ToLocal(worldPos);
        var texSize = tex.GetSize();
        var rect = new Rect2(sprite.Offset, texSize);

        // Normalize for negative scale (flipped)
        if (rect.Size.X < 0) rect = new Rect2(rect.Position.X + rect.Size.X, rect.Position.Y, -rect.Size.X, rect.Size.Y);
        if (rect.Size.Y < 0) rect = new Rect2(rect.Position.X, rect.Position.Y + rect.Size.Y, rect.Size.X, -rect.Size.Y);

        // Stage 1: AABB reject
        if (!rect.HasPoint(local)) return false;

        // Stage 2a: Alpha test via strip cache (players)
        if (obj.StripKey is not null && _stripCache is not null)
        {
            var strip = _stripCache.Get(obj.StripKey);
            if (strip?.AlphaMap is not null)
                return AlphaTest(local, sprite, strip);
        }

        // Stage 2b: Alpha test via inline alpha map (interactive tiles)
        if (obj.AlphaMap is not null && obj.AlphaWidth > 0 && obj.AlphaHeight > 0)
            return TileAlphaTest(local, sprite, obj.AlphaMap, obj.AlphaWidth, obj.AlphaHeight);

        // No alpha data — AABB pass
        return true;
    }

    private static bool TileAlphaTest(Vector2 localPos, Sprite2D sprite, byte[] alphaMap, int alphaW, int alphaH)
    {
        float u = localPos.X - sprite.Offset.X;
        float v = localPos.Y - sprite.Offset.Y;

        int px = (int)u;
        int py = (int)v;
        if (px < 0 || px >= alphaW || py < 0 || py >= alphaH) return false;

        int idx = py * alphaW + px;
        if (idx < 0 || idx >= alphaMap.Length) return false;

        return alphaMap[idx] > AlphaThreshold;
    }

    private bool AlphaTest(Vector2 localPos, Sprite2D sprite, StripData strip)
    {
        if (strip.AlphaMap is null || strip.AlphaWidth == 0 || strip.AlphaHeight == 0)
            return true;

        var tex = sprite.Texture;
        if (tex is null) return true;

        // Get UV in the texture (accounting for offset)
        // ToLocal already handles negative scale (flip), so no manual mirror needed
        float u = localPos.X - sprite.Offset.X;
        float v = localPos.Y - sprite.Offset.Y;

        // For AtlasTexture, we need to add the atlas region offset
        if (tex is AtlasTexture atlas)
        {
            u += atlas.Region.Position.X;
            v += atlas.Region.Position.Y;
        }

        int px = (int)u;
        int py = (int)v;

        if (px < 0 || px >= strip.AlphaWidth || py < 0 || py >= strip.AlphaHeight)
            return false;

        int idx = py * strip.AlphaWidth + px;
        if (idx < 0 || idx >= strip.AlphaMap.Length)
            return false;

        return strip.AlphaMap[idx] > AlphaThreshold;
    }
}
