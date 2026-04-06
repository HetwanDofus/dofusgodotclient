# Dockerfile for the Go game server only (client is native/desktop)
FROM golang:1.26-alpine AS builder

WORKDIR /app
COPY server/go.mod server/go.sum ./
RUN go mod download
COPY server/ .
RUN CGO_ENABLED=0 go build -o dofus-server ./cmd/server/

FROM alpine:3.21
RUN apk add --no-cache ca-certificates
COPY --from=builder /app/dofus-server /usr/local/bin/
COPY server/config.toml /etc/dofus/config.toml

EXPOSE 8080
ENTRYPOINT ["dofus-server", "-config", "/etc/dofus/config.toml"]
