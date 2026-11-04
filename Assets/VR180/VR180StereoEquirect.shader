// VR180 stereo viewer shader for URP (Unity 6.3), single-pass instanced / multiview.
//
// The view direction is calculated per pixel, so the sphere's mesh, size and UVs
// don't matter: the photo always sits at infinity with no seams or pole pinching.
//
// Dead zone fill (v2): beyond the photo's edge we show a mirrored, progressively
// blurred, mono reflection of the photo. Mirroring keeps sky above and ground below
// all the way round, and the two mirrored halves meet seamlessly behind you. The
// blur starts a few degrees *inside* the edge, so there's no line where sharp meets
// soft, and the fill fades towards a very blurred version of the same reflection
// rather than a flat colour.
//
// Photo transitions (v3): _Blur melts the whole view into the photo's own mip chain, ending at
// its average colour (the 1x1 mip). _HoldTex is a 1x1 copy of the previous photo's average,
// shown while the next photo loads (_HoldMix), then crossfaded to the new photo's average.

Shader "VR180/StereoEquirect"
{
    Properties
    {
        [NoScaleOffset] _MainTex ("Side-by-side photo", 2D) = "black" {}

        [Header(Sharpness)]
        _MipBias ("Mip bias (negative = sharper)", Range(-2, 1)) = -0.787

        [Header(Dead zone fill)]
        [ToggleUI] _FillMirror ("Mirror (off = smear edge)", Float) = 1
        _EdgeFeather ("Edge feather (degrees)", Range(0.5, 20)) = 6
        _FillBlur ("Blur, as a fraction of distance", Range(0, 2)) = 0.35
        _FillFadeDegrees ("Fade to ambient over (degrees)", Range(1, 120)) = 90
        _AmbientLevel ("Ambient brightness", Range(0, 1)) = 0.7

        [Header(Adjustments)]
        _Exposure ("Exposure (stops)", Range(-2, 2)) = 0
        _Contrast ("Contrast", Range(0.5, 1.5)) = 1
        _Saturation ("Saturation", Range(0, 2)) = 1
        _Convergence ("Convergence (degrees, + = nearer)", Range(-2, 2)) = 0

        [Header(Diagnostics)]
        [ToggleUI] _DebugEyes ("Tint eyes (left red, right cyan)", Float) = 0

        // Set by the VR180Viewer script
        [HideInInspector] _Fade ("Fade", Range(0, 1)) = 1
        [HideInInspector] _Coverage ("Coverage (radians)", Vector) = (-1.5707963, 1.5707963, -1.5707963, 1.5707963)
        [HideInInspector] _StereoSBS ("Side by side", Float) = 1
        [HideInInspector] _MaxMip ("Max mip", Float) = 13
        [HideInInspector] _LayerMode ("Compositor layer is showing the photo", Float) = 0
        [HideInInspector] _Blur ("Transition blur", Range(0, 1)) = 0
        [HideInInspector] _HoldMix ("Show the held colour", Range(0, 1)) = 0
        [HideInInspector][NoScaleOffset] _HoldTex ("Held colour (1x1)", 2D) = "black" {}
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Background" }

        Pass
        {
            Name "VR180"
            Cull Front      // we're inside the sphere
            ZWrite On       // must write depth, or URP's skybox draws over us

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            TEXTURE2D(_HoldTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_TexelSize;
                float4 _Coverage;          // lonMin, lonMax, latMin, latMax
                float4x4 _WorldToImage;    // world direction -> photo direction
                float _MipBias;
                float _FillMirror, _EdgeFeather, _FillBlur, _FillFadeDegrees, _AmbientLevel;
                float _Exposure, _Contrast, _Saturation, _Convergence;
                float _Fade, _StereoSBS, _MaxMip, _DebugEyes, _LayerMode;
                float _Blur, _HoldMix;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert (Attributes v)
            {
                Varyings o;
                ZERO_INITIALIZE(Varyings, o);
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                return o;
            }

            // Per-eye uv (0..1 across one eye) -> texture uv. Keeps a margin from the
            // eye's left/right edges that grows with blur, so blurred samples never
            // pick up pixels from the other eye's half of the image.
            float2 EyeToTex(float2 uvEye, float eye, float lod)
            {
                float texelsAcrossEye = _MainTex_TexelSize.z * (_StereoSBS > 0.5 ? 0.5 : 1.0);
                float margin = min(0.5 * exp2(lod) / texelsAcrossEye, 0.25);
                uvEye.x = clamp(uvEye.x, margin, 1.0 - margin);
                uvEye.y = saturate(uvEye.y);
                if (_StereoSBS > 0.5) uvEye.x = (uvEye.x + eye) * 0.5;
                return uvEye;
            }

            float3 SampleLod(float2 uvEye, float eye, float lod)
            {
                return SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, EyeToTex(uvEye, eye, lod), lod).rgb;
            }

            half4 frag (Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                #if defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
                    float eye = (float)unity_StereoEyeIndex;   // 0 = left, 1 = right
                #else
                    float eye = 0.0;                           // flat screen: left eye
                #endif

                // The exact ray direction for this pixel, rotated into the photo's frame
                float3 dirWS = normalize(i.positionWS - GetCameraPositionWS());
                float3 d = mul((float3x3)_WorldToImage, dirWS);

                float lon = atan2(d.x, d.z);               // 0 = straight ahead
                // Convergence: shift the left eye's image right and the right eye's left by half
                // each (positive = crossed = nearer), keeping lon in -pi..pi
                lon += (eye < 0.5 ? -0.5 : 0.5) * radians(_Convergence);
                if (lon > PI) lon -= 2.0 * PI;
                else if (lon < -PI) lon += 2.0 * PI;
                float lat = asin(clamp(d.y, -1.0, 1.0));   // + = up
                float cosLat = cos(lat);

                float2 covMin = _Coverage.xz;
                float2 covMax = _Coverage.yw;
                float2 span = covMax - covMin;
                float2 uvRaw = (float2(lon, lat) - covMin) / span;

                // Screen-space derivatives, taken before any branching
                float2 dx = ddx(uvRaw);
                float2 dy = ddy(uvRaw);

                // ---- Signed distance (degrees) from the photo's edge: + outside, - inside
                float lonC = clamp(lon, covMin.x, covMax.x);
                float latC = clamp(lat, covMin.y, covMax.y);
                float dLon = (lon - lonC) * cosLat;
                float dLat = lat - latC;
                float outsideDeg = degrees(sqrt(dLon * dLon + dLat * dLat));

                float inside = min(lon - covMin.x, covMax.x - lon) * cosLat;
                // Top and bottom only count as edges if the photo doesn't reach the poles
                if (covMax.y < 1.5707) inside = min(inside, covMax.y - lat);
                if (covMin.y > -1.5707) inside = min(inside, lat - covMin.y);
                float insideDeg = degrees(max(inside, 0.0));

                float s = outsideDeg > 0.0 ? outsideDeg : -insideDeg;
                float u = s + _EdgeFeather;   // how far into the soft zone we are

                float xScale = _StereoSBS > 0.5 ? 0.5 : 1.0;
                float3 col;

                // When the compositor layer is showing the photo, this sphere only has to
                // provide the soft surround; the sharp image comes from the layer on top.
                bool layerMode = _LayerMode > 0.5;

                // Full-quality sample of this eye's own image, with adjustable mip bias
                float3 sharp = 0.0;
                if (!layerMode && s < 0.0)
                {
                    float g = exp2(_MipBias);
                    float2 uvSharp = EyeToTex(saturate(uvRaw), eye, 0.0);
                    sharp = SAMPLE_TEXTURE2D_GRAD(_MainTex, sampler_MainTex, uvSharp,
                                float2(dx.x * xScale, dx.y) * g,
                                float2(dy.x * xScale, dy.y) * g).rgb;
                }

                // Where to sample the fill from: mirrored back into the photo, or clamped to its
                // edge. Inside the photo this is just the photo's own position.
                float2 pos = float2(lon, lat);
                if (_FillMirror > 0.5)
                {
                    if (lon > covMax.x) pos.x = 2.0 * covMax.x - lon;
                    else if (lon < covMin.x) pos.x = 2.0 * covMin.x - lon;
                    if (lat > covMax.y) pos.y = 2.0 * covMax.y - lat;
                    else if (lat < covMin.y) pos.y = 2.0 * covMin.y - lat;
                }
                pos = clamp(pos, covMin, covMax);
                float2 uvFill = (pos - covMin) / span;

                if (u <= 0.0)
                {
                    col = sharp;   // black in layer mode, where it's hidden under the layer anyway
                }
                else
                {

                    // Blur grows with distance into the soft zone
                    float texelsPerDeg = _MainTex_TexelSize.z * xScale / degrees(span.x);
                    float lod = min(log2(1.0 + u * texelsPerDeg * _FillBlur), _MaxMip);

                    // Go mono across the feather zone, so both eyes see the same fill.
                    // (A mirrored image in stereo would have reversed depth.)
                    float mono = saturate(u / max(_EdgeFeather, 0.01));
                    float3 blurred = lerp(SampleLod(uvFill, eye, lod), SampleLod(uvFill, 0.0, lod), mono);

                    if (s < 0.0)
                    {
                        // Just inside the edge: ease from sharp to soft. In layer mode the
                        // layer's own alpha does this blend, so we just supply the soft part.
                        col = layerMode ? blurred : lerp(sharp, blurred, smoothstep(0.0, 1.0, mono));
                    }
                    else
                    {
                        // Outside: fade towards a very blurred, dimmed version of the same
                        // reflection, which keeps a sense of sky above and ground below
                        float ambLod = max(_MaxMip - 3.0, 0.0);
                        float3 ambient = SampleLod(uvFill, 0.0, ambLod) * _AmbientLevel;
                        col = lerp(blurred, ambient, smoothstep(0.0, _FillFadeDegrees, outsideDeg));
                    }
                }

                // Transition: blur the whole view through the mip chain towards the photo's
                // average colour, going mono as it softens. Then mix in the held colour.
                if (_Blur > 0.0)
                {
                    float tl = _Blur * _MaxMip;
                    float monoT = saturate(_Blur * 3.0);
                    float3 veil = lerp(SampleLod(uvFill, eye, tl), SampleLod(uvFill, 0.0, tl), monoT);
                    col = lerp(col, veil, saturate(_Blur * 5.0));
                }
                if (_HoldMix > 0.0)
                {
                    float3 held = SAMPLE_TEXTURE2D_LOD(_HoldTex, sampler_MainTex, float2(0.5, 0.5), 0).rgb;
                    col = lerp(col, held, _HoldMix);
                }

                // Adjustments (a stop-gap here; they'll move into a one-off image pass later)
                col *= exp2(_Exposure);
                float3 p = pow(max(col, 0.0), 1.0 / 2.2);          // contrast in perceptual space
                p = (p - 0.5) * _Contrast + 0.5;
                col = pow(saturate(p), 2.2);
                float luma = dot(col, float3(0.2126, 0.7152, 0.0722));
                col = max(0.0, lerp(luma.xxx, col, _Saturation));

                // Diagnostic: shows which eye the shader thinks it's drawing
                if (_DebugEyes > 0.5)
                    col *= (eye < 0.5) ? float3(1.0, 0.35, 0.35) : float3(0.35, 1.0, 1.0);

                return half4(col * _Fade, 1.0);
            }
            ENDHLSL
        }
    }
}
