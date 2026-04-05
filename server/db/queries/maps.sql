-- name: GetMapByID :one
SELECT id, width, height, x, y, superarea, background, cells_gzip, walkable_ids
FROM maps
WHERE id = $1;

-- name: GetScriptedCellsByMapID :many
SELECT map_id, cell_id, action_id, event_id, action_args, conditions
FROM scripted_cells
WHERE map_id = $1;

-- name: GetAdjacentMaps :many
SELECT id, x, y
FROM maps
WHERE superarea = $1 AND (
    (x = $2 + 1 AND y = $3) OR
    (x = $2 - 1 AND y = $3) OR
    (x = $2 AND y = $3 + 1) OR
    (x = $2 AND y = $3 - 1)
);
