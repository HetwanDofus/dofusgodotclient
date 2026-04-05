package server

import (
	"context"
	"log"
	"strconv"
	"strings"
	"sync"

	"github.com/gorilla/websocket"
	"google.golang.org/protobuf/proto"

	"dofus-server/internal/game"
	pb "dofus-server/internal/proto"
)

type pendingTransition struct {
	targetMapID  int32
	targetCellID int32
}

type Session struct {
	conn      *websocket.Conn
	world     *game.World
	srv       *Server
	accountID int32
	charID    int32
	actorID   int32
	mapID     int32
	cellID    int32
	direction int16
	name      string
	look      string
	instance  *game.MapInstance
	pending   *pendingTransition
	mu        sync.Mutex
}

func NewSession(conn *websocket.Conn, world *game.World, srv *Server) *Session {
	return &Session{conn: conn, world: world, srv: srv}
}

func (s *Session) Run() {
	defer s.cleanup()

	for {
		_, data, err := s.conn.ReadMessage()
		if err != nil {
			if websocket.IsCloseError(err, websocket.CloseNormalClosure, websocket.CloseGoingAway) {
				log.Printf("[Session] %s disconnected", s.name)
			} else {
				log.Printf("[Session] read error: %v", err)
			}
			return
		}

		msg := &pb.GameMessage{}
		if err := proto.Unmarshal(data, msg); err != nil {
			log.Printf("[Session] proto unmarshal error: %v", err)
			continue
		}

		s.handleMessage(msg)
	}
}

func (s *Session) handleMessage(msg *pb.GameMessage) {
	ctx := context.Background()

	switch m := msg.Msg.(type) {
	case *pb.GameMessage_AuthLogin:
		s.handleAuthLogin(ctx, m.AuthLogin)
	case *pb.GameMessage_CharacterSelect:
		s.handleCharacterSelect(ctx, m.CharacterSelect)
	case *pb.GameMessage_CharacterMove:
		s.handleCharacterMove(ctx, m.CharacterMove)
	case *pb.GameMessage_CharacterMoveEnd:
		s.handleCharacterMoveEnd(ctx)
	case *pb.GameMessage_MapChangeRequest:
		s.handleMapChange(ctx, m.MapChangeRequest)
	case *pb.GameMessage_Ping:
		s.send(&pb.GameMessage{Msg: &pb.GameMessage_Pong{Pong: &pb.Pong{Timestamp: m.Ping.Timestamp}}})
	}
}

func (s *Session) handleAuthLogin(ctx context.Context, msg *pb.AuthLogin) {
	account, err := s.world.Authenticate(ctx, msg.Username, msg.Password)
	if err != nil {
		log.Printf("[Session] Auth failed for %s: %v", msg.Username, err)
		return
	}

	s.accountID = account.ID
	s.name = account.Username
	log.Printf("[Session] Auth: %s (account %d)", s.name, s.accountID)

	chars, err := s.world.GetCharacters(ctx, s.accountID)
	if err != nil {
		log.Printf("[Session] Failed to get characters: %v", err)
		return
	}

	s.send(&pb.GameMessage{
		Msg: &pb.GameMessage_AuthSuccess{
			AuthSuccess: &pb.AuthSuccess{Characters: chars},
		},
	})
}

func (s *Session) handleCharacterSelect(ctx context.Context, msg *pb.CharacterSelect) {
	s.charID = msg.CharacterId

	info, _, err := s.world.GetCharacterInfo(ctx, s.charID)
	if err != nil {
		log.Printf("[Session] Failed to get character %d: %v", s.charID, err)
		return
	}

	s.actorID = s.world.NextActorID()
	s.mapID = info.MapId
	s.cellID = info.CellId
	s.direction = int16(info.Direction)
	s.look = info.Look

	// Send character info with actor ID
	info.Id = s.actorID
	log.Printf("[Session] %s selected char %d → actor %d, map %d cell %d", s.name, s.charID, s.actorID, s.mapID, s.cellID)

	s.send(&pb.GameMessage{
		Msg: &pb.GameMessage_CharacterInfo{CharacterInfo: info},
	})

	// Join map
	s.joinMap(ctx, s.mapID, s.cellID, s.direction)
}

func (s *Session) joinMap(ctx context.Context, mapID, cellID int32, direction int16) {
	inst, err := s.world.GetOrCreateInstance(ctx, mapID)
	if err != nil {
		log.Printf("[Session] Failed to load map %d: %v", mapID, err)
		return
	}

	s.instance = inst
	s.mapID = mapID
	s.cellID = cellID
	s.direction = direction

	s.srv.RegisterSession(mapID, s)

	actor := &game.Actor{
		ID:        s.actorID,
		CellID:    cellID,
		Direction: int32(direction),
		Name:      s.name,
		Look:      s.look,
	}
	inst.AddActor(actor)

	// Send map data
	mapData, err := s.world.GetMapData(ctx, mapID)
	if err != nil {
		log.Printf("[Session] Failed to get map data %d: %v", mapID, err)
		return
	}
	s.send(&pb.GameMessage{
		Msg: &pb.GameMessage_MapData{MapData: mapData},
	})

	// Send existing actors
	actors := inst.GetActors()
	s.send(&pb.GameMessage{
		Msg: &pb.GameMessage_MapActors{MapActors: &pb.MapActors{Actors: actors}},
	})

	// Broadcast our arrival
	s.srv.BroadcastToMap(mapID, s.actorID, &pb.GameMessage{
		Msg: &pb.GameMessage_ActorAdd{ActorAdd: &pb.ActorAdd{
			Id:        s.actorID,
			CellId:    cellID,
			Direction: int32(direction),
			Name:      s.name,
			Look:      s.look,
		}},
	})
}

