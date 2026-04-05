using Godot;
using Godot.Collections;
using DofusRetroFuture.Core;
using DofusRetroFuture.Interaction;
using DofusRetroFuture.Proto;
using static DofusRetroFuture.Core.Constants;

namespace DofusRetroFuture.Rendering;

/// <summary>
/// Renders a Dofus map's tile layers using the Vello GPU renderer.
/// </summary>
public partial class MapRenderer : Node2D
{
    private VelloRenderer _vello = null!;
    private InteractiveObjectDB? _interactiveDb;

    private Node2D _backgroundLayer = null!;
    private Node2D _groundLayer = null!;
    private Node2D _objectLayer1 = null!;
    private Node2D _objectLayer2 = null!;

    private float _tileResolution = 1f;
    private int _mapWidth = 15;
    private int _bgId;
    private Array<Dictionary> _cells = new();
    private System.Collections.Generic.Dictionary<string, Dictionary> _uniqueTiles = new();
    private System.Collections.Generic.List<Dictionary> _batchSpecs = new();
    private System.Collections.Generic.List<string> _tileKeys = new();

    // Interactive tile sprites registered with the picking system
    public System.Collections.Generic.List<InteractiveTileInfo> InteractiveTiles { get; } = new();

    // Track tile sprites so we only free those on re-render (not actor sprites)
    private readonly System.Collections.Generic.List<Sprite2D> _tileSprites = new();

    public Array<Dictionary> Cells => _cells;
    public int MapWidth => _mapWidth;

    /// <summary>
    /// The interleave layer where Object2 tiles and actors share z-index space.
    /// Actors should be added as children of this node.
    /// </summary>
    public Node2D InterleaveLayer => _objectLayer2;

    public void Init(VelloRenderer vello, InteractiveObjectDB? interactiveDb, float tileResolution)
    {
        _vello = vello;
        _interactiveDb = interactiveDb;
        _tileResolution = tileResolution;

        // All layers in one parent — Godot sorts children by z_index globally.
        // Ground/Object1 get fixed z, Object2 tiles get cellId-based z,
        // actors at cellId*100+30 interleave with Object2.
        // Flash ExternalContainer depths: Ground=200, Object1=300, Grid=400, Object2=800
        // All layers use ZAsRelative=false so their children have ABSOLUTE z-indices.
        // This allows the grid (z=400) to interleave between Object1 and Object2.
        _backgroundLayer = new Node2D { Name = "BackgroundLayer", ZAsRelative = false, TextureFilter = TextureFilterEnum.Nearest };
        AddChild(_backgroundLayer);

        _groundLayer = new Node2D { Name = "GroundLayer", ZAsRelative = false, TextureFilter = TextureFilterEnum.Nearest };
        AddChild(_groundLayer);

        _objectLayer1 = new Node2D { Name = "ObjectLayer1", ZAsRelative = false, TextureFilter = TextureFilterEnum.Nearest };
        AddChild(_objectLayer1);

        _objectLayer2 = new Node2D { Name = "InterleaveLayer", ZAsRelative = false, TextureFilter = TextureFilterEnum.Nearest };
        AddChild(_objectLayer2);
    }

    public void LoadMap(int mapId)
    {
        var file = FileAccess.Open($"res://assets/maps/{mapId}.json", FileAccess.ModeFlags.Read);
        if (file is null) { GD.PrintErr($"Cannot open map {mapId}"); return; }

        var json = new Json();
        if (json.Parse(file.GetAsText()) != Error.Ok) { GD.PrintErr("Failed to parse map JSON"); return; }
        file.Close();

        var data = json.Data.AsGodotDictionary();
        _mapWidth = data["width"].AsInt32();
        _cells = data["cells"].AsGodotArray<Dictionary>();
        _bgId = data.ContainsKey("background") ? data["background"].AsInt32() : 0;

        GameManager.Log($"[MapRenderer] Map {mapId}: {_mapWidth}x{data["height"].AsInt32()}, {_cells.Count} cells, bg={_bgId}");
    }

