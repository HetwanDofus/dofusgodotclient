use wgpu;

/// Flash-matching AA pipeline.
///
/// Flash composited in sRGB color space (no gamma-correct blending), producing
/// characteristic darker edge fringes. Vello composites in linear space.
///
/// This module renders at 2x resolution, then downsamples with an sRGB-aware
/// box filter that mimics Flash's compositing behavior:
/// 1. Convert each 2x2 block from linear → sRGB
/// 2. Average in sRGB space (like Flash would)
/// 3. Store the result (Godot reads it as sRGB via R8G8B8A8_SRGB)
pub struct FlashAA {
    downsample_pipeline: wgpu::ComputePipeline,
    bind_group_layout: wgpu::BindGroupLayout,
}

const DOWNSAMPLE_SHADER: &str = r#"
@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var dst: texture_storage_2d<rgba8unorm, write>;

// Linear → sRGB conversion (matches Ruffle's common__linear_to_srgb)
fn linear_to_srgb(c: f32) -> f32 {
    if c <= 0.0031308 {
        return c * 12.92;
    }
    return 1.055 * pow(c, 1.0 / 2.4) - 0.055;
}

// sRGB → Linear conversion
fn srgb_to_linear(c: f32) -> f32 {
    if c <= 0.04045 {
        return c / 12.92;
    }
    return pow((c + 0.055) / 1.055, 2.4);
}

@compute @workgroup_size(8, 8)
fn main(@builtin(global_invocation_id) gid: vec3<u32>) {
    let dst_size = textureDimensions(dst);
    if gid.x >= dst_size.x || gid.y >= dst_size.y {
        return;
    }

    // Sample 2x2 block from source (rendered at 2x resolution by Vello in linear space)
    let sx = gid.x * 2u;
    let sy = gid.y * 2u;

    let p00 = textureLoad(src, vec2<u32>(sx, sy), 0);
    let p10 = textureLoad(src, vec2<u32>(sx + 1u, sy), 0);
    let p01 = textureLoad(src, vec2<u32>(sx, sy + 1u), 0);
    let p11 = textureLoad(src, vec2<u32>(sx + 1u, sy + 1u), 0);

    // Convert from linear to sRGB BEFORE averaging (Flash's sRGB-space compositing)
    // This produces the characteristic darker edge fringes of Flash Player
    let s00 = vec4<f32>(linear_to_srgb(p00.r), linear_to_srgb(p00.g), linear_to_srgb(p00.b), p00.a);
    let s10 = vec4<f32>(linear_to_srgb(p10.r), linear_to_srgb(p10.g), linear_to_srgb(p10.b), p10.a);
    let s01 = vec4<f32>(linear_to_srgb(p01.r), linear_to_srgb(p01.g), linear_to_srgb(p01.b), p01.a);
    let s11 = vec4<f32>(linear_to_srgb(p11.r), linear_to_srgb(p11.g), linear_to_srgb(p11.b), p11.a);

    // Average in sRGB space (matching Flash's non-gamma-correct blending)
    let avg = (s00 + s10 + s01 + s11) * 0.25;

    // Convert back to linear for storage (Godot applies sRGB decode when sampling via R8G8B8A8_SRGB)
    let result = vec4<f32>(
        srgb_to_linear(avg.r),
        srgb_to_linear(avg.g),
        srgb_to_linear(avg.b),
        avg.a  // alpha stays linear
    );

    textureStore(dst, vec2<u32>(gid.x, gid.y), result);
}
"#;

impl FlashAA {
    pub fn new(device: &wgpu::Device) -> Self {
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("flash_aa_downsample"),
            source: wgpu::ShaderSource::Wgsl(DOWNSAMPLE_SHADER.into()),
        });

        let bind_group_layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("flash_aa_bgl"),
            entries: &[
                wgpu::BindGroupLayoutEntry {
                    binding: 0,
                    visibility: wgpu::ShaderStages::COMPUTE,
                    ty: wgpu::BindingType::Texture {
                        sample_type: wgpu::TextureSampleType::Float { filterable: false },
                        view_dimension: wgpu::TextureViewDimension::D2,
                        multisampled: false,
                    },
                    count: None,
                },
                wgpu::BindGroupLayoutEntry {
                    binding: 1,
                    visibility: wgpu::ShaderStages::COMPUTE,
                    ty: wgpu::BindingType::StorageTexture {
                        access: wgpu::StorageTextureAccess::WriteOnly,
                        format: wgpu::TextureFormat::Rgba8Unorm,
                        view_dimension: wgpu::TextureViewDimension::D2,
                    },
                    count: None,
                },
            ],
        });

        let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("flash_aa_layout"),
            bind_group_layouts: &[&bind_group_layout],
            ..Default::default()
        });

        let downsample_pipeline = device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
            label: Some("flash_aa_pipeline"),
            layout: Some(&pipeline_layout),
            module: &shader,
            entry_point: Some("main"),
            compilation_options: Default::default(),
            cache: None,
        });

        Self {
            downsample_pipeline,
            bind_group_layout,
        }
    }

    /// Downsample a 2x texture to 1x with sRGB-space averaging (Flash-matching AA).
    /// Returns the output texture at half the input dimensions.
    pub fn downsample(
        &self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        src_texture: &wgpu::Texture,
        dst_w: u32,
        dst_h: u32,
    ) -> wgpu::Texture {
        let dst_texture = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("flash_aa_output"),
            size: wgpu::Extent3d {
                width: dst_w,
                height: dst_h,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: wgpu::TextureFormat::Rgba8Unorm,
            usage: wgpu::TextureUsages::STORAGE_BINDING
                | wgpu::TextureUsages::TEXTURE_BINDING
                | wgpu::TextureUsages::COPY_SRC,
            view_formats: &[],
        });

        let src_view = src_texture.create_view(&wgpu::TextureViewDescriptor::default());
        let dst_view = dst_texture.create_view(&wgpu::TextureViewDescriptor::default());

        let bind_group = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("flash_aa_bg"),
            layout: &self.bind_group_layout,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: wgpu::BindingResource::TextureView(&src_view),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: wgpu::BindingResource::TextureView(&dst_view),
                },
            ],
        });

        let mut encoder = device.create_command_encoder(&wgpu::CommandEncoderDescriptor {
            label: Some("flash_aa_encoder"),
        });

        {
            let mut pass = encoder.begin_compute_pass(&wgpu::ComputePassDescriptor {
                label: Some("flash_aa_pass"),
                timestamp_writes: None,
            });
            pass.set_pipeline(&self.downsample_pipeline);
            pass.set_bind_group(0, &bind_group, &[]);
            pass.dispatch_workgroups(
                (dst_w + 7) / 8,
                (dst_h + 7) / 8,
                1,
            );
        }

        queue.submit(Some(encoder.finish()));
        dst_texture
    }
}