func (s *Session) handleCharacterMove(ctx context.Context, msg *pb.CharacterMove) {
	if s.instance == nil || len(msg.Path) < 2 {
		return
	}

	if !s.instance.ValidatePath(msg.Path) {
		log.Printf("[Session] %s invalid path", s.name)
		return
	}

	path := msg.Path
	s.pending = nil

	// Check for trigger cells along the path
	triggers := s.world.MapStore.GetTriggers(s.mapID)
	log.Printf("[Session] %s move on map %d, path len=%d, triggers=%d", s.name, s.mapID, len(path), len(triggers))
	if len(triggers) > 0 {
		triggerMap := make(map[int32]*pendingTransition, len(triggers))
		for _, t := range triggers {
			if t.ActionID == 0 && t.EventID == 1 && t.ActionArgs.Valid {
				if target, ok := parseTriggerArgs(t.ActionArgs.String); ok {
					triggerMap[t.CellID] = target
				}
			}
		}

		for i := 1; i < len(path); i++ {
			if trigger, ok := triggerMap[path[i]]; ok {
				path = path[:i+1] // Truncate at trigger cell
				s.pending = trigger
				log.Printf("[Session] %s path truncated at trigger cell %d → map %d cell %d",
					s.name, path[i], trigger.targetMapID, trigger.targetCellID)
				break
			}
		}
	}

	finalCell := path[len(path)-1]
	s.cellID = finalCell

	// Update actor on map
	if actor := s.instance.GetActor(s.actorID); actor != nil {
		actor.CellID = finalCell
	}

	// Persist to DB
	if err := s.world.UpdateCharacterPosition(ctx, s.charID, s.mapID, finalCell, s.direction); err != nil {
		log.Printf("[Session] Failed to update position: %v", err)
	}

	// Broadcast to all (including sender for confirmation)
	s.srv.BroadcastToMap(s.mapID, -1, &pb.GameMessage{
		Msg: &pb.GameMessage_ActorMove{ActorMove: &pb.ActorMove{
			Id:   s.actorID,
			Path: path,
		}},
	})
}

func (s *Session) handleCharacterMoveEnd(ctx context.Context) {
	// Persist final position
	if err := s.world.UpdateCharacterPosition(ctx, s.charID, s.mapID, s.cellID, s.direction); err != nil {
		log.Printf("[Session] Failed to update position on move end: %v", err)
	}

	// Fire pending trigger transition
	if s.pending != nil {
		p := s.pending
		s.pending = nil
		log.Printf("[Session] %s trigger transition → map %d cell %d", s.name, p.targetMapID, p.targetCellID)
		s.handleMapChange(ctx, &pb.MapChangeRequest{
			TargetMapId:  p.targetMapID,
			TargetCellId: p.targetCellID,
		})
	}
}

func (s *Session) handleMapChange(ctx context.Context, msg *pb.MapChangeRequest) {
	if s.instance == nil {
		return
	}

	oldMapID := s.mapID
	newMapID := msg.TargetMapId

	log.Printf("[Session] %s map change %d → %d", s.name, oldMapID, newMapID)

	// Remove from old map
	s.instance.RemoveActor(s.actorID)
	s.srv.UnregisterSession(oldMapID, s)
	s.srv.BroadcastToMap(oldMapID, s.actorID, &pb.GameMessage{
		Msg: &pb.GameMessage_ActorRemove{ActorRemove: &pb.ActorRemove{Id: s.actorID}},
	})

	// Send map change response
	s.send(&pb.GameMessage{
		Msg: &pb.GameMessage_MapChangeResponse{MapChangeResponse: &pb.MapChangeResponse{MapId: newMapID}},
	})

	// Update DB
	if err := s.world.UpdateCharacterPosition(ctx, s.charID, newMapID, msg.TargetCellId, 2); err != nil {
		log.Printf("[Session] Failed to update position for map change: %v", err)
	}

	// Join new map
	s.joinMap(ctx, newMapID, msg.TargetCellId, 2)
}

// parseTriggerArgs parses "targetMapId,targetCellId" from action_args
func parseTriggerArgs(args string) (*pendingTransition, bool) {
	parts := strings.SplitN(args, ",", 2)
	if len(parts) != 2 {
		return nil, false
	}
	mapID, err1 := strconv.ParseInt(strings.TrimSpace(parts[0]), 10, 32)
	cellID, err2 := strconv.ParseInt(strings.TrimSpace(parts[1]), 10, 32)
	if err1 != nil || err2 != nil {
		return nil, false
	}
	return &pendingTransition{targetMapID: int32(mapID), targetCellID: int32(cellID)}, true
}

func (s *Session) send(msg *pb.GameMessage) {
	data, err := proto.Marshal(msg)
	if err != nil {
		log.Printf("[Session] marshal error: %v", err)
		return
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if err := s.conn.WriteMessage(websocket.BinaryMessage, data); err != nil {
		log.Printf("[Session] write error: %v", err)
	}
}

func (s *Session) cleanup() {
	if s.instance != nil {
		s.instance.RemoveActor(s.actorID)
		s.srv.UnregisterSession(s.mapID, s)
		s.srv.BroadcastToMap(s.mapID, s.actorID, &pb.GameMessage{
			Msg: &pb.GameMessage_ActorRemove{ActorRemove: &pb.ActorRemove{Id: s.actorID}},
		})
	}
	s.conn.Close()
}
