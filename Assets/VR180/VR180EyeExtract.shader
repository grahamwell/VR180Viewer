// Used once per photo: copies one eye out of the side-by-side image at full
// resolution, for the compositor layer. It also writes an alpha channel that
// fades the photo out over the edge feather, matching VR180/StereoEquirect, so
// the sharp layer melts into the soft surround drawn underneath it.
// The photo's brightness/contrast/saturation adjustments are baked in here too.
// Fisheye mode (_Fisheye = 1): the source is an unprocessed QooCam photo (two fisheye circles),
// and each eye is converted straight from it (VR180Fisheye.hlsl), with no full-size copy between.

Shader "Hidden/VR180/EyeExtract"
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
            SAMPLER(sampler_MainTex);
            float4 _MainTex_TexelSize;
            #include "Assets/VR180/VR180Fisheye.hlsl"
            float _Fisheye;        // 1 = source is a raw dual-fisheye photo

            float4 _Coverage;      // lonMin, lonMax, latMin, latMax (radians)
            float _Eye;            // 0 = left, 1 = right
            float _StereoSBS;
            float _EdgeFeather;    // degrees
            float _FlipY;
            float _ForceOpaque;    // diagnostic: ignore the feathered edge
            float _Exposure, _Contrast, _Saturation;   // baked into the layer image

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings vert (Attributes a)
            {
                Varyings v;
                v.positionCS = TransformObjectToHClip(a.positionOS.xyz);
                v.uv = a.uv;
                return v;
            }

            float4 frag (Varyings i) : SV_Target
            {
                // Position within the photo (0..1 across one eye), optionally flipped
                float2 img = i.uv;
                if (_FlipY > 0.5) img.y = 1.0 - img.y;

                // Alpha: same edge distance as the main shader uses
                float2 span = _Coverage.yw - _Coverage.xz;
                float lon = _Coverage.x + img.x * span.x;
                float lat = _Coverage.z + img.y * span.y;
                float inside = min(lon - _Coverage.x, _Coverage.y - lon) * cos(lat);
                if (_Coverage.w < 1.5707) inside = min(inside, _Coverage.w - lat);
                if (_Coverage.z > -1.5707) inside = min(inside, lat - _Coverage.z);
                float alpha = smoothstep(0.0, 1.0, saturate(degrees(max(inside, 0.0)) / max(_EdgeFeather, 0.01)));

                // Colour: this eye's half of the image, one source pixel per output pixel
                float3 col;
                if (_Fisheye > 0.5)
                {
                    col = FisheyeColour(lon, lat, _Eye > 0.5);
                }
                else
                {
                    float2 tex = img;
                    if (_StereoSBS > 0.5) tex.x = (img.x + _Eye) * 0.5;
                    col = SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, tex, 0).rgb;
                }

                // Picture adjustments, the same as VR180/StereoEquirect applies
                col *= exp2(_Exposure);
                float3 p = pow(max(col, 0.0), 1.0 / 2.2);   // contrast in perceptual space
                p = (p - 0.5) * _Contrast + 0.5;
                col = pow(saturate(p), 2.2);
                float luma = dot(col, float3(0.2126, 0.7152, 0.0722));
                col = max(0.0, lerp(luma.xxx, col, _Saturation));

                if (_ForceOpaque > 0.5) alpha = 1.0;
                return float4(col, alpha);
            }
            ENDHLSL
        }
    }
}
