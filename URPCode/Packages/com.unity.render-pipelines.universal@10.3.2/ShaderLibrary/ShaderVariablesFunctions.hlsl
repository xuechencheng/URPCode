#ifndef UNITY_SHADER_VARIABLES_FUNCTIONS_INCLUDED
#define UNITY_SHADER_VARIABLES_FUNCTIONS_INCLUDED
// 初始化位置信息
VertexPositionInputs GetVertexPositionInputs(float3 positionOS)
{
    VertexPositionInputs input;
    input.positionWS = TransformObjectToWorld(positionOS);
    input.positionVS = TransformWorldToView(input.positionWS);
    input.positionCS = TransformWorldToHClip(input.positionWS);
    //将xy从[-1, 1]变化到[0, 1]
    float4 ndc = input.positionCS * 0.5f;
    input.positionNDC.xy = float2(ndc.x, ndc.y * _ProjectionParams.x) + ndc.w;
    input.positionNDC.zw = input.positionCS.zw;
    return input;
}

VertexNormalInputs GetVertexNormalInputs(float3 normalOS)
{
    VertexNormalInputs tbn;
    tbn.tangentWS = real3(1.0, 0.0, 0.0);
    tbn.bitangentWS = real3(0.0, 1.0, 0.0);
    tbn.normalWS = TransformObjectToWorldNormal(normalOS);
    return tbn;
}

// 计算世界空间的法线，切线和第二切线
VertexNormalInputs GetVertexNormalInputs(float3 normalOS, float4 tangentOS)
{
    VertexNormalInputs tbn;
    real sign = tangentOS.w * GetOddNegativeScale();
    tbn.normalWS = TransformObjectToWorldNormal(normalOS);
    tbn.tangentWS = TransformObjectToWorldDir(tangentOS.xyz);
    tbn.bitangentWS = cross(tbn.normalWS, tbn.tangentWS) * sign;
    return tbn;
}

float4 GetScaledScreenParams()
{
    //scaledCameraWidth, scaledCameraHeight, 1.0f + 1.0f / scaledCameraWidth, 1.0f + 1.0f / scaledCameraHeight
    return _ScaledScreenParams;
}

// Returns 'true' if the current view performs a perspective projection.
// 1st
bool IsPerspectiveProjection()
{
    return (unity_OrthoParams.w == 0);
}
// 1st
float3 GetCameraPositionWS()
{
    return _WorldSpaceCameraPos;
}

// 1st
float3 GetCurrentViewPosition()
{
    return GetCameraPositionWS();
}

// Returns the forward (central) direction of the current view in the world space.
// 1st
float3 GetViewForwardDir()
{
    float4x4 viewMat = GetWorldToViewMatrix();
    return -viewMat[2].xyz;
}

// 视野方向，从像素指向相机
float3 GetWorldSpaceViewDir(float3 positionWS)
{
    if (IsPerspectiveProjection())
    {
        return GetCurrentViewPosition() - positionWS;
    }
    else
    {
        return -GetViewForwardDir();
    }
}

float3 GetWorldSpaceNormalizeViewDir(float3 positionWS)
{
    if (IsPerspectiveProjection())
    {
        // Perspective
        float3 V = GetCurrentViewPosition() - positionWS;
        return normalize(V);
    }
    else
    {
        // Orthographic
        return -GetViewForwardDir();
    }
}

// UNITY_MATRIX_V defines a right-handed view space with the Z axis pointing towards the viewer.
// This function reverses the direction of the Z axis (so that it points forward),
// making the view space coordinate system left-handed.
void GetLeftHandedViewSpaceMatrices(out float4x4 viewMatrix, out float4x4 projMatrix)
{
    viewMatrix = UNITY_MATRIX_V;
    viewMatrix._31_32_33_34 = -viewMatrix._31_32_33_34;

    projMatrix = UNITY_MATRIX_P;
    projMatrix._13_23_33_43 = -projMatrix._13_23_33_43;
}
// Perfect 1st
void AlphaDiscard(real alpha, real cutoff, real offset = 0.0h)
{
    #ifdef _ALPHATEST_ON
        clip(alpha - cutoff + offset);
    #endif
}
// Perfect
half OutputAlpha(half outputAlpha, half surfaceType = 0.0)
{
    return surfaceType == 1 ? outputAlpha : 1.0;
}

// A word on normalization of normals:
// For better quality normals should be normalized before and after
// interpolation.
// 1) In vertex, skinning or blend shapes might vary significantly the lenght of normal.
// 2) In fragment, because even outputting unit-length normals interpolation can make it non-unit.
// 3) In fragment when using normal map, because mikktspace sets up non orthonormal basis.
// However we will try to balance performance vs quality here as also let users configure that as
// shader quality tiers.
// Low Quality Tier: Normalize either per-vertex or per-pixel depending if normalmap is sampled.
// Medium Quality Tier: Always normalize per-vertex. Normalize per-pixel only if using normal map
// High Quality Tier: Normalize in both vertex and pixel shaders.
real3 NormalizeNormalPerVertex(real3 normalWS)
{
    #if defined(SHADER_QUALITY_LOW) && defined(_NORMALMAP)
        return normalWS;
    #else
        return normalize(normalWS);
    #endif
}
//单位化法线
real3 NormalizeNormalPerPixel(real3 normalWS)
{
    #if defined(SHADER_QUALITY_HIGH) || defined(_NORMALMAP)
        return normalize(normalWS);
    #else
        return normalWS;
    #endif
}

