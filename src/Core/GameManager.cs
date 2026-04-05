using Godot;
using Godot.Collections;
using DofusRetroFuture.Rendering;
using DofusRetroFuture.Entities;
using DofusRetroFuture.Interaction;
using DofusRetroFuture.Network;
using DofusRetroFuture.UI;
using static DofusRetroFuture.Core.Constants;

namespace DofusRetroFuture.Core;

public partial class GameManager : Node2D
{
    [Export] public int MapId { get; set; } = 7411;
    [Export] public int ActorCount { get; set; } = 400;
    [Export] public bool EnableStressTest { get; set; } = true;
    [Export] public bool ConnectToServer { get; set; } = false;
    [Export] public string ServerUrl { get; set; } = "ws://localhost:8080/ws";
    [Export] public string Username { get; set; } = "admin";

    private VelloRenderer _vello = null!;
    private MapRenderer _mapRenderer = null!;
    private InteractionHandler _interactionHandler = null!;
    private InteractiveObjectDB _interactiveDb = null!;
    private ActorManager? _actorManager;
    private StripCache _stripCache = null!;
    private DofusPathfinding _pathfinding = null!;
    private ZaapPopup? _zaapPopup;
    private GridOverlay? _gridOverlay;
    private GameClient? _gameClient;
    private float _tileResolution = 1f;
    private string _spritesPath = "";
    private Timer? _resizeTimer;
    private float _fpsTimer;
    private int _frameCounter;

    // Server state
    private int _myActorId = -1;
    private int _myCellId = -1;
    private readonly System.Collections.Generic.Dictionary<int, ServerActorData> _serverActors = new();
    private record ServerActorData(Actor Actor, int GfxId, int[] Colors, int[] AccInfo);
    private Proto.MapData? _pendingMapData;

    // Map transition (fade to black and back)
    private ColorRect? _fadeRect;
    private CanvasLayer? _fadeLayer;
    private float _fadePhase = -1f; // <0 = inactive, 0..1 = fade out (black), 1..2 = fade in (reveal)
    private const float FadeOutDuration = 0.12f;
    private const float FadeInDuration = 0.2f;

    // Debug
    private bool _debugTileMode;
    private Label? _debugLabel;
    private static FileAccess? _logFile;

