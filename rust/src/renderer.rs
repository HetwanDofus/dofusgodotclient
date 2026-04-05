use std::collections::HashMap;
use godot::prelude::*;

use dofasset_renderer::format::DofAsset;
use dofasset_renderer::scene_builder;
use dofasset_renderer::scene_builder::AccessoryScene;

use vello::peniko::Color;
use vello::{AaConfig, RenderParams, Renderer, RendererOptions};
use wgpu;

use crate::flash_aa::FlashAA;
use crate::texture_bridge::{self, SharedTexture};

#[derive(GodotClass)]
#[class(base=Object)]
pub struct DofusRenderer {
    base: Base<Object>,
    device: Option<wgpu::Device>,
    queue: Option<wgpu::Queue>,
    renderer: Option<Renderer>,
    flash_aa: Option<FlashAA>,
    assets: HashMap<u32, DofAsset>,
    shared_textures: Vec<SharedTexture>,
}

#[godot_api]
impl IObject for DofusRenderer {
    fn init(base: Base<Object>) -> Self {
        Self {
            base,
            device: None,
            queue: None,
            renderer: None,
            flash_aa: None,
            assets: HashMap::new(),
            shared_textures: Vec::new(),
        }
    }
}

#[godot_api]
impl DofusRenderer {
    #[func]
    fn init_gpu(&mut self) -> bool {
        let backends = if cfg!(target_os = "windows") {
            wgpu::Backends::VULKAN
        } else {
            wgpu::Backends::all()
        };
        let instance = wgpu::Instance::new(&wgpu::InstanceDescriptor {
            backends,
            ..Default::default()
        });

        let adapter = pollster::block_on(instance.request_adapter(&wgpu::RequestAdapterOptions {
            power_preference: wgpu::PowerPreference::HighPerformance,
            ..Default::default()
        }));

        let adapter = match adapter {
            Ok(a) => a,
            Err(e) => {
                godot_error!("[DofusRenderer] No GPU adapter found: {e}");
                return false;
            }
        };

        let (device, queue) = match pollster::block_on(adapter.request_device(
            &wgpu::DeviceDescriptor {
                label: Some("dofus-vello"),
                required_features: adapter.features()
                    & (wgpu::Features::TIMESTAMP_QUERY | wgpu::Features::CLEAR_TEXTURE),
                required_limits: adapter.limits(),
                ..Default::default()
            },
        )) {
            Ok(dq) => dq,
            Err(e) => {
                godot_error!("[DofusRenderer] Device creation failed: {e}");
                return false;
            }
        };

        let renderer = match Renderer::new(
            &device,
            RendererOptions {
                use_cpu: false,
                antialiasing_support: vello::AaSupport::area_only(),
                num_init_threads: std::num::NonZeroUsize::new(1),
                pipeline_cache: None,
            },
        ) {
            Ok(r) => r,
            Err(e) => {
                godot_error!("[DofusRenderer] Vello renderer creation failed: {e}");
                return false;
            }
        };

        let flash_aa = FlashAA::new(&device);

        self.device = Some(device);
        self.queue = Some(queue);
        self.renderer = Some(renderer);
        self.flash_aa = Some(flash_aa);
        godot_print!("[DofusRenderer] GPU initialized (Flash AA enabled)");
        true
    }

    #[func]
    fn load_asset(&mut self, id: u32, path: GString) -> bool {
        let path_str = path.to_string();
        let data = match std::fs::read(&path_str) {
            Ok(d) => d,
            Err(e) => {
                godot_error!("[DofusRenderer] Failed to read {path_str}: {e}");
                return false;
            }
        };

        if data.len() < 4 || &data[0..4] != b"DASF" {
            godot_error!("[DofusRenderer] Invalid .dofasset: {path_str}");
            return false;
        }

        let asset = dofasset_renderer::format::load(&data);
        self.assets.insert(id, asset);
        true
    }

    #[func]
    fn load_asset_from_bytes(&mut self, id: u32, data: PackedByteArray) -> bool {
        let bytes = data.as_slice();
        if bytes.len() < 4 || &bytes[0..4] != b"DASF" {
            godot_error!("[DofusRenderer] Invalid DASF header for asset {id}");
            return false;
        }
        let asset = dofasset_renderer::format::load(bytes);
        self.assets.insert(id, asset);
        true
    }

