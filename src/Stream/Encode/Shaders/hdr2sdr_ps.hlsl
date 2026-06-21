// HDR scRGB (R16G16B16A16_FLOAT) -> SDR sRGB pixel shader for HdrToSdrConverter.
// scRGB is linear, Rec.709 primaries, 1.0 = SDR white. SDR desktop content sits in
// [0,1]; HDR highlights (>1) are clipped. 1:1 texel fetch (Load), so no sampler.
//
// Compile (Windows SDK fxc), then base64 the .cso into HdrToSdrConverter.PsBytecodeB64:
//   fxc -nologo -T ps_4_0 -E main -O3 -Fo hdr2sdr_ps.cso hdr2sdr_ps.hlsl
Texture2D<float4> Src : register(t0);

float3 LinearToSrgb(float3 c)
{
    c = saturate(c);
    return (c <= 0.0031308) ? c * 12.92 : 1.055 * pow(c, 1.0 / 2.4) - 0.055;
}

float4 main(float4 pos : SV_Position) : SV_Target
{
    float3 c = Src.Load(int3(pos.xy, 0)).rgb;
    return float4(LinearToSrgb(c), 1.0);
}
