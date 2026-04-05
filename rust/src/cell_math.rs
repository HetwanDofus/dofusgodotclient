/// Dofus isometric cell constants (from @dofus/grid)
pub const CELL_WIDTH: f32 = 53.0;
pub const CELL_HALF_WIDTH: f32 = 26.5;
pub const CELL_HEIGHT: f32 = 27.0;
pub const CELL_HALF_HEIGHT: f32 = 13.5;
pub const LEVEL_HEIGHT: f32 = 20.0;

/// Compute the screen position for a cell on the isometric grid.
///
/// The Dofus grid has alternating row widths:
///   - Even rows: W cells ("long rows")
///   - Odd rows: W-1 cells ("short rows")
///   - Stride = 2*W - 1 (cells per pair of rows)
pub fn get_cell_position(cell_id: u32, map_width: u32, ground_level: u32) -> (f32, f32) {
    let stride = 2 * map_width - 1;
    let pair = cell_id / stride;
    let offset = cell_id % stride;
    let is_long = offset < map_width;
    let row = pair * 2 + if is_long { 0 } else { 1 };
    let col = if is_long { offset } else { offset - map_width };

    let x = col as f32 * CELL_WIDTH + if is_long { 0.0 } else { CELL_HALF_WIDTH };
    let y = row as f32 * CELL_HALF_HEIGHT - LEVEL_HEIGHT * (ground_level as f32 - 7.0);

    (x, y)
}
