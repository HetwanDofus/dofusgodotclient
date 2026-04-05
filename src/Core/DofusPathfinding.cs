using System;
using System.Collections.Generic;

namespace DofusRetroFuture.Core;

/// <summary>
/// A* pathfinding on the Dofus isometric grid.
/// Ported from dofuswebclient2/packages/grid/src/pathfinding.ts.
/// </summary>
public class DofusPathfinding
{
	private const int MaxPathLength = 500;
	private static readonly float[] DirCosts = [1.5f, 1f, 1.5f, 1f, 1.5f, 1f, 1.5f, 1f];
	private const float DirChangePenalty = 0.5f;

	private readonly int _mapWidth;
	private readonly int _totalRows;
	private readonly int[] _dirOffsets;
	private readonly HashSet<int> _walkableSet;
	private readonly HashSet<int> _occupiedCells = new();

	public int MapWidth => _mapWidth;

	public DofusPathfinding(int mapWidth, int mapHeight, IEnumerable<int> walkableCellIds)
	{
		_mapWidth = mapWidth;
		_totalRows = 2 * mapHeight - 1;
		int stride = 2 * mapWidth - 1;
		_dirOffsets = [
			1,              // 0: EAST
			mapWidth,       // 1: SOUTH_EAST
			stride,         // 2: SOUTH
			mapWidth - 1,   // 3: SOUTH_WEST
			-1,             // 4: WEST
			-mapWidth,      // 5: NORTH_WEST
			-stride,        // 6: NORTH
			-(mapWidth - 1) // 7: NORTH_EAST
		];
		_walkableSet = new HashSet<int>(walkableCellIds);
	}

	public void AddOccupied(int cellId) => _occupiedCells.Add(cellId);
	public void RemoveOccupied(int cellId) => _occupiedCells.Remove(cellId);
	public bool IsWalkable(int cellId) => _walkableSet.Contains(cellId);

	public int[]? FindPath(int startId, int goalId)
	{
		if (!_walkableSet.Contains(startId) || !_walkableSet.Contains(goalId))
			return null;
		if (startId == goalId) return [startId];

		var openSet = new Dictionary<int, PathNode>();
		var closedSet = new Dictionary<int, float>();

		float h0 = Heuristic(startId, goalId);
		openSet[startId] = new PathNode(startId, 0, 0, h0, h0, -1, null);

		while (openSet.Count > 0)
		{
			// Find lowest f-score
			PathNode? current = null;
			float lowestF = float.PositiveInfinity;
			foreach (var node in openSet.Values)
			{
				if (node.F < lowestF)
				{
					lowestF = node.F;
					current = node;
				}
			}
			if (current is null) break;

			if (current.CellId == goalId)
				return ReconstructPath(current);

			openSet.Remove(current.CellId);
			closedSet[current.CellId] = current.V;

			var (row, col, isLong) = CellToRowCol(current.CellId);

			for (int dir = 0; dir < 8; dir++)
			{
				if (!IsValidDirection(row, col, isLong, dir)) continue;

				int neighborId = current.CellId + _dirOffsets[dir];
				if (!_walkableSet.Contains(neighborId)) continue;
				if (neighborId != goalId && _occupiedCells.Contains(neighborId)) continue;

				float moveCost = DirCosts[dir];
				float dirChangeCost = current.D >= 0 && dir != current.D ? DirChangePenalty : 0;
				float tentativeG = current.G + moveCost;
				float tentativeV = current.V + moveCost + dirChangeCost;

				float? existingV = null;
				if (openSet.TryGetValue(neighborId, out var openNode))
					existingV = openNode.V;
				else if (closedSet.TryGetValue(neighborId, out float closedV))
					existingV = closedV;

				if ((existingV is null || existingV.Value > tentativeV) && tentativeG <= MaxPathLength)
				{
					closedSet.Remove(neighborId);
					float h = Heuristic(neighborId, goalId);
					openSet[neighborId] = new PathNode(neighborId, tentativeG, tentativeV, h, tentativeV + h, dir, current);
				}
			}
		}

		return null;
	}