    #[func]
    fn unload_asset(&mut self, id: u32) {
        self.assets.remove(&id);
    }

    #[func]
    fn load_assets_batch(&mut self, specs: Array<Variant>) -> i32 {
        self.load_assets_batch_impl(specs)
    }

    #[func]
    fn get_animation_names(&self, asset_id: u32) -> PackedStringArray {
        let mut names = PackedStringArray::new();
        if let Some(asset) = self.assets.get(&asset_id) {
            for name in asset.animation_map.keys() {
                names.push(&GString::from(name.as_str()));
            }
        }
        names
    }

    #[func]
    fn get_animation_info(&self, asset_id: u32, animation: GString, resolution: f32) -> Dictionary<Variant, Variant> {
        let mut dict = Dictionary::new();
        let anim_name = animation.to_string();

        let Some(asset) = self.assets.get(&asset_id) else { return dict };
        let Some(&anim_idx) = asset.animation_map.get(&anim_name) else { return dict };
        let anim = &asset.animations[anim_idx];

        let meta = scene_builder::compute_animation_render_meta(asset, &anim_name, resolution, &[]);

        dict.set("fps", anim.fps as i32);
        dict.set("frameCount", anim.frame_ids.len() as i32);
        dict.set("offsetX", anim.offset_x as f64);
        dict.set("offsetY", anim.offset_y as f64);
        dict.set("anchorX", meta.anchor_x);
        dict.set("anchorY", meta.anchor_y);
        dict.set("canvasWidth", meta.canvas_width as i32);
        dict.set("canvasHeight", meta.canvas_height as i32);
        dict
    }

