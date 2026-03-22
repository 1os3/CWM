shader_type canvas_item;

uniform float sway_strength = 0.8;
uniform float sway_speed = 2.0;

void vertex() {
    float mask = clamp(1.0 - UV.y, 0.0, 1.0);
    vec2 world_vertex = (MODEL_MATRIX * vec4(VERTEX, 0.0, 1.0)).xy;
    VERTEX.x += sin((TIME * sway_speed) + (world_vertex.y * 0.07)) * sway_strength * mask;
}
