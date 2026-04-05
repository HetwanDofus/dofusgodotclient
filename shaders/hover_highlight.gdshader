shader_type canvas_item;

// Dofus 1.29 hover effect: output = input * 0.6 + 0.4
void fragment() {
	vec4 c = texture(TEXTURE, UV);
	c.rgb = c.rgb * 0.6 + 0.4;
	COLOR = c;
}
