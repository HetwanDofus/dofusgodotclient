using Godot;

namespace DofusRetroFuture.Interaction;

/// <summary>
/// Manages the Dofus 1.29 hover highlight shader material.
/// </summary>
public class HoverEffect
{
    private readonly ShaderMaterial _material;

    public HoverEffect()
    {
        var shader = GD.Load<Shader>("res://shaders/hover_highlight.gdshader");
        _material = new ShaderMaterial { Shader = shader };
    }

    public void Apply(Sprite2D sprite)
    {
        sprite.Material = _material;
    }

    public void Remove(Sprite2D sprite, ShaderMaterial? originalMaterial = null)
    {
        sprite.Material = originalMaterial;
    }
}
