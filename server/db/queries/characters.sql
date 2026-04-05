-- name: GetCharactersByAccountID :many
SELECT id, account_id, name, class, sex, color1, color2, color3, gfx, level, map_id, cell_id, direction
FROM characters
WHERE account_id = $1;

-- name: GetCharacterByID :one
SELECT id, account_id, name, class, sex, color1, color2, color3, gfx, level,
       map_id, cell_id, direction, vitality, wisdom, strength, chance, agility, intelligence,
       hp, max_hp, ap, mp, xp, xp_low, xp_high, kama
FROM characters
WHERE id = $1;

-- name: UpdateCharacterPosition :exec
UPDATE characters
SET map_id = $2, cell_id = $3, direction = $4
WHERE id = $1;

-- name: CreateCharacter :one
INSERT INTO characters (account_id, name, class, sex, gfx, color1, color2, color3)
VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
RETURNING id;
