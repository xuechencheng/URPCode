using System;
using System.Diagnostics;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine.Profiling;

namespace UnityEngine.Rendering.Universal
{
    public abstract partial class ScriptableRenderer : IDisposable
    {
        private static class Profiling
        {
            private const string k_Name = nameof(ScriptableRenderer);
            public static readonly ProfilingSampler setPerCameraShaderVariables = new ProfilingSampler($"{k_Name}.{nameof(SetPerCameraShaderVariables)}");
            public static readonly ProfilingSampler sortRenderPasses            = new ProfilingSampler($"Sort Render Passes");
            public static readonly ProfilingSampler setupLights                 = new ProfilingSampler($"{k_Name}.{nameof(SetupLights)}");
            public static readonly ProfilingSampler setupCamera                 = new ProfilingSampler($"Setup Camera Parameters");
            public static readonly ProfilingSampler addRenderPasses             = new ProfilingSampler($"{k_Name}.{nameof(AddRenderPasses)}");
            public static readonly ProfilingSampler clearRenderingState         = new ProfilingSampler($"{k_Name}.{nameof(ClearRenderingState)}");
            public static readonly ProfilingSampler internalStartRendering      = new ProfilingSampler($"{k_Name}.{nameof(InternalStartRendering)}");
            public static readonly ProfilingSampler internalFinishRendering     = new ProfilingSampler($"{k_Name}.{nameof(InternalFinishRendering)}");
            public static class RenderBlock
            {
                private const string k_Name = nameof(RenderPassBlock);
                public static readonly ProfilingSampler beforeRendering          = new ProfilingSampler($"{k_Name}.{nameof(RenderPassBlock.BeforeRendering)}");
                public static readonly ProfilingSampler mainRenderingOpaque      = new ProfilingSampler($"{k_Name}.{nameof(RenderPassBlock.MainRenderingOpaque)}");
                public static readonly ProfilingSampler mainRenderingTransparent = new ProfilingSampler($"{k_Name}.{nameof(RenderPassBlock.MainRenderingTransparent)}");
                public static readonly ProfilingSampler afterRendering           = new ProfilingSampler($"{k_Name}.{nameof(RenderPassBlock.AfterRendering)}");
            }
            public static class RenderPass
            {
                private const string k_Name = nameof(ScriptableRenderPass);
                public static readonly ProfilingSampler configure = new ProfilingSampler($"{k_Name}.{nameof(ScriptableRenderPass.Configure)}");
            }
        }

        protected ProfilingSampler profilingExecute { get; set; }

        public class RenderingFeatures
        {
            public bool cameraStacking { get; set; } = false;
            public bool msaa { get; set; } = true;
        }

        internal static ScriptableRenderer current = null;
        /// <summary>
        /// 设置Shader内矩阵信息 Done
        /// </summary>
        public static void SetCameraMatrices(CommandBuffer cmd, ref CameraData cameraData, bool setInverseMatrices)
        {
#if ENABLE_VR && ENABLE_XR_MODULE
            if (cameraData.xr.enabled)
            {
                cameraData.xr.UpdateGPUViewAndProjectionMatrices(cmd, ref cameraData, cameraData.xr.renderTargetIsRenderTexture);
                return;
            }
#endif
            Matrix4x4 viewMatrix = cameraData.GetViewMatrix();
            Matrix4x4 projectionMatrix = cameraData.GetProjectionMatrix();
            cmd.SetViewProjectionMatrices(viewMatrix, projectionMatrix);
            if (setInverseMatrices)
            {
                Matrix4x4 gpuProjectionMatrix = cameraData.GetGPUProjectionMatrix();
                Matrix4x4 viewAndProjectionMatrix = gpuProjectionMatrix * viewMatrix;
                Matrix4x4 inverseViewMatrix = Matrix4x4.Inverse(viewMatrix);
                Matrix4x4 inverseProjectionMatrix = Matrix4x4.Inverse(gpuProjectionMatrix);
                Matrix4x4 inverseViewProjection = inverseViewMatrix * inverseProjectionMatrix;
                Matrix4x4 worldToCameraMatrix = Matrix4x4.Scale(new Vector3(1.0f, 1.0f, -1.0f)) * viewMatrix;
                Matrix4x4 cameraToWorldMatrix = worldToCameraMatrix.inverse;
                cmd.SetGlobalMatrix(ShaderPropertyId.worldToCameraMatrix, worldToCameraMatrix);
                cmd.SetGlobalMatrix(ShaderPropertyId.cameraToWorldMatrix, cameraToWorldMatrix);
                cmd.SetGlobalMatrix(ShaderPropertyId.inverseViewMatrix, inverseViewMatrix);
                cmd.SetGlobalMatrix(ShaderPropertyId.inverseProjectionMatrix, inverseProjectionMatrix);
                cmd.SetGlobalMatrix(ShaderPropertyId.inverseViewAndProjectionMatrix, inverseViewProjection);
            }
        }

