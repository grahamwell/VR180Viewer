// VR180Fisheye.hlsl: the raw QooCam fisheye lens model, shared by Hidden/VR180/FisheyeToEquirect
// and Hidden/VR180/EyeExtract. The including shader must declare TEXTURE2D(_MainTex) and
// float4 _MainTex_TexelSize first. Parameters are set by VR180Fisheye.SetParams.
//
// FisheyeColour(lon, lat, right): the colour seen in direction (lon, lat) of the levelled view,
// taken from the left or right fisheye circle with Catmull-Rom bicubic filtering from the source
// texels. Black outside the lens's image circle (and outside its own half of the image).
//
// Lens patch (_LensPatch = 1): each lens can see the other lens's barrel and housing at the edge
// of its view (the left lens sees the right one from about 83 degrees off its axis). Where that is,
// the colour comes from the other lens instead, looking in the same direction. So close to the
// side, the two lenses see almost the same thing (the baseline points nearly along the line of
// sight, so there's next to no stereo there anyway), which makes this an almost invisible repair.
// The obstruction is described in each lens's own frame as two rounded boxes (VR180Fisheye.cs).

#ifndef VR180_FISHEYE_INCLUDED
#define VR180_FISHEYE_INCLUDED

float4x4 _M0, _M1;      // view direction -> lens direction (lens calibration x levelling)
float4 _Lens0, _Lens1;  // focal length (px), centre x, centre y (px from the top-left of the whole image), image circle radius (px)
float4 _K0, _K1;        // distortion k1..k4 per lens: theta_d = theta(1 + k1 t^2 + k2 t^4 + k3 t^6 + k4 t^8)
float _LensPatch;       // 1 = fill in where each lens sees the other one
// Obstruction boxes per lens, in degrees in the lens's own frame: (inner edge, measured outwards
// towards the other lens; centre latitude; half height; corner radius). A = barrel, B = housing.
float4 _OccA0, _OccB0, _OccA1, _OccB1;
float2 _OccFeather;     // margin beyond the boxes, then the width of the soft edge (degrees)

float4 FisheyeCatmullRom(float t)
{
    float t2 = t * t, t3 = t2 * t;
    return float4(
        -0.5 * t3 + t2 - 0.5 * t,
         1.5 * t3 - 2.5 * t2 + 1.0,
        -1.5 * t3 + 2.0 * t2 + 0.5 * t,
         0.5 * t3 - 0.5 * t2);
}

// Colour from one lens for direction q in that lens's frame; black outside its image circle or its half of the image
float3 FisheyeSampleLens(float3 q, bool right)
{
    float4 lens = right ? _Lens1 : _Lens0;
    float4 k = right ? _K1 : _K0;

    // Fisheye projection
    float rho = length(q.xy);
    float theta = atan2(rho, q.z);
    float th2 = theta * theta;
    float thetaD = theta * (1.0 + th2 * (k.x + th2 * (k.y + th2 * (k.z + th2 * k.w))));
    float r = lens.x * thetaD;
    if (r > lens.w) return float3(0, 0, 0);
    float2 dir = rho > 1e-7 ? q.xy / rho : float2(0, 0);
    float2 pix = float2(lens.y + r * dir.x, lens.z - r * dir.y);   // from the top-left

    // Each lens has its own sensor, i.e. its own half of the image
    int texW = (int)_MainTex_TexelSize.z, texH = (int)_MainTex_TexelSize.w;
    int halfW = texW / 2;
    int xMin = right ? halfW : 0, xMax = right ? texW - 1 : halfW - 1;
    if (pix.x < xMin - 0.5 || pix.x > xMax + 1.5) return float3(0, 0, 0);

    // Texel position (texture rows run bottom-up), then bicubic
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
            int tx = clamp(base.x - 1 + x, xMin, xMax);
            row += LOAD_TEXTURE2D(_MainTex, int2(tx, ty)).rgb * wx[x];
        }
        col += row * wy[y];
    }
    return saturate(col);
}

// Signed distance (degrees, + outside) from a rounded box: s >= box.x, |lat - box.y| <= box.z, corners of radius box.w
float FisheyeBoxDistance(float s, float lat, float4 box)
{
    float2 q = float2(box.x + box.w - s, abs(lat - box.y) - (box.z - box.w));
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - box.w;
}

// How much of direction q (this lens's frame) is blocked by the other lens: 1 = fully, 0 = clear
float FisheyeObstruction(float3 q, bool right)
{
    float lon = degrees(atan2(q.x, q.z));
    float lat = degrees(asin(clamp(q.y, -1.0, 1.0)));
    float s = right ? -lon : lon;   // measured towards the other lens
    float d = min(FisheyeBoxDistance(s, lat, right ? _OccA1 : _OccA0),
                  FisheyeBoxDistance(s, lat, right ? _OccB1 : _OccB0));
    return 1.0 - smoothstep(0.0, 1.0, saturate((d - _OccFeather.x) / max(_OccFeather.y, 0.01)));
}

float3 FisheyeColour(float lon, float lat, bool right)
{
    // Direction in the view (x right, y up, z ahead), turned into the lens's frame
    float3 d = float3(cos(lat) * sin(lon), sin(lat), cos(lat) * cos(lon));
    float3x3 m = (float3x3)_M0, mOther = (float3x3)_M1;
    if (right) { m = (float3x3)_M1; mOther = (float3x3)_M0; }
    float3 q = mul(m, d);
    float3 col = FisheyeSampleLens(q, right);

    if (_LensPatch > 0.5)
    {
        float w = FisheyeObstruction(q, right);
        if (w > 0.0)
        {
            float3 fill = FisheyeSampleLens(mul(mOther, d), !right);
            col = lerp(col, fill, w);
        }
    }
    return col;
}

#endif
