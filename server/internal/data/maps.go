package data

import (
	"bytes"
	"compress/gzip"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"log"
	"sync"

	"dofus-server/internal/sqlc"

	pb "dofus-server/internal/proto"
)

type MapStore struct {
	q     *sqlc.Queries
	cache map[int32]*pb.MapData
	mu    sync.RWMutex

	triggers map[int32][]sqlc.ScriptedCell
}

func NewMapStore(q *sqlc.Queries) *MapStore {
	return &MapStore{
		q:        q,
		cache:    make(map[int32]*pb.MapData),
		triggers: make(map[int32][]sqlc.ScriptedCell),
	}
}

func (s *MapStore) Count() int {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return len(s.cache)
}

func (s *MapStore) Get(ctx context.Context, mapID int32) (*pb.MapData, error) {
	s.mu.RLock()
	if m, ok := s.cache[mapID]; ok {
		s.mu.RUnlock()
		return m, nil
	}
	s.mu.RUnlock()

	return s.loadFromDB(ctx, mapID)
}

func (s *MapStore) GetTriggers(mapID int32) []sqlc.ScriptedCell {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return s.triggers[mapID]
}

func (s *MapStore) loadFromDB(ctx context.Context, mapID int32) (*pb.MapData, error) {
	row, err := s.q.GetMapByID(ctx, mapID)
	if err != nil {
		return nil, fmt.Errorf("load map %d: %w", mapID, err)
	}

	cells, err := decompressCells(row.CellsGzip)
	if err != nil {
		return nil, fmt.Errorf("decompress map %d: %w", mapID, err)
	}

	mapData := &pb.MapData{
		MapId:      mapID,
		Width:      row.Width,
		Height:     row.Height,
		Background: row.Background.Int32,
		Cells:      cells,
	}

	triggers, err := s.q.GetScriptedCellsByMapID(ctx, mapID)
	if err != nil {
		log.Printf("[MapStore] Warning: scripted cells for map %d: %v", mapID, err)
	}

	s.mu.Lock()
	s.cache[mapID] = mapData
	if len(triggers) > 0 {
		s.triggers[mapID] = triggers
	}
	s.mu.Unlock()

	log.Printf("[MapStore] Loaded map %d (%dx%d, %d cells, %d triggers)", mapID, row.Width, row.Height, len(cells), len(triggers))
	return mapData, nil
}

type rawCell struct {
	ID          int32 `json:"id"`
	Active      bool  `json:"active"`
	Ground      int32 `json:"ground"`
	Layer1      int32 `json:"layer1"`
	Layer2      int32 `json:"layer2"`
	GroundLevel int32 `json:"groundLevel"`
	Walkable    bool  `json:"walkable"`
	Movement    int32 `json:"movement"`
	LineOfSight bool  `json:"lineOfSight"`
	GroundSlope int32 `json:"groundSlope"`
	GroundRot   int32 `json:"layerGroundRot"`
	GroundFlip  bool  `json:"layerGroundFlip"`
	Obj1Rot     int32 `json:"layerObject1Rot"`
	Obj1Flip    bool  `json:"layerObject1Flip"`
	Obj2Rot     int32 `json:"layerObject2Rot"`
	Obj2Flip    bool  `json:"layerObject2Flip"`
}

func decompressCells(cellsGzip []byte) ([]*pb.CellData, error) {
	reader, err := gzip.NewReader(bytes.NewReader(cellsGzip))
	if err != nil {
		return nil, err
	}
	defer reader.Close()

	raw, err := io.ReadAll(reader)
	if err != nil {
		return nil, err
	}

	var cells []rawCell
	if err := json.Unmarshal(raw, &cells); err != nil {
		return nil, err
	}

	result := make([]*pb.CellData, len(cells))
	for i, c := range cells {
		result[i] = &pb.CellData{
			Id:          c.ID,
			Active:      c.Active,
			Ground:      c.Ground,
			Layer1:      c.Layer1,
			Layer2:      c.Layer2,
			GroundLevel: c.GroundLevel,
			Walkable:    c.Walkable,
			Movement:    c.Movement,
			LineOfSight: c.LineOfSight,
			GroundSlope: c.GroundSlope,
			GroundRot:   c.GroundRot,
			GroundFlip:  c.GroundFlip,
			Obj1Rot:     c.Obj1Rot,
			Obj1Flip:    c.Obj1Flip,
			Obj2Rot:     c.Obj2Rot,
			Obj2Flip:    c.Obj2Flip,
		}
	}
	return result, nil
}
