package game

import (
	"context"
	"fmt"
	"log"
	"sync"
	"sync/atomic"

	"github.com/jackc/pgx/v5/pgtype"

	"dofus-server/internal/data"
	"dofus-server/internal/sqlc"

	pb "dofus-server/internal/proto"
)

type World struct {
	Q         *sqlc.Queries
	MapStore  *data.MapStore
	instances map[int32]*MapInstance
	mu        sync.RWMutex
	nextID    atomic.Int32
}

func NewWorld(q *sqlc.Queries, mapStore *data.MapStore) *World {
	w := &World{
		Q:         q,
		MapStore:  mapStore,
		instances: make(map[int32]*MapInstance),
	}
	w.nextID.Store(1000)
	return w
}

func (w *World) NextActorID() int32 {
	return w.nextID.Add(1)
}

func (w *World) GetOrCreateInstance(ctx context.Context, mapID int32) (*MapInstance, error) {
	w.mu.Lock()
	defer w.mu.Unlock()

	if inst, ok := w.instances[mapID]; ok {
		return inst, nil
	}

	mapData, err := w.MapStore.Get(ctx, mapID)
	if err != nil {
		return nil, fmt.Errorf("load map %d: %w", mapID, err)
	}

	inst := NewMapInstance(mapID, mapData)
	w.instances[mapID] = inst
	return inst, nil
}

func (w *World) GetMapData(ctx context.Context, mapID int32) (*pb.MapData, error) {
	return w.MapStore.Get(ctx, mapID)
}

func (w *World) Authenticate(ctx context.Context, username, password string) (*sqlc.Account, error) {
	account, err := w.Q.GetAccountByUsername(ctx, username)
	if err == nil {
		log.Printf("[World] Found account: id=%d username=%s", account.ID, account.Username)
		return &account, nil
	}

	// Auto-create for dev
	log.Printf("[World] Account '%s' not found, creating...", username)
	account, err = w.Q.CreateAccount(ctx, sqlc.CreateAccountParams{
		Username: username,
		Password: password,
		Pseudo:   username,
	})
	if err != nil {
		return nil, fmt.Errorf("create account: %w", err)
	}
	log.Printf("[World] Created account: id=%d", account.ID)
	return &account, nil
}

func (w *World) GetCharacters(ctx context.Context, accountID int32) ([]*pb.CharacterSummary, error) {
	chars, err := w.Q.GetCharactersByAccountID(ctx, accountID)
	if err != nil {
		return nil, err
	}

	result := make([]*pb.CharacterSummary, len(chars))
	for i := range chars {
		c := &chars[i]
		result[i] = &pb.CharacterSummary{
			Id:    c.ID,
			Name:  c.Name,
			Gfx:   c.Gfx,
			Level: c.Level.Int32,
			MapId: c.MapID.Int32,
		}
	}
	return result, nil
}

func (w *World) GetCharacterInfo(ctx context.Context, charID int32) (*pb.CharacterInfo, *sqlc.GetCharacterByIDRow, error) {
	c, err := w.Q.GetCharacterByID(ctx, charID)
	if err != nil {
		return nil, nil, err
	}

	info := &pb.CharacterInfo{
		Id:        c.ID,
		Name:      c.Name,
		Gfx:       c.Gfx,
		MapId:     c.MapID.Int32,
		CellId:    c.CellID.Int32,
		Direction: int32(c.Direction.Int16),
		Color1:    c.Color1.Int32,
		Color2:    c.Color2.Int32,
		Color3:    c.Color3.Int32,
		Look:      fmt.Sprintf("%d|%d|%d|%d|", c.Gfx, c.Color1.Int32, c.Color2.Int32, c.Color3.Int32),
	}
	return info, &c, nil
}

func (w *World) UpdateCharacterPosition(ctx context.Context, charID, mapID, cellID int32, direction int16) error {
	return w.Q.UpdateCharacterPosition(ctx, sqlc.UpdateCharacterPositionParams{
		ID:        charID,
		MapID:     pgtype.Int4{Int32: mapID, Valid: true},
		CellID:    pgtype.Int4{Int32: cellID, Valid: true},
		Direction: pgtype.Int2{Int16: direction, Valid: true},
	})
}
