using Godot;
using Godot.Collections;
using DofusRetroFuture.Core;

namespace DofusRetroFuture.Interaction;

/// <summary>
/// Central input handler: mouse hover/click → picking → hover effects, nametags, zaap popups.
/// </summary>
public partial class InteractionHandler : Node2D
{
    [Signal] public delegate void ObjectHoveredEventHandler(int id, string type, string name);
    [Signal] public delegate void ObjectUnhoveredEventHandler(int id);
    [Signal] public delegate void ObjectClickedEventHandler(int id, string type, int cellId);
    [Signal] public delegate void CellClickedEventHandler(int cellId);
    [Signal] public delegate void ZaapClickedEventHandler(int cellId, Vector2 screenPos);

    private PickingSystem _picking = new();
    private HoverEffect _hover = new();
    private PickableObject? _hovered;
    private Vector2 _lastMousePos = new(-1, -1);
    private bool _mouseInViewport;

    // Map data for cell detection
    private Array<Dictionary>? _cells;
    private int _mapWidth = 15;

    // Nametag
    private PanelContainer? _nametag;
    private Label? _nametagLabel;
    private CanvasLayer? _uiCanvasLayer;

    public PickingSystem Picking => _picking;

    public override void _Ready()
    {
        _hover = new HoverEffect();
        CreateNametag();
    }

    public void SetMapData(Array<Dictionary> cells, int mapWidth)
    {
        _cells = cells;
        _mapWidth = mapWidth;
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventMouseMotion motion)
        {
            _lastMousePos = motion.Position;
            _mouseInViewport = true;
            UpdateHover();
        }
        else if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click)
        {
            HandleClick(click.Position);
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMMouseExit)
        {
            _mouseInViewport = false;
            ClearHover();
        }
    }

    public override void _Process(double delta)
    {
        if (_nametag?.Visible == true && _hovered is not null && GodotObject.IsInstanceValid(_hovered.Sprite))
            UpdateNametagPosition(_hovered.Sprite);
    }

    private void UpdateHover()
    {
        if (!_mouseInViewport) return;
        var worldPos = ScreenToWorld(_lastMousePos);
        var hit = _picking.Pick(worldPos);

        if (hit?.Id == _hovered?.Id) return;

        if (_hovered is not null)
            OnHoverLeave(_hovered);

        _hovered = hit;

        if (_hovered is not null)
            OnHoverEnter(_hovered);
    }

    private void HandleClick(Vector2 screenPos)
    {
        var worldPos = ScreenToWorld(screenPos);
        var hit = _picking.Pick(worldPos);

        if (hit is not null)
        {
            EmitSignal(SignalName.ObjectClicked, hit.Id, hit.Type, hit.CellId);
            if (hit.ObjectType == 3) // Zaap
                EmitSignal(SignalName.ZaapClicked, hit.CellId, screenPos);
            return;
        }

        if (_cells is not null)
        {
            int cellId = CellGrid.FindCellAtPosition(worldPos, _cells, _mapWidth);
            if (cellId >= 0)
                EmitSignal(SignalName.CellClicked, cellId);
        }
    }

    private void OnHoverEnter(PickableObject obj)
    {
        if (GodotObject.IsInstanceValid(obj.Sprite))
            _hover.Apply(obj.Sprite);
        Input.SetDefaultCursorShape(Input.CursorShape.PointingHand);

        if (obj.Type == "player" && !string.IsNullOrEmpty(obj.Name))
            ShowNametag(obj.Sprite, obj.Name);

        EmitSignal(SignalName.ObjectHovered, obj.Id, obj.Type, obj.Name);
    }

    private void OnHoverLeave(PickableObject obj)
    {
        if (GodotObject.IsInstanceValid(obj.Sprite))
            _hover.Remove(obj.Sprite);
        Input.SetDefaultCursorShape(Input.CursorShape.Arrow);
        HideNametag();
        EmitSignal(SignalName.ObjectUnhovered, obj.Id);
    }

    private void ClearHover()
    {
        if (_hovered is not null)
        {
            OnHoverLeave(_hovered);
            _hovered = null;
        }
    }

    private Vector2 ScreenToWorld(Vector2 screenPos)
    {
        var camera = GetViewport().GetCamera2D();
        if (camera is not null)
        {
            var viewportSize = GetViewportRect().Size;
            return camera.Position + (screenPos - viewportSize / 2f) / camera.Zoom;
        }
        return screenPos;
    }

    // --- Nametag ---

    private void CreateNametag()
    {
        _uiCanvasLayer = new CanvasLayer { Layer = 100 };
        AddChild(_uiCanvasLayer);

        _nametag = new PanelContainer { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };

        var style = new StyleBoxFlat
        {
            BgColor = new Color(0, 0, 0, 0.7f),
            CornerRadiusTopLeft = 2, CornerRadiusTopRight = 2,
            CornerRadiusBottomLeft = 2, CornerRadiusBottomRight = 2,
            ContentMarginLeft = 4, ContentMarginRight = 4,
            ContentMarginTop = 2, ContentMarginBottom = 2
        };
        _nametag.AddThemeStyleboxOverride("panel", style);

        _nametagLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _nametagLabel.AddThemeFontSizeOverride("font_size", 10);
        _nametagLabel.AddThemeColorOverride("font_color", Colors.White);
        _nametag.AddChild(_nametagLabel);

        _uiCanvasLayer.AddChild(_nametag);
    }

    private void ShowNametag(Sprite2D sprite, string name)
    {
        if (_nametag is null || _nametagLabel is null) return;
        _nametagLabel.Text = name;
        _nametag.Visible = true;
        UpdateNametagPosition(sprite);
    }

    private void HideNametag()
    {
        if (_nametag is not null) _nametag.Visible = false;
    }

    private void UpdateNametagPosition(Sprite2D sprite)
    {
        if (_nametag is null) return;
        var camera = GetViewport().GetCamera2D();
        var viewportSize = GetViewportRect().Size;
        var zoom = camera?.Zoom ?? Vector2.One;
        var camPos = camera?.Position ?? Vector2.Zero;

        var worldPos = sprite.GlobalPosition;
        var screenPos = (worldPos - camPos) * zoom + viewportSize / 2f;
        float offsetY = 50f * zoom.Y;

        _nametag.Position = new Vector2(
            screenPos.X - _nametag.Size.X / 2f,
            screenPos.Y - offsetY);
    }
}
