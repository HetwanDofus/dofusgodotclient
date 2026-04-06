.PHONY: help setup db db-create db-migrate rust server client clean

GODOT := /Applications/Godot_mono.app/Contents/MacOS/Godot
DB_USER := dofus
DB_PASS := dofus
DB_NAME := dofus
DB_HOST := localhost
DB_PORT := 5432

help: ## Show this help
	@grep -E '^[a-zA-Z_-]+:.*?##' $(MAKEFILE_LIST) | awk 'BEGIN {FS = ":.*?## "}; {printf "  \033[36m%-15s\033[0m %s\n", $$1, $$2}'

setup: db rust server ## Full setup: database + rust + server

# ── Database ──

db: db-create db-migrate ## Create database and run migrations

db-create: ## Create PostgreSQL database and user
	@echo "Creating database..."
	@psql -h $(DB_HOST) -p $(DB_PORT) -U postgres -tc "SELECT 1 FROM pg_roles WHERE rolname='$(DB_USER)'" | grep -q 1 || \
		psql -h $(DB_HOST) -p $(DB_PORT) -U postgres -c "CREATE ROLE $(DB_USER) WITH LOGIN PASSWORD '$(DB_PASS)';"
	@psql -h $(DB_HOST) -p $(DB_PORT) -U postgres -tc "SELECT 1 FROM pg_database WHERE datname='$(DB_NAME)'" | grep -q 1 || \
		psql -h $(DB_HOST) -p $(DB_PORT) -U postgres -c "CREATE DATABASE $(DB_NAME) OWNER $(DB_USER);"
	@echo "Database ready."

db-migrate: ## Run SQL schema migrations
	@echo "Running migrations..."
	@PGPASSWORD=$(DB_PASS) psql -h $(DB_HOST) -p $(DB_PORT) -U $(DB_USER) -d $(DB_NAME) -f server/db/schema/schema.sql
	@echo "Migrations done."

# ── Rust GDExtension ──

rust: ## Build Rust GDExtension (release)
	cd rust && cargo build --release

rust-debug: ## Build Rust GDExtension (debug)
	cd rust && cargo build

# ── Go Server ──

server: ## Build Go server
	cd server && go build -o dofus-server ./cmd/server/

server-run: server ## Build and run the server
	cd server && ./dofus-server

# ── Client ──

client: ## Run the Godot client
	$(GODOT) --path .

client-export-windows: rust ## Export Windows build
	mkdir -p build/windows
	$(GODOT) --headless --export-release "Windows Desktop" build/windows/DofusRetroFuture.exe
	cp client.cfg build/windows/

# ── Clean ──

clean: ## Remove build artifacts
	rm -rf build/
	cd rust && cargo clean
	cd server && rm -f dofus-server
