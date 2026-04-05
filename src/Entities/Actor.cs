using System;
using Godot;
using DofusRetroFuture.Core;
using DofusRetroFuture.Rendering;

namespace DofusRetroFuture.Entities;

public class Actor
{
    // Walk/run speeds in px/ms per direction (from original Dofus SWF)
    private static readonly float[] WalkSpeeds = [0.07f, 0.06f, 0.06f, 0.06f, 0.07f, 0.06f, 0.06f, 0.06f];
    private static readonly float[] RunSpeeds = [0.17f, 0.15f, 0.15f, 0.15f, 0.17f, 0.15f, 0.15f, 0.15f];
    private const int RunThreshold = 6;
    private const float MaxDeltaMs = 125f;

    public Sprite2D Sprite { get; }
    public SpriteAnimator Animator { get; } = new();
    public int CellId { get; set; }
    public int Direction { get; set; }
    public bool Flip => Constants.DirFlip[Direction];
    public string AnimSuffix => Constants.DirSuffix[Direction];

    // Path-based movement state
    public bool Moving { get; private set; }
    public int[]? Path { get; private set; }
    private int _pathIndex;
    private float _moveDistance;
    private float _moveCosRot;
    private float _moveSinRot;
    private float _movePixelSpeed;
    private bool _useRun;
    private Vector2 _segmentFrom;
    private Vector2 _segmentTo;
    private float _segmentTraveled;

    // Idle timer (stress test: random movement)
    public float MoveTimer { get; set; }

    // Picking registration ID
    public int PickId { get; set; } = -1;

    // Pathfinding reference (for direction calculation)
    private DofusPathfinding? _pathfinding;

    public Actor(Node2D parent, int cellId, int direction)
    {
        Sprite = new Sprite2D
        {
            Centered = false,
            TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
            ZIndex = 800 + cellId + 1
        };
        parent.AddChild(Sprite);
        CellId = cellId;
        Direction = direction;
    }

    public void SetPathfinding(DofusPathfinding pf) => _pathfinding = pf;

    public void SetWorldPosition(Vector2 pos)
    {
        Sprite.Position = pos;
    }

    public void UpdateZIndex()
    {
        Sprite.ZIndex = 800 + CellId + 1;
    }

    /// <summary>
    /// Start moving along a path (array of cell IDs).
    /// </summary>
    public void MovePath(int[] path, Func<int, Vector2> getCellWorldPos)
    {
        if (path.Length < 2) return;
        Path = path;
        _pathIndex = 0;
        _useRun = path.Length > RunThreshold;
        Moving = true;
        StartSegment(getCellWorldPos);
    }

    private void StartSegment(Func<int, Vector2> getCellWorldPos)
    {
        if (Path is null || _pathIndex >= Path.Length - 1)
        {
            CompleteMovement();
            return;
        }

        int fromCell = Path[_pathIndex];
        int toCell = Path[_pathIndex + 1];

        // Compute direction
        if (_pathfinding is not null)
            Direction = _pathfinding.GetDirection(fromCell, toCell);
        else
            Direction = CellGrid.ComputeDirection(
                CellGrid.CellToGrid(toCell, 15).X - CellGrid.CellToGrid(fromCell, 15).X,
                CellGrid.CellToGrid(toCell, 15).Y - CellGrid.CellToGrid(fromCell, 15).Y);

        _segmentFrom = getCellWorldPos(fromCell);
        _segmentTo = getCellWorldPos(toCell);

        float dx = _segmentTo.X - _segmentFrom.X;
        float dy = _segmentTo.Y - _segmentFrom.Y;
        _moveDistance = MathF.Sqrt(dx * dx + dy * dy);

        if (_moveDistance > 0)
        {
            float angle = MathF.Atan2(dy, dx);
            _moveCosRot = MathF.Cos(angle);
            _moveSinRot = MathF.Sin(angle);
        }

        _movePixelSpeed = _useRun ? RunSpeeds[Direction] : WalkSpeeds[Direction];
        _segmentTraveled = 0;

        // Z-index depth rule: forward = update immediately, backward = update on arrival
        if (toCell > CellId)
            Sprite.ZIndex = 800 + toCell + 1;
    }

    /// <summary>
    /// Tick movement. deltaMs is in milliseconds. Returns the action to take.
    /// </summary>
    public MovementResult TickMovement(float deltaSec, Func<int, Vector2> getCellWorldPos)
    {
        if (!Moving || Path is null) return MovementResult.Idle;

        float deltaMs = MathF.Min(deltaSec * 1000f, MaxDeltaMs);
        float deltaPx = _movePixelSpeed * deltaMs;
        _segmentTraveled += deltaPx;

        if (_segmentTraveled >= _moveDistance)
        {
            // Snap to segment end
            Sprite.Position = _segmentTo;
            CellId = Path[_pathIndex + 1];
            UpdateZIndex();
            _pathIndex++;

            if (_pathIndex >= Path.Length - 1)
            {
                CompleteMovement();
                return MovementResult.PathComplete;
            }

            // Start next segment
            StartSegment(getCellWorldPos);
            return MovementResult.SegmentComplete;
        }

        // Interpolate position
        Sprite.Position = new Vector2(
            _segmentFrom.X + _segmentTraveled * _moveCosRot,
            _segmentFrom.Y + _segmentTraveled * _moveSinRot);

        return MovementResult.Moving;
    }

    private void CompleteMovement()
    {
        Moving = false;
        Path = null;
    }

    public void TickAnimation(float delta, float tileResolution)
    {
        if (Animator.Tick(delta))
            Animator.ApplyToSprite(Sprite, tileResolution, Flip);
    }

    public bool IsRunning => _useRun;
}

public enum MovementResult
{
    Idle,
    Moving,
    SegmentComplete,
    PathComplete
}