    public override void _Ready()
    {
        InitLog();
        LoadClientConfig();
        _spritesPath = ProjectSettings.GlobalizePath("res://assets/sprites/");
        _tileResolution = ComputeResolution();
        GD.Print($"[GameManager] tile_resolution: {_tileResolution:F2}");

        _interactiveDb = new InteractiveObjectDB();
        _interactiveDb.Load();

        _vello = new VelloRenderer();
        Log("[Init] VelloRenderer created, calling InitGpu...");
        if (!_vello.InitGpu()) { Log("FATAL: InitGpu failed"); return; }
        Log("[Init] GPU initialized OK");

        // Map (empty until server sends MapData)
        _mapRenderer = new MapRenderer { Name = "MapRenderer" };
        AddChild(_mapRenderer);
        _mapRenderer.Init(_vello, _interactiveDb, _tileResolution);

        // Grid overlay (Flash depth 400)
        _gridOverlay = new GridOverlay { Name = "GridOverlay" };
        _mapRenderer.AddChild(_gridOverlay);

        // Interaction
        _interactionHandler = new InteractionHandler { Name = "InteractionHandler" };
        AddChild(_interactionHandler);

        // Pathfinding + strip cache
        _pathfinding = new DofusPathfinding(15, 17, []);
        _stripCache = new StripCache(_vello, _tileResolution);
        _interactionHandler.Picking.SetStripCache(_stripCache);

        // UI
        var uiCanvas = new CanvasLayer { Layer = 200, Name = "UICanvas" };
        AddChild(uiCanvas);
        _zaapPopup = new ZaapPopup { Name = "ZaapPopup" };
        uiCanvas.AddChild(_zaapPopup);
        _interactionHandler.ZaapClicked += OnZaapClicked;
        _zaapPopup.UsePressed += OnZaapUse;
        _interactionHandler.CellClicked += OnCellClicked;
        uiCanvas.AddChild(new UIPlaceholder { Name = "UIBar" });

        // Debug label
        _debugLabel = new Label { Name = "DebugLabel", Position = new Vector2(10, 10), Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _debugLabel.AddThemeFontSizeOverride("font_size", 12);
        _debugLabel.AddThemeColorOverride("font_color", Colors.Yellow);
        _debugLabel.AddThemeColorOverride("font_shadow_color", Colors.Black);
        _debugLabel.AddThemeConstantOverride("shadow_offset_x", 1);
        _debugLabel.AddThemeConstantOverride("shadow_offset_y", 1);
        uiCanvas.AddChild(_debugLabel);

        // Camera
        AddChild(new Camera2D { Name = "Camera2D", Position = new Vector2(DisplayWidth / 2f, DisplayHeight / 2f) });

        // Resize
        _resizeTimer = new Timer { OneShot = true, WaitTime = 0.3 };
        _resizeTimer.Timeout += OnResizeTimeout;
        AddChild(_resizeTimer);
        GetTree().Root.SizeChanged += () => _resizeTimer.Start(0.3);

        // Server
        Log($"connect={ConnectToServer} url={ServerUrl} user={Username}");
        if (ConnectToServer) InitServerConnection();
        else Log("ConnectToServer=false — no server connection");
    }

    // ===================== SERVER CONNECTION =====================

    private void InitServerConnection()
    {
        Log($"[Net] InitServerConnection url={ServerUrl}");
        _gameClient = new GameClient(ServerUrl);
        _gameClient.OnConnected += () => { Log("[Net] Connected, logging in as " + Username); _gameClient.Login(Username); };
        _gameClient.OnDisconnected += () => Log("[Net] Disconnected");
        _gameClient.OnAuthSuccess += auth =>
        {
            Log($"[Net] Auth OK: {auth.Characters.Count} characters");
            if (auth.Characters.Count > 0) _gameClient.SelectCharacter(auth.Characters[0].Id);
        };
        _gameClient.OnCharacterInfo += info =>
        {
            _myActorId = info.Id;
            _myCellId = info.CellId;
            Log($"[Net] Character: {info.Name} actor={info.Id} map={info.MapId} cell={info.CellId} look={info.Look}");
        };
        _gameClient.OnMapActors += actors =>
        {
            Log($"[Net] MapActors: {actors.Actors.Count} actors, pendingMapData={_pendingMapData is not null}");
            // Flush pending map change so actors spawn on the new map
            if (_pendingMapData is not null)
            {
                var mapData = _pendingMapData;
                _pendingMapData = null;
                ChangeMap(mapData);
            }
            foreach (var a in actors.Actors)
            {
                if (_serverActors.ContainsKey(a.Id)) continue;
                var (gfx, colors) = ParseLook(a.Look);
                SpawnServerPlayer(a.Id, a.Name, gfx, a.Look, a.CellId, (int)a.Direction);
                if (a.Id == _myActorId) _myCellId = a.CellId;
            }
        };
        _gameClient.OnActorAdd += a =>
        {
            if (_serverActors.ContainsKey(a.Id)) return;
            var (gfx, _) = ParseLook(a.Look);
            SpawnServerPlayer(a.Id, a.Name, gfx, a.Look, a.CellId, (int)a.Direction);
        };
        _gameClient.OnActorRemove += r =>
        {
            if (_serverActors.Remove(r.Id, out var d)) d.Actor.Sprite.QueueFree();
        };
        _gameClient.OnActorMove += m =>
        {
            if (!_serverActors.TryGetValue(m.Id, out var d)) return;
            var path = new int[m.Path.Count];
            for (int i = 0; i < m.Path.Count; i++) path[i] = m.Path[i];
            d.Actor.MovePath(path, GetCellWorldPos);
            SwitchServerActorAnim(m.Id, path.Length > 6 ? "run" : "walk");
        };
        _gameClient.OnMapData += mapData =>
        {
            Log($"[Net] MapData received: map {mapData.MapId}, {mapData.Cells.Count} cells");
            _pendingMapData = mapData;
        };
        _gameClient.Connect();
    }

    private void SpawnServerPlayer(int actorId, string name, int gfxId, string look, int cellId, int direction)
    {
        Log($"[Spawn] actor={actorId} name={name} gfx={gfxId} cell={cellId} spritesPath={_spritesPath}");
        _vello.LoadSprite(gfxId, _spritesPath);
        var (_, colors) = ParseLook(look);
        int[] accInfo = ParseAccessories(look);

        var actor = new Actor(_mapRenderer.InterleaveLayer, cellId, direction);
        actor.SetPathfinding(_pathfinding);
        var pos = CellGrid.GetCellPosition(cellId, _mapRenderer.MapWidth,
            cellId < _mapRenderer.Cells.Count ? _mapRenderer.Cells[cellId]["groundLevel"].AsInt32() : 7);
        actor.SetWorldPosition(pos);

        string suffix = DirSuffix[direction];
        string animName = "static" + suffix;
        var strip = _stripCache.GetOrRender(gfxId, animName, colors, accInfo);
        if (strip is not null)
        {
            var key = StripCache.MakeKey(gfxId, animName, colors, accInfo);
            actor.Animator.SetStrip(strip, key);
            actor.Animator.ApplyToSprite(actor.Sprite, _tileResolution, actor.Flip);
            actor.PickId = _interactionHandler.Picking.Register(actor.Sprite, "player", name, cellId, stripKey: key);
        }

        _serverActors[actorId] = new ServerActorData(actor, gfxId, colors, accInfo);
        GD.Print($"[Net] Spawned {name} gfx={gfxId} cell={cellId} res={_tileResolution}");
    }

    private void SwitchServerActorAnim(int actorId, string baseAnim)
    {
        if (!_serverActors.TryGetValue(actorId, out var d)) return;
        string animName = baseAnim + DirSuffix[d.Actor.Direction];
        var strip = _stripCache.GetOrRender(d.GfxId, animName, d.Colors, d.AccInfo);
        if (strip is null) return;
        var key = StripCache.MakeKey(d.GfxId, animName, d.Colors, d.AccInfo);
        d.Actor.Animator.SetStrip(strip, key);
        d.Actor.Animator.ApplyToSprite(d.Actor.Sprite, _tileResolution, DirFlip[d.Actor.Direction]);
        if (d.Actor.PickId >= 0) _interactionHandler.Picking.UpdateStripKey(d.Actor.PickId, key);
    }

    // ===================== INPUT =====================

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false } key)
        {
            switch (key.Keycode)
            {
                case Key.G: _gridOverlay?.Toggle(); GetViewport().SetInputAsHandled(); break;
                case Key.F12:
                    var img = GetViewport().GetTexture().GetImage();
                    var path = $"/tmp/godot_capture_{Time.GetTicksMsec()}.png";
                    img.SavePng(path); GD.Print($"[Screenshot] {path}");
                    GetViewport().SetInputAsHandled(); break;
                case Key.Key0: _mapRenderer.ToggleLayer("background"); GetViewport().SetInputAsHandled(); break;
                case Key.Key1: _mapRenderer.ToggleLayer("ground"); GetViewport().SetInputAsHandled(); break;
                case Key.Key2: _mapRenderer.ToggleLayer("object1"); GetViewport().SetInputAsHandled(); break;
                case Key.Key3: _mapRenderer.ToggleLayer("object2"); GetViewport().SetInputAsHandled(); break;
                case Key.D: _debugTileMode = !_debugTileMode; GetViewport().SetInputAsHandled(); break;
            }
        }
    }

    // ===================== PROCESS =====================

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        TickTransition(dt);
        _gameClient?.Poll();
        _actorManager?.Tick(dt);

        // Tick server actors
        foreach (var kv in _serverActors)
        {
            var d = kv.Value;
            d.Actor.TickAnimation(dt, _tileResolution);
            var r = d.Actor.TickMovement(dt, GetCellWorldPos);
            if (r == MovementResult.SegmentComplete)
            {
                GD.Print($"[Move] actor {kv.Key} segment complete, now at cell {d.Actor.CellId}");
                SwitchServerActorAnim(kv.Key, d.Actor.IsRunning ? "run" : "walk");
            }
            else if (r == MovementResult.PathComplete)
            {
                GD.Print($"[Move] actor {kv.Key} path complete at cell {d.Actor.CellId}");
                SwitchServerActorAnim(kv.Key, "static");
                if (kv.Key == _myActorId)
                {
                    _myCellId = d.Actor.CellId;
                    _gameClient?.SendMoveEnd();
                    break; // MoveEnd may trigger map change on next Poll
                }
            }
        }

        // Debug tile info
        if (_debugTileMode && _debugLabel is not null)
        {
            _debugLabel.Visible = true;
            var mousePos = GetViewport().GetMousePosition();
            var cam = GetViewport().GetCamera2D();
            var vpSize = GetViewportRect().Size;
            var worldPos = cam is not null ? cam.Position + (mousePos - vpSize / 2f) / cam.Zoom : mousePos;
            int cellId = CellGrid.FindCellAtPosition(worldPos, _mapRenderer.Cells, _mapRenderer.MapWidth);
            if (cellId >= 0 && cellId < _mapRenderer.Cells.Count)
            {
                var c = _mapRenderer.Cells[cellId];
                int g = c.ContainsKey("ground") ? c["ground"].AsInt32() : 0;
                int l1 = c.ContainsKey("layer1") ? c["layer1"].AsInt32() : 0;
                int l2 = c.ContainsKey("layer2") ? c["layer2"].AsInt32() : 0;
                var cellPos = CellGrid.GetCellPosition(cellId, _mapRenderer.MapWidth,
                    c.ContainsKey("groundLevel") ? c["groundLevel"].AsInt32() : 7);
                _debugLabel.Text = $"Cell:{cellId} G:{g} L1:{l1} L2:{l2}\nWorld:({worldPos.X:F0},{worldPos.Y:F0}) CellPos:({cellPos.X:F0},{cellPos.Y:F0})\nMyCell:{_myCellId}";
            }
            else _debugLabel.Text = $"No cell ({worldPos.X:F0},{worldPos.Y:F0})";
        }
        else if (_debugLabel is not null) _debugLabel.Visible = false;

        // Deferred map change (safe — outside actor iteration)
        if (_pendingMapData is not null)
        {
            var mapData = _pendingMapData;
            _pendingMapData = null;
            ChangeMap(mapData);
        }

        _fpsTimer += dt;
        _frameCounter++;
        if (_fpsTimer >= 1f)
        {
            GD.Print($"[FPS] {_frameCounter / _fpsTimer:F1} | Actors:{_actorManager?.Count ?? 0}+{_serverActors.Count} | Strips:{_stripCache?.Count ?? 0}");
            _fpsTimer = 0; _frameCounter = 0;
        }
    }

    // ===================== EVENTS =====================

    private void OnZaapClicked(int cellId, Vector2 screenPos) => _zaapPopup?.ShowAt(screenPos, cellId);
    private void OnZaapUse(int cellId) => GD.Print($"[Zaap] Use at cell {cellId}");

    private void ChangeMap(Proto.MapData mapData)
    {
        Log($"[MapChange] {MapId} → {mapData.MapId} ({mapData.Cells.Count} cells)");

        // Fade transition
        StartFadeTransition();

        // Clear server actors
        foreach (var kv in _serverActors)
            kv.Value.Actor.Sprite.QueueFree();
        _serverActors.Clear();

        // Clear picking
        _interactionHandler.Picking.Clear();

        // Load from server proto data + render
        MapId = mapData.MapId;
        _mapRenderer.LoadMapFromProto(mapData);
        Log("[MapChange] LoadMapFromProto done, calling RenderMap...");
        _mapRenderer.RenderMap();
        Log($"[MapChange] RenderMap done, tiles={_mapRenderer.InteractiveTiles.Count}");

        // Rebuild pathfinding
        var walkableCells = GetWalkableCells();
        int mapHeight = _mapRenderer.Cells.Count > 0 ? (_mapRenderer.Cells.Count / _mapRenderer.MapWidth + 1) : 17;
        _pathfinding = new DofusPathfinding(_mapRenderer.MapWidth, mapHeight, walkableCells);

        // Update grid overlay
        _gridOverlay?.SetMapData(_mapRenderer.Cells, _mapRenderer.MapWidth);

        // Update interaction handler
        _interactionHandler.SetMapData(_mapRenderer.Cells, _mapRenderer.MapWidth);
        RegisterInteractiveTiles();

        GD.Print($"[MapChange] Loaded map {mapData.MapId}: {_mapRenderer.Cells.Count} cells, {walkableCells.Length} walkable");
    }

    private void EnsureFadeLayer()
    {
        if (_fadeLayer is not null) return;
        _fadeLayer = new CanvasLayer { Layer = 100, Name = "FadeLayer" };
        AddChild(_fadeLayer);
        _fadeRect = new ColorRect
        {
            Color = new Color(0f, 0f, 0f, 0f),
            AnchorRight = 1f,
            AnchorBottom = 1f,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _fadeLayer.AddChild(_fadeRect);
    }

    private void StartFadeTransition()
    {
        if (_mapRenderer.Cells.Count == 0) return;
        EnsureFadeLayer();
        _fadePhase = 1f; // skip fade-out, go straight to full black then fade in
        _fadeRect!.Color = new Color(0f, 0f, 0f, 1f);
    }

    private void TickTransition(float dt)
    {
        if (_fadePhase < 0f || _fadeRect is null) return;

        _fadePhase += dt / FadeInDuration;
        float alpha = Mathf.Clamp(1f - (_fadePhase - 1f), 0f, 1f);

        if (_fadePhase >= 2f)
        {
            _fadeRect.Color = new Color(0f, 0f, 0f, 0f);
            _fadePhase = -1f;
        }
        else
        {
            _fadeRect.Color = new Color(0f, 0f, 0f, alpha);
        }
    }

    private void OnCellClicked(int cellId)
    {
        GD.Print($"[Click] cell={cellId} from={_myCellId}");
        if (_gameClient is { IsConnected: true } && _myActorId > 0)
        {
            var path = _pathfinding.FindPath(_myCellId, cellId);
            if (path is { Length: >= 2 })
            {
                GD.Print($"[Click] path: [{string.Join(",", path)}]");
                _gameClient.SendMove(path);
                // Don't update _myCellId here — wait for ActorMove from server
            }
            else
            {
                GD.Print($"[Click] no path found from {_myCellId} to {cellId}");
            }
        }
    }

    // ===================== RESIZE =====================

    private void OnResizeTimeout()
    {
        float newRes = ComputeResolution();
        if (Mathf.Abs(newRes - _tileResolution) / Mathf.Max(_tileResolution, 0.01f) < 0.05f) return;
        _tileResolution = newRes;
        _mapRenderer.Rerender(_tileResolution);
        RegisterInteractiveTiles();
        _stripCache.ClearAndUpdateResolution(_tileResolution);
        _actorManager?.OnResolutionChanged(_tileResolution);
        foreach (var kv in _serverActors) SwitchServerActorAnim(kv.Key, "static");
    }

    // ===================== HELPERS =====================

    private static void InitLog()
    {
        var logPath = OS.GetExecutablePath().GetBaseDir() + "/game.log";
        _logFile = FileAccess.Open(logPath, FileAccess.ModeFlags.Write);
        if (_logFile is null)
        {
            // Fallback to user://
            _logFile = FileAccess.Open("user://game.log", FileAccess.ModeFlags.Write);
        }
        Log("=== Game started ===");
        Log($"exe={OS.GetExecutablePath()}");
    }

    public static void Log(string msg)
    {
        var line = $"[{Time.GetTicksMsec():D8}] {msg}";
        GD.Print(line);
        _logFile?.StoreLine(line);
        _logFile?.Flush();
    }

    private void LoadClientConfig()
    {
        var exeDir = OS.GetExecutablePath().GetBaseDir();
        var exePath = exeDir.PathJoin("client.cfg");
        var candidates = new[] { exePath, "user://client.cfg", "res://client.cfg" };
        Log($"Looking for client.cfg in: {string.Join(", ", candidates)}");

        string? path = null;
        foreach (var p in candidates)
        {
            bool exists = FileAccess.FileExists(p);
            Log($"  {p} → {(exists ? "FOUND" : "not found")}");
            if (exists && path is null) path = p;
        }

        if (path is null)
        {
            Log("No client.cfg found, using defaults");
            return;
        }

        var cfg = new ConfigFile();
        if (cfg.Load(path) != Error.Ok) { Log($"Failed to load {path}"); return; }

        ServerUrl = (string)cfg.GetValue("server", "url", ServerUrl);
        Username = (string)cfg.GetValue("server", "username", Username);
        ConnectToServer = (bool)cfg.GetValue("client", "connect", ConnectToServer);

        Log($"Config loaded from {path}: server={ServerUrl} user={Username} connect={ConnectToServer}");
    }

    private void RegisterInteractiveTiles()
    {
        foreach (var tile in _mapRenderer.InteractiveTiles)
            _interactionHandler.Picking.Register(tile.Sprite, "interactive_tile", tile.Name, tile.CellId, tile.ObjectType,
                alphaMap: tile.AlphaMap, alphaWidth: tile.AlphaWidth, alphaHeight: tile.AlphaHeight);
    }

    private Vector2 GetCellWorldPos(int cellId)
    {
        int gl = cellId < _mapRenderer.Cells.Count && _mapRenderer.Cells[cellId].ContainsKey("groundLevel")
            ? _mapRenderer.Cells[cellId]["groundLevel"].AsInt32() : 7;
        return CellGrid.GetCellPosition(cellId, _mapRenderer.MapWidth, gl);
    }

    private int[] ParseAccessories(string look)
    {
        if (string.IsNullOrEmpty(look)) return [];
        var parts = look.Split('|');
        if (parts.Length < 5 || string.IsNullOrEmpty(parts[4])) return [];
        var slots = parts[4].Split(',');
        var result = new System.Collections.Generic.List<int>();
        for (int i = 0; i < slots.Length && i < 5; i++)
        {
            if (string.IsNullOrEmpty(slots[i].Trim())) continue;
            var aid = _vello.LoadAccessory(slots[i].Trim(), _spritesPath);
            if (aid.HasValue) { result.Add((int)aid.Value); result.Add(i); }
        }
        return result.ToArray();
    }

    private static (int gfxId, int[] colors) ParseLook(string look)
    {
        if (string.IsNullOrEmpty(look)) return (10, [0, 0, 0]);
        var p = look.Split('|');
        return (
            p.Length > 0 && int.TryParse(p[0], out int g) ? g : 10,
            [p.Length > 1 && int.TryParse(p[1], out int c1) ? c1 : 0,
             p.Length > 2 && int.TryParse(p[2], out int c2) ? c2 : 0,
             p.Length > 3 && int.TryParse(p[3], out int c3) ? c3 : 0]
        );
    }

    private int[] GetWalkableCells()
    {
        var list = new System.Collections.Generic.List<int>();
        foreach (var cell in _mapRenderer.Cells)
        {
            if ((cell.ContainsKey("mov") && cell["mov"].AsInt32() == 1) ||
                (cell.ContainsKey("walkable") && cell["walkable"].AsBool()))
                list.Add(cell["id"].AsInt32());
        }
        if (list.Count == 0) foreach (var cell in _mapRenderer.Cells) list.Add(cell["id"].AsInt32());
        return list.ToArray();
    }

    private static float ComputeResolution()
    {
        var winSize = DisplayServer.WindowGetSize();
        return Mathf.Clamp(Mathf.Min((float)winSize.X / DisplayWidth, (float)winSize.Y / DisplayHeight), 1f, 4f);
    }
}
