package main

import (
	"flag"
	"log"
	"os"

	"github.com/BurntSushi/toml"

	"dofus-server/internal/data"
	"dofus-server/internal/game"
	"dofus-server/internal/server"
	"dofus-server/internal/sqlc"
)

type Config struct {
	Server   ServerConfig   `toml:"server"`
	Database DatabaseConfig `toml:"database"`
}

type ServerConfig struct {
	Addr string `toml:"addr"`
}

type DatabaseConfig struct {
	Host     string `toml:"host"`
	Port     string `toml:"port"`
	User     string `toml:"user"`
	Password string `toml:"password"`
	Name     string `toml:"name"`
}

func main() {
	configPath := flag.String("config", "config.toml", "Path to config file")
	flag.Parse()

	cfg := Config{
		Server:   ServerConfig{Addr: ":8080"},
		Database: DatabaseConfig{Host: "localhost", Port: "5432", User: "dofus", Password: "dofus", Name: "dofus"},
	}

	if _, err := os.Stat(*configPath); err == nil {
		if _, err := toml.DecodeFile(*configPath, &cfg); err != nil {
			log.Fatalf("Failed to parse config %s: %v", *configPath, err)
		}
		log.Printf("[Config] Loaded %s", *configPath)
	}

	pool, err := data.NewPool(cfg.Database.Host, cfg.Database.Port, cfg.Database.User, cfg.Database.Password, cfg.Database.Name)
	if err != nil {
		log.Fatal("Database connection failed:", err)
	}
	defer pool.Close()

	q := sqlc.New(pool)
	mapStore := data.NewMapStore(q)
	world := game.NewWorld(q, mapStore)

	srv := server.New(world, cfg.Server.Addr)
	log.Println("[Server] Starting on", cfg.Server.Addr)
	if err := srv.Start(); err != nil {
		log.Fatal(err)
	}
}