        /// <summary>
        /// 设置Shader内相机变量 Done
        /// </summary>
        void SetPerCameraShaderVariables(CommandBuffer cmd, ref CameraData cameraData)
        {
            using var profScope = new ProfilingScope(cmd, Profiling.setPerCameraShaderVariables);
            Camera camera = cameraData.camera;
            Rect pixelRect = cameraData.pixelRect;
            float renderScale = cameraData.isSceneViewCamera ? 1f : cameraData.renderScale;
            float scaledCameraWidth = (float)pixelRect.width * renderScale;
            float scaledCameraHeight = (float)pixelRect.height * renderScale;
            float cameraWidth = (float)pixelRect.width;
            float cameraHeight = (float)pixelRect.height;
            // Use eye texture's width and height as screen params when XR is enabled
            if(cameraData.xr.enabled)
            {
                scaledCameraWidth = (float)cameraData.cameraTargetDescriptor.width;
                scaledCameraHeight = (float)cameraData.cameraTargetDescriptor.height;
                cameraWidth = (float)cameraData.cameraTargetDescriptor.width;
                cameraHeight = (float)cameraData.cameraTargetDescriptor.height;
            }
            if (camera.allowDynamicResolution)
            {
                scaledCameraWidth *= ScalableBufferManager.widthScaleFactor;
                scaledCameraHeight *= ScalableBufferManager.heightScaleFactor;
            }
            float near = camera.nearClipPlane;
            float far = camera.farClipPlane;
            float invNear = Mathf.Approximately(near, 0.0f) ? 0.0f : 1.0f / near;
            float invFar = Mathf.Approximately(far, 0.0f) ? 0.0f : 1.0f / far;
            float isOrthographic = camera.orthographic ? 1.0f : 0.0f;
            // From http://www.humus.name/temp/Linearize%20depth.txt
            // But as depth component textures on OpenGL always return in 0..1 range (as in D3D), we have to use
            // the same constants for both D3D and OpenGL here.
            // OpenGL would be this:
            // zc0 = (1.0 - far / near) / 2.0;
            // zc1 = (1.0 + far / near) / 2.0;
            // D3D is this:
            float zc0 = 1.0f - far * invNear;// 1 - f / r
            float zc1 = far * invNear;// f / r
            Vector4 zBufferParams = new Vector4(zc0, zc1, zc0 * invFar, zc1 * invFar);// 1 - f/r, f/r, (1 - f/r) / f, f/r/f
            if (SystemInfo.usesReversedZBuffer)
            {
                zBufferParams.y += zBufferParams.x;
                zBufferParams.x = -zBufferParams.x;
                zBufferParams.w += zBufferParams.z;
                zBufferParams.z = -zBufferParams.z;
            }
            // Projection flip sign logic is very deep in GfxDevice::SetInvertProjectionMatrix
            // For now we don't deal with _ProjectionParams.x and let SetupCameraProperties handle it.
            // We need to enable this when we remove SetupCameraProperties
            // float projectionFlipSign = ???
            // Vector4 projectionParams = new Vector4(projectionFlipSign, near, far, 1.0f * invFar);
            // cmd.SetGlobalVector(ShaderPropertyId.projectionParams, projectionParams);
            Vector4 orthoParams = new Vector4(camera.orthographicSize * cameraData.aspectRatio, camera.orthographicSize, 0.0f, isOrthographic);
            // Camera and Screen variables as described in https://docs.unity3d.com/Manual/SL-UnityShaderVariables.html
            cmd.SetGlobalVector(ShaderPropertyId.worldSpaceCameraPos, camera.transform.position);
            cmd.SetGlobalVector(ShaderPropertyId.screenParams, new Vector4(cameraWidth, cameraHeight, 1.0f + 1.0f / cameraWidth, 1.0f + 1.0f / cameraHeight));
            cmd.SetGlobalVector(ShaderPropertyId.scaledScreenParams, new Vector4(scaledCameraWidth, scaledCameraHeight, 1.0f + 1.0f / scaledCameraWidth, 1.0f + 1.0f / scaledCameraHeight));
            cmd.SetGlobalVector(ShaderPropertyId.zBufferParams, zBufferParams);
            cmd.SetGlobalVector(ShaderPropertyId.orthoParams, orthoParams);
        }

        /// <summary>
        /// 设置Shader内的时间变量 Done
        /// </summary>
        /// https://docs.unity3d.com/Manual/SL-UnityShaderVariables.html
        void SetShaderTimeValues(CommandBuffer cmd, float time, float deltaTime, float smoothDeltaTime)
        {
            float timeEights = time / 8f;
            float timeFourth = time / 4f;
            float timeHalf = time / 2f;

            // Time values
            Vector4 timeVector = time * new Vector4(1f / 20f, 1f, 2f, 3f);
            Vector4 sinTimeVector = new Vector4(Mathf.Sin(timeEights), Mathf.Sin(timeFourth), Mathf.Sin(timeHalf), Mathf.Sin(time));
            Vector4 cosTimeVector = new Vector4(Mathf.Cos(timeEights), Mathf.Cos(timeFourth), Mathf.Cos(timeHalf), Mathf.Cos(time));
            Vector4 deltaTimeVector = new Vector4(deltaTime, 1f / deltaTime, smoothDeltaTime, 1f / smoothDeltaTime);
            Vector4 timeParametersVector = new Vector4(time, Mathf.Sin(time), Mathf.Cos(time), 0.0f);

            cmd.SetGlobalVector(ShaderPropertyId.time, timeVector);
            cmd.SetGlobalVector(ShaderPropertyId.sinTime, sinTimeVector);
            cmd.SetGlobalVector(ShaderPropertyId.cosTime, cosTimeVector);
            cmd.SetGlobalVector(ShaderPropertyId.deltaTime, deltaTimeVector);
            cmd.SetGlobalVector(ShaderPropertyId.timeParameters, timeParametersVector);
        }

