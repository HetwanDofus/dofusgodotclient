using System.Collections.Generic;
using Godot;
using Godot.Collections;
using DofusRetroFuture.Core;

namespace DofusRetroFuture.Rendering;

/// <summary>
/// 1:1 port of ank.battlefield.GridHandler from GridHandler.as / grid-overlay.ts.
/// Draws 3-point segments (left→top→right) for walkable cells.
/// </summary>
public partial class GridOverlay : Node2D
{
    private static readonly Color GridColor = new(1f, 1f, 1f, 0.3f);

    // CELL_COORD from Constants.as line 18 — 4 corners [left, top, right, bottom] as [dx, dy]
    // Index = groundSlope (1-15). Offsets relative to cell pixel position.
    private static readonly float[][][] CellCoord = [
        [], // 0: unused
        [[-26.5f,0f],[0f,-13.5f],[26.5f,0f],[0f,13.5f]],           // 1: flat
        [[-26.5f,-20f],[0f,-13.5f],[26.5f,0f],[0f,13.5f]],         // 2
        [[-26.5f,0f],[0f,-33.5f],[26.5f,0f],[0f,13.5f]],           // 3
        [[-26.5f,-20f],[0f,-33.5f],[26.5f,0f],[0f,13.5f]],         // 4
        [[-26.5f,0f],[0f,-13.5f],[26.5f,-20f],[0f,13.5f]],         // 5
        [[-26.5f,-20f],[0f,-13.5f],[26.5f,-20f],[0f,13.5f]],       // 6
        [[-26.5f,0f],[0f,-33.5f],[26.5f,-20f],[0f,13.5f]],         // 7
        [[-26.5f,-20f],[0f,-33.5f],[26.5f,-20f],[0f,13.5f]],       // 8
        [[-26.5f,0f],[0f,-13.5f],[26.5f,0f],[0f,-6.5f]],           // 9
        [[-26.5f,-20f],[0f,-13.5f],[26.5f,0f],[0f,-6.5f]],         // 10
        [[-26.5f,0f],[0f,-33.5f],[26.5f,0f],[0f,-6.5f]],           // 11
        [[-26.5f,-20f],[0f,-33.5f],[26.5f,0f],[0f,-6.5f]],         // 12
        [[-26.5f,0f],[0f,-13.5f],[26.5f,-20f],[0f,-6.5f]],         // 13
        [[-26.5f,-20f],[0f,-13.5f],[26.5f,-20f],[0f,-6.5f]],       // 14
        [[-26.5f,0f],[0f,-33.5f],[26.5f,-20f],[0f,-6.5f]],         // 15
    ];

    private Array<Godot.Collections.Dictionary>? _cells;
    private int _mapWidth;
    private bool _visible;

    public void SetMapData(Array<Godot.Collections.Dictionary> cells, int mapWidth)
    {
        _cells = cells;
        _mapWidth = mapWidth;
        QueueRedraw();
    }

    public void Toggle()
    {
        _visible = !_visible;
        Visible = _visible;
        QueueRedraw();
    }

    public override void _Ready()
    {
        Visible = false;
        ZAsRelative = false;
        ZIndex = 400; // Flash depth: Ground=200, Object1=300, Grid=400, Object2=800
    }

    public override void _Draw()
    {
        if (!_visible || _cells is null) return;

        // Build non-grid cell set for Pass 2
        var nonGridCells = new System.Collections.Generic.Dictionary<int, Godot.Collections.Dictionary>();
        var cellById = new System.Collections.Generic.Dictionary<int, Godot.Collections.Dictionary>();

        // Pass 1: draw 3-point segments for walkable cells
        foreach (var cell in _cells)
        {
            int cellId = cell["id"].AsInt32();
            cellById[cellId] = cell;

            bool active = !cell.ContainsKey("active") || cell["active"].AsBool();
            if (!active) continue;

            int movement = cell.ContainsKey("movement") ? cell["movement"].AsInt32() : 0;
            bool los = !cell.ContainsKey("lineOfSight") || cell["lineOfSight"].AsBool();
            int groundLevel = cell.ContainsKey("groundLevel") ? cell["groundLevel"].AsInt32() : 7;
            int slope = cell.ContainsKey("groundSlope") ? cell["groundSlope"].AsInt32() : 1;

            var pos = CellGrid.GetCellPosition(cellId, _mapWidth, groundLevel);

            if (slope < 1 || slope > 15) slope = 1;
            var coords = CellCoord[slope];
            if (coords.Length < 3) continue;

            if (movement != 0 && los)
            {
                // Draw left → top → right (3 corners, 2 line segments)
                var left = new Vector2(coords[0][0] + pos.X, coords[0][1] + pos.Y);
                var top = new Vector2(coords[1][0] + pos.X, coords[1][1] + pos.Y);
                var right = new Vector2(coords[2][0] + pos.X, coords[2][1] + pos.Y);

                DrawLine(left, top, GridColor, 1f);
                DrawLine(top, right, GridColor, 1f);
            }
            else
            {
                nonGridCells[cellId] = cell;
            }
        }

        // Pass 2: border edges for non-grid cells adjacent to grid cells
        int[] neighborOffsets = [-_mapWidth, -(_mapWidth - 1)];

        foreach (var kv in nonGridCells)
        {
            var cell = kv.Value;
            int cellId = cell["id"].AsInt32();
            int groundLevel = cell.ContainsKey("groundLevel") ? cell["groundLevel"].AsInt32() : 7;
            int slope = cell.ContainsKey("groundSlope") ? cell["groundSlope"].AsInt32() : 1;

            var pos = CellGrid.GetCellPosition(cellId, _mapWidth, groundLevel);

            if (slope < 1 || slope > 15) slope = 1;
            var coords = CellCoord[slope];
            if (coords.Length < 4) continue;

            for (int i = 0; i < 2; i++)
            {
                int neighborId = cellId + neighborOffsets[i];
                if (nonGridCells.ContainsKey(neighborId)) continue;

                if (cellById.TryGetValue(neighborId, out var neighbor))
                {
                    bool neighborActive = !neighbor.ContainsKey("active") || neighbor["active"].AsBool();
                    if (!neighborActive) continue;

                    int nextCorner = (i + 1) % 4;
                    var from = new Vector2(coords[i][0] + pos.X, coords[i][1] + pos.Y);
                    var to = new Vector2(coords[nextCorner][0] + pos.X, coords[nextCorner][1] + pos.Y);
                    DrawLine(from, to, GridColor, 1f);
                }
            }
        }
    }
}
