// SPDX-License-Identifier: MIT
Shader "Hidden/Gaussian Splatting/Composite"
{
    SubShader
    {
CGINCLUDE
#include "UnityCG.cginc"

struct v2f
{
    float4 vertex : SV_POSITION;
    UNITY_VERTEX_OUTPUT_STEREO
};

v2f vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
#if defined(UNITY_STEREO_INSTANCING_ENABLED)
    UnitySetupInstanceID(instID);
    UnitySetupCompoundMatrices();
#endif
    v2f o = (v2f)0;
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
    float2 quadPos = float2(vtxID&1, (vtxID>>1)&1) * 4.0 - 1.0;
	o.vertex = float4(quadPos, 1, 1);
    return o;
}

#if defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
Texture2DArray _GaussianSplatRT;
#else
Texture2D _GaussianSplatRT;
#endif

half4 LoadGaussianSplatColor(float2 pixelPosition)
{
#if defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
    return _GaussianSplatRT.Load(int4(pixelPosition, unity_StereoEyeIndex, 0));
#else
    return _GaussianSplatRT.Load(int3(pixelPosition, 0));
#endif
}
#if defined(GSAC_ENV_VISIBILITY_CAP)
#if defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
Texture2DArray _GaussianEnvironmentVisibilityRT;
#else
Texture2D _GaussianEnvironmentVisibilityRT;
#endif

float LoadGaussianEnvironmentVisibility(float2 pixelPosition)
{
#if defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
    return _GaussianEnvironmentVisibilityRT.Load(int4(pixelPosition, unity_StereoEyeIndex, 0)).r;
#else
    return _GaussianEnvironmentVisibilityRT.Load(int3(pixelPosition, 0)).r;
#endif
}
#endif

float _GaussianSceneDepthAlphaThreshold;

half4 frag (v2f i) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
    half4 col = LoadGaussianSplatColor(i.vertex.xy);
    col.rgb = GammaToLinearSpace(col.rgb);
    col.a = saturate(col.a * 1.5);
    return col;
}

// The Gaussian RT stores gamma-space color already multiplied by coverage.
// Convert straight color to linear, then premultiply by the final coverage.
// This avoids darkening partially occluded pixels and keeps camera alpha valid.
half4 fragPremultiplied(v2f i) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
    half4 col = LoadGaussianSplatColor(i.vertex.xy);
    half3 straightColor = col.a > 0.00001 ? col.rgb / col.a : half3(0, 0, 0);
    half outputAlpha = saturate(col.a * 1.5);
#if defined(GSAC_ENV_VISIBILITY_CAP)
    // A real-depth edge is shared coverage, not independent opacity per splat.
    // Bound accumulated coverage before applying it once to straight color.
    outputAlpha = min(outputAlpha, LoadGaussianEnvironmentVisibility(i.vertex.xy));
#endif
    return half4(GammaToLinearSpace(straightColor) * outputAlpha, outputAlpha);
}

v2f vertNearDepth(uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
    v2f o = vert(vtxID, instID);
#if defined(UNITY_REVERSED_Z)
    o.vertex.z = o.vertex.w;
#else
    o.vertex.z = UNITY_NEAR_CLIP_VALUE * o.vertex.w;
#endif
    return o;
}

half4 fragDepthFromAlpha(v2f i) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
    half4 col = LoadGaussianSplatColor(i.vertex.xy);
    clip(col.a - _GaussianSceneDepthAlphaThreshold);
    return 0;
}

ENDCG

        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha

CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute
// Use Unity's default compiler so native Multiview gl_ViewID is translated.
#pragma multi_compile _ STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON
ENDCG
        }

        Pass
        {
            ZWrite On
            ZTest Always
            ColorMask 0
            Cull Off
            Blend Zero One

CGPROGRAM
#pragma vertex vertNearDepth
#pragma fragment fragDepthFromAlpha
#pragma require compute
// Use Unity's default compiler so native Multiview gl_ViewID is translated.
#pragma multi_compile _ STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON
ENDCG
        }

        // Quest environment-depth profile: the camera target and splat RT both
        // carry premultiplied color. This replaces, rather than adds, a draw.
        Pass
        {
            Name "QuestPremultipliedComposite"
            ZWrite Off
            ZTest Always
            Cull Off
            Blend One OneMinusSrcAlpha

CGPROGRAM
#pragma vertex vert
#pragma fragment fragPremultiplied
#pragma multi_compile _ GSAC_ENV_VISIBILITY_CAP
#pragma require compute
// Use Unity's default compiler so native Multiview gl_ViewID is translated.
#pragma multi_compile _ STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON
ENDCG
        }
    }
}