        public RenderTargetIdentifier cameraColorTarget
        {
            get
            {
                if (!(m_IsPipelineExecuting || isCameraColorTargetValid))
                {
                    Debug.LogWarning("You can only call cameraColorTarget inside the scope of a ScriptableRenderPass. Otherwise the pipeline camera target texture might have not been created or might have already been disposed.");
                }
                return m_CameraColorTarget;
            }
        }

        public RenderTargetIdentifier cameraDepthTarget
        {
            get
            {
                if (!m_IsPipelineExecuting)
                {
                    Debug.LogWarning("You can only call cameraDepthTarget inside the scope of a ScriptableRenderPass. Otherwise the pipeline camera target texture might have not been created or might have already been disposed.");
                }
                return m_CameraDepthTarget;
            }
        }

        protected List<ScriptableRendererFeature> rendererFeatures
        {
            get => m_RendererFeatures;
        }

        protected List<ScriptableRenderPass> activeRenderPassQueue
        {
            get => m_ActiveRenderPassQueue;
        }

        public RenderingFeatures supportedRenderingFeatures { get; set; } = new RenderingFeatures();

        public GraphicsDeviceType[] unsupportedGraphicsDeviceTypes { get; set; } = new GraphicsDeviceType[0];
        static class RenderPassBlock
        {
            public static readonly int BeforeRendering = 0;
            public static readonly int MainRenderingOpaque = 1;
            public static readonly int MainRenderingTransparent = 2;
            public static readonly int AfterRendering = 3;
        }
        const int k_RenderPassBlockCount = 4;
        List<ScriptableRenderPass> m_ActiveRenderPassQueue = new List<ScriptableRenderPass>(32);
        List<ScriptableRendererFeature> m_RendererFeatures = new List<ScriptableRendererFeature>(10);
        RenderTargetIdentifier m_CameraColorTarget;
        RenderTargetIdentifier m_CameraDepthTarget;
        bool m_FirstTimeCameraColorTargetIsBound = true; 
        bool m_FirstTimeCameraDepthTargetIsBound = true;
        bool m_IsPipelineExecuting = false;
        internal bool isCameraColorTargetValid = false;
        static RenderTargetIdentifier[] m_ActiveColorAttachments = new RenderTargetIdentifier[]{0, 0, 0, 0, 0, 0, 0, 0 };
        static RenderTargetIdentifier m_ActiveDepthAttachment;
        static RenderTargetIdentifier[][] m_TrimmedColorAttachmentCopies = new RenderTargetIdentifier[][]
        {
            new RenderTargetIdentifier[0],                          // m_TrimmedColorAttachmentCopies[0] is an array of 0 RenderTargetIdentifier - only used to make indexing code easier to read
            new RenderTargetIdentifier[]{0},                        // m_TrimmedColorAttachmentCopies[1] is an array of 1 RenderTargetIdentifier
            new RenderTargetIdentifier[]{0, 0},                     // m_TrimmedColorAttachmentCopies[2] is an array of 2 RenderTargetIdentifiers
            new RenderTargetIdentifier[]{0, 0, 0},                  // m_TrimmedColorAttachmentCopies[3] is an array of 3 RenderTargetIdentifiers
            new RenderTargetIdentifier[]{0, 0, 0, 0},               // m_TrimmedColorAttachmentCopies[4] is an array of 4 RenderTargetIdentifiers
            new RenderTargetIdentifier[]{0, 0, 0, 0, 0},            // m_TrimmedColorAttachmentCopies[5] is an array of 5 RenderTargetIdentifiers
            new RenderTargetIdentifier[]{0, 0, 0, 0, 0, 0},         // m_TrimmedColorAttachmentCopies[6] is an array of 6 RenderTargetIdentifiers
            new RenderTargetIdentifier[]{0, 0, 0, 0, 0, 0, 0},      // m_TrimmedColorAttachmentCopies[7] is an array of 7 RenderTargetIdentifiers
            new RenderTargetIdentifier[]{0, 0, 0, 0, 0, 0, 0, 0 },  // m_TrimmedColorAttachmentCopies[8] is an array of 8 RenderTargetIdentifiers
        };

        /// <summary>
        /// 配置颜色和深度的RenderTargetIdentifier
        /// </summary>
        /// <param name="colorAttachment"></param>
        /// <param name="depthAttachment"></param>
        internal static void ConfigureActiveTarget(RenderTargetIdentifier colorAttachment,
            RenderTargetIdentifier depthAttachment)
        {
            m_ActiveColorAttachments[0] = colorAttachment;
            for (int i = 1; i < m_ActiveColorAttachments.Length; ++i)
                m_ActiveColorAttachments[i] = 0;
            m_ActiveDepthAttachment = depthAttachment;
        }
        /// <summary>
        /// 初始化ScriptableRendererFeature List，并清理数据
        /// </summary>
        /// <param name="data"></param>
        public ScriptableRenderer(ScriptableRendererData data)
        {
            profilingExecute = new ProfilingSampler($"{nameof(ScriptableRenderer)}.{nameof(ScriptableRenderer.Execute)}: {data.name}");
            foreach (var feature in data.rendererFeatures)
            {
                if (feature == null)
                    continue;
                feature.Create();
                m_RendererFeatures.Add(feature);
            }
            Clear(CameraRenderType.Base);
            m_ActiveRenderPassQueue.Clear();
        }
        /// <summary>
        /// Dispose self和ScriptableRendererFeature List
        /// </summary>
        public void Dispose()
        {
            for (int i = 0; i < m_RendererFeatures.Count; ++i)
            {
                if (rendererFeatures[i] == null)
                    continue;
                rendererFeatures[i].Dispose();
            }
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing)
        {
        }
        // Done
        public void ConfigureCameraTarget(RenderTargetIdentifier colorTarget, RenderTargetIdentifier depthTarget)
        {
            m_CameraColorTarget = colorTarget;
            m_CameraDepthTarget = depthTarget;
        }
        internal void ConfigureCameraColorTarget(RenderTargetIdentifier colorTarget)
        {
            m_CameraColorTarget = colorTarget;
        }
        public abstract void Setup(ScriptableRenderContext context, ref RenderingData renderingData);

