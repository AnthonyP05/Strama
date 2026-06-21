// Fullscreen-triangle vertex shader: emits 3 verts covering the screen from
// SV_VertexID alone (no vertex/index buffers). Paired with hdr2sdr_ps.hlsl.
//
// Compile (Windows SDK fxc), then base64 the .cso into HdrToSdrConverter.VsBytecodeB64:
//   fxc -nologo -T vs_4_0 -E main -O3 -Fo fsq_vs.cso fsq_vs.hlsl
float4 main(uint id : SV_VertexID) : SV_Position
{
    float2 uv = float2((id << 1) & 2, id & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}
