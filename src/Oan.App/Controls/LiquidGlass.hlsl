// A slab of clear glass with a rounded, polished edge.
//
// Compiled by hand, because WPF loads the bytecode and the SDK has no build step for it:
//   fxc /T ps_3_0 /E main /Fo LiquidGlass.ps LiquidGlass.hlsl
//
// The middle of the slab is left almost alone. In a band inside the edge the surface curves
// away, and a curved surface bends what is seen through it: each pixel there is looked up some
// way back towards the middle, further the nearer the edge, so the picture is squeezed up
// against the rim the way it is at the side of a thick lens. Red bends a little more than blue,
// which is the coloured fringe real glass has. On top of that, only what a polished edge does
// with light: a thin bright line where it faces the lamp, a fainter one opposite where the
// light comes back out, and a slight darkening where the curve turns away.

sampler2D picture : register(s0);

float2 size   : register(c0);   // the slab, in pixels
float  radius : register(c1);   // of its corners
float  edge   : register(c2);   // how far in the curved band reaches
float  bend   : register(c3);   // how far the edge bends the picture, at most
float  shine  : register(c4);   // strength of the highlights, 0..1

float Outside(float2 p)
{
    float2 q = abs(p) - size * 0.5 + radius;
    return length(max(q, 0)) + min(max(q.x, q.y), 0) - radius;
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 p = (uv - 0.5) * size;
    float inside = -Outside(p);

    // Which way the edge faces here: the direction in which "outside" grows fastest.
    float2 normal = normalize(float2(
        Outside(p + float2(1, 0)) - Outside(p - float2(1, 0)),
        Outside(p + float2(0, 1)) - Outside(p - float2(0, 1))) + 1e-5);

    // 0 in the flat middle, 1 at the rim, and steep near the rim the way a rounded edge is.
    float curve = saturate(1 - inside / max(edge, 1));
    curve = curve * curve * curve;

    float2 shift = -normal * curve * bend / size;
    float3 colour = float3(
        tex2D(picture, uv + shift * 1.18).r,
        tex2D(picture, uv + shift).g,
        tex2D(picture, uv + shift * 0.84).b);

    // The lamp is up and to the left.
    float facing = dot(normal, normalize(float2(-1, -1)));
    float rim = smoothstep(2.2, 0.2, inside);
    float glint = rim * (saturate(facing) * 0.9 + saturate(-facing) * 0.35 + 0.08);

    colour *= 1 - 0.22 * curve;
    colour += shine * glint;

    float alpha = saturate(inside + 0.5);
    return float4(colour * alpha, alpha);
}