//齐次空间的屏幕坐标
// TODO: A similar function should be already available in SRP lib on master. Use that instead
float4 ComputeScreenPos(float4 positionCS)
{
    float4 o = positionCS * 0.5f;
    o.xy = float2(o.x, o.y * _ProjectionParams.x) + o.w;
    o.zw = positionCS.zw;
    return o;
}
// 根据深度值z计算雾的影响
real ComputeFogFactor(float z)
{
    float clipZ_01 = UNITY_Z_0_FAR_FROM_CLIPSPACE(z);
    #if defined(FOG_LINEAR)
        // factor = (end-z)/(end-start) = z * (-1/(end-start)) + (end/(end-start))
        float fogFactor = saturate(clipZ_01 * unity_FogParams.z + unity_FogParams.w);
        return real(fogFactor);
    #elif defined(FOG_EXP) || defined(FOG_EXP2)
        // factor = exp(-(density*z)^2)
        // -density * z computed at vertex
        return real(unity_FogParams.x * clipZ_01);
    #else
        return 0.0h;
    #endif
}

real ComputeFogIntensity(real fogFactor)
{
    real fogIntensity = 0.0h;
    #if defined(FOG_LINEAR) || defined(FOG_EXP) || defined(FOG_EXP2)
        #if defined(FOG_EXP)
            // factor = exp(-density*z)
            // fogFactor = density*z compute at vertex
            fogIntensity = saturate(exp2(-fogFactor));//exp2以2为底的指数函数
        #elif defined(FOG_EXP2)
            // factor = exp(-(density*z)^2)
            // fogFactor = density*z compute at vertex
            fogIntensity = saturate(exp2(-fogFactor * fogFactor));
        #elif defined(FOG_LINEAR)
            fogIntensity = fogFactor;
        #endif
    #endif
    return fogIntensity;
}

half3 MixFogColor(real3 fragColor, real3 fogColor, real fogFactor)
{
    #if defined(FOG_LINEAR) || defined(FOG_EXP) || defined(FOG_EXP2)
        real fogIntensity = ComputeFogIntensity(fogFactor);
        fragColor = lerp(fogColor, fragColor, fogIntensity);
    #endif
    return fragColor;
}
// MixFog
half3 MixFog(real3 fragColor, real fogFactor)
{
    return MixFogColor(fragColor, unity_FogColor.rgb, fogFactor);
}

void TransformScreenUV(inout float2 uv, float screenHeight)
{
    #if UNITY_UV_STARTS_AT_TOP
        uv.y = screenHeight - (uv.y * _ScaleBiasRt.x + _ScaleBiasRt.y * screenHeight);
    #endif
}

void TransformScreenUV(inout float2 uv)
{
    #if UNITY_UV_STARTS_AT_TOP
    TransformScreenUV(uv, GetScaledScreenParams().y);
    #endif
}

void TransformNormalizedScreenUV(inout float2 uv)
{
    #if UNITY_UV_STARTS_AT_TOP
        TransformScreenUV(uv, 1.0);
    #endif
}
// 屏幕空间UV
float2 GetNormalizedScreenSpaceUV(float2 positionCS)
{
    float2 normalizedScreenSpaceUV = positionCS.xy * rcp(GetScaledScreenParams().xy); //rcp 近似的倒数 scaledCameraWidth, scaledCameraHeight
    TransformNormalizedScreenUV(normalizedScreenSpaceUV);
    return normalizedScreenSpaceUV;
}

float2 GetNormalizedScreenSpaceUV(float4 positionCS)
{
    return GetNormalizedScreenSpaceUV(positionCS.xy);
}

#if defined(UNITY_SINGLE_PASS_STEREO)
    float2 TransformStereoScreenSpaceTex(float2 uv, float w)
    {
        // TODO: RVS support can be added here, if Universal decides to support it
        float4 scaleOffset = unity_StereoScaleOffset[unity_StereoEyeIndex];
        return uv.xy * scaleOffset.xy + scaleOffset.zw * w;
    }

    float2 UnityStereoTransformScreenSpaceTex(float2 uv)
    {
        return TransformStereoScreenSpaceTex(saturate(uv), 1.0);
    }
#else
    #define UnityStereoTransformScreenSpaceTex(uv) uv
#endif // defined(UNITY_SINGLE_PASS_STEREO)

#endif // UNITY_SHADER_VARIABLES_FUNCTIONS_INCLUDED