    /// Render ALL frames of an animation into a single strip texture (one GPU dispatch).
    /// Returns Dictionary with: texture, frameWidth, frameHeight, frameCount, gridCols,
    /// anchorX, anchorY, fps.
    #[func]
    fn render_animation_strip(
        &mut self,
        asset_id: u32,
        animation: GString,
        resolution: f32,
        colors: PackedInt32Array,
        acc_info: PackedInt32Array,
    ) -> Dictionary<Variant, Variant> {
        let mut result: Dictionary<Variant, Variant> = Dictionary::new();

        if self.device.is_none() || self.queue.is_none() || self.renderer.is_none() {
            return result;
        }

        let device = self.device.as_ref().unwrap();
        let queue = self.queue.as_ref().unwrap();
        let renderer = self.renderer.as_mut().unwrap();

        let Some(asset) = self.assets.get(&asset_id) else { return result };
        let anim_name = animation.to_string();
        let Some(&anim_idx) = asset.animation_map.get(&anim_name) else { return result };
        let anim = &asset.animations[anim_idx];
        let frame_count = anim.frame_ids.len();
        if frame_count == 0 { return result; }

        let player_colors: Option<[u32; 3]> = if colors.len() >= 3 {
            Some([colors[0] as u32, colors[1] as u32, colors[2] as u32])
        } else {
            None
        };

        let acc_info_vec: Option<Vec<u32>> = if acc_info.len() >= 2 {
            Some((0..acc_info.len()).map(|i| acc_info[i] as u32).collect())
        } else {
            None
        };

        // Compute global bounds across ALL frames (matching WASM renderAnimationStrip)
        let mut global_min_x = 0.0_f64;
        let mut global_min_y = 0.0_f64;
        let mut global_max_x = 0.0_f64;
        let mut global_max_y = 0.0_f64;
        for i in 0..frame_count {
            let acc_scenes_i = Self::build_accessory_scenes(
                &self.assets, &acc_info_vec, &anim_name, i,
            );
            let acc_refs_i: Vec<&AccessoryScene> = acc_scenes_i.iter().collect();
            let (bmin_x, bmin_y, bw, bh) = scene_builder::compute_frame_bounds(
                asset, &anim_name, i, resolution, &acc_refs_i,
            );
            global_min_x = global_min_x.min(bmin_x);
            global_min_y = global_min_y.min(bmin_y);
            global_max_x = global_max_x.max(bmin_x + bw);
            global_max_y = global_max_y.max(bmin_y + bh);
        }

        // bounds_offset shifts content so negative accessory positions are visible
        let bounds_offset = (-global_min_x, -global_min_y);
        let max_w = ((global_max_x - global_min_x).ceil() as u32).max(1).min(8192);
        let max_h = ((global_max_y - global_min_y).ceil() as u32).max(1).min(8192);

        // Also get anchor from compute_animation_render_meta for positioning
        let acc_scenes_0 = Self::build_accessory_scenes(
            &self.assets, &acc_info_vec, &anim_name, 0,
        );
        let acc_refs_0: Vec<&AccessoryScene> = acc_scenes_0.iter().collect();
        let meta = scene_builder::compute_animation_render_meta(
            asset, &anim_name, resolution, &acc_refs_0,
        );

        // Grid layout to stay within 8192x8192
        let max_cols = (8192 / max_w).max(1) as usize;
        let grid_cols = frame_count.min(max_cols);
        let grid_rows = (frame_count + grid_cols - 1) / grid_cols;
        let strip_w = max_w * grid_cols as u32;
        let strip_h = max_h * grid_rows as u32;

        if strip_w > 8192 || strip_h > 8192 { return result; }

        // Build ONE composite scene with all frames in a grid
        let mut composite = vello::Scene::new();
        for i in 0..frame_count {
            let col = i % grid_cols;
            let row = i / grid_cols;

            let acc_scenes = Self::build_accessory_scenes(
                &self.assets, &acc_info_vec, &anim_name, i,
            );
            let acc_refs: Vec<&AccessoryScene> = acc_scenes.iter().collect();

            let frame_scene = scene_builder::build_frame_scene(
                asset, &anim_name, i, player_colors.as_ref(), resolution, &acc_refs, bounds_offset,
            );

            let cx = (col as u32 * max_w) as f64;
            let cy = (row as u32 * max_h) as f64;
            let clip = vello::kurbo::Rect::new(cx, cy, cx + max_w as f64, cy + max_h as f64);
            composite.push_layer(vello::peniko::Fill::NonZero, vello::peniko::Mix::Normal, 1.0, vello::kurbo::Affine::IDENTITY, &clip);
            composite.append(&frame_scene, Some(vello::kurbo::Affine::translate((cx, cy))));
            composite.pop_layer();
        }

        // Direct render at target resolution — Vello's area AA handles edge anti-aliasing
        let render_target = texture_bridge::create_render_texture(device, strip_w, strip_h);
        let rt_view = render_target.create_view(&wgpu::TextureViewDescriptor::default());
        let params = RenderParams {
            base_color: Color::TRANSPARENT,
            width: strip_w,
            height: strip_h,
            antialiasing_method: AaConfig::Area,
        };

        if renderer.render_to_texture(device, queue, &composite, &rt_view, &params).is_err() {
            godot_error!("[DofusRenderer] Strip render failed");
            return result;
        }

        // Read back alpha channel for pixel-perfect picking
        let alpha_map = Self::read_alpha_channel(device, queue, &render_target, strip_w, strip_h);
        let alpha_bytes = PackedByteArray::from(alpha_map.as_slice());
        result.set("alphaMap", &alpha_bytes.to_variant());
        result.set("alphaWidth", strip_w as i32);
        result.set("alphaHeight", strip_h as i32);

        // Share texture with Godot via zero-copy
        if let Some(handle) = texture_bridge::extract_native_handle(&render_target) {
            if let Some((godot_tex, rid)) = texture_bridge::create_godot_texture(handle, strip_w, strip_h) {
                result.set("texture", &godot_tex.to_variant());
                self.shared_textures.push(SharedTexture {
                    wgpu_texture: render_target,
                    godot_rid: rid,
                    width: strip_w,
                    height: strip_h,
                });
            }
        }

        result.set("frameWidth", max_w as i32);
        result.set("frameHeight", max_h as i32);
        result.set("frameCount", frame_count as i32);
        result.set("gridCols", grid_cols as i32);
        // Anchor adjusted by bounds_offset (same as WASM boundsOffsetX/Y subtraction)
        result.set("anchorX", meta.anchor_x + bounds_offset.0);
        result.set("anchorY", meta.anchor_y + bounds_offset.1);
        result.set("fps", anim.fps as i32);

        result
    }

    /// Render a batch of individual tiles. Each spec is a Dictionary with
    /// asset_id, animation, frame_index, and optional colors/acc_info.
    #[func]
    fn render_tiles_batch(
        &mut self,
        tile_specs: Array<Variant>,
        resolution: f32,
    ) -> Array<Variant> {
        self.render_tiles_batch_impl(tile_specs, resolution)
    }