    public void LoadMapFromProto(MapData mapData)
    {
        _mapWidth = mapData.Width;
        _bgId = mapData.Background;
        _cells = new Array<Dictionary>();
        foreach (var c in mapData.Cells)
        {
            _cells.Add(new Dictionary
            {
                ["id"] = c.Id,
                ["active"] = c.Active,
                ["ground"] = c.Ground,
                ["layer1"] = c.Layer1,
                ["layer2"] = c.Layer2,
                ["groundLevel"] = c.GroundLevel,
                ["walkable"] = c.Walkable,
                ["movement"] = c.Movement,
                ["lineOfSight"] = c.LineOfSight,
                ["groundSlope"] = c.GroundSlope,
                ["layerGroundRot"] = c.GroundRot,
                ["layerGroundFlip"] = c.GroundFlip,
                ["layerObject1Rot"] = c.Obj1Rot,
                ["layerObject1Flip"] = c.Obj1Flip,
                ["layerObject2Rot"] = c.Obj2Rot,
                ["layerObject2Flip"] = c.Obj2Flip,
            });
        }
        // Log first few cells to verify data
        for (int i = 0; i < System.Math.Min(3, _cells.Count); i++)
        {
            var c = _cells[i];
            GameManager.Log($"[MapRenderer] cell[{i}] id={c["id"]} ground={c["ground"]} layer1={c["layer1"]} layer2={c["layer2"]} movement={c["movement"]}");
        }
        GameManager.Log($"[MapRenderer] Map {mapData.MapId} (proto): {_mapWidth}x{mapData.Height}, {_cells.Count} cells, bg={_bgId}");
    }

    public void RenderMap()
    {
        var groundTilesPath = "res://assets/tiles/ground/";
        var objectTilesPath = "res://assets/tiles/objects/";
        GameManager.Log($"[MapRenderer] RenderMap groundPath={groundTilesPath} objectPath={objectTilesPath}");
        GameManager.Log($"[MapRenderer] groundPath exists={DirAccess.DirExistsAbsolute(groundTilesPath)} objectPath exists={DirAccess.DirExistsAbsolute(objectTilesPath)}");

        // Collect unique tiles
        _uniqueTiles.Clear();
        if (_bgId > 0) RegisterTile($"ground_{_bgId}", groundTilesPath, objectTilesPath);

        foreach (var cell in _cells)
        {
            int ground = cell.ContainsKey("ground") ? cell["ground"].AsInt32() : 0;
            int layer1 = cell.ContainsKey("layer1") ? cell["layer1"].AsInt32() : 0;
            int layer2 = cell.ContainsKey("layer2") ? cell["layer2"].AsInt32() : 0;
            if (ground > 0) RegisterTile($"ground_{ground}", groundTilesPath, objectTilesPath);
            if (layer1 > 0) RegisterTile($"objects_{layer1}", groundTilesPath, objectTilesPath);
            if (layer2 > 0) RegisterTile($"objects_{layer2}", groundTilesPath, objectTilesPath);
        }

        GameManager.Log($"[MapRenderer] Registered {_uniqueTiles.Count} unique tiles");

        // Resolve animation names (assets already loaded by RegisterTile → LoadTileAsset)
        foreach (var kv in _uniqueTiles)
        {
            var info = kv.Value;
            var anims = _vello.GetAnimationNames((uint)info["asset_id"].AsInt32());
            if (anims.Length > 0) info["animation"] = anims[0];
        }

        // Build batch specs
        _tileKeys.Clear();
        _batchSpecs.Clear();
        foreach (var kv in _uniqueTiles)
        {
            var info = kv.Value;
            string anim = info.ContainsKey("animation") ? info["animation"].AsString() : "";
            if (string.IsNullOrEmpty(anim)) continue;
            // Check if this tile is interactive (needs alpha for picking)
            var parts = kv.Key.Split('_', 2);
            int tileGfxId = int.Parse(parts[1]);
            bool isInteractive = _interactiveDb is not null && _interactiveDb.IsInteractive(tileGfxId);

            _tileKeys.Add(kv.Key);
            _batchSpecs.Add(new Dictionary
            {
                ["asset_id"] = info["asset_id"],
                ["animation"] = anim,
                ["frame_index"] = 0,
                ["need_alpha"] = isInteractive
            });
        }

        RenderAndPlaceTiles();
    }

