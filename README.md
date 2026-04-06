# Dofus Retro Future (Godot)

Desktop client for Dofus 1.29 built with Godot 4.6, C#, and a Rust GPU renderer (Vello).

## Architecture

```
C# (game logic) ──> Godot 4.6 (scene tree, input, UI)
                        │
Rust GDExtension ──> Vello/wgpu (GPU tile & sprite rendering)
                        │
                   Zero-copy texture sharing (Metal/Vulkan)
```

- **C#** — Game manager, networking (protobuf over WebSocket), pathfinding, interaction
- **Rust GDExtension** — Vello vector renderer, `.dofasset` format loader, GPU texture bridge
- **Go server** — WebSocket game server with PostgreSQL persistence
- **Shared renderer** — [`vello-dofasset-format`](https://github.com/HetwanDofus/vello-dofasset-format) git submodule

## Prerequisites

- [Godot 4.6 .NET](https://godotengine.org/download)
- [Rust](https://rustup.rs/)
- [Go 1.26+](https://go.dev/dl/)
- [PostgreSQL 15+](https://www.postgresql.org/download/)

## Quick Start

```bash
# Full setup: database + Rust GDExtension + Go server
make setup

# Start the server
make server-run

# Open in Godot editor
make client
```

## Commands

| Command | Description |
|---------|-------------|
| `make setup` | Create DB, build Rust + Go |
| `make db` | Create database and run migrations |
| `make rust` | Build Rust GDExtension (release) |
| `make server` | Build Go server |
| `make server-run` | Build and run the server |
| `make client` | Open project in Godot |
| `make client-export-windows` | Export Windows build |
| `make clean` | Remove all build artifacts |

## Project Structure

```
src/
  Core/           # GameManager, CellGrid, pathfinding, constants
  Rendering/      # MapRenderer, VelloRenderer, GridOverlay, SpriteAnimator
  Network/        # WebSocket client, protobuf message handling
  Entities/       # Actor movement and animation
  Interaction/    # Picking system, hover effects, UI interactions
  UI/             # ZaapPopup, UIPlaceholder
rust/
  src/            # Vello GPU renderer (GDExtension)
  dofasset-renderer/  # Shared .dofasset format (git submodule)
server/
  cmd/server/     # Go server entry point
  internal/       # Server logic, database, game world
  config.toml     # Server configuration
proto/
  game.proto      # Protobuf message definitions
assets/
  tiles/          # Ground and object .dofasset tiles
  sprites/        # Character .dofasset sprites
  maps/           # Map JSON data (local fallback)
```

## Configuration

**Server** (`server/config.toml`):
```toml
[server]
addr = "0.0.0.0:8080"

[database]
host = "localhost"
port = "5432"
user = "dofus"
password = "dofus"
name = "dofus"
```

**Client** (`client.cfg`):
```ini
[server]
url="ws://localhost:8080/ws"
username="admin"

[client]
connect=true
```

## Docker

```bash
# Start PostgreSQL + game server
docker compose up -d

# Or just the database
docker compose up -d postgres
```

## Windows Build

Automated via GitHub Actions — push to `main` triggers a build. Download the artifact from the Actions tab.

Manual: `make client-export-windows`

## Rendering

The client uses a zero-copy GPU rendering pipeline:

1. **Vello** renders vector `.dofasset` tiles/sprites on the GPU
2. Native texture handle (Metal on macOS, Vulkan on Windows/Linux) is shared directly with Godot's `Texture2DRd`
3. No CPU readback — Godot draws from the same GPU memory

This enables 1000+ animated actors at 60fps.
