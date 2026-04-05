# Implementation Plan: Vello 0.8 Upgrade + Godot GDExtension

## Overview

Upgrade the dofasset-renderer from Vello 0.5 to 0.8, create a Godot 4.6 GDExtension in Rust that wraps it, and render Dofus maps natively. The renderer crate stays shared between the WASM wrapper (web) and the new GDExtension (Godot).

## Phase 1: Vello 0.5 → 0.8 Upgrade (renderer crate only)

Three versions of breaking changes to handle: 0.5→0.6→0.7→0.8.
WASM wrapper is NOT being updated — Godot replaces it.

### `packages/renderer/Cargo.toml`
- Update `vello = "0.5"` → `vello = "0.8"`
- Update `wgpu = "24"` → `wgpu = "28"` (vello 0.8 requires wgpu 28)
- Remove `vello_svg` if unused (or update to matching version)
- Remove the `[patch.crates-io] wgpu` override — no more patched wgpu needed for native

### `packages/renderer/src/scene_builder.rs`
- **Gradients (0.6 breaking):** All `Gradient::new_linear()` and `Gradient::new_two_point_radial()` calls must add `.with_alpha_interpolation(peniko::InterpolationAlphaSpace::Premultiplied)`
- **Image → ImageBrush (0.6 breaking):** Replace `peniko::Image` with `peniko::ImageBrush`. The `ImageData` and `ImageSampler` components replace the old `Image` struct
- **push_layer (0.7 breaking):** All `scene.push_layer(mix, alpha, affine, &clip)` calls that use clipping need to be updated. If using `Mix::Clip`, replace with `scene.push_clip_layer(fill_style, affine, &clip)` using `Fill::NonZero`. For non-clip layers, add the draw style parameter
- **Stroke rendering:** Verify zero-width strokes don't change behavior (0.6 changed zero-width stroke rendering)

### `packages/renderer/src/pattern.rs`
- **Image → ImageBrush:** Update `create_pattern_brush()` to use `ImageBrush` instead of `Image`
- Update `Extend::Repeat` if the import path changed

### `packages/renderer/src/format.rs`
- **Image decoding:** Update `peniko::Image` references to `peniko::ImageBrush` / `ImageData`
- The `Blob` type for image data may have changed — verify `Blob::new(Arc<[u8]>)` still works

### `packages/renderer/src/color.rs`
- **Color API:** Verify `peniko::Color::from_rgba8()` and `to_rgba8()` still exist (likely unchanged)

### Verification
- Run `cargo build` for the renderer crate (native only, no WASM)
- Test rendering a .dofasset file with the bench/windowed binaries
- Verify visual correctness against known-good reference renders

---

## Phase 2: Godot GDExtension Crate

### `dofus-retro-future-godot/rust/Cargo.toml` (new file)
- Create cdylib crate with dependencies:
  - `godot = { git = "https://github.com/godot-rust/gdext", branch = "master" }`
  - `dofasset-renderer = { path = "../../dofus-vello-custom-format/packages/renderer" }`
  - `vello = "0.8"`
  - `wgpu = "28"`
  - `pollster = "0.4"` (for blocking async GPU operations)
- Set `crate-type = ["cdylib"]`
- Add release profile with `opt-level = 3` and `lto = true`

### `dofus-retro-future-godot/rust/src/lib.rs` (new file)
- Entry point with `#[gdextension]` macro and `ExtensionLibrary` impl
- Register all custom classes

### `dofus-retro-future-godot/rust/src/renderer.rs` (new file)
- Singleton `DofusRenderer` class (`#[class(base=Object)]`)
- Owns: wgpu Instance, Adapter, Device, Queue, vello::Renderer
- Initialize in `_ready()` or explicit `init()` method using `pollster::block_on`
- Methods:
  - `load_asset(id: u32, path: GString) -> bool` — read file, parse .dofasset, cache in HashMap
  - `render_frame(asset_id: u32, animation: GString, frame: u32, width: u32, height: u32, colors: PackedInt32Array) -> Gd<Image>` — render to vello Scene, render_to_texture, readback pixels to Godot Image
  - `get_animation_info(asset_id: u32, animation: GString) -> Dictionary` — return fps, frameCount, offsetX, offsetY
  - `get_animation_names(asset_id: u32) -> PackedStringArray` — list all animations
