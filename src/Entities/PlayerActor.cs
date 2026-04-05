using Godot;

namespace DofusRetroFuture.Entities;

public class PlayerActor : Actor
{
    public string PlayerName { get; }
    public int GfxId { get; }
    public int[] Colors { get; }
    public int[] AccInfo { get; }

    public PlayerActor(
        Node2D parent, int cellId, int direction,
        string playerName, int gfxId, int[] colors, int[] accInfo)
        : base(parent, cellId, direction)
    {
        PlayerName = playerName;
        GfxId = gfxId;
        Colors = colors;
        AccInfo = accInfo;
    }
}
