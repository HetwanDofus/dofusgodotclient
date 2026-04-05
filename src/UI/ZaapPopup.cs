using Godot;

namespace DofusRetroFuture.UI;

/// <summary>
/// Dofus 1.29 style zaap context menu: "Zaap" title + "Use" action.
/// </summary>
public partial class ZaapPopup : Control
{
    [Signal] public delegate void UsePressedEventHandler(int cellId);

    private int _cellId = -1;
    private static readonly Color BorderColor = Colors.White;
    private static readonly Color InnerBorderColor = new(0.45f, 0.3f, 0.15f);
    private static readonly Color TitleBg = new(0.55f, 0.35f, 0.18f);
    private static readonly Color ActionBg = new(0.85f, 0.75f, 0.55f);
    private static readonly Color ActionHoverBg = new(0.95f, 0.85f, 0.65f);
    private static readonly Color TextColor = Colors.White;
    private static readonly Color ActionTextColor = new(0.2f, 0.15f, 0.05f);
    private const int FontSize = 12;
    private const int RowHeight = 22;
    private const int PaddingH = 12;
    private const int Border = 2;
    private const int InnerBorder = 1;
    private const int MenuWidth = 80;

    public override void _Ready()
    {
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
        ZIndex = 10001;
    }

    public void ShowAt(Vector2 screenPos, int cellId)
    {
        _cellId = cellId;
        Position = screenPos;
        Size = new Vector2(MenuWidth, Border * 2 + InnerBorder * 2 + RowHeight * 2);
        Visible = true;
    }

    public override void _Draw()
    {
        if (!Visible) return;
        int w = MenuWidth;
        int h = (int)Size.Y;

        DrawRect(new Rect2(0, 0, w, h), BorderColor);
        DrawRect(new Rect2(Border, Border, w - Border * 2, h - Border * 2), InnerBorderColor);

        int cx = Border + InnerBorder;
        int cy = Border + InnerBorder;
        int cw = w - (Border + InnerBorder) * 2;

        DrawRect(new Rect2(cx, cy, cw, RowHeight), TitleBg);

        int ay = cy + RowHeight;
        var actionBg = IsMouseInActionRow() ? ActionHoverBg : ActionBg;
        DrawRect(new Rect2(cx, ay, cw, RowHeight), actionBg);

        var font = ThemeDB.FallbackFont;
        DrawString(font, new Vector2(cx + PaddingH, cy + RowHeight - 6), "Zaap", HorizontalAlignment.Left, -1, FontSize, TextColor);
        DrawString(font, new Vector2(cx + PaddingH, ay + RowHeight - 6), "Use", HorizontalAlignment.Left, -1, FontSize, ActionTextColor);
    }

    private bool IsMouseInActionRow()
    {
        var local = GetLocalMousePosition();
        int ay = Border + InnerBorder + RowHeight;
        return new Rect2(0, ay, MenuWidth, RowHeight).HasPoint(local);
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
        {
            if (IsMouseInActionRow())
            {
                EmitSignal(SignalName.UsePressed, _cellId);
                Visible = false;
                AcceptEvent();
            }
        }
    }

    public override void _Process(double delta)
    {
        if (Visible) QueueRedraw();
    }
}
