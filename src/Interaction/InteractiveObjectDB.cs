using Godot;
using System.Collections.Generic;

namespace DofusRetroFuture.Interaction;

public record InteractiveInfo(int Type, string Name);

public class InteractiveObjectDB
{
    private readonly Dictionary<int, InteractiveInfo> _gfxToInfo = new();

    public int Count => _gfxToInfo.Count;

    public void Load()
    {
        var file = FileAccess.Open("res://assets/data/interactive-objects.json", FileAccess.ModeFlags.Read);
        if (file is null) { GD.Print("[InteractiveObjectDB] No interactive-objects.json found"); return; }

        var json = new Json();
        if (json.Parse(file.GetAsText()) != Error.Ok) return;
        file.Close();

        var data = json.Data.AsGodotDictionary();
        var objects = data.ContainsKey("interactiveObjects") ? data["interactiveObjects"].AsGodotDictionary() : null;
        if (objects is null) return;

        foreach (var key in objects.Keys)
        {
            var obj = objects[key].AsGodotDictionary();
            int type = obj.ContainsKey("type") ? obj["type"].AsInt32() : 0;
            string name = obj.ContainsKey("name") ? obj["name"].AsString() : "";
            var gfxIds = obj.ContainsKey("gfxIds") ? obj["gfxIds"].AsGodotArray() : null;
            if (gfxIds is null) continue;

            foreach (var gfxId in gfxIds)
                _gfxToInfo[gfxId.AsInt32()] = new InteractiveInfo(type, name);
        }

        GD.Print($"[InteractiveObjectDB] Loaded {_gfxToInfo.Count} interactive gfx IDs");
    }

    public bool IsInteractive(int gfxId) => _gfxToInfo.ContainsKey(gfxId);
    public InteractiveInfo GetInfo(int gfxId) => _gfxToInfo.GetValueOrDefault(gfxId, new InteractiveInfo(0, ""));
}
