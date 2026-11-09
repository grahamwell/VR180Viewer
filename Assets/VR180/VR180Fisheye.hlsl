// VR180Fisheye.hlsl: the raw QooCam fisheye lens model, shared by Hidden/VR180/FisheyeToEquirect
// and Hidden/VR180/EyeExtract. The including shader must declare TEXTURE2D(_MainTex) and
// float4 _MainTex_TexelSize first. Parameters are set by VR180Fisheye.SetParams.
//
// FisheyeColour(lon, lat, right): the colour seen in direction (lon, lat) of the levelled view,
// taken from the left or right fisheye circle with Catmull-Rom bicubic filtering from the source
// texels. Black outside the lens's image circle.

#ifndef VR180_FISHEYE_INCLUDED
#define VR180_FISHEYE_INCLUDED

float4x4 _M0, _M1;      // view direction -> lens direction (lens calibration x levelling)
float4 _Lens0, _Lens1;  // focal length (px), centre x, centre y (px from the top-left of the whole image), image circle radius (px)
float4 _K0, _K1;        // distortion k1..k4 per lens: theta_d = theta(1 + k1 t^2 + k2 t^4 + k3 t^6 + k4 t^8)

float4 FisheyeCatmullRom(float t)
{
    float t2 = t * t, t3 = t2 * t;
    return float4(
        -0.5 * t3 + t2 - 0.5 * t,
         1.5 * t3 - 2.5 * t2 + 1.0,
        -1.5 * t3 + 2.0 * t2 + 0.5 * t,
         0.5 * t3 - 0.5 * t2);
}

float3 FisheyeColour(float lon, float lat, bool right)
{
    // Direction in the view (x right, y up, z ahead), turned into the lens's frame
    float3 d = float3(cos(lat) * sin(lon), sin(lat), cos(lat) * cos(lon));
    float3x3 m = (float3x3)_M0;
    float4 lens = _Lens0;
    float4 k = _K0;
    if (right) { m = (float3x3)_M1; lens = _Lens1; k = _K1; }
    float3 q = mul(m, d);

    // Fisheye projection
    float rho = length(q.xy);
    float theta = atan2(rho, q.z);
    float th2 = theta * theta;
    float thetaD = theta * (1.0 + th2 * (k.x + th2 * (k.y + th2 * (k.z + th2 * k.w))));
    float r = lens.x * thetaD;
    if (r > lens.w) return float3(0, 0, 0);
    float2 dir = rho > 1e-7 ? q.xy / rho : float2(0, 0);
    float2 pix = float2(lens.y + r * dir.x, lens.z - r * dir.y);   // from the top-left

    // Texel position (texture rows run bottom-up), then bicubic
    int texW = (int)_MainTex_TexelSize.z, texH = (int)_MainTex_TexelSize.w;
    float2 tc = float2(pix.x, _MainTex_TexelSize.w - pix.y) - 0.5;
    int2 base = (int2)floor(tc);
    float2 fr = tc - base;
    float4 wx = FisheyeCatmullRom(fr.x);
    float4 wy = FisheyeCatmullRom(fr.y);

    float3 col = 0.0;
    [unroll] for (int y = 0; y < 4; y++)
    {
        int ty = clamp(base.y - 1 + y, 0, texH - 1);
        float3 row = 0.0;
        [unroll] for (int x = 0; x < 4; x++)
        {
            int tx = clamp(base.x - 1 + x, 0, texW - 1);
            row += LOAD_TEXTURE2D(_MainTex, int2(tx, ty)).rgb * wx[x];
        }
        col += row * wy[y];
    }
    return saturate(col);
}

#endif
