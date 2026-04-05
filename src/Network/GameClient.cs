using System;
using Godot;
using DofusRetroFuture.Proto;

namespace DofusRetroFuture.Network;

/// <summary>
/// High-level game network client. Handles login, character select, movement, map change.
/// </summary>
public class GameClient
{
    private readonly GameConnection _conn;

    // Events for GameManager to handle
    public event Action<AuthSuccess>? OnAuthSuccess;
    public event Action<CharacterInfo>? OnCharacterInfo;
    public event Action<MapData>? OnMapData;
    public event Action<MapActors>? OnMapActors;
    public event Action<ActorAdd>? OnActorAdd;
    public event Action<ActorRemove>? OnActorRemove;
    public event Action<ActorMove>? OnActorMove;
    public event Action<MapChangeResponse>? OnMapChange;
    public event Action? OnConnected;
    public event Action? OnDisconnected;

    public bool IsConnected => _conn.IsConnected;

    public GameClient(string url = "ws://localhost:8080/ws")
    {
        _conn = new GameConnection(url);
        _conn.Connected += () => OnConnected?.Invoke();
        _conn.Disconnected += () => OnDisconnected?.Invoke();
        _conn.MessageReceived += HandleMessage;
    }

    public Error Connect() => _conn.Connect();
    public void Poll() => _conn.Poll();
    public void Close() => _conn.Close();

    public void Login(string username, string password = "")
    {
        _conn.Send(new GameMessage
        {
            AuthLogin = new AuthLogin { Username = username, Password = password }
        });
    }

    public void SelectCharacter(int characterId)
    {
        _conn.Send(new GameMessage
        {
            CharacterSelect = new CharacterSelect { CharacterId = characterId }
        });
    }

    public void SendMove(int[] path)
    {
        var msg = new CharacterMove();
        foreach (int cellId in path)
            msg.Path.Add(cellId);
        _conn.Send(new GameMessage { CharacterMove = msg });
    }

    public void SendMoveEnd()
    {
        _conn.Send(new GameMessage { CharacterMoveEnd = new CharacterMoveEnd() });
    }

    public void RequestMapChange(int targetMapId, int targetCellId)
    {
        _conn.Send(new GameMessage
        {
            MapChangeRequest = new MapChangeRequest
            {
                TargetMapId = targetMapId,
                TargetCellId = targetCellId
            }
        });
    }

    private void HandleMessage(GameMessage msg)
    {
        switch (msg.MsgCase)
        {
            case GameMessage.MsgOneofCase.AuthSuccess:
                OnAuthSuccess?.Invoke(msg.AuthSuccess);
                break;
            case GameMessage.MsgOneofCase.CharacterInfo:
                OnCharacterInfo?.Invoke(msg.CharacterInfo);
                break;
            case GameMessage.MsgOneofCase.MapData:
                OnMapData?.Invoke(msg.MapData);
                break;
            case GameMessage.MsgOneofCase.MapActors:
                OnMapActors?.Invoke(msg.MapActors);
                break;
            case GameMessage.MsgOneofCase.ActorAdd:
                OnActorAdd?.Invoke(msg.ActorAdd);
                break;
            case GameMessage.MsgOneofCase.ActorRemove:
                OnActorRemove?.Invoke(msg.ActorRemove);
                break;
            case GameMessage.MsgOneofCase.ActorMove:
                OnActorMove?.Invoke(msg.ActorMove);
                break;
            case GameMessage.MsgOneofCase.MapChangeResponse:
                OnMapChange?.Invoke(msg.MapChangeResponse);
                break;
            case GameMessage.MsgOneofCase.Pong:
                GD.Print($"[GameClient] Pong: {msg.Pong.Timestamp}");
                break;
        }
    }
}
