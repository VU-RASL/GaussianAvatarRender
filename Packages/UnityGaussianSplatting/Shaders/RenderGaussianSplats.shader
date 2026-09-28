// SPDX-License-Identifier: MIT
Shader "Gaussian Splatting/Render Splats"
{
    Properties
    {
        _GaussianSceneZTest ("Scene ZTest", Float) = 4
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }

        Pass
        {
            ZWrite Off
            ZTest [_GaussianSceneZTest]
            Blend 0 OneMinusDstAlpha One
            Blend 1 One One
            BlendOp 1 Max
            ColorMask R 1
            Cull Off
			
            
CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute
// Use Unity's default compiler so native Multiview gl_ViewID is translated.
#pragma multi_compile _ HARD_OCCLUSION SOFT_OCCLUSION
#pragma multi_compile _ GSAC_ENV_VISIBILITY_CAP
#pragma multi_compile _ STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON

#include "UnityCG.cginc"
#include "GaussianSplatting.hlsl"

#if defined(HARD_OCCLUSION) || defined(SOFT_OCCLUSION)
#include "Packages/com.meta.xr.sdk.core/Shaders/EnvironmentDepth/BiRP/EnvironmentOcclusionBiRP.cginc"
float _GsacEnvironmentDepthBias;
#endif

StructuredBuffer<uint> _OrderBuffer;
StructuredBuffer<vector> _TBuffer;
// RWStructuredBuffer<float3> _RECORD;
struct v2f
{	
    half4 col : COLOR0;
    float2 pos : TEXCOORD0;
    float4 vertex : SV_POSITION;
#if defined(HARD_OCCLUSION) || defined(SOFT_OCCLUSION)
    float3 environmentWorldPos : TEXCOORD1;
#endif
    UNITY_VERTEX_OUTPUT_STEREO
};

StructuredBuffer<SplatViewData> _SplatViewData;
ByteAddressBuffer _SplatSelectedBits;
uint _SplatBitsValid;
float _GaussianSplatClipFlipY;
#if defined(HARD_OCCLUSION) || defined(SOFT_OCCLUSION)
// Matches the projection used by CalcViewData, including render-target orientation.
float4x4 _GsacEnvironmentInverseViewProjection;
#endif

v2f vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
#if defined(UNITY_STEREO_INSTANCING_ENABLED)
    // Unity's stereo instance pair shares one Gaussian index. Multiview uses
    // the hardware view ID instead and retains the original instance index.
    UnitySetupInstanceID(instID);
    UnitySetupCompoundMatrices();
    instID = unity_InstanceID;
#endif
    v2f o = (v2f)0;
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
    instID = _OrderBuffer[instID];
	SplatViewData view = _SplatViewData[instID];
	float4 centerClipPos = view.pos;





	bool behindCam = centerClipPos.w <= 0;
	if (behindCam)
	{
		o.vertex = asfloat(0x7fc00000); // NaN discards the primitive
	}
	else
	{
		o.col.r = f16tof32(view.color.x >> 16);
		o.col.g = f16tof32(view.color.x);
		o.col.b = f16tof32(view.color.y >> 16);
		o.col.a = f16tof32(view.color.y);

		// float3 T = _TBuffer[instID];
		
		// float4 face_camSpace = mul(UNITY_MATRIX_MV, float4(T,0));
		
		// float gs_depth = centerClipPos.z;
		// float face_depth = face_camSpace.z;


		// if (gs_depth < face_depth)
		// {
		// 	o.col.a = 0;
		// }

		uint idx = vtxID;
		float2 quadPos = float2(idx&1, (idx>>1)&1) * 2.0 - 1.0;
		quadPos *= 2;

		o.pos = quadPos;

		float2 deltaScreenPos = (quadPos.x * view.axis1 + quadPos.y * view.axis2) * 2 / _ScreenParams.xy;
		o.vertex = centerClipPos;
		o.vertex.xy += deltaScreenPos * centerClipPos.w;
#if defined(HARD_OCCLUSION) || defined(SOFT_OCCLUSION)
        // Each billboard corner has its own world position: a real edge can cut
        // through a splat instead of hiding the entire splat at its center.
        float4 environmentWorld = mul(_GsacEnvironmentInverseViewProjection, o.vertex);
        o.environmentWorldPos = environmentWorld.xyz / environmentWorld.w;
#endif
		if (_GaussianSplatClipFlipY > 0.5)
			o.vertex.y = -o.vertex.y;




		// is this splat selected?
		if (_SplatBitsValid)
		{
			uint wordIdx = instID / 32;
			uint bitIdx = instID & 31;
			uint selVal = _SplatSelectedBits.Load(wordIdx * 4);
			if (selVal & (1 << bitIdx))
			{
				o.col.a = -1;				
			}
		}
	} 
    return o;
}

#if defined(GSAC_ENV_VISIBILITY_CAP)
struct GsacFragmentOutput
{
    half4 color : SV_Target0;
    float visibility : SV_Target1;
};
GsacFragmentOutput frag(v2f i)
#else
half4 frag (v2f i) : SV_Target
#endif
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
	float power = -dot(i.pos, i.pos);
	half alpha = exp(power);
	if (i.col.a >= 0)
	{
		alpha = saturate(alpha * i.col.a);
	}
	else
	{
		// "selected" splat: magenta outline, increase opacity, magenta tint
		half3 selectedColor = half3(1,0,1);
		if (alpha > 7.0/255.0)
		{
			if (alpha < 10.0/255.0)
			{
				alpha = 1;
				i.col.rgb = selectedColor;
			}
			alpha = saturate(alpha + 0.3);
		}
		i.col.rgb = lerp(i.col.rgb, selectedColor, 0.5);
	}
	
    if (alpha < 1.0/255.0)
        discard;

    half4 res = half4(i.col.rgb * alpha, alpha);
#if defined(GSAC_ENV_VISIBILITY_CAP)
    float gsacVisibility = 1.0;
#endif
#if defined(HARD_OCCLUSION) || defined(SOFT_OCCLUSION)
    // Skip depth sampling for already discarded Gaussian tails. The SDK uses
    // this eye's environment texture and preserves premultiplied alpha.
#if defined(GSAC_ENV_VISIBILITY_CAP)
    // Reuse the same sample for color attenuation and a separate visibility
    // envelope. The envelope must not become opaque as splats accumulate.
    gsacVisibility = CalculateEnvironmentDepthOcclusion(i.environmentWorldPos, _GsacEnvironmentDepthBias);
    if (gsacVisibility < 0.01)
        discard;
    res *= gsacVisibility;
#else
    META_DEPTH_OCCLUDE_OUTPUT_PREMULTIPLY_WORLDPOS(i.environmentWorldPos, res, _GsacEnvironmentDepthBias);
#endif
#endif
#if defined(GSAC_ENV_VISIBILITY_CAP)
    GsacFragmentOutput output;
    output.color = res;
    output.visibility = gsacVisibility;
    return output;
#else
    return res;
#endif
}
ENDCG
        }

        Pass
        {
            ZWrite Off
            ZTest [_GaussianSceneZTest]
            Blend 0 OneMinusDstAlpha One
            Blend 1 One One
            BlendOp 1 Max
            ColorMask R 1
            Cull Off

CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute
// Use Unity's default compiler so native Multiview gl_ViewID is translated.
#pragma multi_compile _ HARD_OCCLUSION SOFT_OCCLUSION
#pragma multi_compile _ GSAC_ENV_VISIBILITY_CAP
#pragma multi_compile _ STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON

#include "UnityCG.cginc"
#include "GaussianSplatting.hlsl"

#if defined(HARD_OCCLUSION) || defined(SOFT_OCCLUSION)
#include "Packages/com.meta.xr.sdk.core/Shaders/EnvironmentDepth/BiRP/EnvironmentOcclusionBiRP.cginc"
float _GsacEnvironmentDepthBias;
#endif

StructuredBuffer<uint> _OrderBuffer;
ByteAddressBuffer _SplatSelectedBits;
uint _SplatBitsValid;
float _SplatScale;
float _SplatOpacityScale;
float _GaussianSplatClipFlipY;

struct v2f
{
    half4 col : COLOR0;
    float2 pos : TEXCOORD0;
    float4 vertex : SV_POSITION;
#if defined(HARD_OCCLUSION) || defined(SOFT_OCCLUSION)
    float3 environmentWorldPos : TEXCOORD1;
#endif
    UNITY_VERTEX_OUTPUT_STEREO
};

void DecomposeCovarianceForVertex(float3 cov2d, out float2 v1, out float2 v2)
{
    float diag1 = cov2d.x, diag2 = cov2d.z, offDiag = cov2d.y;
    float mid = 0.5f * (diag1 + diag2);
    float radius = length(float2((diag1 - diag2) / 2.0, offDiag));
    float lambda1 = mid + radius;
    float lambda2 = max(mid - radius, 0.1);
    float2 diagVec = normalize(float2(offDiag, lambda1 - diag1));
#if !defined(UNITY_STEREO_INSTANCING_ENABLED) && !defined(UNITY_STEREO_MULTIVIEW_ENABLED)
    // Preserve the legacy 2D-target orientation. Stereo covariance below
    // already contains the signed per-eye projection, so needs no Y reflection.
    diagVec.y = -diagVec.y;
#endif
    float maxSize = 4096.0;
    v1 = min(sqrt(2.0 * lambda1), maxSize) * diagVec;
    v2 = min(sqrt(2.0 * lambda2), maxSize) * float2(diagVec.y, -diagVec.x);
}

v2f vert(uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
#if defined(UNITY_STEREO_INSTANCING_ENABLED)
    // Unity's stereo instance pair shares one Gaussian index. Multiview uses
    // the hardware view ID instead and retains the original instance index.
    UnitySetupInstanceID(instID);
    UnitySetupCompoundMatrices();
    instID = unity_InstanceID;
#endif
    v2f o = (v2f)0;
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
    instID = _OrderBuffer[instID];

    SplatData splat = LoadSplatData(instID);
    float3 centerWorldPos = mul(unity_ObjectToWorld, float4(splat.pos, 1)).xyz;
    float4 centerClipPos = mul(UNITY_MATRIX_VP, float4(centerWorldPos, 1));

    if (centerClipPos.w <= 0)
    {
        o.vertex = asfloat(0x7fc00000);
        return o;
    }

    float3x3 splatRotScaleMat = CalcMatrixFromRotationScale(splat.rot, splat.scale);
    float3 cov3d0, cov3d1;
    CalcCovariance3D(splatRotScaleMat, cov3d0, cov3d1);

    float splatScale2 = _SplatScale * _SplatScale;
    cov3d0 *= splatScale2;
    cov3d1 *= splatScale2;

    float3 cov2d = CalcCovariance2D(splat.pos, cov3d0, cov3d1, UNITY_MATRIX_MV, UNITY_MATRIX_P, _ScreenParams);
#if defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
    // CalcCovariance2D uses the X focal length for both Jacobian rows.
    // Convert its Y row to this eye's signed Y focal length, retaining the
    // isotropic 0.3-pixel low-pass term added by that function.
    float focalRatio = (_ScreenParams.y * UNITY_MATRIX_P._m11) /
        (_ScreenParams.x * UNITY_MATRIX_P._m00);
    cov2d.y *= focalRatio;
    cov2d.z = (cov2d.z - 0.3) * focalRatio * focalRatio + 0.3;
#endif
    float2 axis1, axis2;
    DecomposeCovarianceForVertex(cov2d, axis1, axis2);

    o.col.rgb = saturate(splat.sh.col);
    o.col.a = min(splat.opacity * _SplatOpacityScale, 65000);

    uint idx = vtxID;
    float2 quadPos = float2(idx & 1, (idx >> 1) & 1) * 2.0 - 1.0;
    quadPos *= 2;
    o.pos = quadPos;

    float2 deltaScreenPos = (quadPos.x * axis1 + quadPos.y * axis2) * 2 / _ScreenParams.xy;
    o.vertex = centerClipPos;
    o.vertex.xy += deltaScreenPos * centerClipPos.w;
#if defined(HARD_OCCLUSION) || defined(SOFT_OCCLUSION)
    // Recover the camera-facing billboard offset using this eye's projection.
    // Do this before the existing render-target Y flip.
    float2 viewOffset = deltaScreenPos * centerClipPos.w /
        float2(UNITY_MATRIX_P._m00, UNITY_MATRIX_P._m11);
    o.environmentWorldPos = centerWorldPos +
        mul((float3x3)UNITY_MATRIX_I_V, float3(viewOffset, 0.0));
#endif
    if (_GaussianSplatClipFlipY > 0.5)
        o.vertex.y = -o.vertex.y;

    if (_SplatBitsValid)
    {
        uint wordIdx = instID / 32;
        uint bitIdx = instID & 31;
        uint selVal = _SplatSelectedBits.Load(wordIdx * 4);
        if (selVal & (1 << bitIdx))
            o.col.a = -1;
    }

    return o;
}

#if defined(GSAC_ENV_VISIBILITY_CAP)
struct GsacFragmentOutput
{
    half4 color : SV_Target0;
    float visibility : SV_Target1;
};
GsacFragmentOutput frag(v2f i)
#else
half4 frag(v2f i) : SV_Target
#endif
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
    float power = -dot(i.pos, i.pos);
    half alpha = exp(power);
    if (i.col.a >= 0)
    {
        alpha = saturate(alpha * i.col.a);
    }
    else
    {
        half3 selectedColor = half3(1,0,1);
        if (alpha > 7.0/255.0)
        {
            if (alpha < 10.0/255.0)
            {
                alpha = 1;
                i.col.rgb = selectedColor;
            }
            alpha = saturate(alpha + 0.3);
        }
        i.col.rgb = lerp(i.col.rgb, selectedColor, 0.5);
    }

    if (alpha < 1.0/255.0)
        discard;

    half4 res = half4(i.col.rgb * alpha, alpha);
#if defined(GSAC_ENV_VISIBILITY_CAP)
    float gsacVisibility = 1.0;
#endif
#if defined(HARD_OCCLUSION) || defined(SOFT_OCCLUSION)
#if defined(GSAC_ENV_VISIBILITY_CAP)
    // Reuse the same sample for color attenuation and a separate visibility
    // envelope. The envelope must not become opaque as splats accumulate.
    gsacVisibility = CalculateEnvironmentDepthOcclusion(i.environmentWorldPos, _GsacEnvironmentDepthBias);
    if (gsacVisibility < 0.01)
        discard;
    res *= gsacVisibility;
#else
    META_DEPTH_OCCLUDE_OUTPUT_PREMULTIPLY_WORLDPOS(i.environmentWorldPos, res, _GsacEnvironmentDepthBias);
#endif
#endif
#if defined(GSAC_ENV_VISIBILITY_CAP)
    GsacFragmentOutput output;
    output.color = res;
    output.visibility = gsacVisibility;
    return output;
#else
    return res;
#endif
}
ENDCG
        }
    }
}