    #[func]
    fn cleanup(&mut self) {
        self.free_shared_textures();
    }
}

impl DofusRenderer {
    fn free_shared_textures(&mut self) {
        for shared in self.shared_textures.drain(..) {
            texture_bridge::free_shared(shared);
        }
        if let Some(device) = &self.device {
            device.poll(wgpu::PollType::Wait { submission_index: None, timeout: None }).ok();
        }
    }

    /// Read back just the alpha channel from a wgpu texture. Returns one byte per pixel.
    fn read_alpha_channel(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        texture: &wgpu::Texture,
        w: u32,
        h: u32,
    ) -> Vec<u8> {
        let bytes_per_row_unaligned = w * 4;
        let bytes_per_row = ((bytes_per_row_unaligned + 255) / 256) * 256;
        let buffer_size = (bytes_per_row * h) as u64;

        let staging = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("alpha_readback"),
            size: buffer_size,
            usage: wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::MAP_READ,
            mapped_at_creation: false,
        });

        let mut encoder = device.create_command_encoder(&wgpu::CommandEncoderDescriptor::default());
        encoder.copy_texture_to_buffer(
            wgpu::TexelCopyTextureInfo {
                texture,
                mip_level: 0,
                origin: wgpu::Origin3d::ZERO,
                aspect: wgpu::TextureAspect::All,
            },
            wgpu::TexelCopyBufferInfo {
                buffer: &staging,
                layout: wgpu::TexelCopyBufferLayout {
                    offset: 0,
                    bytes_per_row: Some(bytes_per_row),
                    rows_per_image: None,
                },
            },
            wgpu::Extent3d { width: w, height: h, depth_or_array_layers: 1 },
        );
        queue.submit(Some(encoder.finish()));

        let slice = staging.slice(..);
        slice.map_async(wgpu::MapMode::Read, |_| {});
        device.poll(wgpu::PollType::Wait { submission_index: None, timeout: None }).ok();

        let mapped = slice.get_mapped_range();
        let mut alpha = vec![0u8; (w * h) as usize];
        for row in 0..h {
            let src_offset = (row * bytes_per_row) as usize;
            for col in 0..w {
                // Alpha is byte 3 of each RGBA pixel
                alpha[(row * w + col) as usize] = mapped[src_offset + (col as usize) * 4 + 3];
            }
        }
        drop(mapped);
        staging.unmap();

        alpha
    }

    fn load_assets_batch_impl(&mut self, specs: Array<Variant>) -> i32 {
        let pairs: Vec<(u32, String)> = specs.iter_shared()
            .filter_map(|v| {
                let dict: Dictionary<Variant, Variant> = v.to();
                let id = dict.get("id").map(|v| v.to::<u32>())?;
                let path = dict.get("path").map(|v| v.to::<GString>())?.to_string();
                Some((id, path))
            })
            .collect();

        let handles: Vec<_> = pairs.into_iter()
            .map(|(id, path)| {
                std::thread::spawn(move || {
                    let data = std::fs::read(&path).ok()?;
                    if data.len() >= 4 && &data[0..4] == b"DASF" {
                        Some((id, dofasset_renderer::format::load(&data)))
                    } else {
                        None
                    }
                })
            })
            .collect();

        let mut count = 0i32;
        for handle in handles {
            if let Ok(Some((id, asset))) = handle.join() {
                self.assets.insert(id, asset);
                count += 1;
            }
        }
        count
    }

    fn render_tiles_batch_impl(
        &mut self,
        tile_specs: Array<Variant>,
        resolution: f32,
    ) -> Array<Variant> {
        let mut results: Array<Variant> = Array::new();

        if self.device.is_none() || self.queue.is_none() || self.renderer.is_none() {
            return results;
        }

        let device = self.device.as_ref().unwrap();
        let queue = self.queue.as_ref().unwrap();
        let renderer = self.renderer.as_mut().unwrap();

        device.poll(wgpu::PollType::Wait { submission_index: None, timeout: None }).ok();

        struct TileRender {
            texture: wgpu::Texture,
            meta: scene_builder::AnimationRenderMeta,
            fps: u16,
            frame_count: usize,
            w: u32,
            h: u32,
            need_alpha: bool,
        }

        let mut tile_renders: Vec<Option<TileRender>> = Vec::with_capacity(tile_specs.len());

        for spec_variant in tile_specs.iter_shared() {
            let spec: Dictionary<Variant, Variant> = spec_variant.to();
            let asset_id = spec.get("asset_id").map(|v| v.to::<u32>()).unwrap_or(0);
            let animation = spec.get("animation").map(|v| v.to::<GString>()).unwrap_or_default();
            let frame_index = spec.get("frame_index").map(|v| v.to::<u32>()).unwrap_or(0);
            let need_alpha = spec.get("need_alpha").map(|v| v.to::<bool>()).unwrap_or(false);
            let anim_name = animation.to_string();

            let Some(asset) = self.assets.get(&asset_id) else {
                tile_renders.push(None);
                continue;
            };

            let Some(&anim_idx) = asset.animation_map.get(&anim_name) else {
                tile_renders.push(None);
                continue;
            };
            let anim = &asset.animations[anim_idx];

            let player_colors: Option<[u32; 3]> = spec.get("colors")
                .and_then(|v| {
                    let colors = v.to::<PackedInt32Array>();
                    if colors.len() >= 3 {
                        Some([colors[0] as u32, colors[1] as u32, colors[2] as u32])
                    } else {
                        None
                    }
                });

            let acc_info = Self::parse_acc_info(&spec);
            let acc_scenes = Self::build_accessory_scenes(
                &self.assets, &acc_info, &anim_name, frame_index as usize,
            );
            let acc_refs: Vec<&AccessoryScene> = acc_scenes.iter().collect();

            let meta = scene_builder::compute_animation_render_meta(
                asset, &anim_name, resolution, &acc_refs,
            );

            let w = meta.canvas_width.max(1).min(8192);
            let h = meta.canvas_height.max(1).min(8192);

            let scene = scene_builder::build_frame_scene(
                asset, &anim_name, frame_index as usize,
                player_colors.as_ref(), resolution, &acc_refs, (0.0, 0.0),
            );

            let render_target = texture_bridge::create_render_texture(device, w, h);
            let rt_view = render_target.create_view(&wgpu::TextureViewDescriptor::default());

            let params = RenderParams {
                base_color: Color::TRANSPARENT,
                width: w,
                height: h,
                antialiasing_method: AaConfig::Area,
            };

            if renderer.render_to_texture(device, queue, &scene, &rt_view, &params).is_err() {
                godot_error!("[DofusRenderer] Vello render failed for tile");
                tile_renders.push(None);
                continue;
            }

            tile_renders.push(Some(TileRender {
                texture: render_target,
                meta,
                fps: anim.fps,
                frame_count: anim.frame_ids.len(),
                w,
                h,
                need_alpha,
            }));
        }

        device.poll(wgpu::PollType::Wait { submission_index: None, timeout: None }).ok();

        for tile_opt in tile_renders {
            let mut dict: Dictionary<Variant, Variant> = Dictionary::new();

            let Some(tile) = tile_opt else {
                results.push(&dict.to_variant());
                continue;
            };

            // Alpha map for interactive tiles (must read before texture is moved)
            if tile.need_alpha {
                let alpha = Self::read_alpha_channel(device, queue, &tile.texture, tile.w, tile.h);
                let alpha_bytes = PackedByteArray::from(alpha.as_slice());
                dict.set("alphaMap", &alpha_bytes.to_variant());
                dict.set("alphaWidth", tile.w as i32);
                dict.set("alphaHeight", tile.h as i32);
            }

            if let Some(handle) = texture_bridge::extract_native_handle(&tile.texture) {
                if let Some((godot_tex, rid)) = texture_bridge::create_godot_texture(handle, tile.w, tile.h) {
                    dict.set("texture", &godot_tex.to_variant());
                    self.shared_textures.push(SharedTexture {
                        wgpu_texture: tile.texture,
                        godot_rid: rid,
                        width: tile.w,
                        height: tile.h,
                    });
                }
            }

            dict.set("anchorX", tile.meta.anchor_x);
            dict.set("anchorY", tile.meta.anchor_y);
            dict.set("canvasWidth", tile.w as i32);
            dict.set("canvasHeight", tile.h as i32);
            dict.set("fps", tile.fps as i32);
            dict.set("frameCount", tile.frame_count as i32);

            results.push(&dict.to_variant());
        }

        results
    }

    fn parse_acc_info(spec: &Dictionary<Variant, Variant>) -> Option<Vec<u32>> {
        let arr = spec.get("acc_info").map(|v| v.to::<PackedInt32Array>())?;
        if arr.len() < 2 { return None; }
        Some((0..arr.len()).map(|i| arr[i] as u32).collect())
    }

    fn build_accessory_scenes(
        assets: &HashMap<u32, DofAsset>,
        acc_info: &Option<Vec<u32>>,
        animation: &str,
        frame_index: usize,
    ) -> Vec<AccessoryScene> {
        let Some(info) = acc_info else { return Vec::new() };
        if info.len() < 2 { return Vec::new(); }

        let dir_suffix = if animation.len() >= 2 {
            &animation[animation.len()-1..]
        } else {
            "S"
        };

        let anim_type = if animation.starts_with("run") { 2u8 }
            else if animation.starts_with("static") { 0 }
            else { 1 };

        let mut scenes = Vec::new();
        for pair in info.chunks(2) {
            if pair.len() < 2 { continue; }
            let acc_asset_id = pair[0];
            let slot_id = pair[1] as u8;

            let Some(acc_asset) = assets.get(&acc_asset_id) else { continue };

            let Some(resolved_anim) = resolve_accessory_anim(
                acc_asset, animation, dir_suffix, anim_type, slot_id,
            ) else { continue };

            let Some(&anim_idx) = acc_asset.animation_map.get(resolved_anim.as_str()) else { continue };
            let anim = &acc_asset.animations[anim_idx];
            if anim.frame_ids.is_empty() { continue; }

            let actual_frame = frame_index % anim.frame_ids.len();
            let scene = scene_builder::build_accessory_scene_unscaled(
                acc_asset, &resolved_anim, actual_frame,
            );

            let global_fid = anim.frame_ids[actual_frame] as usize;
            let (offset_x, offset_y, acc_w, acc_h) = if let Some(acc_frame) = acc_asset.frames.get(global_fid) {
                let net = scene_builder::compute_net_offset(acc_frame, &acc_asset.transforms);
                (-net.0, -net.1, acc_frame.clip_rect[2] as f64, acc_frame.clip_rect[3] as f64)
            } else {
                (anim.offset_x as f64, anim.offset_y as f64, 0.0, 0.0)
            };

            scenes.push(AccessoryScene {
                slot_id,
                scene,
                offset_x,
                offset_y,
                width: acc_w,
                height: acc_h,
            });
        }
        scenes
    }
}

