using Godot;
using DofusRetroFuture.Core;

namespace DofusRetroFuture.UI;

/// <summary>
/// Bottom bar placeholder for the future HUD.
/// </summary>
public partial class UIPlaceholder : ColorRect
{
    public override void _Ready()
    {
        Color = new Color(0.08f, 0.08f, 0.08f, 0.95f);
        Size = new Vector2(Constants.DisplayWidth, Constants.UiBarHeight);
        Position = new Vector2(0, Constants.DisplayHeight - Constants.UiBarHeight);
        MouseFilter = MouseFilterEnum.Stop;
    }
}
