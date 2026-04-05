using Godot;
using DofusRetroFuture.Core;

namespace DofusRetroFuture.Rendering;

/// <summary>
/// Manages strip-based animation playback for a single entity.
/// </summary>
public class SpriteAnimator
{
    private StripData? _currentStrip;
    private string _currentKey = "";
    private int _frame;
    private float _elapsed;
    private int _fps;

    public int Frame => _frame;
    public int FrameCount => _currentStrip?.FrameCount ?? 0;
    public string CurrentKey => _currentKey;
    public StripData? CurrentStrip => _currentStrip;

    public void SetStrip(StripData strip, string key)
    {
        if (key == _currentKey) return;
        _currentStrip = strip;
        _currentKey = key;
        _frame = 0;
        _elapsed = 0;
        _fps = strip.Fps;
    }

    public void SetRandomOffset(float maxSeconds)
    {
        _elapsed = GD.Randf() * maxSeconds;
    }

    /// <summary>
    /// Advance the animation. Returns true if the frame changed.
    /// </summary>
    public bool Tick(float delta)
    {
        if (_currentStrip is null || _currentStrip.FrameCount <= 1)
            return false;

        _elapsed += delta;
        float frameDuration = 1f / _fps;
        if (_elapsed < frameDuration) return false;

        _elapsed -= frameDuration;
        _frame = (_frame + 1) % _currentStrip.FrameCount;
        return true;
    }

    public Texture2D? GetCurrentFrame()
    {
        if (_currentStrip is null || _frame >= _currentStrip.Frames.Length)
            return null;
        return _currentStrip.Frames[_frame];
    }

    public void ApplyToSprite(Sprite2D sprite, float tileResolution, bool flip)
    {
        if (_currentStrip is null) return;

        var frame = GetCurrentFrame();
        if (frame is not null)
            sprite.Texture = frame;

        sprite.Offset = new Vector2(-_currentStrip.AnchorX, -_currentStrip.AnchorY);
        float s = 1f / tileResolution;
        sprite.Scale = new Vector2(flip ? -s : s, s);
    }
}
