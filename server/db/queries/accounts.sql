-- name: GetAccountByUsername :one
SELECT id, username, password, pseudo
FROM accounts
WHERE username = $1;

-- name: CreateAccount :one
INSERT INTO accounts (username, password, pseudo)
VALUES ($1, $2, $3)
RETURNING id, username, password, pseudo;
