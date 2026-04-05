use godot::classes::{RenderingServer, Texture2Drd};
use godot::classes::rendering_device::{DataFormat, TextureSamples, TextureType as RdTextureType, TextureUsageBits};
use godot::prelude::*;

/// A wgpu texture shared with Godot's RenderingServer.
/// The wgpu::Texture MUST remain alive while Godot references its native handle.
pub struct SharedTexture {
    pub wgpu_texture: wgpu::Texture,
    pub godot_rid: Rid,
    #[allow(dead_code)]
    pub width: u32,
    #[allow(dead_code)]
    pub height: u32,
}

/// Extract the native GPU handle from a wgpu texture.
pub fn extract_native_handle(texture: &wgpu::Texture) -> Option<u64> {
    #[cfg(target_vendor = "apple")]
    {
        return extract_native_handle_metal(texture);
    }

    #[cfg(all(not(target_vendor = "apple"), any(target_os = "linux", target_os = "windows")))]
    {
        return extract_native_handle_vulkan(texture);
    }

    #[cfg(not(any(target_vendor = "apple", target_os = "linux", target_os = "windows")))]
    {
        return None;
    }
}

#[cfg(target_vendor = "apple")]
fn extract_native_handle_metal(texture: &wgpu::Texture) -> Option<u64> {
    use metal::foreign_types::ForeignType;
    unsafe {
        let hal_texture = texture.as_hal::<wgpu_hal::api::Metal>()?;
        let raw = hal_texture.raw_handle();
        Some(raw.as_ptr() as u64)
    }
}

#[cfg(all(not(target_vendor = "apple"), any(target_os = "linux", target_os = "windows")))]
fn extract_native_handle_vulkan(texture: &wgpu::Texture) -> Option<u64> {
    use ash::vk::Handle;
    unsafe {
        let hal_texture = texture.as_hal::<wgpu_hal::api::Vulkan>()?;
        let raw = hal_texture.raw_handle();
        Some(raw.as_raw())
    }
}

/// Create a Godot Texture2DRd backed by a native GPU handle (zero-copy).
pub fn create_godot_texture(native_handle: u64, w: u32, h: u32) -> Option<(Gd<Texture2Drd>, Rid)> {
    let rs = RenderingServer::singleton();
    let mut rd = rs.get_rendering_device()?;

    // Vello blends in sRGB space (Color::from_rgba8 doesn't linearize).
    // Output values are sRGB. Use UNORM so Godot reads them as-is without sRGB decode.
    let rd_rid = rd.texture_create_from_extension(
        RdTextureType::TYPE_2D,
        DataFormat::R8G8B8A8_UNORM,
        TextureSamples::SAMPLES_1,
        TextureUsageBits::SAMPLING_BIT,
        native_handle,
        w as u64,
        h as u64,
        1,
        1,
    );

    if !rd_rid.is_valid() {
        godot_error!("[texture_bridge] texture_create_from_extension returned invalid Rid");
        return None;
    }

    let mut tex = Texture2Drd::new_gd();
    tex.set_texture_rd_rid(rd_rid);

    Some((tex, rd_rid))
}

/// Create a wgpu render target texture suitable for Vello + Godot sharing.
pub fn create_render_texture(device: &wgpu::Device, w: u32, h: u32) -> wgpu::Texture {
    device.create_texture(&wgpu::TextureDescriptor {
        label: Some("vello_shared"),
        size: wgpu::Extent3d {
            width: w,
            height: h,
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
    })
}

/// Free a shared texture: release Godot's RD Rid, then drop the wgpu texture.
pub fn free_shared(shared: SharedTexture) {
    let rs = RenderingServer::singleton();
    if let Some(mut rd) = rs.get_rendering_device() {
        if shared.godot_rid.is_valid() {
            rd.free_rid(shared.godot_rid);
        }
    }
    drop(shared.wgpu_texture);
}
