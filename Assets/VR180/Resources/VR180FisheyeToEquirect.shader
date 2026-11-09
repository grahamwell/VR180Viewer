// Turns an unprocessed QooCam 3 Ultra photo (two fisheye circles side by side, straight from the
// camera) into a 180 x 180 degree side-by-side equirectangular image, the same geometry as QooCam
// Studio produces. Used for the sphere's (usually reduced-size) copy and for flat export; the
// layer's eye images are made directly by Hidden/VR180/EyeExtract's fisheye mode instead.
// The lens model is in VR180Fisheye.hlsl; parameters come from VR180Fisheye.SetParams.
// Lives in a Resources folder so it's always included in builds.

Shader "Hidden/VR180/FisheyeToEquirect"
{
    Properties
    {
        _MainTex ("Raw dual-fisheye photo", 2D) = "black" {}
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
            #include "Assets/VR180/VR180Fisheye.hlsl"

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
                bool right = i.uv.x >= 0.5;
                float u = right ? i.uv.x * 2.0 - 1.0 : i.uv.x * 2.0;
                float lon = (u - 0.5) * PI;
                float lat = (i.uv.y - 0.5) * PI;
                return float4(FisheyeColour(lon, lat, right), 1.0);
            }
            ENDHLSL
        }
    }
}