    public void ToggleLayer(string layer)
    {
        Node2D? node = layer switch
        {
            "background" => _backgroundLayer,
            "ground" => _groundLayer,
            "object1" => _objectLayer1,
            "object2" => _objectLayer2,
            _ => null
        };
        if (node is not null)
        {
            node.Visible = !node.Visible;
            GameManager.Log($"[MapRenderer] {layer}: {(node.Visible ? "ON" : "OFF")}");
        }
    }

    public void Rerender(float newResolution)
    {
        _tileResolution = newResolution;
        RenderAndPlaceTiles();
    }

    private void RenderAndPlaceTiles()
    {
        InteractiveTiles.Clear();
        FreeLayerChildren(_backgroundLayer);
        FreeLayerChildren(_groundLayer);
        FreeLayerChildren(_objectLayer1);
        // Only free tile sprites in interleave layer, not actor sprites
        foreach (var sprite in _tileSprites)
        {
            if (GodotObject.IsInstanceValid(sprite))
            {
                sprite.GetParent()?.RemoveChild(sprite);
                sprite.Free();
            }
        }
        _tileSprites.Clear();

        ulong t0 = Time.GetTicksMsec();
        var specs = new Array<Dictionary>();
        foreach (var s in _batchSpecs) specs.Add(s);
        var batchResults = _vello.RenderTilesBatch(specs, _tileResolution);

        // Build cache
        var tileCache = new System.Collections.Generic.Dictionary<string, Dictionary?>();
        int withTexture = 0;
        for (int i = 0; i < batchResults.Count; i++)
        {
            var result = batchResults[i];
            bool hasTex = result.ContainsKey("texture");
            tileCache[_tileKeys[i]] = hasTex ? result : null;
            if (hasTex) withTexture++;
        }
        GameManager.Log($"[MapRenderer] Rendered {withTexture}/{_batchSpecs.Count} tiles in {Time.GetTicksMsec() - t0} ms (res={_tileResolution:F1})");

        // Background
        if (_bgId > 0)
        {
            var bgKey = $"ground_{_bgId}";
            if (tileCache.TryGetValue(bgKey, out var bgCached) && bgCached is not null)
            {
                var sprite = CreateTileSprite(bgCached);
                sprite.ZIndex = 100;
                _backgroundLayer.AddChild(sprite);
            }
        }

        // Place tiles
        foreach (var cellData in _cells)
        {
            int cellId = cellData["id"].AsInt32();
            int ground = cellData.ContainsKey("ground") ? cellData["ground"].AsInt32() : 0;
            int layer1 = cellData.ContainsKey("layer1") ? cellData["layer1"].AsInt32() : 0;
            int layer2 = cellData.ContainsKey("layer2") ? cellData["layer2"].AsInt32() : 0;
            int groundLevel = cellData.ContainsKey("groundLevel") ? cellData["groundLevel"].AsInt32() : 7;
            int groundRot = cellData.ContainsKey("layerGroundRot") ? cellData["layerGroundRot"].AsInt32() : 0;
            bool groundFlip = cellData.ContainsKey("layerGroundFlip") && cellData["layerGroundFlip"].AsBool();
            int obj1Rot = cellData.ContainsKey("layerObject1Rot") ? cellData["layerObject1Rot"].AsInt32() : 0;
            bool obj1Flip = cellData.ContainsKey("layerObject1Flip") && cellData["layerObject1Flip"].AsBool();
            bool obj2Flip = cellData.ContainsKey("layerObject2Flip") && cellData["layerObject2Flip"].AsBool();

            var pos = CellGrid.GetCellPosition(cellId, _mapWidth, groundLevel);

            if (ground > 0)
                PlaceTile(tileCache, $"ground_{ground}", pos, 200, groundRot, groundFlip, _groundLayer);
            if (layer1 > 0)
                PlaceTile(tileCache, $"objects_{layer1}", pos, 300, obj1Rot, obj1Flip, _objectLayer1, cellId, layer1);
            if (layer2 > 0)
                PlaceTile(tileCache, $"objects_{layer2}", pos, 800 + cellId, 0, obj2Flip, _objectLayer2, cellId, layer2);
        }
    }

