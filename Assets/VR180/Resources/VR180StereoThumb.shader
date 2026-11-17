// The picker's stereo thumbnails: a UI shader (for a RawImage) that shows each eye its own half
// of a side-by-side thumbnail, so each tile is a small window into the photo's depth.
// _Window shifts the halves apart (left eye's image left, right eye's right), putting distant
// scenery a little behind the panel and nearer things up to its surface, like a stereo window.
// Rounded corners are drawn here too. Supports RectMask2D clipping (the picker's scrolling grid).
// Off a headset (Editor game view) the left eye's half is shown.

Shader "Hidden/VR180/StereoThumb"
{
    Properties
    {
        [PerRendererData] _MainTex ("Side-by-side thumbnail", 2D) = "black" {}
        _Window ("Stereo window shift (fraction of each half's width)", Range(0, 0.1)) = 0.015
        _Aspect ("Tile width / height", Float) = 0.8
        _Corner ("Corner radius (fraction of tile height)", Range(0, 0.5)) = 0.06
        _Stereo ("Show in stereo", Float) = 1
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "PreviewType"="Plane" "CanUseSpriteAtlas"="True" }
        Cull Off
        ZWrite Off
        ZTest LEqual
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            float _Window, _Aspect, _Corner, _Stereo;
            float4 _ClipRect;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                float2 local : TEXCOORD1;   // canvas-space position, for clipping
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert (Attributes a)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(a);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(a.positionOS.xyz);
                o.color = a.color;
                o.uv = a.uv;
                o.local = a.positionOS.xy;
                return o;
            }

            float4 frag (Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float eye = 0.0;
                #if defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
                    eye = (float)unity_StereoEyeIndex;   // 0 = left, 1 = right
                #endif
                if (_Stereo < 0.5) eye = 0.0;

                // Each eye's half, cropped by the window shift: the left eye sees its image moved
                // left, the right eye its image moved right (uncrossed: behind the panel)
                float w = _Stereo > 0.5 ? _Window : 0.0;
                float u = i.uv.x * (1.0 - 2.0 * w) + (eye < 0.5 ? 2.0 * w : 0.0);
                float2 src = float2((eye + u) * 0.5, i.uv.y);
                float4 col = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, src) * i.color;

                // Rounded corners
                float2 p = float2(i.uv.x * _Aspect, i.uv.y);
                float2 size = float2(_Aspect, 1.0);
                float r = _Corner;
                float2 q = abs(p - size * 0.5) - (size * 0.5 - r);
                float dist = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
                float aa = max(fwidth(dist), 1e-4);
                col.a *= saturate(0.5 - dist / aa);

                #ifdef UNITY_UI_CLIP_RECT
                    float2 inside = step(_ClipRect.xy, i.local) * step(i.local, _ClipRect.zw);
                    col.a *= inside.x * inside.y;
                #endif
                clip(col.a - 0.001);
                return col;
            }
            ENDHLSL
        }
    }
}
