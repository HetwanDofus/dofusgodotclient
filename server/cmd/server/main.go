package main

import (
	"flag"
	"log"

	"dofus-server/internal/data"
	"dofus-server/internal/game"
	"dofus-server/internal/server"
	"dofus-server/internal/sqlc"
)

func main() {
	addr := flag.String("addr", ":8080", "WebSocket listen address")
	dbHost := flag.String("db-host", "localhost", "PostgreSQL host")
	dbPort := flag.String("db-port", "5432", "PostgreSQL port")
	dbUser := flag.String("db-user", "dofus", "PostgreSQL user")
	dbPass := flag.String("db-pass", "dofus", "PostgreSQL password")
	dbName := flag.String("db-name", "dofus", "PostgreSQL database")
	flag.Parse()

	pool, err := data.NewPool(*dbHost, *dbPort, *dbUser, *dbPass, *dbName)
	if err != nil {
		log.Fatal("Database connection failed:", err)
	}
	defer pool.Close()

	q := sqlc.New(pool)
	mapStore := data.NewMapStore(q)
	world := game.NewWorld(q, mapStore)

	srv := server.New(world, *addr)
	log.Println("[Server] Starting on", *addr)
	if err := srv.Start(); err != nil {
		log.Fatal(err)
	}
}
