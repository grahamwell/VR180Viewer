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
// Head-turn parallax (11 November 2026): _Parallax.w > 0 puts the photo on a sphere of that radius
// (metres) instead of at infinity, centred where each eye would be if the head hadn't turned;
// _Parallax.xyz is how far turning the head has moved the eyes (world space, the same for both).
// Things at that distance then stay put as the head turns. 0 = off (the photo at infinity).
//
// Zoom (11 November 2026): _Zoom.x = magnification (1 = none), about the photo direction
// (_Zoom.y, _Zoom.z) = (longitude, latitude) in radians. Longitude and latitude are scaled about
// that point, matching what the compositor layer does with a cropped image over scaled angles.
// Telephoto zoom (15 November 2026): _Zoom.w = 1 (zooming in only) instead squashes the sphere
// along the zoom axis: each view direction's components across the axis are divided by the
// magnification, i.e. tan(source angle) = tan(view angle) / zoom. That is a true telephoto
// (rectilinear) zoom: great circles stay great circles, so straight lines stay straight and the
// horizon stays level wherever the zoom is centred. Magnification is strongest at the centre.
//
// Photo transitions (v3): _Blur melts the whole view into the photo's own mip chain, ending at
// its average colour (the 1x1 mip). _HoldTex is a 1x1 copy of the previous photo's average,
// shown while the next photo loads (_HoldMix), then crossfaded to the new photo's average.
//
// Poles (v4, 10 November 2026): a 180 x 180 photo's two side edges meet at the zenith and nadir,
// so looking straight up you're at its edge. The soft fill used to be blurred through the
// equirectangular mip chain, which near a pole blurs along the meridians only, giving a radial
// "vortex". The fill now comes from _FillMap0/_FillMap1 (one per eye), made once per photo by
// Hidden/VR180/FillBake: the same mirrored photo, reprojected into an octahedral map, which has
// no poles, so its mips blur evenly in every direction. Edge distances are now true great-circle
// distances, and the sharp sample's mip level no longer counts the squeezed-up longitude near the
// poles (which made it far too soft there).

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
        _Exposure ("Exposure (stops)", Range(-2, 4)) = 0
        _Contrast ("Contrast", Range(0.5, 1.5)) = 1
        _Saturation ("Saturation", Range(0, 2)) = 1
        _Convergence ("Convergence (degrees, + = nearer)", Range(-2, 2)) = 0

        [Header(Diagnostics)]
        [ToggleUI] _DebugEyes ("Tint eyes (left red, right cyan)", Float) = 0

        // Set by the VR180Viewer script
        [HideInInspector] _Fade ("Fade", Range(0, 1)) = 1
        [HideInInspector] _Parallax ("Head-turn parallax (eye offset xyz, radius w)", Vector) = (0, 0, 0, 0)
        [HideInInspector] _Zoom ("Zoom (magnification, centre lon, centre lat)", Vector) = (1, 0, 0, 0)
        [HideInInspector] _Coverage ("Coverage (radians)", Vector) = (-1.5707963, 1.5707963, -1.5707963, 1.5707963)
        [HideInInspector] _StereoSBS ("Side by side", Float) = 1
        [HideInInspector] _MaxMip ("Max mip", Float) = 13
        [HideInInspector] _LayerMode ("Compositor layer is showing the photo", Float) = 0
        [HideInInspector] _Blur ("Transition blur", Range(0, 1)) = 0
        [HideInInspector] _HoldMix ("Show the held colour", Range(0, 1)) = 0
        [HideInInspector][NoScaleOffset] _HoldTex ("Held colour (1x1)", 2D) = "black" {}
        [HideInInspector][NoScaleOffset] _FillMap0 ("Fill, left eye (octahedral)", 2D) = "black" {}
        [HideInInspector][NoScaleOffset] _FillMap1 ("Fill, right eye (octahedral)", 2D) = "black" {}
        [HideInInspector] _FillMapSize ("Fill map size (0 = not made yet)", Float) = 0
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
            TEXTURE2D(_FillMap0);
            SAMPLER(sampler_FillMap0);
            TEXTURE2D(_FillMap1);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_TexelSize;
                float4 _Coverage;          // lonMin, lonMax, latMin, latMax
                float4x4 _WorldToImage;    // world direction -> photo direction
                float _MipBias;
                float _FillMirror, _EdgeFeather, _FillBlur, _FillFadeDegrees, _AmbientLevel;
                float _Exposure, _Contrast, _Saturation, _Convergence;
                float _Fade, _StereoSBS, _MaxMip, _DebugEyes, _LayerMode;
                float _Blur, _HoldMix;
                float _FillMapSize;
                float4 _Parallax;          // xyz: the eyes' displacement from turning the head; w: radius (0 = off)
                float4 _Zoom;              // x: magnification (1 = none); y, z: centre longitude, latitude (radians); w: 1 = telephoto
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

            // Octahedral mapping (the same as Hidden/VR180/FillBake): direction <-> 0..1 square.
            // The front hemisphere (z >= 0) is the central diamond.
            float2 OctEncode(float3 n)
            {
                n /= abs(n.x) + abs(n.y) + abs(n.z);
                float2 p = n.xy;
                if (n.z < 0.0) p = (1.0 - abs(n.yx)) * float2(n.x >= 0.0 ? 1.0 : -1.0, n.y >= 0.0 ? 1.0 : -1.0);
                return p * 0.5 + 0.5;
            }

            // The soft fill at photo position pos (lon, lat; already mirrored and clamped into the
            // photo), blurred over about footDeg degrees. Uses the pole-free fill maps once they've
            // been made, otherwise the photo's own mips.
            float3 SampleFill(float2 pos, float eye, float footDeg, float texelsPerDeg, float2 covMin, float2 span)
            {
                if (_FillMapSize > 0.5)
                {
                    float3 d = float3(cos(pos.y) * sin(pos.x), sin(pos.y), cos(pos.y) * cos(pos.x));
                    float2 uv = OctEncode(d);
                    // The map has about size/180 texels per degree across the front hemisphere
                    float lod = clamp(log2(max(footDeg * _FillMapSize / 180.0, 1.0)), 0.0, log2(_FillMapSize));
                    return eye < 0.5 ? SAMPLE_TEXTURE2D_LOD(_FillMap0, sampler_FillMap0, uv, lod).rgb
                                     : SAMPLE_TEXTURE2D_LOD(_FillMap1, sampler_FillMap0, uv, lod).rgb;
                }
                float lodE = min(log2(max(footDeg * texelsPerDeg, 1.0)), _MaxMip);
                return SampleLod((pos - covMin) / span, eye, lodE);
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
                if (_Parallax.w > 0.0)
                {
                    // Where the ray from the eye (at _Parallax.xyz from the sphere's centre) meets the
                    // sphere, as a direction from the centre
                    float3 o = _Parallax.xyz;
                    float b = dot(o, dirWS);
                    float t = -b + sqrt(max(b * b - dot(o, o) + _Parallax.w * _Parallax.w, 0.0));
                    dirWS = normalize(o + t * dirWS);
                }
                float3 d = mul((float3x3)_WorldToImage, dirWS);
                if (_Zoom.w > 0.5 && _Zoom.x > 1.0001)
                {
                    // Telephoto: shrink the part of the direction across the zoom axis
                    float cz = cos(_Zoom.z);
                    float3 axis = float3(cz * sin(_Zoom.y), sin(_Zoom.z), cz * cos(_Zoom.y));
                    float along = dot(d, axis);
                    d = normalize(axis * along + (d - axis * along) / _Zoom.x);
                }
                else if (_Zoom.x > 0.01 && abs(_Zoom.x - 1.0) > 1e-4)
                {
                    // Scale longitude and latitude about the zoom centre (beyond a pole, cos goes
                    // negative and the direction carries on over it, into the surround)
                    float zl = atan2(d.x, d.z) - _Zoom.y;
                    if (zl > PI) zl -= 2.0 * PI;
                    else if (zl < -PI) zl += 2.0 * PI;
                    zl = _Zoom.y + zl / _Zoom.x;
                    float zp = _Zoom.z + (asin(clamp(d.y, -1.0, 1.0)) - _Zoom.z) / _Zoom.x;
                    d = float3(cos(zp) * sin(zl), sin(zp), cos(zp) * cos(zl));
                }

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

                // ---- Signed distance (degrees) from the photo's edge: + outside, - inside.
                // Side edges are meridians, so the distance to one is a great-circle distance
                // (it shrinks to nothing at the poles, where a 180-degree photo's side edges meet).
                // The asin arguments are saturated: GPU sin/cos can come out a hair over 1, and
                // asin of that is NaN (which showed as a seam straight ahead and a blurred view).
                float lonC = clamp(lon, covMin.x, covMax.x);
                float latC = clamp(lat, covMin.y, covMax.y);
                // Only work out the distance outside when actually outside: the GPU's asin(0) isn't
                // exactly 0, and a tiny positive distance made the whole photo count as "outside"
                bool isOutside = lon != lonC || lat != latC;
                float dLon = lon != lonC ? asin(saturate(cosLat * sin(min(abs(lon - lonC), 1.5707963)))) : 0.0;
                float dLat = lat - latC;
                float outsideDeg = isOutside ? degrees(sqrt(dLon * dLon + dLat * dLat)) : 0.0;

                float inside = min(asin(saturate(cosLat * sin(clamp(lon - covMin.x, 0.0, 1.5707963)))),
                                   asin(saturate(cosLat * sin(clamp(covMax.x - lon, 0.0, 1.5707963)))));
                // Top and bottom only count as edges if the photo doesn't reach the poles
                if (covMax.y < 1.5707) inside = min(inside, covMax.y - lat);
                if (covMin.y > -1.5707) inside = min(inside, lat - covMin.y);
                float insideDeg = degrees(max(inside, 0.0));

                float s = isOutside ? outsideDeg : -insideDeg;
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
                    // Near the poles the photo's rows are stretched (many texels of longitude per
                    // degree), so count longitude at its true size there, or the mip level is far too coarse
                    float lonScale = xScale * max(cosLat, 0.02);
                    sharp = SAMPLE_TEXTURE2D_GRAD(_MainTex, sampler_MainTex, uvSharp,
                                float2(dx.x * lonScale, dx.y) * g,
                                float2(dy.x * lonScale, dy.y) * g).rgb;
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
                float texelsPerDeg = _MainTex_TexelSize.z * xScale / degrees(span.x);

                if (u <= 0.0)
                {
                    col = sharp;   // black in layer mode, where it's hidden under the layer anyway
                }
                else
                {

                    // Blur grows with distance into the soft zone (footDeg: the blur's size in degrees)
                    float footDeg = min(1.0 / texelsPerDeg + u * _FillBlur, exp2(_MaxMip) / texelsPerDeg);

                    // Go mono across the feather zone, so both eyes see the same fill.
                    // (A mirrored image in stereo would have reversed depth.)
                    float mono = saturate(u / max(_EdgeFeather, 0.01));
                    float3 blurred = lerp(SampleFill(pos, eye, footDeg, texelsPerDeg, covMin, span),
                                          SampleFill(pos, 0.0, footDeg, texelsPerDeg, covMin, span), mono);

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
                        float3 ambient = SampleFill(pos, 0.0, exp2(ambLod) / texelsPerDeg, texelsPerDeg, covMin, span) * _AmbientLevel;
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
