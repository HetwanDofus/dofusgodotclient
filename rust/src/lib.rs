mod flash_aa;
mod renderer;
mod texture_bridge;

use godot::prelude::*;

struct DofusVelloExtension;

#[gdextension]
unsafe impl ExtensionLibrary for DofusVelloExtension {}
