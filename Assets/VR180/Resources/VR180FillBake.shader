// Made once per photo (and per eye) by VR180Viewer: reprojects the photo, mirrored beyond its
// edges just as VR180/StereoEquirect's soft fill is, into an octahedral map: a square texture
// covering every direction, with the front hemisphere as its central diamond and no poles.
// Its mip chain then blurs evenly in every direction, so the soft fill has no "vortex" where
// a 180 x 180 photo's side edges meet at the zenith and nadir.
// Lives in a Resources folder so it's always included in builds.

Shader "Hidden/VR180/FillBake"
{
    Properties
    {
        _MainTex ("Photo (the sphere's mipmapped copy)", 2D) = "black" {}
    }

    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        ZTest Always
        ZWrite Off
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            float4 _MainTex_TexelSize;   // 1/w, 1/h, w, h

            float4 _Coverage;      // lonMin, lonMax, latMin, latMax (radians)
            float _Eye;            // 0 = left, 1 = right
            float _StereoSBS;
            float _FillMirror;     // 1 = mirror beyond the edges, 0 = smear the edge
            float _MapSize;        // this map's width in texels

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings   { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings vert (Attributes a)
            {
                Varyings v;
                v.positionCS = TransformObjectToHClip(a.positionOS.xyz);
                v.uv = a.uv;
                return v;
            }

            // Inverse of OctEncode in VR180/StereoEquirect
            float3 OctDecode(float2 uv)
            {
                float2 f = uv * 2.0 - 1.0;
                float3 n = float3(f, 1.0 - abs(f.x) - abs(f.y));
                float t = saturate(-n.z);
                n.x += n.x >= 0.0 ? -t : t;
                n.y += n.y >= 0.0 ? -t : t;
                return normalize(n);
            }

            float4 frag (Varyings i) : SV_Target
            {
                float3 d = OctDecode(i.uv);
                float lon = atan2(d.x, d.z);
                float lat = asin(clamp(d.y, -1.0, 1.0));

                // Mirror (or clamp) into the photo, as the viewer's fill does
                float2 covMin = _Coverage.xz, covMax = _Coverage.yw;
                float2 pos = float2(lon, lat);
                if (_FillMirror > 0.5)
                {
                    if (lon > covMax.x) pos.x = 2.0 * covMax.x - lon;
                    else if (lon < covMin.x) pos.x = 2.0 * covMin.x - lon;
                    if (lat > covMax.y) pos.y = 2.0 * covMax.y - lat;
                    else if (lat < covMin.y) pos.y = 2.0 * covMin.y - lat;
                }
                pos = clamp(pos, covMin, covMax);
                float2 span = covMax - covMin;
                float2 uvEye = (pos - covMin) / span;

                // Sample the photo at about this map's resolution (its texels are much coarser)
                float xScale = _StereoSBS > 0.5 ? 0.5 : 1.0;
                float texelsPerDeg = _MainTex_TexelSize.z * xScale / degrees(span.x);
                float lod = max(log2(texelsPerDeg * 180.0 / _MapSize), 0.0);

                // Keep clear of the other eye's half when blurred
                float texelsAcrossEye = _MainTex_TexelSize.z * xScale;
                float margin = min(0.5 * exp2(lod) / texelsAcrossEye, 0.25);
                uvEye.x = clamp(uvEye.x, margin, 1.0 - margin);
                uvEye.y = saturate(uvEye.y);
                if (_StereoSBS > 0.5) uvEye.x = (uvEye.x + _Eye) * 0.5;

                return float4(SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, uvEye, lod).rgb, 1.0);
            }
            ENDHLSL
        }
    }
}
