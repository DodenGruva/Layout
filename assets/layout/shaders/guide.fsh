#version 330 core

// Layout guide fragment shader - the lean replacement for Vintage Story's standard.fsh.
//
// ASCII ONLY - see the note in guide.vsh.
//
// What standard.fsh does per fragment that a guide does not need, and this omits:
//   - texture(tex, uv)              sampling a white texture to multiply by 1
//   - getBrightnessFromShadowMap()  a 3x3 PCF shadow lookup, 9 texture fetches, per fragment
//   - getSkyMurkiness / underwater effects
//   - glow mixing, damage-effect noise, overlay-texture blending, reflective/shiny effects
// A guide covers a lot of screen and overlaps itself, so per-fragment work is paid many times over.
//
// KNOWN BEHAVIOUR CHANGE - the tradeoff, stated plainly:
// standard.fsh runs guide colour through applyLight() (ambient/sun/block/point-light mixing) and then
// multiplies by shadow-map brightness. Guides today are therefore tinted by ambient light and DARKENED
// IN SHADOW, despite the "full-bright" comment in GuideRenderer. This shader emits the palette verbatim,
// so guides become genuinely self-lit: slightly brighter, and no longer varying with time of day or
// shadow. Reproducing the old behaviour needs shadow-map samplers and light uniforms the modding API
// does not expose, so it is not a matter of effort. Judge it with /layout shader on|off.

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;

in vec4 layoutColor;
in vec4 layoutFogColor;
in float layoutFogAmount;
in vec3 layoutLocalPos;

// Voxel boundary frame. layoutVoxelSizeIn is the cell edge in blocks (scale/16); 0 disables the frame
// entirely, which is also what non-voxel geometry such as remote draft markers uses.
uniform float layoutVoxelSizeIn;
uniform vec3 layoutGridOffsetIn;
uniform float layoutFrameStrengthIn;

// Returns 0..1, peaking on a voxel edge.
//
// No face normal is needed, which matters because this mesh format deliberately carries none. The face
// normal axis is identified instead by its SCREEN-SPACE DERIVATIVE: that coordinate is constant across a
// flat axis-aligned face, so its fwidth is zero, while both tangent axes vary. Excluding it leaves
// min(tangent1, tangent2) - the distance to the nearest cell edge in the plane of the face.
//
// v0.3.67 FIX - this previously took the SECOND-SMALLEST of the three distances, assuming the normal axis
// would always be the smallest because it sits exactly on a cell boundary. That assumption breaks the
// moment a face carries a z-fight inset: the inset displaces the face off the boundary, so the normal
// axis becomes the LARGEST distance instead of the smallest, and second-smallest then returns
// max(tangent1, tangent2) rather than min. The frame collapsed to cell corners and read as absent - on
// exactly the layer resting against world blocks, which is the layer that gets inset. Reported as "one
// layer that never had outlines from any angle".
float layoutVoxelFrame()
{
    if (layoutVoxelSizeIn <= 0.0 || layoutFrameStrengthIn <= 0.0) return 0.0;

    vec3 g = (layoutLocalPos + layoutGridOffsetIn) / layoutVoxelSizeIn;

    // Distance to the nearest cell boundary per axis: 0 on a boundary, 0.5 at the cell centre.
    vec3 e = 0.5 - abs(fract(g) - 0.5);

    // Convert to pixels so the line keeps a constant screen width instead of thinning with distance.
    vec3 gw = fwidth(g);
    vec3 d = e / max(gw, vec3(1e-5));

    // Drop the face-normal axis out of contention. Its derivative is zero across the face; a tangent
    // axis's derivative is cells-per-pixel, which is many orders of magnitude larger for any surface big
    // enough to see, so the threshold separates them cleanly.
    d = mix(d, vec3(1e6), step(gw, vec3(1e-7)));

    float frameDist = min(d.x, min(d.y, d.z));

    float line = 1.0 - smoothstep(0.0, 1.0, frameDist);

    // Fade out before the grid can alias. Once a cell covers only a pixel or two the frame degenerates
    // into moire, so it is faded to nothing rather than left to shimmer - this is what keeps scale 1
    // (cells 1/16 of a block) usable at distance.
    // Only tangent axes carry a meaningful rate here; the normal axis is zero and never wins this max.
    float cellsPerPixel = max(gw.x, max(gw.y, gw.z));
    float fade = clamp((0.75 - cellsPerPixel) / 0.5, 0.0, 1.0);

    return line * fade;
}

void main()
{
    // Same mix as applyFog() in game/shaderincludes/fogandlight.fsh - alpha is preserved, only rgb fades.
    outColor = vec4(mix(layoutColor.rgb, layoutFogColor.rgb, layoutFogAmount), layoutColor.a);

    // Darken toward the cell edge. RGB only: changing alpha here would make the frame read as a change in
    // transparency, and would interact with the order-dependent blending between overlapping guide faces.
    outColor.rgb *= 1.0 - layoutVoxelFrame() * layoutFrameStrengthIn;

    // standard.fsh discards below alphaTest (default 0.001). Matched so a fully transparent guide voxel
    // behaves identically, notably for hidden guides at reduced anchor alpha.
    if (outColor.a < 0.001) discard;

    // The Opaque stage binds more than one render target. Leaving outGlow unwritten gives the
    // godray/bloom pass undefined values, which shows up as flickering bloom around guides.
    // standard.fsh writes (glow, extraGodray - fogAmount, 0, alpha); guides have neither glow nor extra
    // godray, so both terms are zero before the fog subtraction.
    outGlow = vec4(0.0, -layoutFogAmount, 0.0, outColor.a);
}
