// Used once per photo: copies one eye out of the side-by-side image at full
// resolution, for the compositor layer. It also writes an alpha channel that
// fades the photo out over the edge feather, matching VR180/StereoEquirect, so
// the sharp layer melts into the soft surround drawn underneath it.
// The photo's brightness/contrast/saturation adjustments are baked in here too.
// Fisheye mode (_Fisheye = 1): the source is an unprocessed QooCam photo (two fisheye circles),
// and each eye is converted straight from it (VR180Fisheye.hlsl), with no full-size copy between.
// Telephoto zoom (15 November 2026, _TeleZoom.x > 1): the zoomed view is baked into the image,
// the same "squashed sphere" mapping as VR180/StereoEquirect (tan(source) = tan(view) / zoom about
// the axis at longitude _TeleZoom.y, latitude _TeleZoom.z), so the Quest's layer shows a sharp
// telephoto view with its usual (unzoomed) angles. Magnified samples use Catmull-Rom.

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
            float4 _TeleZoom;      // x: telephoto magnification (> 1 = on); y, z: axis longitude, latitude (radians)

            // Catmull-Rom from the source's texels in 9 bilinear taps (for magnified samples)
            float3 EyeCatmullRom(float2 uv)
            {
                float2 size = _MainTex_TexelSize.zw;
                float2 pos = uv * size;
                float2 p1 = floor(pos - 0.5) + 0.5;
                float2 f = pos - p1;
                float2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
                float2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
                float2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
                float2 w3 = f * f * (-0.5 + 0.5 * f);
                float2 w12 = w1 + w2;
                float2 t0 = (p1 - 1.0) / size;
                float2 t3 = (p1 + 2.0) / size;
                float2 t12 = (p1 + w2 / w12) / size;
                float3 c = 0.0;
                c += SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, float2(t0.x,  t0.y),  0).rgb * w0.x  * w0.y;
                c += SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, float2(t12.x, t0.y),  0).rgb * w12.x * w0.y;
                c += SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, float2(t3.x,  t0.y),  0).rgb * w3.x  * w0.y;
                c += SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, float2(t0.x,  t12.y), 0).rgb * w0.x  * w12.y;
                c += SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, float2(t12.x, t12.y), 0).rgb * w12.x * w12.y;
                c += SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, float2(t3.x,  t12.y), 0).rgb * w3.x  * w12.y;
                c += SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, float2(t0.x,  t3.y),  0).rgb * w0.x  * w3.y;
                c += SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, float2(t12.x, t3.y),  0).rgb * w12.x * w3.y;
                c += SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, float2(t3.x,  t3.y),  0).rgb * w3.x  * w3.y;
                return max(c, 0.0);
            }

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
                bool tele = _TeleZoom.x > 1.0001;
                if (tele)
                {
                    // Where this view direction comes from in the photo, zoomed (see the header)
                    float3 d = float3(cos(lat) * sin(lon), sin(lat), cos(lat) * cos(lon));
                    float cz = cos(_TeleZoom.z);
                    float3 axis = float3(cz * sin(_TeleZoom.y), sin(_TeleZoom.z), cz * cos(_TeleZoom.y));
                    float along = dot(d, axis);
                    d = normalize(axis * along + (d - axis * along) / _TeleZoom.x);
                    lon = atan2(d.x, d.z);
                    lat = asin(clamp(d.y, -1.0, 1.0));
                    img = float2((lon - _Coverage.x) / span.x, (lat - _Coverage.z) / span.y);
                }
                // (great-circle distance to the nearer side edge, which matters near the poles,
                // where the side edges of a 180-degree photo meet)
                float cosLat = cos(lat);
                float inside = min(asin(saturate(cosLat * sin(clamp(lon - _Coverage.x, 0.0, 1.5707963)))),
                                   asin(saturate(cosLat * sin(clamp(_Coverage.y - lon, 0.0, 1.5707963)))));
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
                    float2 tex = saturate(img);
                    if (_StereoSBS > 0.5) tex.x = (tex.x + _Eye) * 0.5;
                    col = tele ? EyeCatmullRom(tex) : SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, tex, 0).rgb;
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
