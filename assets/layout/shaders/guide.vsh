#version 330 core

// Layout guide vertex shader - the lean replacement for Vintage Story's standard.vsh.
//
// ASCII ONLY. A previous revision carried em-dashes in these comments; GLSL source is expected to be
// plain ASCII and the program failed to compile. Do not paste typographic punctuation into this file.
//
// Attribute locations match a MeshData built with xyz + uv + rgba (withNormals:false, withFlags:false),
// which is what GuideMeshBuilder emits. layoutUvIn is declared but unused: keeping the mesh format
// byte-identical to the standard-shader path makes shader EXECUTION the only changed variable.
//
// What standard.vsh does per vertex that a guide does not need, and this omits:
//   - applyVertexWarping / applyGlobalWarping  (wind + noise vertex animation)
//   - calcShadowMapCoords                      (shadow-map coordinate transforms)
//   - unpackNormal + normalize + mat4 multiply (a normal nothing downstream reads)
//   - applyLight                               (sun/block/point-light mixing)
//   - getSpheresFogAmount, distance-fade
// At 17.5M vertices per frame on an immense guide, none of that is free.
//
// Locals and uniforms carry a layout* prefix so an engine-injected #define cannot substitute one of
// them and produce a baffling syntax error.

layout(location = 0) in vec3 layoutPositionIn;
layout(location = 1) in vec2 layoutUvIn;
layout(location = 2) in vec4 layoutColorIn;

uniform mat4 projectionMatrix;
uniform mat4 viewMatrix;
uniform mat4 modelMatrix;

// Per-frame RGB multiplier standing in for what standard.vsh's applyLight() and standard.fsh's
// shadow-map term used to do. Computed once per frame on the CPU from the world's ambient colour, so it
// costs one multiply here instead of a light mix per vertex and nine shadow fetches per fragment.
uniform vec3 layoutBrightnessIn;

uniform vec4 layoutFogColorIn;
uniform float layoutFogMinIn;
uniform float layoutFogDensityIn;
uniform float layoutFlatFogDensityIn;
uniform float layoutFlatFogStartIn;

out vec4 layoutColor;
out vec4 layoutFogColor;
out float layoutFogAmount;

void main(void)
{
    vec4 layoutWorldPos = modelMatrix * vec4(layoutPositionIn, 1.0);
    vec4 layoutCamPos = viewMatrix * layoutWorldPos;

    // GuideMeshBuilder already resolved every role colour and alpha on the CPU. The only thing applied
    // here is the ambient/brightness multiplier - rgb only, so guide transparency is never affected.
    layoutColor = vec4(layoutColorIn.rgb * layoutBrightnessIn, layoutColorIn.a);
    layoutFogColor = layoutFogColorIn;

    // Transcribed from getFogLevel() in game/shaderincludes/fogandlight.vsh so distance fade matches the
    // standard shader's curve exactly. Inlined rather than kept as a function to minimise the surface a
    // preprocessor collision could touch.
    float layoutDepth = length(layoutWorldPos.xyz);
    float layoutClampedDepth = min(250.0, layoutDepth);
    float layoutHeightDiff = layoutWorldPos.y - layoutFlatFogStartIn;
    float layoutExtraDistanceFog =
        max(-layoutFlatFogDensityIn * layoutClampedDepth * layoutFlatFogStartIn / 60.0, 0.0);
    float layoutDistanceFog =
        1.0 - 1.0 / exp(layoutClampedDepth * layoutFogDensityIn + layoutExtraDistanceFog);
    float layoutFlatFog = 1.0 - 1.0 / exp(layoutHeightDiff * layoutFlatFogDensityIn);

    float layoutFogVal = max(layoutFlatFog, layoutDistanceFog);
    float layoutNearness = clamp((8.0 - layoutDepth) / 8.0, 0.0, 0.9);
    layoutFogVal = max(min(0.04, layoutFogVal), layoutFogVal - layoutNearness);
    layoutFogVal += layoutFogMinIn;
    layoutFogAmount = clamp(layoutFogVal, 0.0, 1.0);

    gl_Position = projectionMatrix * layoutCamPos;
}
