// Used once per export: renders a flat (rectilinear) picture from the full-resolution
// VR180 photo, as an ordinary camera would see it.
//
// The virtual camera is level (no tilt) and its image is shifted up or down to where you
// were looking, like an architectural shift lens, so vertical lines stay vertical.
// It samples the left eye with Catmull-Rom bicubic filtering straight from the source
// texels, so the result is as sharp as the photo allows. Lives in a Resources folder
// so it's always included in builds (VR180FlatExport loads it by name).

Shader "Hidden/VR180/FlatExport"
{
    Properties
    {
        _MainTex ("Side-by-side photo", 2D) = "black" {}
        _Exposure ("Exposure (stops)", Float) = 0
        _Contrast ("Contrast", Float) = 1
        _Saturation ("Saturation", Float) = 1
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
            float4 _MainTex_TexelSize;   // 1/w, 1/h, w, h

            float4x4 _CamToImage;   // camera direction -> photo direction
            float4 _Coverage;       // lonMin, lonMax, latMin, latMax (radians)
            float _StereoSBS;
            float _TanHalf;         // tan(horizontal field of view / 2)
            float _Aspect;          // height / width
            float _Shift;           // vertical shift, in units of the image plane at distance 1
            float _FlipY;
            float _Exposure, _Contrast, _Saturation;   // the photo's saved adjustments

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings   { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings vert (Attributes a)
            {
                Varyings v;
                v.positionCS = TransformObjectToHClip(a.positionOS.xyz);
                v.uv = a.uv;
                return v;
            }

            // Catmull-Rom weights for the four taps around a sample
            float4 CatmullRom(float t)
            {
                float t2 = t * t, t3 = t2 * t;
                return float4(
                    -0.5 * t3 + t2 - 0.5 * t,
                     1.5 * t3 - 2.5 * t2 + 1.0,
                    -1.5 * t3 + 2.0 * t2 + 0.5 * t,
                     0.5 * t3 - 0.5 * t2);
            }

            float4 frag (Varyings i) : SV_Target
            {
                float2 uv = i.uv;
                if (_FlipY > 0.5) uv.y = 1.0 - uv.y;

                // Ray through this output pixel, for a level camera looking along +z
                float2 p = (uv * 2.0 - 1.0) * float2(_TanHalf, _TanHalf * _Aspect) + float2(0.0, _Shift);
                float3 d = mul((float3x3)_CamToImage, normalize(float3(p, 1.0)));

                float lon = atan2(d.x, d.z);
                float lat = asin(clamp(d.y, -1.0, 1.0));
                float2 span = _Coverage.yw - _Coverage.xz;
                float2 uvEye = (float2(lon, lat) - _Coverage.xz) / span;

                // Outside the photo's coverage: black
                if (any(uvEye < 0.0) || any(uvEye > 1.0)) return float4(0, 0, 0, 1);

                // Left eye's texels
                int eyeW = (int)(_MainTex_TexelSize.z * (_StereoSBS > 0.5 ? 0.5 : 1.0));
                int texH = (int)_MainTex_TexelSize.w;
                float2 t = uvEye * float2(eyeW, texH) - 0.5;
                int2 base = (int2)floor(t);
                float2 f = t - base;
                float4 wx = CatmullRom(f.x);
                float4 wy = CatmullRom(f.y);

                float3 col = 0.0;
                [unroll] for (int y = 0; y < 4; y++)
                {
                    int ty = clamp(base.y - 1 + y, 0, texH - 1);
                    float3 row = 0.0;
                    [unroll] for (int x = 0; x < 4; x++)
                    {
                        int tx = clamp(base.x - 1 + x, 0, eyeW - 1);
                        row += LOAD_TEXTURE2D(_MainTex, int2(tx, ty)).rgb * wx[x];
                    }
                    col += row * wy[y];
                }
                col = saturate(col);

                // Picture adjustments, the same as VR180/StereoEquirect applies
                col *= exp2(_Exposure);
                float3 g = pow(max(col, 0.0), 1.0 / 2.2);   // contrast in perceptual space
                g = (g - 0.5) * _Contrast + 0.5;
                col = pow(saturate(g), 2.2);
                float luma = dot(col, float3(0.2126, 0.7152, 0.0722));
                col = max(0.0, lerp(luma.xxx, col, _Saturation));
                return float4(saturate(col), 1.0);
            }
            ENDHLSL
        }
    }
}