- Pixel readback flow:
  1. Build Scene via `build_frame_scene()`
  2. Create wgpu texture (STORAGE_BINDING | COPY_SRC)
  3. `renderer.render_to_texture()`
  4. Create staging buffer (COPY_DST | MAP_READ)
  5. Copy texture → buffer
  6. `buffer.map_async()` + `device.poll(Maintain::Wait)` (blocking in native)
  7. Read mapped bytes → `Image::create_from_data(w, h, false, Format::RGBA8, PackedByteArray)`

### `dofus-retro-future-godot/rust/src/map_renderer.rs` (new file)
- `DofusMapRenderer` class (`#[class(base=Node2D)]`)
- Exports: `map_data_path: GString`
- Owns reference to `DofusRenderer` singleton
- Methods:
  - `load_map(path: GString)` — parse map JSON, load all referenced tile .dofassets
  - `render_map()` — render all cells, create Godot ImageTextures, spawn Sprite2D children
- Map cell rendering:
  - Parse map JSON (id, width, height, cells array)
  - For each cell: compute isometric position using ported `getCellPosition()` logic
  - Render ground/object1/object2 tiles as individual frames
  - Cache rendered tiles (same tileId+rotation+flip = same texture)
  - Create Sprite2D per cell with correct position and z_index (cellId * 100 for objects, cellId for ground)

### `dofus-retro-future-godot/rust/src/cell_math.rs` (new file)
- Port isometric cell math from `@dofus/grid` package:
  - `get_cell_position(cell_id, map_width, ground_level) -> (f32, f32)`
  - Constants: CELL_WIDTH=53, CELL_HALF_WIDTH=26.5, CELL_HEIGHT=27, CELL_HALF_HEIGHT=13.5, LEVEL_HEIGHT=20
  - Zigzag row/column calculation: stride = 2*width - 1, pair = cell_id / stride, offset = cell_id % stride

### `dofus-retro-future-godot/dofus_vello.gdextension` (new file)
- Configuration section: `entry_symbol = "gdext_rust_init"`, `compatibility_minimum = 4.6`
- Libraries section: macos.arm64 pointing to `res://rust/target/aarch64-apple-darwin/release/libdofus_vello.dylib`
- Add linux and windows paths for cross-platform

---

## Phase 3: Godot Project Setup

### `dofus-retro-future-godot/project.godot`
- No rendering changes needed (Forward Plus is fine for 2D with Sprite2D nodes)
- Add autoload for the DofusRenderer singleton

### `dofus-retro-future-godot/scenes/map.tscn` (new file)
- Root: Node2D
- Child: DofusMapRenderer node (custom class from GDExtension)
- Child: Camera2D for panning/zooming

### `dofus-retro-future-godot/scenes/main.tscn` (new file)
- Root: Node
- Child: map.tscn instance
- Script: Load map data and trigger rendering

### `dofus-retro-future-godot/scripts/main.gd` (new file)
- `_ready()`: Get DofusRenderer singleton, call `init()`
- Load map JSON from `res://assets/maps/{mapId}.json`
- Pass to DofusMapRenderer
- Set up Camera2D controls (pan, zoom)

### Asset Symlinks/Copies
- Link or copy `.dofasset` tile files to `res://assets/tiles/ground/` and `res://assets/tiles/objects/`
- Link or copy map JSON files to `res://assets/maps/`
- Link or copy sprite `.dofasset` files to `res://assets/sprites/`

---

## Phase 4: Optimization (after basic rendering works)

### Tile Atlas Batching
- Instead of one Sprite2D per tile, batch tiles into large atlas textures
- Render all ground tiles for visible area into one texture
- Render all object tiles into another texture with proper z-ordering
- Use SubViewport or manual texture composition

### Strip Rendering for Characters
- Port the grid-based strip rendering from WASM (renderAnimationStrip)
- Pre-render all animation frames into atlas textures
- Use AnimatedSprite2D with SpriteFrames from the atlas

---

## Dependencies / Order

1. **Vello 0.8 upgrade** (Phase 1) — renderer crate only, no WASM
2. **GDExtension crate setup** (Phase 2) depends on Phase 1
3. **Godot project** (Phase 3) depends on Phase 2
4. **Optimization** (Phase 4) after basic rendering works

## Risks

- **Pixel readback performance:** CPU readback per tile is slow. Batch rendering into atlas textures is critical for map rendering performance.
- **gdext stability:** godot-rust/gdext is pre-1.0. API may change. Pin to a specific commit.
- **Godot texture format:** Verify RGBA8Unorm from wgpu maps to Godot's `Image.FORMAT_RGBA8`.
