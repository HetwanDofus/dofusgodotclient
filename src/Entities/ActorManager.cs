using System.Collections.Generic;
using Godot;
using Godot.Collections;
using DofusRetroFuture.Core;
using DofusRetroFuture.Rendering;
using DofusRetroFuture.Interaction;
using static DofusRetroFuture.Core.Constants;

namespace DofusRetroFuture.Entities;

/// <summary>
/// Manages all actors: spawning, animation updates, movement.
/// </summary>
public class ActorManager
{
    private readonly List<PlayerActor> _actors = new();
    private readonly Node2D _parent;
    private readonly VelloRenderer _vello;
    private readonly StripCache _stripCache;
    private readonly PickingSystem? _pickingSystem;
    private float _tileResolution;
    private readonly int _mapWidth;
    private readonly Array<Godot.Collections.Dictionary> _cells;
    private readonly int[] _walkableCells;
    private readonly string _spritesPath;
    private DofusPathfinding? _pathfinding;

    // Lazy render queue
    private readonly Queue<RenderRequest> _renderQueue = new();
    private const int StripsPerFrame = 10;
    private const float MoveIntervalMin = 1.5f;
    private const float MoveIntervalMax = 4f;

    public int Count => _actors.Count;
    public int StripCount => _stripCache.Count;
    public int QueueCount => _renderQueue.Count;

    private record RenderRequest(int ActorIndex, string AnimName, bool Flip);

    public ActorManager(
        Node2D parent, VelloRenderer vello, StripCache stripCache,
        PickingSystem? pickingSystem, float tileResolution,
        int mapWidth, Array<Godot.Collections.Dictionary> cells,
        int[] walkableCells, string spritesPath)
    {
        _parent = parent;
        _vello = vello;
        _stripCache = stripCache;
        _pickingSystem = pickingSystem;
        _tileResolution = tileResolution;
        _mapWidth = mapWidth;
        _cells = cells;
        _walkableCells = walkableCells;
        _spritesPath = spritesPath;
    }

    public void SetPathfinding(DofusPathfinding pf) => _pathfinding = pf;

    private Vector2 GetCellWorldPos(int cellId)
    {
        if (cellId < _cells.Count)
        {
            var cd = _cells[cellId];
            int gl = cd.ContainsKey("groundLevel") ? cd["groundLevel"].AsInt32() : 7;
            return CellGrid.GetCellPosition(cellId, _mapWidth, gl) + new Vector2(CellHalfWidth, CellHalfHeight);
        }
        return CellGrid.GetCellPosition(cellId, _mapWidth, 7) + new Vector2(CellHalfWidth, CellHalfHeight);
    }

    public void OnResolutionChanged(float newResolution)
    {
        _tileResolution = newResolution;
        // Queue all actors for re-render with new resolution
        for (int i = 0; i < _actors.Count; i++)
        {
            var actor = _actors[i];
            string suffix = DirSuffix[actor.Direction];
            string baseAnim = actor.Moving ? "walk" : "static";
            string animName = baseAnim + suffix;
            bool flip = DirFlip[actor.Direction];
            _renderQueue.Enqueue(new RenderRequest(i, animName, flip));
        }
    }

    public void SpawnActors(int count)
    {
        var rng = new RandomNumberGenerator();
        rng.Randomize();

        for (int i = 0; i < count; i++)
        {
            int gfxId = GfxPool[rng.RandiRange(0, GfxPool.Length - 1)];
            int direction = rng.RandiRange(0, 7);
            int cellIdx = rng.RandiRange(0, _walkableCells.Length - 1);
            int cellId = _walkableCells[cellIdx];

            int[] colors = [(int)(rng.Randi() & 0xFFFFFF), (int)(rng.Randi() & 0xFFFFFF), (int)(rng.Randi() & 0xFFFFFF)];
            int[] accInfo = BuildRandomAccInfo(rng);
            string name = PlayerNames[rng.RandiRange(0, PlayerNames.Length - 1)] + $"-{i}";

            // Ensure sprite GFX is loaded
            _vello.LoadSprite(gfxId, _spritesPath);

            var actor = new PlayerActor(_parent, cellId, direction, name, gfxId, colors, accInfo);
            if (_pathfinding is not null) actor.SetPathfinding(_pathfinding);

            // Position
            var cellData = _cells[cellId];
            int groundLevel = cellData.ContainsKey("groundLevel") ? cellData["groundLevel"].AsInt32() : 7;
            var pos = CellGrid.GetCellPosition(cellId, _mapWidth, groundLevel);
            actor.SetWorldPosition(pos);

            // Render initial animation
            string suffix = DirSuffix[direction];
            string animName = "walk" + suffix;
            var strip = _stripCache.GetOrRender(gfxId, animName, colors, accInfo);
            if (strip is null)
            {
                animName = "static" + suffix;
                strip = _stripCache.GetOrRender(gfxId, animName, colors, accInfo);
            }
            if (strip is null) continue;

            var key = StripCache.MakeKey(gfxId, animName, colors, accInfo);
            actor.Animator.SetStrip(strip, key);
            actor.Animator.SetRandomOffset(10f);
            actor.Animator.ApplyToSprite(actor.Sprite, _tileResolution, actor.Flip);

            // Movement timer
            actor.MoveTimer = rng.RandfRange(MoveIntervalMin, MoveIntervalMax);

            // Register with picking system (include strip key for alpha picking)
            if (_pickingSystem is not null)
            {
                actor.PickId = _pickingSystem.Register(actor.Sprite, "player", name, cellId, stripKey: key);
            }

            _actors.Add(actor);
        }
    }