	/// <summary>
	/// Get direction (0-7) between two adjacent cells.
	/// </summary>
	public int GetDirection(int fromId, int toId)
	{
		int diff = toId - fromId;
		for (int dir = 7; dir >= 0; dir--)
		{
			if (_dirOffsets[dir] == diff) return dir;
		}

		var from = CellToCoord(fromId);
		var to = CellToCoord(toId);
		int dx = to.X - from.X;
		int dy = to.Y - from.Y;

		if (dx == 0) return dy > 0 ? 3 : 7;
		return dx > 0 ? 1 : 5;
	}

	/// <summary>
	/// Get valid neighbor cell IDs.
	/// </summary>
	public int[] GetNeighbors(int cellId)
	{
		var (row, col, isLong) = CellToRowCol(cellId);
		var neighbors = new List<int>(8);
		for (int dir = 0; dir < 8; dir++)
		{
			if (IsValidDirection(row, col, isLong, dir))
				neighbors.Add(cellId + _dirOffsets[dir]);
		}
		return neighbors.ToArray();
	}

	/// <summary>
	/// Validate that a path is walkable and connected.
	/// </summary>
	public bool ValidatePath(int[] path, int currentCellId)
	{
		if (path.Length < 2) return false;
		if (path[0] != currentCellId) return false;

		foreach (int cellId in path)
			if (!_walkableSet.Contains(cellId)) return false;

		for (int i = 0; i < path.Length - 1; i++)
		{
			var neighbors = GetNeighbors(path[i]);
			if (Array.IndexOf(neighbors, path[i + 1]) < 0) return false;
		}
		return true;
	}

	// --- Internal helpers ---

	private (int Row, int Col, bool IsLong) CellToRowCol(int cellId)
	{
		int stride = 2 * _mapWidth - 1;
		int pair = cellId / stride;
		int offset = cellId % stride;
		bool isLong = offset < _mapWidth;
		return (isLong ? 2 * pair : 2 * pair + 1, isLong ? offset : offset - _mapWidth, isLong);
	}

	private (int X, int Y) CellToCoord(int cellId)
	{
		int stride = 2 * _mapWidth - 1;
		int line = cellId / stride;
		int column = cellId - line * stride;
		int off = column % _mapWidth;
		int y = line - off;
		int x = (int)Math.Round((double)(cellId - (_mapWidth - 1) * y) / _mapWidth);
		return (x, y);
	}

	private float Heuristic(int fromId, int toId)
	{
		var a = CellToCoord(fromId);
		var b = CellToCoord(toId);
		float dx = Math.Abs(a.X - b.X);
		float dy = Math.Abs(a.Y - b.Y);
		return MathF.Sqrt(dx * dx + dy * dy);
	}

	private bool IsValidDirection(int row, int col, bool isLong, int dir)
	{
		int w = _mapWidth;
		return dir switch
		{
			0 => col < (isLong ? w - 1 : w - 2),                  // EAST
			1 => isLong ? row < _totalRows - 1 && col < w - 1 : row < _totalRows - 1, // SE
			2 => row + 2 < _totalRows,                              // SOUTH
			3 => isLong ? row < _totalRows - 1 && col > 0 : row < _totalRows - 1,     // SW
			4 => col > 0,                                            // WEST
			5 => isLong ? row > 0 && col > 0 : row > 0,            // NW
			6 => row >= 2,                                           // NORTH
			7 => isLong ? row > 0 && col < w - 1 : row > 0,        // NE
			_ => false
		};
	}

	private static int[] ReconstructPath(PathNode node)
	{
		var path = new List<int>();
		PathNode? current = node;
		while (current is not null)
		{
			path.Add(current.CellId);
			current = current.Parent;
		}
		path.Reverse();
		return path.ToArray();
	}

	private record PathNode(int CellId, float G, float V, float H, float F, int D, PathNode? Parent);
}
