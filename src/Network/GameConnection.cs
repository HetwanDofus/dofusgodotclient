using System;
using System.Threading.Tasks;
using Godot;
using Google.Protobuf;
using DofusRetroFuture.Proto;

namespace DofusRetroFuture.Network;

/// <summary>
/// WebSocket connection to the game server. Sends/receives protobuf GameMessages.
/// </summary>
public class GameConnection
{
    private WebSocketPeer _ws = new();
    private string _url;
    private bool _connected;

    public event Action? Connected;
    public event Action? Disconnected;
    public event Action<GameMessage>? MessageReceived;

    public bool IsConnected => _connected;

    public GameConnection(string url = "ws://localhost:8080/ws")
    {
        _url = url;
    }

    public Error Connect()
    {
        GD.Print($"[GameConnection] Connecting to {_url}");
        return _ws.ConnectToUrl(_url);
    }

    public void Poll()
    {
        _ws.Poll();

        var state = _ws.GetReadyState();

        switch (state)
        {
            case WebSocketPeer.State.Open:
                if (!_connected)
                {
                    _connected = true;
                    GD.Print("[GameConnection] Connected");
                    Connected?.Invoke();
                }
                // Read all available packets
                while (_ws.GetAvailablePacketCount() > 0)
                {
                    var data = _ws.GetPacket();
                    try
                    {
                        var msg = GameMessage.Parser.ParseFrom(data);
                        MessageReceived?.Invoke(msg);
                    }
                    catch (Exception e)
                    {
                        GD.PrintErr($"[GameConnection] Proto parse error: {e.Message}");
                    }
                }
                break;

            case WebSocketPeer.State.Closed:
                if (_connected)
                {
                    _connected = false;
                    GD.Print("[GameConnection] Disconnected");
                    Disconnected?.Invoke();
                }
                break;
        }
    }

    public void Send(GameMessage msg)
    {
        if (!_connected) return;
        var data = msg.ToByteArray();
        _ws.PutPacket(data);
    }

    public void Close()
    {
        _ws.Close();
        _connected = false;
    }
}