    public void Tick(float delta)
    {
        // Process lazy render queue
        int rendered = 0;
        while (_renderQueue.Count > 0 && rendered < StripsPerFrame)
        {
            var req = _renderQueue.Dequeue();
            if (req.ActorIndex >= _actors.Count) continue;
            var actor = _actors[req.ActorIndex];
            var strip = _stripCache.GetOrRender(actor.GfxId, req.AnimName, actor.Colors, actor.AccInfo);
            if (strip is not null)
            {
                var key = StripCache.MakeKey(actor.GfxId, req.AnimName, actor.Colors, actor.AccInfo);
                actor.Animator.SetStrip(strip, key);
                actor.Animator.ApplyToSprite(actor.Sprite, _tileResolution, req.Flip);
                if (_pickingSystem is not null && actor.PickId >= 0)
                    _pickingSystem.UpdateStripKey(actor.PickId, key);
                rendered++;
            }
        }

        // Update all actors
        var rng = new RandomNumberGenerator();
        for (int i = 0; i < _actors.Count; i++)
        {
            var actor = _actors[i];
            actor.TickAnimation(delta, _tileResolution);

            if (actor.Moving)
            {
                var result = actor.TickMovement(delta, GetCellWorldPos);
                if (result == MovementResult.SegmentComplete)
                {
                    // Update animation direction for new segment
                    SwitchAnimation(i, actor.IsRunning ? "run" : "walk");
                }
                else if (result == MovementResult.PathComplete)
                {
                    actor.MoveTimer = rng.RandfRange(MoveIntervalMin, MoveIntervalMax);
                    SwitchAnimation(i, "static");
                }
            }
            else
            {
                actor.MoveTimer -= delta;
                if (actor.MoveTimer <= 0)
                    StartRandomMove(i, rng);
            }
        }
    }

    private void SwitchAnimation(int actorIdx, string baseAnim)
    {
        var actor = _actors[actorIdx];
        string suffix = DirSuffix[actor.Direction];
        string animName = baseAnim + suffix;
        bool flip = DirFlip[actor.Direction];

        var key = StripCache.MakeKey(actor.GfxId, animName, actor.Colors, actor.AccInfo);
        var cached = _stripCache.Get(key);
        if (cached is not null)
        {
            actor.Animator.SetStrip(cached, key);
            actor.Animator.ApplyToSprite(actor.Sprite, _tileResolution, flip);
            if (_pickingSystem is not null && actor.PickId >= 0)
                _pickingSystem.UpdateStripKey(actor.PickId, key);
            return;
        }

        // Queue for lazy rendering
        _renderQueue.Enqueue(new RenderRequest(actorIdx, animName, flip));
    }

    private void StartRandomMove(int actorIdx, RandomNumberGenerator rng)
    {
        var actor = _actors[actorIdx];
        int targetCell = _walkableCells[rng.RandiRange(0, _walkableCells.Length - 1)];
        if (targetCell == actor.CellId)
        {
            actor.MoveTimer = rng.RandfRange(0.5f, 1f);
            return;
        }

        // Use pathfinding if available
        int[]? path = _pathfinding?.FindPath(actor.CellId, targetCell);
        if (path is null || path.Length < 2)
        {
            actor.MoveTimer = rng.RandfRange(0.5f, 1f);
            return;
        }

        // Truncate long paths (3-8 steps like Pixi stress test)
        int maxSteps = rng.RandiRange(3, 8);
        if (path.Length > maxSteps + 1)
        {
            var truncated = new int[maxSteps + 1];
            System.Array.Copy(path, truncated, maxSteps + 1);
            path = truncated;
        }

        actor.MovePath(path, GetCellWorldPos);
        string baseAnim = path.Length > 6 ? "run" : "walk";
        SwitchAnimation(actorIdx, baseAnim);
    }

    private int[] BuildRandomAccInfo(RandomNumberGenerator rng)
    {
        var list = new List<int>();
        if (rng.Randf() < 0.7f && HatPool.Length > 0)
        {
            string key = HatPool[rng.RandiRange(0, HatPool.Length - 1)];
            var aid = _vello.LoadAccessory(key, _spritesPath);
            if (aid.HasValue) { list.Add((int)aid.Value); list.Add(1); }
        }
        if (rng.Randf() < 0.7f && CapePool.Length > 0)
        {
            string key = CapePool[rng.RandiRange(0, CapePool.Length - 1)];
            var aid = _vello.LoadAccessory(key, _spritesPath);
            if (aid.HasValue) { list.Add((int)aid.Value); list.Add(2); }
        }
        if (rng.Randf() < 0.7f && ShieldPool.Length > 0)
        {
            string key = ShieldPool[rng.RandiRange(0, ShieldPool.Length - 1)];
            var aid = _vello.LoadAccessory(key, _spritesPath);
            if (aid.HasValue) { list.Add((int)aid.Value); list.Add(4); }
        }
        return list.ToArray();
    }
}