    private Sprite2D CreateTileSprite(Dictionary cached)
    {
        var sprite = new Sprite2D();
        sprite.Texture = cached["texture"].As<Texture2D>();
        sprite.Centered = false;
        float ax = (float)cached["anchorX"].AsDouble();
        float ay = (float)cached["anchorY"].AsDouble();
        sprite.Offset = new Vector2(-ax, -ay);
        sprite.Scale = new Vector2(1f / _tileResolution, 1f / _tileResolution);
        return sprite;
    }

    private void PlaceTile(
        System.Collections.Generic.Dictionary<string, Dictionary?> cache,
        string key, Vector2 pos, int z, int rot, bool flip, Node2D layer,
        int cellId = -1, int tileGfxId = -1)
    {
        if (!cache.TryGetValue(key, out var cached) || cached is null) return;

        var sprite = CreateTileSprite(cached);
        sprite.Position = pos;
        sprite.ZIndex = z;

        float s = 1f / _tileResolution;
        float sx = flip ? -s : s;
        float sy = s;
        int r = rot % 4;
        if (r == 1 || r == 3) { sx *= 0.5185f; sy *= 1.9286f; }
        sprite.Scale = new Vector2(sx, sy);
        if (r != 0) sprite.RotationDegrees = r * 90f;

        layer.AddChild(sprite);

        // Track interleave layer tiles for selective cleanup on re-render
        if (layer == _objectLayer2)
            _tileSprites.Add(sprite);

        // Register interactive tiles
        if (cellId >= 0 && tileGfxId >= 0 && _interactiveDb is not null && _interactiveDb.IsInteractive(tileGfxId))
        {
            var info = _interactiveDb.GetInfo(tileGfxId);
            // Extract alpha map from batch result
            byte[]? alphaMap = null;
            int alphaW = 0, alphaH = 0;
            if (cached.ContainsKey("alphaMap"))
            {
                var packed = cached["alphaMap"].AsByteArray();
                alphaMap = new byte[packed.Length];
                for (int ai = 0; ai < packed.Length; ai++) alphaMap[ai] = packed[ai];
                alphaW = cached.ContainsKey("alphaWidth") ? cached["alphaWidth"].AsInt32() : 0;
                alphaH = cached.ContainsKey("alphaHeight") ? cached["alphaHeight"].AsInt32() : 0;
            }
            InteractiveTiles.Add(new InteractiveTileInfo(sprite, cellId, tileGfxId, info.Type, info.Name, alphaMap, alphaW, alphaH));
        }
    }

    private void RegisterTile(string key, string groundPath, string objectPath)
    {
        if (_uniqueTiles.ContainsKey(key)) return;
        var parts = key.Split('_', 2);
        string basePath = parts[0] == "ground" ? groundPath : objectPath;
        string path = $"{basePath}{parts[1]}.dofasset";
        if (!FileAccess.FileExists(path)) return;

        uint aid = _vello.LoadTileAsset(path);

        _uniqueTiles[key] = new Dictionary
        {
            ["path"] = path,
            ["asset_id"] = (int)aid,
            ["animation"] = ""
        };
    }

    private static void FreeLayerChildren(Node2D layer)
    {
        foreach (var child in layer.GetChildren())
        {
            layer.RemoveChild(child);
            child.Free();
        }
    }
}

public record InteractiveTileInfo(Sprite2D Sprite, int CellId, int GfxId, int ObjectType, string Name, byte[]? AlphaMap = null, int AlphaWidth = 0, int AlphaHeight = 0);
