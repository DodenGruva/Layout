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

void main()
{
    // Same mix as applyFog() in game/shaderincludes/fogandlight.fsh - alpha is preserved, only rgb fades.
    outColor = vec4(mix(layoutColor.rgb, layoutFogColor.rgb, layoutFogAmount), layoutColor.a);

    // standard.fsh discards below alphaTest (default 0.001). Matched so a fully transparent guide voxel
    // behaves identically, notably for hidden guides at reduced anchor alpha.
    if (outColor.a < 0.001) discard;

    // The Opaque stage binds more than one render target. Leaving outGlow unwritten gives the
    // godray/bloom pass undefined values, which shows up as flickering bloom around guides.
    // standard.fsh writes (glow, extraGodray - fogAmount, 0, alpha); guides have neither glow nor extra
    // godray, so both terms are zero before the fog subtraction.
    outGlow = vec4(0.0, -layoutFogAmount, 0.0, outColor.a);
}