fn resolve_accessory_anim(
    acc_asset: &DofAsset,
    animation: &str,
    dir_suffix: &str,
    anim_type: u8,
    slot_id: u8,
) -> Option<String> {
    if acc_asset.animation_map.contains_key(animation) {
        return Some(animation.to_string());
    }

    if anim_type > 0 {
        let w_dir = format!("W{}", dir_suffix);
        if acc_asset.animation_map.contains_key(w_dir.as_str()) {
            return Some(w_dir);
        }
    }

    let candidates: &[&str] = match slot_id {
        2 => match (anim_type, dir_suffix) {
            (0, "R") | (0, "F") | (0, "S") => &["R", "L"],
            (0, "B") | (0, "L")            => &["L", "R"],
            (1, "R") | (1, "F")            => &["R", "L"],
            (1, "B") | (1, "L") | (1, "S") => &["L", "R"],
            (2, "R") | (2, "F")            => &["RR", "R", "L"],
            (2, "B") | (2, "L")            => &["RL", "L", "R"],
            (2, _)                          => &["L", "R"],
            _                               => &["R", "L"],
        },
        4 => match (anim_type, dir_suffix) {
            (0, "L")                        => &["L", "R"],
            (0, _)                          => &["R", "L"],
            (1, "F") | (1, "B") | (1, "L") => &["L", "R"],
            (1, _)                          => &["R", "L"],
            (2, "F") | (2, "L")            => &["L", "R"],
            (2, _)                          => &["R", "L"],
            _                               => &["R", "L"],
        },
        _ => match dir_suffix {
            "R" => &["R", "S", "F"],
            "L" => &["L", "S", "F"],
            "F" => &["F", "S", "R"],
            "B" => &["B", "S", "L"],
            "S" => &["S", "R", "F"],
            _   => &["R", "L", "S"],
        },
    };

    for &c in candidates {
        if acc_asset.animation_map.contains_key(c) {
            return Some(c.to_string());
        }
    }

    None
}