        public virtual void SetupLights(ScriptableRenderContext context, ref RenderingData renderingData)
        {
        }

        public virtual void SetupCullingParameters(ref ScriptableCullingParameters cullingParameters,
            ref CameraData cameraData)
        {
        }

        public virtual void FinishRendering(CommandBuffer cmd)
        {
        }

        /// <summary>
        /// 执行渲染Pass Done
        /// </summary>
        public void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            m_IsPipelineExecuting = true;
            ref CameraData cameraData = ref renderingData.cameraData;
            Camera camera = cameraData.camera;
            CommandBuffer cmd = CommandBufferPool.Get();
            using (new ProfilingScope(cmd, profilingExecute))
            {
                InternalStartRendering(context, ref renderingData);
#if UNITY_EDITOR
                float time = Application.isPlaying ? Time.time : Time.realtimeSinceStartup;
#else
                float time = Time.time;
#endif
                float deltaTime = Time.deltaTime;
                float smoothDeltaTime = Time.smoothDeltaTime;
                ClearRenderingState(cmd);
                SetPerCameraShaderVariables(cmd, ref cameraData);
                SetShaderTimeValues(cmd, time, deltaTime, smoothDeltaTime);
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();
                using (new ProfilingScope(cmd, Profiling.sortRenderPasses))
                {
                    SortStable(m_ActiveRenderPassQueue);
                }
                using var renderBlocks = new RenderBlocks(m_ActiveRenderPassQueue);
                using (new ProfilingScope(cmd, Profiling.setupLights))
                {
                    SetupLights(context, ref renderingData);
                }
                using (new ProfilingScope(cmd, Profiling.RenderBlock.beforeRendering))
                {
                    ExecuteBlock(RenderPassBlock.BeforeRendering, in renderBlocks, context, ref renderingData);
                }
                using (new ProfilingScope(cmd, Profiling.setupCamera))
                {
                    context.SetupCameraProperties(camera);
                    SetCameraMatrices(cmd, ref cameraData, true);
                    SetShaderTimeValues(cmd, time, deltaTime, smoothDeltaTime);
#if VISUAL_EFFECT_GRAPH_0_0_1_OR_NEWER
            //Triggers dispatch per camera, all global parameters should have been setup at this stage.
            VFX.VFXManager.ProcessCameraCommand(camera, cmd);
#endif
                }
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();
                BeginXRRendering(cmd, context, ref renderingData.cameraData);
                if (renderBlocks.GetLength(RenderPassBlock.MainRenderingOpaque) > 0)
                {
                    using var profScope = new ProfilingScope(cmd, Profiling.RenderBlock.mainRenderingOpaque);
                    ExecuteBlock(RenderPassBlock.MainRenderingOpaque, in renderBlocks, context, ref renderingData);
                }
                if (renderBlocks.GetLength(RenderPassBlock.MainRenderingTransparent) > 0)
                {
                    using var profScope = new ProfilingScope(cmd, Profiling.RenderBlock.mainRenderingTransparent);
                    ExecuteBlock(RenderPassBlock.MainRenderingTransparent, in renderBlocks, context, ref renderingData);
                }
                DrawGizmos(context, camera, GizmoSubset.PreImageEffects);
                if (renderBlocks.GetLength(RenderPassBlock.AfterRendering) > 0)
                {
                    using var profScope = new ProfilingScope(cmd, Profiling.RenderBlock.afterRendering);
                    ExecuteBlock(RenderPassBlock.AfterRendering, in renderBlocks, context, ref renderingData);
                }
                EndXRRendering(cmd, context, ref renderingData.cameraData);
                DrawWireOverlay(context, camera);
                DrawGizmos(context, camera, GizmoSubset.PostImageEffects);
                InternalFinishRendering(context, cameraData.resolveFinalTarget);
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        /// <summary>
        /// 把Pass加入到渲染队列 Done
        /// </summary>
        public void EnqueuePass(ScriptableRenderPass pass)
        {
            m_ActiveRenderPassQueue.Add(pass);
        }

        /// <summary>
        /// 获取Clear Flag
        /// </summary>
        protected static ClearFlag GetCameraClearFlag(ref CameraData cameraData)
        {
            var cameraClearFlags = cameraData.camera.clearFlags;
            if (cameraData.renderType == CameraRenderType.Overlay)
                return (cameraData.clearDepth) ? ClearFlag.Depth : ClearFlag.None;
            if (Application.isMobilePlatform)
                return ClearFlag.All;
            if ((cameraClearFlags == CameraClearFlags.Skybox && RenderSettings.skybox != null) || cameraClearFlags == CameraClearFlags.Nothing)
                return ClearFlag.Depth;
            return ClearFlag.All;
        }

        /// <summary>
        /// 把RenderFeatures中的Pass添加到ScriptRenderder中 Done
        /// </summary>
        protected void AddRenderPasses(ref RenderingData renderingData)
        {
            using var profScope = new ProfilingScope(null, Profiling.addRenderPasses);
            //把RenderFeatures中的Pass添加到ScriptRenderder中
            for (int i = 0; i < rendererFeatures.Count; ++i)
            {
                if (!rendererFeatures[i].isActive)
                {
                    continue;
                }
                rendererFeatures[i].AddRenderPasses(this, ref renderingData);
            }
            int count = activeRenderPassQueue.Count;
            for (int i = count - 1; i >= 0; i--)
            {
                if (activeRenderPassQueue[i] == null)
                    activeRenderPassQueue.RemoveAt(i);
            }
        }

        /// <summary>
        /// Disable ShaderKeywords Done
        /// </summary>
        void ClearRenderingState(CommandBuffer cmd)
        {
            using var profScope = new ProfilingScope(cmd, Profiling.clearRenderingState);
            cmd.DisableShaderKeyword(ShaderKeywordStrings.MainLightShadows);
            cmd.DisableShaderKeyword(ShaderKeywordStrings.MainLightShadowCascades);
            cmd.DisableShaderKeyword(ShaderKeywordStrings.AdditionalLightsVertex);
            cmd.DisableShaderKeyword(ShaderKeywordStrings.AdditionalLightsPixel);
            cmd.DisableShaderKeyword(ShaderKeywordStrings.AdditionalLightShadows);
            cmd.DisableShaderKeyword(ShaderKeywordStrings.SoftShadows);
            cmd.DisableShaderKeyword(ShaderKeywordStrings.MixedLightingSubtractive); // Backward compatibility
            cmd.DisableShaderKeyword(ShaderKeywordStrings.LightmapShadowMixing);
            cmd.DisableShaderKeyword(ShaderKeywordStrings.ShadowsShadowMask);
            cmd.DisableShaderKeyword(ShaderKeywordStrings.LinearToSRGBConversion);
        }
        
        /// <summary>
        /// Clear Done
        /// </summary>
        internal void Clear(CameraRenderType cameraType)
        {
            m_ActiveColorAttachments[0] = BuiltinRenderTextureType.CameraTarget;
            for (int i = 1; i < m_ActiveColorAttachments.Length; ++i)
                m_ActiveColorAttachments[i] = 0;
            m_ActiveDepthAttachment = BuiltinRenderTextureType.CameraTarget;
            m_FirstTimeCameraColorTargetIsBound = cameraType == CameraRenderType.Base;
            m_FirstTimeCameraDepthTargetIsBound = true;
            m_CameraColorTarget = BuiltinRenderTextureType.CameraTarget;
            m_CameraDepthTarget = BuiltinRenderTextureType.CameraTarget;
        }
        /// <summary>
        /// 按块执行，每个块内有几个索引
        /// </summary>
        void ExecuteBlock(int blockIndex, in RenderBlocks renderBlocks,
            ScriptableRenderContext context, ref RenderingData renderingData, bool submit = false)
        {
            foreach (int currIndex in renderBlocks.GetRange(blockIndex))
            {
                var renderPass = m_ActiveRenderPassQueue[currIndex];
                ExecuteRenderPass(context, renderPass, ref renderingData);
            }
            if (submit)
                context.Submit();
        }
        /// <summary>
        /// 执行渲染Pass
        /// </summary>
        void ExecuteRenderPass(ScriptableRenderContext context, ScriptableRenderPass renderPass, ref RenderingData renderingData)
        {
            using var profScope = new ProfilingScope(null, renderPass.profilingSampler);

            ref CameraData cameraData = ref renderingData.cameraData;

            CommandBuffer cmd = CommandBufferPool.Get();
            using (new ProfilingScope(cmd, Profiling.RenderPass.configure))
            {
                renderPass.Configure(cmd, cameraData.cameraTargetDescriptor);
                SetRenderPassAttachments(cmd, renderPass, ref cameraData);
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
            renderPass.Execute(context, ref renderingData);
        }
        // Done
        void SetRenderPassAttachments(CommandBuffer cmd, ScriptableRenderPass renderPass, ref CameraData cameraData)
        {
            Camera camera = cameraData.camera;
            ClearFlag cameraClearFlag = GetCameraClearFlag(ref cameraData);
            uint validColorBuffersCount = RenderingUtils.GetValidColorBufferCount(renderPass.colorAttachments);
            if (validColorBuffersCount == 0)
                return;
            if (RenderingUtils.IsMRT(renderPass.colorAttachments))
            {
                bool needCustomCameraColorClear = false;
                bool needCustomCameraDepthClear = false;
                int cameraColorTargetIndex = RenderingUtils.IndexOf(renderPass.colorAttachments, m_CameraColorTarget);
                if (cameraColorTargetIndex != -1 && (m_FirstTimeCameraColorTargetIsBound))
                {
                    m_FirstTimeCameraColorTargetIsBound = false; 
                    needCustomCameraColorClear = (cameraClearFlag & ClearFlag.Color) != (renderPass.clearFlag & ClearFlag.Color)
                        || CoreUtils.ConvertSRGBToActiveColorSpace(camera.backgroundColor) != renderPass.clearColor;
                }
                if (renderPass.depthAttachment == m_CameraDepthTarget && m_FirstTimeCameraDepthTargetIsBound)
                {
                    m_FirstTimeCameraDepthTargetIsBound = false;
                    needCustomCameraDepthClear = (cameraClearFlag & ClearFlag.Depth) != (renderPass.clearFlag & ClearFlag.Depth);
                }
                if (needCustomCameraColorClear)
                {
                    if ((cameraClearFlag & ClearFlag.Color) != 0)
                        SetRenderTarget(cmd, renderPass.colorAttachments[cameraColorTargetIndex], renderPass.depthAttachment, ClearFlag.Color, CoreUtils.ConvertSRGBToActiveColorSpace(camera.backgroundColor));
                    if ((renderPass.clearFlag & ClearFlag.Color) != 0)
                    {
                        uint otherTargetsCount = RenderingUtils.CountDistinct(renderPass.colorAttachments, m_CameraColorTarget);
                        var nonCameraAttachments = m_TrimmedColorAttachmentCopies[otherTargetsCount];
                        int writeIndex = 0;
                        for (int readIndex = 0; readIndex < renderPass.colorAttachments.Length; ++readIndex)
                        {
                            if (renderPass.colorAttachments[readIndex] != m_CameraColorTarget && renderPass.colorAttachments[readIndex] != 0)
                            {
                                nonCameraAttachments[writeIndex] = renderPass.colorAttachments[readIndex];
                                ++writeIndex;
                            }
                        }
                        if (writeIndex != otherTargetsCount)
                            Debug.LogError("writeIndex and otherTargetsCount values differed. writeIndex:" + writeIndex + " otherTargetsCount:" + otherTargetsCount);
                        SetRenderTarget(cmd, nonCameraAttachments, m_CameraDepthTarget, ClearFlag.Color, renderPass.clearColor);
                    }
                }
                ClearFlag finalClearFlag = ClearFlag.None;
                finalClearFlag |= needCustomCameraDepthClear ? (cameraClearFlag & ClearFlag.Depth) : (renderPass.clearFlag & ClearFlag.Depth);
                finalClearFlag |= needCustomCameraColorClear ? 0 : (renderPass.clearFlag & ClearFlag.Color);
                if (!RenderingUtils.SequenceEqual(renderPass.colorAttachments, m_ActiveColorAttachments) || renderPass.depthAttachment != m_ActiveDepthAttachment || finalClearFlag != ClearFlag.None)
                {
                    int lastValidRTindex = RenderingUtils.LastValid(renderPass.colorAttachments);
                    if (lastValidRTindex >= 0)
                    {
                        int rtCount = lastValidRTindex + 1;
                        var trimmedAttachments = m_TrimmedColorAttachmentCopies[rtCount];
                        for (int i = 0; i < rtCount; ++i)
                            trimmedAttachments[i] = renderPass.colorAttachments[i];
                        SetRenderTarget(cmd, trimmedAttachments, renderPass.depthAttachment, finalClearFlag, renderPass.clearColor);
                    #if ENABLE_VR && ENABLE_XR_MODULE
                        if (cameraData.xr.enabled)
                        {
                            int xrTargetIndex = RenderingUtils.IndexOf(renderPass.colorAttachments, cameraData.xr.renderTarget);
                            bool isRenderToBackBufferTarget = (xrTargetIndex != -1) && !cameraData.xr.renderTargetIsRenderTexture;
                            cameraData.xr.UpdateGPUViewAndProjectionMatrices(cmd, ref cameraData, !isRenderToBackBufferTarget);
                        }
                    #endif
                    }
                }
            }
            else
            {
                RenderTargetIdentifier passColorAttachment = renderPass.colorAttachment;
                RenderTargetIdentifier passDepthAttachment = renderPass.depthAttachment;
                if (!renderPass.overrideCameraTarget)
                {
                    if (renderPass.renderPassEvent < RenderPassEvent.BeforeRenderingOpaques)
                        return;
                    passColorAttachment = m_CameraColorTarget;
                    passDepthAttachment = m_CameraDepthTarget;
                }
                ClearFlag finalClearFlag = ClearFlag.None;
                Color finalClearColor;
                if (passColorAttachment == m_CameraColorTarget && (m_FirstTimeCameraColorTargetIsBound))
                {
                    m_FirstTimeCameraColorTargetIsBound = false; 
                    finalClearFlag |= (cameraClearFlag & ClearFlag.Color);
                    finalClearColor = CoreUtils.ConvertSRGBToActiveColorSpace(camera.backgroundColor);
                    if (m_FirstTimeCameraDepthTargetIsBound)
                    {
                        m_FirstTimeCameraDepthTargetIsBound = false;
                        finalClearFlag |= (cameraClearFlag & ClearFlag.Depth);
                    }
                }
                else
                {
                    finalClearFlag |= (renderPass.clearFlag & ClearFlag.Color);
                    finalClearColor = renderPass.clearColor;
                }
                if ((m_CameraDepthTarget != BuiltinRenderTextureType.CameraTarget) && (passDepthAttachment == m_CameraDepthTarget || passColorAttachment == m_CameraDepthTarget) && m_FirstTimeCameraDepthTargetIsBound)
                {
                    m_FirstTimeCameraDepthTargetIsBound = false;
                    finalClearFlag |= (cameraClearFlag & ClearFlag.Depth);
                }
                else
                    finalClearFlag |= (renderPass.clearFlag & ClearFlag.Depth);
                if (passColorAttachment != m_ActiveColorAttachments[0] || passDepthAttachment != m_ActiveDepthAttachment || finalClearFlag != ClearFlag.None)
                {
                    SetRenderTarget(cmd, passColorAttachment, passDepthAttachment, finalClearFlag, finalClearColor);
#if ENABLE_VR && ENABLE_XR_MODULE
                    if (cameraData.xr.enabled)
                    {
                        // SetRenderTarget might alter the internal device state(winding order).
                        // Non-stereo buffer is already updated internally when switching render target. We update stereo buffers here to keep the consistency.
                        bool isRenderToBackBufferTarget = (passColorAttachment == cameraData.xr.renderTarget) && !cameraData.xr.renderTargetIsRenderTexture;
                        cameraData.xr.UpdateGPUViewAndProjectionMatrices(cmd, ref cameraData, !isRenderToBackBufferTarget);
                    }
#endif
                }
            }
        }

        void BeginXRRendering(CommandBuffer cmd, ScriptableRenderContext context, ref CameraData cameraData)
        {
#if ENABLE_VR && ENABLE_XR_MODULE
            if (cameraData.xr.enabled)
            {
                cameraData.xr.StartSinglePass(cmd);
                cmd.EnableShaderKeyword(ShaderKeywordStrings.UseDrawProcedural);
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();
            }
#endif
        }

        void EndXRRendering(CommandBuffer cmd, ScriptableRenderContext context, ref CameraData cameraData)
        {
#if ENABLE_VR && ENABLE_XR_MODULE
            if (cameraData.xr.enabled)
            {
                cameraData.xr.StopSinglePass(cmd);
                cmd.DisableShaderKeyword(ShaderKeywordStrings.UseDrawProcedural);
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();
            }
#endif
        }
        /// <summary>
        /// 设置渲染目标
        /// </summary>
        internal static void SetRenderTarget(CommandBuffer cmd, RenderTargetIdentifier colorAttachment, RenderTargetIdentifier depthAttachment, ClearFlag clearFlag, Color clearColor)
        {
            m_ActiveColorAttachments[0] = colorAttachment;
            for (int i = 1; i < m_ActiveColorAttachments.Length; ++i)
                m_ActiveColorAttachments[i] = 0;
            m_ActiveDepthAttachment = depthAttachment;
            RenderBufferLoadAction colorLoadAction = ((uint)clearFlag & (uint)ClearFlag.Color) != 0 ? RenderBufferLoadAction.DontCare : RenderBufferLoadAction.Load;
            RenderBufferLoadAction depthLoadAction = ((uint)clearFlag & (uint)ClearFlag.Depth) != 0 ? RenderBufferLoadAction.DontCare : RenderBufferLoadAction.Load;
            SetRenderTarget(cmd, colorAttachment, colorLoadAction, RenderBufferStoreAction.Store, depthAttachment, depthLoadAction, RenderBufferStoreAction.Store, clearFlag, clearColor);
        }

        
        static void SetRenderTarget( CommandBuffer cmd, RenderTargetIdentifier colorAttachment,
            RenderBufferLoadAction colorLoadAction, RenderBufferStoreAction colorStoreAction, ClearFlag clearFlags, Color clearColor)
        {
            CoreUtils.SetRenderTarget(cmd, colorAttachment, colorLoadAction, colorStoreAction, clearFlags, clearColor);
        }
        static void SetRenderTarget(CommandBuffer cmd, RenderTargetIdentifier colorAttachment, RenderBufferLoadAction colorLoadAction, RenderBufferStoreAction colorStoreAction,
            RenderTargetIdentifier depthAttachment, RenderBufferLoadAction depthLoadAction, RenderBufferStoreAction depthStoreAction, ClearFlag clearFlags, Color clearColor){
            if (depthAttachment == BuiltinRenderTextureType.CameraTarget)
            {
                SetRenderTarget(cmd, colorAttachment, colorLoadAction, colorStoreAction, clearFlags, clearColor);
            }
            else
            {
                CoreUtils.SetRenderTarget(cmd, colorAttachment, colorLoadAction, colorStoreAction,
                        depthAttachment, depthLoadAction, depthStoreAction, clearFlags, clearColor);
            }
        }

        static void SetRenderTarget(CommandBuffer cmd, RenderTargetIdentifier[] colorAttachments, RenderTargetIdentifier depthAttachment, ClearFlag clearFlag, Color clearColor)
        {
            m_ActiveColorAttachments = colorAttachments;
            m_ActiveDepthAttachment = depthAttachment;
            CoreUtils.SetRenderTarget(cmd, colorAttachments, depthAttachment, clearFlag, clearColor);
        }

        [Conditional("UNITY_EDITOR")]
        void DrawGizmos(ScriptableRenderContext context, Camera camera, GizmoSubset gizmoSubset)
        {
#if UNITY_EDITOR
            if (UnityEditor.Handles.ShouldRenderGizmos())
                context.DrawGizmos(camera, gizmoSubset);
#endif
        }

        [Conditional("UNITY_EDITOR")]
        void DrawWireOverlay(ScriptableRenderContext context, Camera camera)
        {
            context.DrawWireOverlay(camera);
        }

        /// <summary>
        /// 调用List<ScriptableRenderPass>的OnCameraSetup Done
        /// </summary>
        void InternalStartRendering(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            CommandBuffer cmd = CommandBufferPool.Get();
            using (new ProfilingScope(cmd, Profiling.internalStartRendering))
            {
                for (int i = 0; i < m_ActiveRenderPassQueue.Count; ++i)
                {
                    m_ActiveRenderPassQueue[i].OnCameraSetup(cmd, ref renderingData);
                }
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
        /// <summary>
        /// 调用List<ScriptableRenderPass>的FrameCleanup和OnFinishCameraStackRendering
        /// </summary>
        /// <param name="context"></param>
        /// <param name="resolveFinalTarget"></param>
        void InternalFinishRendering(ScriptableRenderContext context, bool resolveFinalTarget)
        {
            CommandBuffer cmd = CommandBufferPool.Get();
            using (new ProfilingScope(cmd, Profiling.internalFinishRendering))
            {
                for (int i = 0; i < m_ActiveRenderPassQueue.Count; ++i)
                    m_ActiveRenderPassQueue[i].FrameCleanup(cmd);
                if (resolveFinalTarget)
                {
                    for (int i = 0; i < m_ActiveRenderPassQueue.Count; ++i)
                        m_ActiveRenderPassQueue[i].OnFinishCameraStackRendering(cmd);
                    FinishRendering(cmd);
                    m_IsPipelineExecuting = false;
                }
                m_ActiveRenderPassQueue.Clear();
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
        // 根据RenderPassEvent排序 Done
        internal static void SortStable(List<ScriptableRenderPass> list)
        {
            int j;
            for (int i = 1; i < list.Count; ++i)
            {
                ScriptableRenderPass curr = list[i];
                j = i - 1;
                for (; j >= 0 && curr < list[j]; --j)
                    list[j + 1] = list[j];
                list[j + 1] = curr;
            }
        }

        internal struct RenderBlocks : IDisposable
        {
            private NativeArray<RenderPassEvent> m_BlockEventLimits;//长度为4的枚举
            private NativeArray<int> m_BlockRanges; //存储ScriptableRenderPass的索引范围 长度为5 存储Pass范围索引，最后一个存总Pass数
            private NativeArray<int> m_BlockRangeLengths;//每个范围内的Pass数量 长度为5
            /// <summary>
            /// 构建块的Pass范围信息
            /// </summary>
            /// <param name="activeRenderPassQueue"></param>
            public RenderBlocks(List<ScriptableRenderPass> activeRenderPassQueue)
            {
                m_BlockEventLimits = new NativeArray<RenderPassEvent>(k_RenderPassBlockCount, Allocator.Temp);// length = 4
                m_BlockRanges = new NativeArray<int>(m_BlockEventLimits.Length + 1, Allocator.Temp);// length = 5
                m_BlockRangeLengths = new NativeArray<int>(m_BlockRanges.Length, Allocator.Temp);// length = 5
                //BeforeRendering -- BeforeRenderingShadows -- AfterRenderingShadows
                m_BlockEventLimits[RenderPassBlock.BeforeRendering] = RenderPassEvent.BeforeRenderingPrepasses;
                //BeforeRenderingPrepasses -- AfterRenderingPrePasses -- BeforeRenderingOpaques
                m_BlockEventLimits[RenderPassBlock.MainRenderingOpaque] = RenderPassEvent.AfterRenderingOpaques;
                //BeforeRenderingSkybox -- AfterRenderingSkybox -- BeforeRenderingTransparents -- AfterRenderingTransparents -- BeforeRenderingPostProcessing 
                m_BlockEventLimits[RenderPassBlock.MainRenderingTransparent] = RenderPassEvent.AfterRenderingPostProcessing;
                //AfterRenderingPostProcessing -- AfterRendering
                m_BlockEventLimits[RenderPassBlock.AfterRendering] = (RenderPassEvent) Int32.MaxValue;
                FillBlockRanges(activeRenderPassQueue);
                m_BlockEventLimits.Dispose();
                for (int i = 0; i < m_BlockRanges.Length - 1; i++)
                {
                    m_BlockRangeLengths[i] = m_BlockRanges[i + 1] - m_BlockRanges[i];
                }
            }

            public void Dispose()
            {
                m_BlockRangeLengths.Dispose();
                m_BlockRanges.Dispose();
            }

            /// <summary>
            /// 使用List<ScriptableRenderPass>填充区块的Pass范围信息
            /// </summary>
            /// <param name="activeRenderPassQueue"></param>
            void FillBlockRanges(List<ScriptableRenderPass> activeRenderPassQueue)
            {
                int currRangeIndex = 0;
                int currRenderPass = 0;
                m_BlockRanges[currRangeIndex++] = 0;
                for (int i = 0; i < m_BlockEventLimits.Length - 1; ++i)
                {
                    while (currRenderPass < activeRenderPassQueue.Count && activeRenderPassQueue[currRenderPass].renderPassEvent < m_BlockEventLimits[i])
                        currRenderPass++;
                    m_BlockRanges[currRangeIndex++] = currRenderPass;
                }
                m_BlockRanges[currRangeIndex] = activeRenderPassQueue.Count;
            }

            public int GetLength(int index)
            {
                return m_BlockRangeLengths[index];
            }

            // Minimal foreach support
            public struct BlockRange : IDisposable
            {
                int m_Current;
                int m_End;
                public BlockRange(int begin, int end)
                {
                    Assertions.Assert.IsTrue(begin <= end);
                    m_Current = begin < end ? begin : end;
                    m_End   = end >= begin ? end : begin;
                    m_Current -= 1;
                }
                public BlockRange GetEnumerator() { return this; }
                public bool MoveNext() { return ++m_Current < m_End; }
                public int Current { get => m_Current; }
                public void Dispose() {}
            }

            public BlockRange GetRange(int index)
            {
                return new BlockRange(m_BlockRanges[index], m_BlockRanges[index + 1]);
            }
        }
    }
}
