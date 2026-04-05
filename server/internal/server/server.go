package server

import (
	"log"
	"net/http"
	"sync"

	"github.com/gorilla/websocket"
	"google.golang.org/protobuf/proto"

	"dofus-server/internal/game"
	pb "dofus-server/internal/proto"
)

var upgrader = websocket.Upgrader{
	CheckOrigin: func(r *http.Request) bool { return true },
}

type Server struct {
	world *game.World
	addr  string

	// Map ID -> list of sessions on that map
	sessions map[int32][]*Session
	mu       sync.RWMutex
}

func New(world *game.World, addr string) *Server {
	return &Server{
		world:    world,
		addr:     addr,
		sessions: make(map[int32][]*Session),
	}
}

func (s *Server) Start() error {
	http.HandleFunc("/ws", s.handleWS)
	return http.ListenAndServe(s.addr, nil)
}

func (s *Server) handleWS(w http.ResponseWriter, r *http.Request) {
	conn, err := upgrader.Upgrade(w, r, nil)
	if err != nil {
		log.Printf("[Server] upgrade error: %v", err)
		return
	}
	log.Printf("[Server] New connection from %s", conn.RemoteAddr())
	session := NewSession(conn, s.world, s)
	go session.Run()
}

func (s *Server) RegisterSession(mapID int32, session *Session) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.sessions[mapID] = append(s.sessions[mapID], session)
}

func (s *Server) UnregisterSession(mapID int32, session *Session) {
	s.mu.Lock()
	defer s.mu.Unlock()
	sessions := s.sessions[mapID]
	for i, sess := range sessions {
		if sess == session {
			s.sessions[mapID] = append(sessions[:i], sessions[i+1:]...)
			return
		}
	}
}

// BroadcastToMap sends a message to all sessions on a map except the actor with excludeActorID.
// Pass -1 to send to everyone.
func (s *Server) BroadcastToMap(mapID int32, excludeActorID int32, msg *pb.GameMessage) {
	s.mu.RLock()
	sessions := make([]*Session, len(s.sessions[mapID]))
	copy(sessions, s.sessions[mapID])
	s.mu.RUnlock()

	data, err := proto.Marshal(msg)
	if err != nil {
		return
	}

	for _, sess := range sessions {
		if sess.actorID == excludeActorID {
			continue
		}
		sess.mu.Lock()
		sess.conn.WriteMessage(websocket.BinaryMessage, data)
		sess.mu.Unlock()
	}
}
