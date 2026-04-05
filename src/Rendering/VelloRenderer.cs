using Godot;
using Godot.Collections;

namespace DofusRetroFuture.Rendering;

/// <summary>
/// Wraps the Rust DofusRenderer GDExtension. Manages GPU lifecycle and asset loading.
/// </summary>
public class VelloRenderer
{
    private readonly GodotObject _renderer;
    private readonly System.Collections.Generic.Dictionary<int, uint> _gfxToAssetId = new();
    private readonly System.Collections.Generic.Dictionary<string, uint> _accToAssetId = new();
    private readonly System.Collections.Generic.Dictionary<string, uint> _tileAssetIds = new();
    private uint _nextAssetId;

    public VelloRenderer()
    {
        _renderer = ClassDB.Instantiate("DofusRenderer").AsGodotObject();
    }

    public bool InitGpu()
    {
        return _renderer.Call("init_gpu").AsBool();
    }

    public int LoadAssetsBatch(Array<Dictionary> specs)
    {
        var arr = new Array<Variant>();
        foreach (var spec in specs)
            arr.Add(spec);
        return _renderer.Call("load_assets_batch", arr).AsInt32();
    }

    public string[] GetAnimationNames(uint assetId)
    {
        var packed = _renderer.Call("get_animation_names", assetId).AsStringArray();
        var result = new string[packed.Length];
        for (int i = 0; i < packed.Length; i++)
            result[i] = packed[i];
        return result;
    }

    public Dictionary GetAnimationInfo(uint assetId, string animation, float resolution)
    {
        return _renderer.Call("get_animation_info", assetId, animation, resolution).AsGodotDictionary();
    }

    public Dictionary RenderAnimationStrip(uint assetId, string animation, float resolution, int[] colors, int[] accInfo)
    {
        var colorsArr = new Godot.Collections.Array<int>();
        foreach (var c in colors) colorsArr.Add(c);
        var accArr = new Godot.Collections.Array<int>();
        foreach (var a in accInfo) accArr.Add(a);

        return _renderer.Call("render_animation_strip", assetId, animation, resolution,
            ToPackedInt32(colors), ToPackedInt32(accInfo)).AsGodotDictionary();
    }

    public Array<Dictionary> RenderTilesBatch(Array<Dictionary> specs, float resolution)
    {
        var arr = new Array<Variant>();
        foreach (var spec in specs)
            arr.Add(spec);
        var result = _renderer.Call("render_tiles_batch", arr, resolution).AsGodotArray();
        var typed = new Array<Dictionary>();
        foreach (var item in result)
            typed.Add(item.AsGodotDictionary());
        return typed;
    }

    public void Cleanup()
    {
        _renderer.Call("cleanup");
    }

    /// <summary>
    /// Load a tile asset and return its asset ID. Caches by file path.
    /// </summary>
    public uint LoadTileAsset(string path)
    {
        if (_tileAssetIds.TryGetValue(path, out uint existing))
            return existing;

        uint aid = _nextAssetId++;
        _tileAssetIds[path] = aid;
        LoadAssetViaGodot(aid, path);
        return aid;
    }

    /// <summary>
    /// Load a sprite GFX and return its asset ID. Caches by gfxId.
    /// </summary>
    public uint LoadSprite(int gfxId, string spritesPath)
    {
        if (_gfxToAssetId.TryGetValue(gfxId, out uint existing))
            return existing;

        uint aid = _nextAssetId++;
        _gfxToAssetId[gfxId] = aid;
        var path = $"{spritesPath}{gfxId}.dofasset";
        LoadAssetViaGodot(aid, path);
        return aid;
    }

    /// <summary>
    /// Load an accessory and return its asset ID. Caches by key (e.g., "16_10").
    /// </summary>
    public uint? LoadAccessory(string accKey, string spritesPath)
    {
        if (_accToAssetId.TryGetValue(accKey, out uint existing))
            return existing;

        var path = $"{spritesPath}acc_{accKey}.dofasset";
        if (!FileAccess.FileExists(path))
            return null;

        uint aid = _nextAssetId++;
        _accToAssetId[accKey] = aid;
        LoadAssetViaGodot(aid, path);
        return aid;
    }

    /// <summary>
    /// Read file through Godot's FileAccess (works with PCK) and pass bytes to Rust.
    /// </summary>
    private void LoadAssetViaGodot(uint id, string path)
    {
        var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        if (file is null)
        {
            GD.PrintErr($"[VelloRenderer] Cannot open {path}");
            return;
        }
        var bytes = file.GetBuffer((long)file.GetLength());
        file.Close();
        _renderer.Call("load_asset_from_bytes", id, bytes);
    }

    public uint? GetSpriteAssetId(int gfxId)
    {
        return _gfxToAssetId.TryGetValue(gfxId, out uint id) ? id : null;
    }

    public uint? GetAccessoryAssetId(string accKey)
    {
        return _accToAssetId.TryGetValue(accKey, out uint id) ? id : null;
    }

    private static int[] ToPackedInt32(int[] arr)
    {
        return arr;
    }
}
