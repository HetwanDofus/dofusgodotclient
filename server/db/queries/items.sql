-- name: GetCharacterItems :many
SELECT ci.id, ci.character_id, ci.template_id, ci.quantity, ci.position, ci.effects,
       it.name, it.type, it.gfx_id, it.weight, it.equip_positions
FROM character_items ci
JOIN item_templates it ON ci.template_id = it.id
WHERE ci.character_id = $1;

-- name: GetItemTemplate :one
SELECT id, name, type, super_type, level, gfx_id, description, weight,
       equip_positions, two_handed, effects, item_set_id, usable, stackable
FROM item_templates
WHERE id = $1;

-- name: AddCharacterItem :one
INSERT INTO character_items (character_id, template_id, quantity, position, effects)
VALUES ($1, $2, $3, $4, $5)
RETURNING id;

-- name: UpdateCharacterItemPosition :exec
UPDATE character_items SET position = $2 WHERE id = $1;

-- name: UpdateCharacterItemQuantity :exec
UPDATE character_items SET quantity = $2 WHERE id = $1;

-- name: DeleteCharacterItem :exec
DELETE FROM character_items WHERE id = $1;
