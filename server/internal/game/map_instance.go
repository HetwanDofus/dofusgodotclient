package game

import (
	"sync"

	pb "dofus-server/internal/proto"
)

type Actor struct {
	ID        int32
	CellID    int32
	Direction int32
	Name      string
	Look      string
}

type MapInstance struct {
	MapID  int32
	Data   *pb.MapData
	actors map[int32]*Actor
	mu     sync.RWMutex

	// Walkable cells for path validation
	walkable map[int32]bool
}

func NewMapInstance(mapID int32, data *pb.MapData) *MapInstance {
	walkable := make(map[int32]bool)
	for _, cell := range data.Cells {
		if cell.Movement != 0 || cell.Walkable {
			walkable[cell.Id] = true
		}
	}

	return &MapInstance{
		MapID:    mapID,
		Data:     data,
		actors:   make(map[int32]*Actor),
		walkable: walkable,
	}
}

func (m *MapInstance) AddActor(actor *Actor) {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.actors[actor.ID] = actor
}

func (m *MapInstance) RemoveActor(id int32) {
	m.mu.Lock()
	defer m.mu.Unlock()
	delete(m.actors, id)
}

func (m *MapInstance) GetActor(id int32) *Actor {
	m.mu.RLock()
	defer m.mu.RUnlock()
	return m.actors[id]
}

func (m *MapInstance) GetActors() []*pb.ActorInfo {
	m.mu.RLock()
	defer m.mu.RUnlock()

	actors := make([]*pb.ActorInfo, 0, len(m.actors))
	for _, a := range m.actors {
		actors = append(actors, &pb.ActorInfo{
			Id:        a.ID,
			CellId:    a.CellID,
			Direction: a.Direction,
			Name:      a.Name,
			Look:      a.Look,
		})
	}
	return actors
}

func (m *MapInstance) IsWalkable(cellID int32) bool {
	return m.walkable[cellID]
}

// ValidatePath checks that all cells are walkable and adjacent
func (m *MapInstance) ValidatePath(path []int32) bool {
	if len(path) < 2 {
		return false
	}
	for _, cellID := range path {
		if !m.walkable[cellID] {
			return false
		}
	}
	return true
}

// BroadcastExcept sends a message to all sessions on this map except the given one
type BroadcastFunc func(actorID int32, msg *pb.GameMessage)

var broadcastRegistry = make(map[int32][]BroadcastFunc)
var broadcastMu sync.RWMutex
