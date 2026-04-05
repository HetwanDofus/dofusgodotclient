using Godot;
using static DofusRetroFuture.Core.Constants;

namespace DofusRetroFuture.Core;

public static class CellGrid
{
	public static Vector2 GetCellPosition(int cellId, int mapWidth, int groundLevel)
	{
		int stride = 2 * mapWidth - 1;
		int pair = cellId / stride;
		int offset = cellId % stride;
		bool isLong = offset < mapWidth;
		int row = pair * 2 + (isLong ? 0 : 1);
		int col = isLong ? offset : offset - mapWidth;

		float x = col * CellWidth + (isLong ? 0f : CellHalfWidth);
		float y = row * CellHalfHeight - LevelHeight * (groundLevel - 7);

		return new Vector2(x, y);
	}

	public static Vector2I CellToGrid(int cellId, int mapWidth)
	{
		int stride = 2 * mapWidth - 1;
		int pair = cellId / stride;
		int offset = cellId % stride;
		bool isLong = offset < mapWidth;
		int row = pair * 2 + (isLong ? 0 : 1);
		int col = isLong ? offset : offset - mapWidth;
		return new Vector2I(col, row);
	}

	public static int ComputeDirection(int dx, int dy)
	{
		if (dx > 0 && dy == 0) return 0; // East
		if (dx > 0 && dy > 0) return 1;  // South-East
		if (dx == 0 && dy > 0) return 2;  // South
		if (dx < 0 && dy > 0) return 3;  // South-West
		if (dx < 0 && dy == 0) return 4;  // West
		if (dx < 0 && dy < 0) return 5;  // North-West
		if (dx == 0 && dy < 0) return 6;  // North
		if (dx > 0 && dy < 0) return 7;  // North-East
		return 2; // default South
	}

	/// <summary>
	/// Diamond hit test: is the point inside the isometric cell at the given position?
	/// </summary>
	public static bool PointInCell(Vector2 point, Vector2 cellCenter)
	{
		float dx = point.X - cellCenter.X;
		float dy = point.Y - cellCenter.Y;
		return Mathf.Abs(dx / CellHalfWidth) + Mathf.Abs(dy / CellHalfHeight) <= 1f;
	}

	/// <summary>
	/// Find which cell contains the given world position.
	/// </summary>
	public static int FindCellAtPosition(Vector2 worldPos, Godot.Collections.Array<Godot.Collections.Dictionary> cells, int mapWidth)
	{
		// GetCellPosition returns the diamond center (matching PixiJS convention).
		int result = -1;
		foreach (var cellData in cells)
		{
			int cellId = cellData["id"].AsInt32();
			int groundLevel = cellData.ContainsKey("groundLevel") ? cellData["groundLevel"].AsInt32() : 7;
			var pos = GetCellPosition(cellId, mapWidth, groundLevel);

			float dx = worldPos.X - pos.X;
			float dy = worldPos.Y - pos.Y;
			if (Mathf.Abs(dx / CellHalfWidth) + Mathf.Abs(dy / CellHalfHeight) <= 1f)
				result = cellId;
		}
		return result;
	}
}
