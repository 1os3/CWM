shader_type canvas_item;

uniform float wave_speed = 1.15;
uniform float wave_strength = 0.025;

void fragment() {
    vec2 uv = UV;
    uv.y += sin((UV.x * 30.0) + (TIME * wave_speed)) * wave_strength;
    uv.x += cos((UV.y * 21.0) - (TIME * wave_speed * 0.7)) * wave_strength;

    vec4 base_color = texture(TEXTURE, uv);
    float sparkle = sin((UV.x + UV.y + TIME * 0.25) * 42.0) * 0.04;
    COLOR = vec4(base_color.rgb + vec3(0.02, 0.04, 0.07) + sparkle, base_color.a);
}
