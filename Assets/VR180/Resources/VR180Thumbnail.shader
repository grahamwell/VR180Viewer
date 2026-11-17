// Renders a picker thumbnail from a photo: an ordinary (rectilinear) 2D view straight ahead,
// level, for each eye, side by side in the output (left eye in the left half). The source is
// the photo's equirectangular image (side-by-side or mono), already converted if it was a raw
// fisheye photo, with mips. Used by VR180Thumbnails; lives in Resources so builds include it.

Shader "Hidden/VR180/Thumbnail"
{
    Properties
    {
        _MainTex ("Equirectangular photo", 2D) = "black" {}
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
            float4 _Coverage;            // lonMin, lonMax, latMin, latMax (radians)
            float _Stereo;               // 1 = side-by-side source
            float4 _View;                // tan(half horizontal fov), tan(half vertical fov), centre longitude, output width in pixels
            float4x4 _LevelToImage;      // level frame -> photo's frame (a raw photo's levelling turn, inverted)
            float _FlipY;

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings   { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings vert (Attributes a)
            {
                Varyings v;
                v.positionCS = TransformObjectToHClip(a.positionOS.xyz);
                v.uv = a.uv;
                return v;
            }

            float4 frag (Varyings i) : SV_Target
            {
                float2 uv = i.uv;
                if (_FlipY > 0.5) uv.y = 1.0 - uv.y;
                float eye = uv.x >= 0.5 ? 1.0 : 0.0;
                float u = uv.x * 2.0 - eye;

                // The view direction through this pixel (level frame), turned to face the photo's centre
                float3 d = float3((u * 2.0 - 1.0) * _View.x, (uv.y * 2.0 - 1.0) * _View.y, 1.0);
                float s = sin(_View.z), c = cos(_View.z);
                d = float3(c * d.x + s * d.z, d.y, -s * d.x + c * d.z);
                d = mul((float3x3)_LevelToImage, d);
                d = normalize(d);

                float lon = atan2(d.x, d.z);
                float lat = asin(clamp(d.y, -1.0, 1.0));
                float2 img = float2((lon - _Coverage.x) / (_Coverage.y - _Coverage.x),
                                    (lat - _Coverage.z) / (_Coverage.w - _Coverage.z));
                if (any(img < 0.0) || any(img > 1.0)) return float4(0, 0, 0, 1);

                float2 src = img;
                if (_Stereo > 0.5) src.x = (eye + img.x) * 0.5;

                // Mip level: source pixels per output pixel near this point
                float srcPerRad = (_Stereo > 0.5 ? 0.5 : 1.0) * _MainTex_TexelSize.z / (_Coverage.y - _Coverage.x);
                float outPerRad = 0.5 * _View.w * 0.5 / _View.x;   // half the output width covers 2 * tan(half fov)
                float lod = max(0.0, log2(max(srcPerRad / outPerRad, 1e-3)));
                return float4(SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, src, lod).rgb, 1.0);
            }
            ENDHLSL
        }
    }
}
