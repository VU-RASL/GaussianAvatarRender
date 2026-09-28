// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.XR;
using System.IO;

namespace GaussianSplatting.Runtime
{
    
    class GaussianSplatRenderSystem
    {
        // ReSharper disable MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        internal static readonly ProfilerMarker s_ProfDraw = new(ProfilerCategory.Render, "GaussianSplat.Draw", MarkerFlags.SampleGPU);
        internal static readonly ProfilerMarker s_ProfDepthMask = new(ProfilerCategory.Render, "GaussianSplat.DepthMask", MarkerFlags.SampleGPU);
        internal static readonly ProfilerMarker s_ProfDepthProxy = new(ProfilerCategory.Render, "GaussianSplat.DepthProxy", MarkerFlags.SampleGPU);
        internal static readonly ProfilerMarker s_ProfCompose = new(ProfilerCategory.Render, "GaussianSplat.Compose", MarkerFlags.SampleGPU);
        internal static readonly ProfilerMarker s_ProfCalcView = new(ProfilerCategory.Render, "GaussianSplat.CalcView", MarkerFlags.SampleGPU);
        // ReSharper restore MemberCanBePrivate.Global

        const int kDepthProxyPass = 3;
        const int kQuestPremultipliedCompositePass = 2;
        const string kEnvironmentVisibilityCapKeyword = "GSAC_ENV_VISIBILITY_CAP";
        static bool s_VisibilitySupportChecked;
        static bool s_VisibilityCapSupported;
        internal static bool EnvironmentVisibilityCapActiveLastFrame { get; private set; }
        internal static bool EnvironmentVisibilityCapSupported
        {
            get
            {
                if (!s_VisibilitySupportChecked)
                {
                    s_VisibilityCapSupported = SystemInfo.supportedRenderTargetCount >= 2 &&
                        SystemInfo.supportsSeparatedRenderTargetsBlend && SystemInfo.supports2DArrayTextures &&
                        SystemInfo.IsFormatSupported(GraphicsFormat.R8_UNorm, FormatUsage.Render) &&
                        SystemInfo.IsFormatSupported(GraphicsFormat.R8_UNorm, FormatUsage.Sample) &&
                        SystemInfo.IsFormatSupported(GraphicsFormat.R8_UNorm, FormatUsage.Blend);
                    s_VisibilitySupportChecked = true;
                }
                return s_VisibilityCapSupported;
            }
        }
        // Used by the raw splat depth path on non-post-opaque cameras. Quest AR uses
        // post-opaque splat compositing instead so Gaussian edges blend over virtual objects.
        const float kDepthMaskVisibilityEpsilon = 1.0f / 255.0f;
        const float kRawDepthMaskLocalAlphaEpsilon = 0.00001f;
        const float kRawDepthMaskCoverageThreshold = kDepthMaskVisibilityEpsilon;

        public static GaussianSplatRenderSystem instance => ms_Instance ??= new GaussianSplatRenderSystem();
        static GaussianSplatRenderSystem ms_Instance;

        readonly Dictionary<GaussianSplatRenderer, MaterialPropertyBlock> m_Splats = new();
        readonly HashSet<Camera> m_CameraCommandBuffersDone = new();
        readonly List<(GaussianSplatRenderer, MaterialPropertyBlock)> m_ActiveSplats = new();

        CommandBuffer m_CommandBuffer;
        CommandBuffer m_DepthMaskCommandBuffer;
        readonly RenderTargetIdentifier[] m_VisibilityColorTargets = new RenderTargetIdentifier[2];
        bool m_ReportedVisibilitySupport;

        public void RegisterSplat(GaussianSplatRenderer r)
        {
            if (m_Splats.Count == 0)
            {
                if (GraphicsSettings.currentRenderPipeline == null)
                    Camera.onPreCull += OnPreCullCamera;
            }

            m_Splats.Add(r, new MaterialPropertyBlock());
        }

        public void UnregisterSplat(GaussianSplatRenderer r)
        {
            if (!m_Splats.ContainsKey(r))
                return;
            m_Splats.Remove(r);
            if (m_Splats.Count == 0)
            {
                if (m_CameraCommandBuffersDone != null)
                {
                    if (m_CommandBuffer != null)
                    {
                        foreach (var cam in m_CameraCommandBuffersDone)
                        {
                            if (cam)
                            {
                                if (m_DepthMaskCommandBuffer != null)
                                {
                                    cam.RemoveCommandBuffer(CameraEvent.BeforeForwardOpaque, m_DepthMaskCommandBuffer);
                                }
                                cam.RemoveCommandBuffer(CameraEvent.BeforeForwardOpaque, m_CommandBuffer);
                                cam.RemoveCommandBuffer(CameraEvent.AfterForwardOpaque, m_CommandBuffer);
                            }
                        }
                    }
                    m_CameraCommandBuffersDone.Clear();
                }

                m_ActiveSplats.Clear();
                m_CommandBuffer?.Dispose();
                m_CommandBuffer = null;
                m_DepthMaskCommandBuffer?.Dispose();
                m_DepthMaskCommandBuffer = null;
                Camera.onPreCull -= OnPreCullCamera;
                EnvironmentVisibilityCapActiveLastFrame = false;
            }
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        public bool GatherSplatsForCamera(Camera cam)
        {
            if (cam.cameraType == CameraType.Preview)
                return false;
            // gather all active & valid splat objects
            m_ActiveSplats.Clear();
            foreach (var kvp in m_Splats)
            {
                var gs = kvp.Key;
                // Debug.Log(gs);
                if (gs == null || !gs.isActiveAndEnabled || !gs.HasValidAsset || !gs.HasValidRenderSetup)
                    continue;
                m_ActiveSplats.Add((kvp.Key, kvp.Value));
            }
            if (m_ActiveSplats.Count == 0)
                return false;

            // sort them by depth from camera
            var camTr = cam.transform;
            m_ActiveSplats.Sort((a, b) =>
            {
                var trA = a.Item1.transform;
                var trB = b.Item1.transform;
                var posA = camTr.InverseTransformPoint(trA.position);
                var posB = camTr.InverseTransformPoint(trB.position);
                return posA.z.CompareTo(posB.z);
            });

            return true;
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        public Material SortAndRenderSplats(Camera cam, CommandBuffer cmb, CompareFunction sceneZTest = CompareFunction.LessEqual)
        {

            Material matComposite = null;
            bool useVertexViewDataFallback = ShouldUseVertexViewDataFallback(cam);
            foreach (var kvp in m_ActiveSplats)
            {
                var gs = kvp.Item1;
                matComposite = gs.m_MatComposite;
                var mpb = kvp.Item2;

                // sort
                var matrix = gs.transform.localToWorldMatrix;
                int sortInterval = GetEffectiveSortInterval(cam, gs);
                bool sortThisFrame = ShouldSortThisFrame(cam, gs, sortInterval);
                if (useVertexViewDataFallback)
                {
                    if (sortThisFrame)
                        gs.SortPointsCpu(cam, matrix);
                }
                else if (sortThisFrame)
                    gs.SortPoints(cmb, cam, matrix);
                ++gs.m_FrameCounter;

                // cache view
                kvp.Item2.Clear();
                Material displayMat = gs.m_RenderMode switch
                {
                    GaussianSplatRenderer.RenderMode.DebugPoints => gs.m_MatDebugPoints,
                    GaussianSplatRenderer.RenderMode.DebugPointIndices => gs.m_MatDebugPoints,
                    GaussianSplatRenderer.RenderMode.DebugBoxes => gs.m_MatDebugBoxes,
                    GaussianSplatRenderer.RenderMode.DebugChunkBounds => gs.m_MatDebugBoxes,
                    _ => gs.m_MatSplats
                };
                if (displayMat == null)
                    continue;
                bool useVertexViewData = useVertexViewDataFallback &&
                                         gs.m_RenderMode == GaussianSplatRenderer.RenderMode.Splats &&
                                         displayMat.passCount > 1;
                displayMat.SetInt(GaussianSplatRenderer.Props.GaussianSceneZTest, (int)sceneZTest);
                // mpb.SetBuffer("_RECORD",gs.m_recordBuffer);
                
                // float3[] gs_xyz = new float3[13541];
                //     gs.m_GpuPosData.GetData(gs_xyz);
                //     Debug.Log("GPU");
                //     Debug.Log(gs_xyz[0]);
                
                
                gs.m_TBuffer.SetData(gs.T);
 
                // Debug.Log(gs.T[0]);
                mpb.SetBuffer("_TBuffer",gs.m_TBuffer); 


                gs.SetAssetDataOnMaterial(mpb);
                mpb.SetBuffer(GaussianSplatRenderer.Props.SplatChunks, gs.m_GpuChunks);

                mpb.SetBuffer(GaussianSplatRenderer.Props.SplatViewData, gs.m_GpuView);

                mpb.SetBuffer(GaussianSplatRenderer.Props.OrderBuffer, gs.m_GpuSortKeys);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatScale, gs.m_SplatScale);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatOpacityScale, gs.m_OpacityScale);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatSize, gs.m_PointDisplaySize);
                mpb.SetFloat(GaussianSplatRenderer.Props.GaussianSplatClipFlipY, ShouldFlipSplatClipY(cam) ? 1.0f : 0.0f);
                mpb.SetInteger(GaussianSplatRenderer.Props.SHOrder, gs.m_SHOrder);
                mpb.SetInteger(GaussianSplatRenderer.Props.SHOnly, gs.m_SHOnly ? 1 : 0);
                mpb.SetInteger(GaussianSplatRenderer.Props.DCOnly, gs.m_DCOnly ? 1 : 0);

                mpb.SetInteger(GaussianSplatRenderer.Props.DisplayIndex, gs.m_RenderMode == GaussianSplatRenderer.RenderMode.DebugPointIndices ? 1 : 0);
                mpb.SetInteger(GaussianSplatRenderer.Props.DisplayChunks, gs.m_RenderMode == GaussianSplatRenderer.RenderMode.DebugChunkBounds ? 1 : 0);

                if (!useVertexViewData)
                {
                    if (Shader.IsKeywordEnabled("HARD_OCCLUSION") || Shader.IsKeywordEnabled("SOFT_OCCLUSION"))
                    {
                        // Pass 0 uses clip positions generated by CalcViewData,
                        // not the current GPU camera matrices used by pass 1.
                        Matrix4x4 viewProjection = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true) * cam.worldToCameraMatrix;
                        mpb.SetMatrix(GaussianSplatRenderer.Props.EnvironmentInverseViewProjection, viewProjection.inverse);
                    }
                    cmb.BeginSample(s_ProfCalcView);
                    gs.CalcViewData(cmb, cam, matrix);
                    cmb.EndSample(s_ProfCalcView);
                }

                // draw
                int indexCount = 6;
                int instanceCount = gs.splatCount;
                MeshTopology topology = MeshTopology.Triangles;
                if (gs.m_RenderMode is GaussianSplatRenderer.RenderMode.DebugBoxes or GaussianSplatRenderer.RenderMode.DebugChunkBounds)
                    indexCount = 36;
                if (gs.m_RenderMode == GaussianSplatRenderer.RenderMode.DebugChunkBounds)
                    instanceCount = gs.m_GpuChunksValid ? gs.m_GpuChunks.count : 0;

                cmb.BeginSample(s_ProfDraw);
                cmb.DrawProcedural(gs.m_GpuIndexBuffer, matrix, displayMat, useVertexViewData ? 1 : 0, topology, indexCount, instanceCount, mpb);



                cmb.EndSample(s_ProfDraw);
            }
            return matComposite;
        }

        void RenderDepthMasks(Camera cam, CommandBuffer cmb, bool useRawSplatData)
        {
            foreach (var kvp in m_ActiveSplats)
            {
                var gs = kvp.Item1;
                if (!gs.m_OccludeSceneObjects || gs.m_MatDepthMask == null)
                    continue;

                bool drewDepthProxy = !useRawSplatData && RenderSkinnedMeshDepthProxy(cam, cmb, gs);
                if (drewDepthProxy && !useRawSplatData)
                    continue;

                var matrix = gs.transform.localToWorldMatrix;
                var mpb = kvp.Item2;
                mpb.Clear();

                gs.SetAssetDataOnMaterial(mpb);
                mpb.SetBuffer(GaussianSplatRenderer.Props.SplatViewData, gs.m_GpuView);
                mpb.SetBuffer(GaussianSplatRenderer.Props.OrderBuffer, gs.m_GpuSortKeys);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatScale, gs.m_SplatScale);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatOpacityScale, gs.m_OpacityScale);
                Matrix4x4 matView = cam.worldToCameraMatrix;
                Matrix4x4 matProj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);
                mpb.SetMatrix(GaussianSplatRenderer.Props.MatrixVP, matProj * matView);
                mpb.SetMatrix(GaussianSplatRenderer.Props.MatrixMV, matView * matrix);
                mpb.SetMatrix(GaussianSplatRenderer.Props.MatrixP, matProj);
                float alphaThreshold = useRawSplatData ? kRawDepthMaskCoverageThreshold : gs.m_OcclusionAlphaThreshold < 0.02f ? 0.12f : gs.m_OcclusionAlphaThreshold;
                float localAlphaEpsilon = useRawSplatData ? kRawDepthMaskLocalAlphaEpsilon : kDepthMaskVisibilityEpsilon;
                float edgeShrinkPixels = useRawSplatData ? 0.0f : gs.m_OcclusionEdgeShrinkPixels;
                mpb.SetFloat(GaussianSplatRenderer.Props.DepthMaskAlphaThreshold, alphaThreshold);
                mpb.SetFloat(GaussianSplatRenderer.Props.DepthMaskLocalAlphaEpsilon, localAlphaEpsilon);
                mpb.SetFloat(GaussianSplatRenderer.Props.DepthMaskEdgeShrinkPixels, edgeShrinkPixels);
                mpb.SetFloat(GaussianSplatRenderer.Props.DepthMaskUseRawSplatData, useRawSplatData ? 1.0f : 0.0f);
                mpb.SetFloat(GaussianSplatRenderer.Props.DepthMaskForceNearDepth, useRawSplatData && ShouldForceNearQuestDepthMask(cam) && gs.m_QuestDepthMaskForceNearDepth ? 1.0f : 0.0f);
                mpb.SetFloat(GaussianSplatRenderer.Props.GaussianSplatClipFlipY, ShouldFlipDepthMaskClipY(cam) ? 1.0f : 0.0f);

                if (useRawSplatData)
                {
                    gs.SortPointsCpu(cam, matrix);
                }
                else
                {
                    cmb.BeginSample(s_ProfCalcView);
                    gs.CalcViewData(cmb, cam, matrix);
                    cmb.EndSample(s_ProfCalcView);
                }

                cmb.BeginSample(s_ProfDepthMask);
                cmb.GetTemporaryRT(GaussianSplatRenderer.Props.GaussianDepthCoverageRT, -1, -1, 0, FilterMode.Bilinear, GraphicsFormat.R16G16B16A16_SFloat);
                cmb.SetRenderTarget(GaussianSplatRenderer.Props.GaussianDepthCoverageRT);
                cmb.ClearRenderTarget(RTClearFlags.Color, new Color(0, 0, 0, 0), 0, 0);
                cmb.DrawProcedural(gs.m_GpuIndexBuffer, matrix, gs.m_MatDepthMask, 0, MeshTopology.Triangles, 6, gs.splatCount, mpb);

                cmb.SetGlobalTexture(GaussianSplatRenderer.Props.GaussianDepthCoverageRT, new RenderTargetIdentifier(GaussianSplatRenderer.Props.GaussianDepthCoverageRT));
                SetCameraDepthWriteTarget(cmb);
                cmb.DrawProcedural(gs.m_GpuIndexBuffer, matrix, gs.m_MatDepthMask, 1, MeshTopology.Triangles, 6, gs.splatCount, mpb);
                cmb.ReleaseTemporaryRT(GaussianSplatRenderer.Props.GaussianDepthCoverageRT);
                cmb.EndSample(s_ProfDepthMask);
            }
        }

        bool RenderSkinnedMeshDepthProxy(Camera cam, CommandBuffer cmb, GaussianSplatRenderer gs)
        {
            if (!ShouldUseSkinnedMeshDepthProxy(cam, gs) || gs.m_MatDepthMask.passCount <= kDepthProxyPass)
                return false;

            var proxyRenderers = gs.GetDepthProxyRenderers();
            if (proxyRenderers == null || proxyRenderers.Length == 0)
                return false;

            bool drewAny = false;
            SetCameraDepthWriteTarget(cmb);
            cmb.BeginSample(s_ProfDepthProxy);

            foreach (var proxy in proxyRenderers)
            {
                if (proxy == null || proxy.sharedMesh == null || !proxy.gameObject.activeInHierarchy)
                    continue;

                Mesh proxyMesh = gs.GetBakedDepthProxyMesh(proxy);
                if (proxyMesh == null)
                    continue;

                Matrix4x4 matrix = proxy.localToWorldMatrix;
                if (gs.m_AlignDepthProxyToGaussianOnAndroid)
                    matrix = Matrix4x4.Translate(gs.transform.position - proxy.transform.position) * matrix;

                float proxyScale = gs.m_DepthProxyMeshScaleOnAndroid > 0.0f ? gs.m_DepthProxyMeshScaleOnAndroid : 1.0f;
                if (!Mathf.Approximately(proxyScale, 1.0f))
                {
                    Vector3 pivot = matrix.MultiplyPoint(proxyMesh.bounds.center);
                    matrix = Matrix4x4.Translate(pivot) *
                             Matrix4x4.Scale(Vector3.one * proxyScale) *
                             Matrix4x4.Translate(-pivot) *
                             matrix;
                }

                int subMeshCount = Math.Max(1, proxyMesh.subMeshCount);
                for (int subMesh = 0; subMesh < subMeshCount; ++subMesh)
                    cmb.DrawMesh(proxyMesh, matrix, gs.m_MatDepthMask, subMesh, kDepthProxyPass);
                drewAny = true;
            }

            cmb.EndSample(s_ProfDepthProxy);
            return drewAny;
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        // ReSharper disable once UnusedMethodReturnValue.Global - used by HDRP/URP features that are not always compiled
        public CommandBuffer InitialClearCmdBuffer(Camera cam)
        {
            m_CommandBuffer ??= new CommandBuffer {name = "RenderGaussianSplats"};
            m_DepthMaskCommandBuffer ??= new CommandBuffer { name = "RenderGaussianSplatDepthMask" };
            if (GraphicsSettings.currentRenderPipeline == null && cam != null && !m_CameraCommandBuffersDone.Contains(cam))
            {
                cam.AddCommandBuffer(CameraEvent.BeforeForwardOpaque, m_DepthMaskCommandBuffer);
                cam.AddCommandBuffer(GetColorCommandBufferEvent(cam), m_CommandBuffer);
                m_CameraCommandBuffersDone.Add(cam);
            }

            // get render target for all splats
            m_CommandBuffer.Clear();
            m_DepthMaskCommandBuffer.Clear();
            return m_CommandBuffer;
        }

        void OnPreCullCamera(Camera cam)
        {
            InitialClearCmdBuffer(cam);
            m_CommandBuffer.DisableShaderKeyword(kEnvironmentVisibilityCapKeyword);
            if (!GatherSplatsForCamera(cam))
            {
                if (cam.cameraType == CameraType.Game)
                    EnvironmentVisibilityCapActiveLastFrame = false;
                return;
            }

            bool useVertexViewDataFallback = ShouldUseVertexViewDataFallback(cam);
            bool renderSplatsAfterOpaque = ShouldRenderSplatsAfterOpaque(cam);
            bool useRawDepthMask = ShouldUseRawQuestDepthMask(cam) || useVertexViewDataFallback;
            if (!renderSplatsAfterOpaque)
                RenderDepthMasks(cam, m_DepthMaskCommandBuffer, useRawDepthMask);

            bool useStereoArray = ShouldUseStereoArrayTarget(cam, renderSplatsAfterOpaque);
            bool wantsVisibilityCap = useStereoArray && Shader.IsKeywordEnabled("SOFT_OCCLUSION");
            // Debug materials do not write the visibility target. Preserve their
            // existing display, including when mixed with normal splat objects.
            if (wantsVisibilityCap)
                foreach (var activeSplat in m_ActiveSplats)
                    if (activeSplat.Item1.m_RenderMode != GaussianSplatRenderer.RenderMode.Splats)
                    {
                        wantsVisibilityCap = false;
                        break;
                    }
            bool useVisibilityCap = wantsVisibilityCap && EnvironmentVisibilityCapSupported;
            if (cam.cameraType == CameraType.Game)
                EnvironmentVisibilityCapActiveLastFrame = useVisibilityCap;
            if (wantsVisibilityCap && !m_ReportedVisibilitySupport)
            {
                m_ReportedVisibilitySupport = true;
                if (useVisibilityCap)
                    Debug.Log("GSAC Gaussian soft-depth visibility cap enabled: R8 stereo MRT, existing draw and composite.");
                else
                    Debug.LogWarning("GSAC Gaussian soft-depth visibility cap unavailable: independent MRT/R8 rendering, sampling and blending are required. Using the previous occlusion path.");
            }
            if (useStereoArray)
            {
                // Multiview needs one color layer per eye, attached to the matching
                // camera depth layers. Keep the existing post-opaque ZTest path.
                RenderTextureDescriptor desc = XRSettings.eyeTextureDesc;
                desc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
                desc.depthBufferBits = 0;
                desc.msaaSamples = 1;
                desc.bindMS = false;
                desc.useMipMap = false;
                desc.autoGenerateMips = false;
                desc.enableRandomWrite = false;
                desc.memoryless = RenderTextureMemoryless.None;
                m_CommandBuffer.GetTemporaryRT(GaussianSplatRenderer.Props.GaussianSplatRT, desc, FilterMode.Point);
                var splatTarget = new RenderTargetIdentifier(GaussianSplatRenderer.Props.GaussianSplatRT, 0, CubemapFace.Unknown, -1);
                var sceneDepth = new RenderTargetIdentifier(BuiltinRenderTextureType.CurrentActive, 0, CubemapFace.Unknown, -1);
                if (useVisibilityCap)
                {
                    // Accumulate a visibility bound alongside the existing color.
                    // Its independent MAX blend prevents dense splats from turning
                    // a partially occluded real-world edge opaque again.
                    RenderTextureDescriptor visibilityDesc = desc;
                    visibilityDesc.graphicsFormat = GraphicsFormat.R8_UNorm;
                    m_CommandBuffer.GetTemporaryRT(GaussianSplatRenderer.Props.GaussianEnvironmentVisibilityRT,
                        visibilityDesc, FilterMode.Point);
                    m_VisibilityColorTargets[0] = splatTarget;
                    m_VisibilityColorTargets[1] = new RenderTargetIdentifier(
                        GaussianSplatRenderer.Props.GaussianEnvironmentVisibilityRT, 0, CubemapFace.Unknown, -1);
                    m_CommandBuffer.SetRenderTarget(m_VisibilityColorTargets, sceneDepth,
                        0, CubemapFace.Unknown, -1);
                    m_CommandBuffer.EnableShaderKeyword(kEnvironmentVisibilityCapKeyword);
                }
                else
                    m_CommandBuffer.SetRenderTarget(splatTarget, sceneDepth);
            }
            else
            {
                m_CommandBuffer.GetTemporaryRT(GaussianSplatRenderer.Props.GaussianSplatRT, -1, -1, 0, FilterMode.Point, GraphicsFormat.R16G16B16A16_SFloat);
                m_CommandBuffer.SetRenderTarget(GaussianSplatRenderer.Props.GaussianSplatRT, BuiltinRenderTextureType.CurrentActive);
            }
            m_CommandBuffer.ClearRenderTarget(RTClearFlags.Color, new Color(0, 0, 0, 0), 0, 0);

            // add sorting, view calc and drawing commands for each splat object
            Material matComposite = SortAndRenderSplats(cam, m_CommandBuffer, renderSplatsAfterOpaque ? CompareFunction.LessEqual : CompareFunction.Always);

            // compose
            m_CommandBuffer.BeginSample(s_ProfCompose);
            if (useStereoArray)
                m_CommandBuffer.SetRenderTarget(new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget, 0, CubemapFace.Unknown, -1));
            else
                m_CommandBuffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            if (matComposite != null)
            {
                // The Quest depth profile uses stereo array rendering. Keep its
                // partial-coverage color premultiplied through the camera target.
                // The original composite remains unchanged for the legacy path.
                int compositePass = useStereoArray ? kQuestPremultipliedCompositePass : 0;
                m_CommandBuffer.DrawProcedural(Matrix4x4.identity, matComposite, compositePass, MeshTopology.Triangles, 3, 1);
            }
            m_CommandBuffer.EndSample(s_ProfCompose);
            m_CommandBuffer.DisableShaderKeyword(kEnvironmentVisibilityCapKeyword);
            if (useVisibilityCap)
                m_CommandBuffer.ReleaseTemporaryRT(GaussianSplatRenderer.Props.GaussianEnvironmentVisibilityRT);
            m_CommandBuffer.ReleaseTemporaryRT(GaussianSplatRenderer.Props.GaussianSplatRT);
        }

        static bool ShouldUseStereoArrayTarget(Camera cam, bool renderSplatsAfterOpaque)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // Keep desktop and the existing MultiPass path unchanged. Android AR
            // uses pass 1, whose projection and covariance are calculated per eye.
            return renderSplatsAfterOpaque && cam != null && cam.stereoEnabled &&
                   XRSettings.eyeTextureDesc.dimension == TextureDimension.Tex2DArray;
#else
            return false;
#endif
        }

        static bool ShouldUseVertexViewDataFallback(Camera cam)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return cam != null && cam.cameraType == CameraType.Game;
#else
            return false;
#endif
        }

        static int GetEffectiveSortInterval(Camera cam, GaussianSplatRenderer gs)
        {
            int interval = Mathf.Max(1, gs != null ? gs.m_SortNthFrame : 1);
#if UNITY_ANDROID && !UNITY_EDITOR
            if (cam != null && cam.cameraType == CameraType.Game)
                interval = Mathf.Max(interval, 2);
#endif
            return interval;
        }

        static bool ShouldSortThisFrame(Camera cam, GaussianSplatRenderer gs, int interval)
        {
            interval = Mathf.Max(1, interval);
#if UNITY_ANDROID && !UNITY_EDITOR
            if (cam != null && cam.cameraType == CameraType.Game)
                return Time.frameCount % interval == interval - 1;
#endif
            return gs != null && gs.m_FrameCounter % interval == 0;
        }

        static CameraEvent GetColorCommandBufferEvent(Camera cam)
        {
            return ShouldRenderSplatsAfterOpaque(cam) ? CameraEvent.AfterForwardOpaque : CameraEvent.BeforeForwardOpaque;
        }

        static bool ShouldUseRawQuestDepthMask(Camera cam)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return cam != null && cam.cameraType != CameraType.Preview;
#else
            return false;
#endif
        }

        static bool ShouldRenderSplatsAfterOpaque(Camera cam)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return cam != null && cam.cameraType == CameraType.Game && IsSceneARMode(cam);
#else
            return false;
#endif
        }

        static bool IsSceneARMode(Camera cam)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            Type generalOperatorType = Type.GetType("GeneralOperator, Assembly-CSharp") ?? Type.GetType("GeneralOperator");
            if (generalOperatorType != null)
            {
                UnityEngine.Object[] operators = UnityEngine.Object.FindObjectsOfType(generalOperatorType, true);
                foreach (UnityEngine.Object op in operators)
                {
                    var buildARProperty = generalOperatorType.GetProperty("BuildAR");
                    if (buildARProperty != null && buildARProperty.PropertyType == typeof(bool))
                        return (bool)buildARProperty.GetValue(op);

                    var modeProperty = generalOperatorType.GetProperty("Mode");
                    if (modeProperty != null)
                        return string.Equals(modeProperty.GetValue(op)?.ToString(), "AR", StringComparison.Ordinal);
                }
            }

            if (cam != null)
            {
                foreach (Behaviour behaviour in cam.GetComponents<Behaviour>())
                {
                    Type type = behaviour != null ? behaviour.GetType() : null;
                    if (type != null && type.FullName == "UnityEngine.XR.ARFoundation.ARCameraManager" && behaviour.enabled)
                        return true;
                }
            }
#endif
            return false;
        }

        static bool ShouldForceNearQuestDepthMask(Camera cam)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return cam != null && cam.cameraType != CameraType.Preview;
#else
            return false;
#endif
        }

        static bool ShouldUseSkinnedMeshDepthProxy(Camera cam, GaussianSplatRenderer gs)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return gs != null && gs.m_UseSkinnedMeshDepthProxyOnAndroid && cam != null && cam.cameraType == CameraType.Game;
#else
            return false;
#endif
        }

        static void SetCameraDepthWriteTarget(CommandBuffer cmb)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            cmb.SetRenderTarget(BuiltinRenderTextureType.CameraTarget, BuiltinRenderTextureType.Depth);
#else
            cmb.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
#endif
        }

        static bool ShouldFlipSplatClipY(Camera cam)
        {
            if (cam == null || cam.cameraType != CameraType.Game)
                return false;

            // XR array targets already use the per-eye render-target projection.
            // The legacy 2D intermediate needed a manual Y flip; applying it to
            // Multiview flips the avatar while opaque scene geometry stays upright.
            if (ShouldUseStereoArrayTarget(cam, ShouldRenderSplatsAfterOpaque(cam)))
                return false;

#if UNITY_ANDROID
            return true;
#elif UNITY_EDITOR
            return UnityEditor.EditorUserBuildSettings.activeBuildTarget == UnityEditor.BuildTarget.Android;
#else
            return false;
#endif
        }

        static bool ShouldFlipDepthMaskClipY(Camera cam)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return cam != null && cam.cameraType != CameraType.Preview;
#elif UNITY_EDITOR
            return UnityEditor.EditorUserBuildSettings.activeBuildTarget == UnityEditor.BuildTarget.Android &&
                   cam != null && cam.cameraType == CameraType.Game;
#else
            return false;
#endif
        }
    }

    [ExecuteInEditMode]
    public class GaussianSplatRenderer : MonoBehaviour
    {
        [UnityEngine.Scripting.Preserve]
        public static bool EnvironmentVisibilityCapSupported => GaussianSplatRenderSystem.EnvironmentVisibilityCapSupported;
        [UnityEngine.Scripting.Preserve]
        public static bool EnvironmentVisibilityCapActiveLastFrame => GaussianSplatRenderSystem.EnvironmentVisibilityCapActiveLastFrame;
        //  Updated Code for renderring
        public GraphicsBuffer updatedPositionsBuffer;
        public GraphicsBuffer updatedOtherBuffer;
        public GraphicsBuffer TBuffer;
        Vector3[] m_CpuSortPositions;
        float[] m_CpuSortDistances;
        uint[] m_CpuSortKeys;

        public void SetUpdatedPositionsBuffer(GraphicsBuffer buffer)
        {
            updatedPositionsBuffer = buffer;
        }

        public void SetCpuSortPositions(Vector3[] positions)
        {
            m_CpuSortPositions = positions;
            if (positions == null)
            {
                m_CpuSortDistances = null;
                m_CpuSortKeys = null;
                return;
            }

            if (m_CpuSortDistances == null || m_CpuSortDistances.Length != positions.Length)
            {
                m_CpuSortDistances = new float[positions.Length];
                m_CpuSortKeys = new uint[positions.Length];
            }
        }

 
        //  END Updated Code for renderring

        public enum RenderMode
        {
            Splats,
            DebugPoints,
            DebugPointIndices,
            DebugBoxes,
            DebugChunkBounds
           
        }
        public GaussianSplatAsset m_Asset;

        // [Range(0.1f, 2.0f)] [Tooltip("Additional scaling factor for the splats")]
        public float m_SplatScale = 1.0f;
        // [Range(0.05f, 20.0f)]
        // [Tooltip("Additional scaling factor for opacity")]
        public float m_OpacityScale = 1.0f;
        [Tooltip("Render an invisible Gaussian depth mask before opaque scene objects so meshes behind the avatar are hidden without changing the Gaussian color pass.")]
        public bool m_OccludeSceneObjects = true;
        [Range(0.02f, 0.5f)]
        public float m_OcclusionAlphaThreshold = 0.12f;
        [Range(0.0f, 2.0f)]
        public float m_OcclusionEdgeShrinkPixels = 1.0f;
        [Tooltip("Quest fallback: write the Gaussian depth mask at foreground depth. Keep this off for correct depth ordering with objects in front of the avatar.")]
        public bool m_QuestDepthMaskForceNearDepth = false;
        [Tooltip("Quest/Android: draw nearby SMPLX/skinned meshes as invisible depth-only proxies before scene opaques. This blocks objects behind the Gaussian avatar without changing the Gaussian color pass.")]
        public bool m_UseSkinnedMeshDepthProxyOnAndroid = true;
        [Tooltip("Quest/Android: draw the baked proxy at the Gaussian object's position instead of the source SMPLX renderer's position. Keep this on when the visible SMPLX test mesh is placed behind the ball.")]
        public bool m_AlignDepthProxyToGaussianOnAndroid = true;
        [Range(1.0f, 1.2f)]
        [Tooltip("Quest/Android: uniformly enlarge the invisible SMPLX depth proxy around its baked mesh center. This affects depth only and helps cover Gaussian edge leaks without drawing visible black proxy bodies.")]
        public float m_DepthProxyMeshScaleOnAndroid = 1.0f;
        [Tooltip("Optional explicit SMPLX/skinned mesh depth proxies. If empty, the renderer searches the nearest parent hierarchy for active SkinnedMeshRenderers.")]
        public SkinnedMeshRenderer[] m_DepthProxyRenderers;
        [Range(0, 3)] [Tooltip("Spherical Harmonics order to use")]
        public int m_SHOrder = 3;

        [Tooltip("Show only Spherical Harmonics contribution, using gray color")]
        public bool m_SHOnly;

        [Tooltip("Show only Direct Color contribution, ignoring SH")]
        public bool m_DCOnly ;

        [Range(1,30)] [Tooltip("Sort splats only every N frames")]
        public int m_SortNthFrame = 1;

        public RenderMode m_RenderMode = RenderMode.Splats;
        [Range(1.0f,15.0f)] public float m_PointDisplaySize = 3.0f;

        public GaussianCutout[] m_Cutouts;

        public Shader m_ShaderSplats;
        public Shader m_ShaderComposite;
        public Shader m_ShaderDepthMask;
        public Shader m_ShaderDebugPoints;
        public Shader m_ShaderDebugBoxes;
        [Tooltip("Gaussian splatting compute shader")]
        public ComputeShader m_CSSplatUtilities;

        int m_SplatCount; // initially same as asset splat count, but editing can change this
        GraphicsBuffer m_GpuSortDistances;
        internal GraphicsBuffer m_GpuSortKeys;
        public GraphicsBuffer m_GpuPosData;
        public ComputeBuffer m_TBuffer;
        public GraphicsBuffer m_recordBuffer;
        public float3[] T;
        public GraphicsBuffer m_GpuOtherData;
        public GraphicsBuffer m_GpuSHData;
        public Texture m_GpuColorData;
        internal GraphicsBuffer m_GpuChunks;
        internal bool m_GpuChunksValid;
        internal GraphicsBuffer m_GpuView;
        internal GraphicsBuffer m_GpuIndexBuffer;

        // these buffers are only for splat editing, and are lazily created
        GraphicsBuffer m_GpuEditCutouts;
        GraphicsBuffer m_GpuEditCountsBounds;
        GraphicsBuffer m_GpuEditSelected;
        GraphicsBuffer m_GpuEditDeleted;
        GraphicsBuffer m_GpuEditSelectedMouseDown; // selection state at start of operation
        GraphicsBuffer m_GpuEditPosMouseDown; // position state at start of operation
        GraphicsBuffer m_GpuEditOtherMouseDown; // rotation/scale state at start of operation

        GpuSorting m_Sorter;
        GpuSorting.Args m_SorterArgs;

        internal Material m_MatSplats;
        internal Material m_MatComposite;
        internal Material m_MatDepthMask;
        internal Material m_MatDebugPoints;
        internal Material m_MatDebugBoxes;

        [NonSerialized] SkinnedMeshRenderer[] m_AutoDepthProxyRenderers;
        [NonSerialized] Dictionary<SkinnedMeshRenderer, Mesh> m_DepthProxyBakeMeshes;

        internal int m_FrameCounter;
        GaussianSplatAsset m_PrevAsset;
        Hash128 m_PrevHash;

        internal SkinnedMeshRenderer[] GetDepthProxyRenderers()
        {
            if (m_DepthProxyRenderers != null && m_DepthProxyRenderers.Length != 0)
                return m_DepthProxyRenderers;

            if (m_AutoDepthProxyRenderers == null || m_AutoDepthProxyRenderers.Length == 0)
                m_AutoDepthProxyRenderers = FindNearestDepthProxyRenderers();

            return m_AutoDepthProxyRenderers;
        }

        SkinnedMeshRenderer[] FindNearestDepthProxyRenderers()
        {
            var proxies = new List<SkinnedMeshRenderer>();
            for (Transform root = transform; root != null; root = root.parent)
            {
                proxies.Clear();
                var candidates = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                foreach (var candidate in candidates)
                {
                    if (candidate != null && candidate.sharedMesh != null && candidate.gameObject.activeInHierarchy)
                        proxies.Add(candidate);
                }

                if (proxies.Count != 0)
                    return proxies.ToArray();
            }

            return Array.Empty<SkinnedMeshRenderer>();
        }

        internal Mesh GetBakedDepthProxyMesh(SkinnedMeshRenderer proxy)
        {
            if (proxy == null || proxy.sharedMesh == null)
                return null;

            m_DepthProxyBakeMeshes ??= new Dictionary<SkinnedMeshRenderer, Mesh>();
            if (!m_DepthProxyBakeMeshes.TryGetValue(proxy, out var mesh) || mesh == null)
            {
                mesh = new Mesh { name = $"{proxy.name}_GaussianDepthProxy" };
                mesh.MarkDynamic();
                m_DepthProxyBakeMeshes[proxy] = mesh;
            }

            proxy.BakeMesh(mesh);
            return mesh;
        }

        void DisposeDepthProxyBakeMeshes()
        {
            if (m_DepthProxyBakeMeshes == null)
                return;

            foreach (var kvp in m_DepthProxyBakeMeshes)
            {
                if (kvp.Value != null)
                    DestroyImmediate(kvp.Value);
            }

            m_DepthProxyBakeMeshes.Clear();
        }

        static readonly ProfilerMarker s_ProfSort = new(ProfilerCategory.Render, "GaussianSplat.Sort", MarkerFlags.SampleGPU);

        internal static class Props
        {
            public static readonly int SplatPos = Shader.PropertyToID("_SplatPos");
            public static readonly int SplatOther = Shader.PropertyToID("_SplatOther");
            public static readonly int SplatSH = Shader.PropertyToID("_SplatSH");
            public static readonly int SplatColor = Shader.PropertyToID("_SplatColor");
            public static readonly int SplatSelectedBits = Shader.PropertyToID("_SplatSelectedBits");
            public static readonly int SplatDeletedBits = Shader.PropertyToID("_SplatDeletedBits");
            public static readonly int SplatBitsValid = Shader.PropertyToID("_SplatBitsValid");
            public static readonly int SplatFormat = Shader.PropertyToID("_SplatFormat");
            public static readonly int SplatChunks = Shader.PropertyToID("_SplatChunks");
            public static readonly int SplatChunkCount = Shader.PropertyToID("_SplatChunkCount");
            public static readonly int SplatViewData = Shader.PropertyToID("_SplatViewData");
            public static readonly int OrderBuffer = Shader.PropertyToID("_OrderBuffer");
            public static readonly int SplatScale = Shader.PropertyToID("_SplatScale");
            public static readonly int SplatOpacityScale = Shader.PropertyToID("_SplatOpacityScale");
            public static readonly int SplatSize = Shader.PropertyToID("_SplatSize");
            public static readonly int SplatCount = Shader.PropertyToID("_SplatCount");
            public static readonly int SHOrder = Shader.PropertyToID("_SHOrder");
            public static readonly int SHOnly = Shader.PropertyToID("_SHOnly");
            public static readonly int DCOnly = Shader.PropertyToID("_DCOnly");
            public static readonly int DisplayIndex = Shader.PropertyToID("_DisplayIndex");
            public static readonly int DisplayChunks = Shader.PropertyToID("_DisplayChunks");
            public static readonly int GaussianSplatRT = Shader.PropertyToID("_GaussianSplatRT");
            public static readonly int GaussianEnvironmentVisibilityRT = Shader.PropertyToID("_GaussianEnvironmentVisibilityRT");
            public static readonly int GaussianSplatClipFlipY = Shader.PropertyToID("_GaussianSplatClipFlipY");
            public static readonly int GaussianDepthCoverageRT = Shader.PropertyToID("_GaussianDepthCoverageRT");
            public static readonly int EnvironmentInverseViewProjection = Shader.PropertyToID("_GsacEnvironmentInverseViewProjection");
            public static readonly int DepthMaskAlphaThreshold = Shader.PropertyToID("_DepthMaskAlphaThreshold");
            public static readonly int DepthMaskLocalAlphaEpsilon = Shader.PropertyToID("_DepthMaskLocalAlphaEpsilon");
            public static readonly int DepthMaskEdgeShrinkPixels = Shader.PropertyToID("_DepthMaskEdgeShrinkPixels");
            public static readonly int DepthMaskUseRawSplatData = Shader.PropertyToID("_DepthMaskUseRawSplatData");
            public static readonly int DepthMaskForceNearDepth = Shader.PropertyToID("_DepthMaskForceNearDepth");
            public static readonly int GaussianSceneZTest = Shader.PropertyToID("_GaussianSceneZTest");
            public static readonly int SplatSortKeys = Shader.PropertyToID("_SplatSortKeys");
            public static readonly int SplatSortDistances = Shader.PropertyToID("_SplatSortDistances");
            public static readonly int SrcBuffer = Shader.PropertyToID("_SrcBuffer");
            public static readonly int DstBuffer = Shader.PropertyToID("_DstBuffer");
            public static readonly int BufferSize = Shader.PropertyToID("_BufferSize");
            public static readonly int MatrixVP = Shader.PropertyToID("_MatrixVP");
            public static readonly int MatrixMV = Shader.PropertyToID("_MatrixMV");
            public static readonly int MatrixP = Shader.PropertyToID("_MatrixP");
            public static readonly int MatrixObjectToWorld = Shader.PropertyToID("_MatrixObjectToWorld");
            public static readonly int MatrixWorldToObject = Shader.PropertyToID("_MatrixWorldToObject");
            public static readonly int VecScreenParams = Shader.PropertyToID("_VecScreenParams");
            public static readonly int VecWorldSpaceCameraPos = Shader.PropertyToID("_VecWorldSpaceCameraPos");
            public static readonly int SelectionCenter = Shader.PropertyToID("_SelectionCenter");
            public static readonly int SelectionDelta = Shader.PropertyToID("_SelectionDelta");
            public static readonly int SelectionDeltaRot = Shader.PropertyToID("_SelectionDeltaRot");
            public static readonly int SplatCutoutsCount = Shader.PropertyToID("_SplatCutoutsCount");
            public static readonly int SplatCutouts = Shader.PropertyToID("_SplatCutouts");
            public static readonly int SelectionMode = Shader.PropertyToID("_SelectionMode");
            public static readonly int SplatPosMouseDown = Shader.PropertyToID("_SplatPosMouseDown");
            public static readonly int SplatOtherMouseDown = Shader.PropertyToID("_SplatOtherMouseDown");

            public static readonly int FaceCentroid = Shader.PropertyToID("_TBuffer");
            public static readonly int RECORD = Shader.PropertyToID("_RECORD");
        }

        [field: NonSerialized] public bool editModified { get; private set; }
        [field: NonSerialized] public uint editSelectedSplats { get; private set; }
        [field: NonSerialized] public uint editDeletedSplats { get; private set; }
        [field: NonSerialized] public uint editCutSplats { get; private set; }
        [field: NonSerialized] public Bounds editSelectedBounds { get; private set; }

        public GaussianSplatAsset asset => m_Asset;
        public int splatCount => m_SplatCount;

        enum KernelIndices
        {
            SetIndices,
            CalcDistances,
            CalcViewData,
            UpdateEditData,
            InitEditData,
            ClearBuffer,
            InvertSelection,
            SelectAll,
            OrBuffers,
            SelectionUpdate,
            TranslateSelection,
            RotateSelection,
            ScaleSelection,
            ExportData,
            CopySplats,
        }

        public bool HasValidAsset =>
            m_Asset != null &&
            m_Asset.splatCount > 0 &&
            m_Asset.formatVersion == GaussianSplatAsset.kCurrentVersion &&
            m_Asset.posData != null &&
            m_Asset.otherData != null &&
            m_Asset.shData != null &&
            m_Asset.colorData != null;
        public bool HasValidRenderSetup => m_GpuPosData != null && m_GpuOtherData != null && m_GpuChunks != null;

        const int kGpuViewDataSize = 40;

        void CreateResourcesForAsset()
        {
            if (!HasValidAsset)
                return;

            m_SplatCount = asset.splatCount;
            m_GpuPosData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource | GraphicsBuffer.Target.CopyDestination, (int) (asset.posData.dataSize / 4), 4) { name = "GaussianPosData" };
            m_GpuPosData.SetData(asset.posData.GetData<uint>());
            m_GpuOtherData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource | GraphicsBuffer.Target.CopyDestination, (int) (asset.otherData.dataSize / 4), 4) { name = "GaussianOtherData" };
            m_GpuOtherData.SetData(asset.otherData.GetData<uint>());
            m_GpuSHData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, (int) (asset.shData.dataSize / 4), 4) { name = "GaussianSHData" };
            m_GpuSHData.SetData(asset.shData.GetData<uint>());
            var (texWidth, texHeight) = GaussianSplatAsset.CalcTextureSize(asset.splatCount);
            var texFormat = GaussianSplatAsset.ColorFormatToGraphics(asset.colorFormat);
            var tex = new Texture2D(texWidth, texHeight, texFormat, TextureCreationFlags.DontInitializePixels | TextureCreationFlags.IgnoreMipmapLimit | TextureCreationFlags.DontUploadUponCreate) { name = "GaussianColorData" };
            tex.SetPixelData(asset.colorData.GetData<byte>(), 0);
            tex.Apply(false, true);
            m_GpuColorData = tex;
            if (asset.chunkData != null && asset.chunkData.dataSize != 0)
            {
                m_GpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                    (int) (asset.chunkData.dataSize / UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>()),
                    UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>()) {name = "GaussianChunkData"};
                m_GpuChunks.SetData(asset.chunkData.GetData<GaussianSplatAsset.ChunkInfo>());
                m_GpuChunksValid = true;
            }
            else
            {
                // just a dummy chunk buffer
                m_GpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1,
                    UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>()) {name = "GaussianChunkData"};
                m_GpuChunksValid = false;
            }

            m_GpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_Asset.splatCount, kGpuViewDataSize);
            m_GpuIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Index, 36, 2);
            // cube indices, most often we use only the first quad
            m_GpuIndexBuffer.SetData(new ushort[]
            {
                0, 1, 2, 1, 3, 2,
                4, 6, 5, 5, 6, 7,
                0, 2, 4, 4, 2, 6,
                1, 5, 3, 5, 7, 3,
                0, 4, 1, 4, 5, 1,
                2, 3, 6, 3, 7, 6
            });

            InitSortBuffers(splatCount);


            m_TBuffer = new ComputeBuffer(splatCount, sizeof(float) * 3);

            T = new float3[splatCount];
            m_TBuffer.SetData(T);


            m_recordBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, (int) (asset.posData.dataSize / 4), 4) { name = "GaussianPosData" };

            m_recordBuffer.SetData(T);
        }

        void InitSortBuffers(int count)
        {
            m_GpuSortDistances?.Dispose();
            m_GpuSortKeys?.Dispose();
            m_SorterArgs.resources.Dispose();

            m_GpuSortDistances = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 4) { name = "GaussianSplatSortDistances" };
            m_GpuSortKeys = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 4) { name = "GaussianSplatSortIndices" };

            var indices = new uint[count];
            for (int i = 0; i < indices.Length; ++i)
                indices[i] = (uint)i;
            m_GpuSortKeys.SetData(indices);

            m_SorterArgs.inputKeys = m_GpuSortDistances;
            m_SorterArgs.inputValues = m_GpuSortKeys;
            m_SorterArgs.count = (uint)count;
            if (m_Sorter.Valid)
                m_SorterArgs.resources = GpuSorting.SupportResources.Load((uint)count);
        }

        public void OnEnable()
        {
            m_FrameCounter = 0;
            if (m_ShaderSplats == null || m_ShaderComposite == null || m_ShaderDebugPoints == null || m_ShaderDebugBoxes == null || m_CSSplatUtilities == null)
                return;
            if (!SystemInfo.supportsComputeShaders)
                return;

            m_MatSplats = new Material(m_ShaderSplats) {name = "GaussianSplats"};
            m_MatComposite = new Material(m_ShaderComposite) {name = "GaussianClearDstAlpha"};
            Shader depthMaskShader = m_ShaderDepthMask != null ? m_ShaderDepthMask : Shader.Find("Hidden/Gaussian Splatting/Depth Mask");
            if (depthMaskShader != null)
            {
                m_MatDepthMask = new Material(depthMaskShader) { name = "GaussianDepthMask" };
            }
            else if (m_OccludeSceneObjects)
            {
                Debug.LogError("Gaussian splat depth occlusion is enabled, but the Depth Mask shader is missing. Assign GaussianDepthMask.shader on the renderer or include it in Graphics Settings.", this);
            }
            m_MatDebugPoints = new Material(m_ShaderDebugPoints) {name = "GaussianDebugPoints"};
            m_MatDebugBoxes = new Material(m_ShaderDebugBoxes) {name = "GaussianDebugBoxes"};

            m_Sorter = new GpuSorting(m_CSSplatUtilities);
            if (!m_Sorter.Valid)
                Debug.LogWarning("Gaussian splat GPU sorter is not supported on this device; rendering splats in asset order.");
            GaussianSplatRenderSystem.instance.RegisterSplat(this);

            CreateResourcesForAsset();
        }

        void SetAssetDataOnCS(CommandBuffer cmb, KernelIndices kernel)
        {
            ComputeShader cs = m_CSSplatUtilities;
            int kernelIndex = (int) kernel;
            GraphicsBuffer splatPosBuffer = (kernel == KernelIndices.CalcViewData || kernel == KernelIndices.CalcDistances)
                ? updatedPositionsBuffer ?? m_GpuPosData
                : m_GpuPosData;
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatPos, splatPosBuffer);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatChunks, m_GpuChunks);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatOther, m_GpuOtherData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatSH, m_GpuSHData);
            cmb.SetComputeTextureParam(cs, kernelIndex, Props.SplatColor, m_GpuColorData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatSelectedBits, m_GpuEditSelected ?? m_GpuPosData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatDeletedBits, m_GpuEditDeleted ?? m_GpuPosData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatViewData, m_GpuView);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.OrderBuffer, m_GpuSortKeys);

            cmb.SetComputeIntParam(cs, Props.SplatBitsValid, m_GpuEditSelected != null && m_GpuEditDeleted != null ? 1 : 0);
            uint format = (uint)m_Asset.posFormat | ((uint)m_Asset.scaleFormat << 8) | ((uint)m_Asset.shFormat << 16);
            cmb.SetComputeIntParam(cs, Props.SplatFormat, (int)format);
            cmb.SetComputeIntParam(cs, Props.SplatCount, m_SplatCount);
            cmb.SetComputeIntParam(cs, Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);

            UpdateCutoutsBuffer();
            cmb.SetComputeIntParam(cs, Props.SplatCutoutsCount, m_Cutouts?.Length ?? 0);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatCutouts, m_GpuEditCutouts);
        }

        internal void SetAssetDataOnMaterial(MaterialPropertyBlock mat)
        {
            mat.SetBuffer(Props.SplatPos, updatedPositionsBuffer ?? m_GpuPosData);
            mat.SetBuffer(Props.SplatChunks, m_GpuChunks);
            mat.SetBuffer(Props.SplatOther, m_GpuOtherData);
            mat.SetBuffer(Props.SplatSH, m_GpuSHData);
            mat.SetTexture(Props.SplatColor, m_GpuColorData);
            mat.SetBuffer(Props.SplatSelectedBits, m_GpuEditSelected ?? m_GpuPosData);
            mat.SetBuffer(Props.SplatDeletedBits, m_GpuEditDeleted ?? m_GpuPosData);
            mat.SetInt(Props.SplatBitsValid, m_GpuEditSelected != null && m_GpuEditDeleted != null ? 1 : 0);
            uint format = (uint)m_Asset.posFormat | ((uint)m_Asset.scaleFormat << 8) | ((uint)m_Asset.shFormat << 16);
            mat.SetInteger(Props.SplatFormat, (int)format);
            mat.SetInteger(Props.SplatCount, m_SplatCount);
            mat.SetInteger(Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);
        }

        static void DisposeBuffer(ref GraphicsBuffer buf)
        {
            buf?.Dispose();
            buf = null;
        }

        void DisposeResourcesForAsset()
        {
            DestroyImmediate(m_GpuColorData);

            DisposeBuffer(ref m_GpuPosData);
            DisposeBuffer(ref m_GpuOtherData);
            DisposeBuffer(ref m_GpuSHData);
            DisposeBuffer(ref m_GpuChunks);

            DisposeBuffer(ref m_GpuView);
            DisposeBuffer(ref m_GpuIndexBuffer);
            DisposeBuffer(ref m_GpuSortDistances);
            DisposeBuffer(ref m_GpuSortKeys);

            DisposeBuffer(ref m_GpuEditSelectedMouseDown);
            DisposeBuffer(ref m_GpuEditPosMouseDown);
            DisposeBuffer(ref m_GpuEditOtherMouseDown);
            DisposeBuffer(ref m_GpuEditSelected);
            DisposeBuffer(ref m_GpuEditDeleted);
            DisposeBuffer(ref m_GpuEditCountsBounds);
            DisposeBuffer(ref m_GpuEditCutouts);

            m_SorterArgs.resources.Dispose();

            m_SplatCount = 0;
            m_GpuChunksValid = false;

            editSelectedSplats = 0;
            editDeletedSplats = 0;
            editCutSplats = 0;
            editModified = false;
            editSelectedBounds = default;
        }

        public void OnDisable()
        {
            DisposeResourcesForAsset();
            DisposeDepthProxyBakeMeshes();
            GaussianSplatRenderSystem.instance.UnregisterSplat(this);

            DestroyImmediate(m_MatSplats);
            DestroyImmediate(m_MatComposite);
            DestroyImmediate(m_MatDepthMask);
            DestroyImmediate(m_MatDebugPoints);
            DestroyImmediate(m_MatDebugBoxes);
        }

        internal void CalcViewData(CommandBuffer cmb, Camera cam, Matrix4x4 matrix)
        {
            if (cam.cameraType == CameraType.Preview)
                return;

            var tr = transform;

            Matrix4x4 matView = cam.worldToCameraMatrix;
            Matrix4x4 matProj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);
            Matrix4x4 matO2W = tr.localToWorldMatrix;
            Matrix4x4 matW2O = tr.worldToLocalMatrix;
            int screenW = cam.pixelWidth, screenH = cam.pixelHeight;
            Vector4 screenPar = new Vector4(screenW, screenH, 0, 0);
            Vector4 camPos = cam.transform.position;
            // Debug.Log("MatrixVP");
            // Matrix4x4 MatrixVP = matProj * matView;
            // for (int i = 0; i < 4; i++)
            // {
            //     string row = $"{MatrixVP[i, 0]}, {MatrixVP[i, 1]}, {MatrixVP[i, 2]}, {MatrixVP[i, 3]}";
            //     Debug.Log($"Row {i}: {row}");
            // }
            // SaveMatrixToFile("Assets/GaussianAssets/MatrixObjectToWorld.txt",matO2W);
            // SaveMatrixToFile("Assets/GaussianAssets/MatrixVP.txt",matProj * matView);
 
            // calculate view dependent data for each splat
            SetAssetDataOnCS(cmb, KernelIndices.CalcViewData);
            

            cmb.SetComputeBufferParam(m_CSSplatUtilities,(int)KernelIndices.CalcViewData, "_TBuffer", m_TBuffer);
            
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixVP, matProj * matView);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixMV, matView * matO2W);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixP, matProj);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, matO2W);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, matW2O);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.VecScreenParams, screenPar);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.VecWorldSpaceCameraPos, camPos);
            cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.SplatScale, m_SplatScale);
            cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.SplatOpacityScale, m_OpacityScale);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SHOrder, m_SHOrder);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SHOnly, m_SHOnly ? 1 : 0);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.DCOnly, m_DCOnly ? 1 : 0);


            

            // m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.CalcViewData, out uint gsX, out _, out _);
            // cmb.DispatchCompute(m_CSSplatUtilities, (int)KernelIndices.CalcViewData, (m_GpuView.count + (int)gsX - 1)/(int)gsX, 1, 1);
            // Vector[] debugData = new Vector[];
            // m_GpuView.GetData(debugData);
            // Debug.Log(debugData[0]);

            m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.CalcViewData, out uint gsX, out _, out _);
            cmb.DispatchCompute(m_CSSplatUtilities, (int)KernelIndices.CalcViewData, (m_GpuView.count + (int)gsX - 1) / (int)gsX, 1, 1);

            // // Retrieve the compute shader output for debugging
            // SplatViewData[] debugData = new SplatViewData[m_GpuView.count];
            // m_GpuView.GetData(debugData);
            // Debug.Log($"Debug Data Before Manual Update: {debugData[0].pos}");

            // // Manually update m_GpuView
            // SplatViewData[] updatedData = new SplatViewData[m_GpuView.count];
            // for (int i = 0; i < updatedData.Length; i++)
            // {
            //     updatedData[i].pos = new float4(0, 0, 0, 1); // Example data
            //     updatedData[i].axis1 = new float2(1, 0);
            //     updatedData[i].axis2 = new float2(0, 1);
            //     updatedData[i].color = new uint2(0xFFFF, 0xFFFF);
            // }




        }
        public struct SplatViewData
        {
            public float4 pos;       // Center position in clip space
            public float2 axis1;     // Axis1 for splat quad
            public float2 axis2;     // Axis2 for splat quad
            public uint2 color;      // Encoded RGBA color (16 bits per channel)
        }

        internal void SortPoints(CommandBuffer cmd, Camera cam, Matrix4x4 matrix)
        {
            if (cam.cameraType == CameraType.Preview)
                return;
            if (!m_Sorter.Valid)
                return;

            Matrix4x4 worldToCamMatrix = cam.worldToCameraMatrix;
            worldToCamMatrix.m20 *= -1;
            worldToCamMatrix.m21 *= -1;
            worldToCamMatrix.m22 *= -1;

            // calculate distance to the camera for each splat
            cmd.BeginSample(s_ProfSort);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CalcDistances, Props.SplatSortDistances, m_GpuSortDistances);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CalcDistances, Props.SplatSortKeys, m_GpuSortKeys);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CalcDistances, Props.SplatChunks, m_GpuChunks);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CalcDistances, Props.SplatPos, updatedPositionsBuffer ?? m_GpuPosData);
            cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SplatFormat, (int)m_Asset.posFormat);
            cmd.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixMV, worldToCamMatrix * matrix);
            cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SplatCount, m_SplatCount);
            cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);
            m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.CalcDistances, out uint gsX, out _, out _);
            cmd.DispatchCompute(m_CSSplatUtilities, (int)KernelIndices.CalcDistances, (m_GpuSortDistances.count + (int)gsX - 1)/(int)gsX, 1, 1);

            // sort the splats
            m_Sorter.Dispatch(cmd, m_SorterArgs);
            cmd.EndSample(s_ProfSort);
        }

        internal void SortPointsCpu(Camera cam, Matrix4x4 matrix)
        {
            if (cam == null || cam.cameraType == CameraType.Preview)
                return;
            if (m_CpuSortPositions == null || m_CpuSortDistances == null || m_CpuSortKeys == null)
                return;
            if (m_GpuSortKeys == null)
                return;

            Matrix4x4 worldToCamMatrix = cam.worldToCameraMatrix;
            worldToCamMatrix.m20 *= -1;
            worldToCamMatrix.m21 *= -1;
            worldToCamMatrix.m22 *= -1;
            Matrix4x4 matrixMV = worldToCamMatrix * matrix;

            int count = math.min(m_CpuSortPositions.Length, math.min(splatCount, m_GpuSortKeys.count));
            if (count <= 0)
                return;

            for (int i = 0; i < count; ++i)
            {
                m_CpuSortDistances[i] = matrixMV.MultiplyPoint3x4(m_CpuSortPositions[i]).z;
                m_CpuSortKeys[i] = (uint)i;
            }

            Array.Sort(m_CpuSortDistances, m_CpuSortKeys, 0, count);
            m_GpuSortKeys.SetData(m_CpuSortKeys, 0, 0, count);
        }

        public void Update()
        {
            var curHash = m_Asset ? m_Asset.dataHash : new Hash128();
            if (m_PrevAsset != m_Asset || m_PrevHash != curHash)
            {
                m_PrevAsset = m_Asset;
                m_PrevHash = curHash;
                DisposeResourcesForAsset();
                CreateResourcesForAsset();
            }
        }

        public void ActivateCamera(int index)
        {
            Camera mainCam = Camera.main;
            if (!mainCam)
                return;
            if (!m_Asset || m_Asset.cameras == null)
                return;

            var selfTr = transform;
            var camTr = mainCam.transform;
            var prevParent = camTr.parent;
            var cam = m_Asset.cameras[index];
            camTr.parent = selfTr;
            camTr.localPosition = cam.pos;
            camTr.localRotation = Quaternion.LookRotation(cam.axisZ, cam.axisY);
            camTr.parent = prevParent;
            camTr.localScale = Vector3.one;
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(camTr);
#endif
        }

        void ClearGraphicsBuffer(GraphicsBuffer buf)
        {
            m_CSSplatUtilities.SetBuffer((int)KernelIndices.ClearBuffer, Props.DstBuffer, buf);
            m_CSSplatUtilities.SetInt(Props.BufferSize, buf.count);
            m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.ClearBuffer, out uint gsX, out _, out _);
            m_CSSplatUtilities.Dispatch((int)KernelIndices.ClearBuffer, (int)((buf.count+gsX-1)/gsX), 1, 1);
        }

        void UnionGraphicsBuffers(GraphicsBuffer dst, GraphicsBuffer src)
        {
            m_CSSplatUtilities.SetBuffer((int)KernelIndices.OrBuffers, Props.SrcBuffer, src);
            m_CSSplatUtilities.SetBuffer((int)KernelIndices.OrBuffers, Props.DstBuffer, dst);
            m_CSSplatUtilities.SetInt(Props.BufferSize, dst.count);
            m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.OrBuffers, out uint gsX, out _, out _);
            m_CSSplatUtilities.Dispatch((int)KernelIndices.OrBuffers, (int)((dst.count+gsX-1)/gsX), 1, 1);
        }

        static float SortableUintToFloat(uint v)
        {
            uint mask = ((v >> 31) - 1) | 0x80000000u;
            return math.asfloat(v ^ mask);
        }

        public void UpdateEditCountsAndBounds()
        {
            if (m_GpuEditSelected == null)
            {
                editSelectedSplats = 0;
                editDeletedSplats = 0;
                editCutSplats = 0;
                editModified = false;
                editSelectedBounds = default;
                return;
            }

            m_CSSplatUtilities.SetBuffer((int)KernelIndices.InitEditData, Props.DstBuffer, m_GpuEditCountsBounds);
            m_CSSplatUtilities.Dispatch((int)KernelIndices.InitEditData, 1, 1, 1);

            using CommandBuffer cmb = new CommandBuffer();
            SetAssetDataOnCS(cmb, KernelIndices.UpdateEditData);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.UpdateEditData, Props.DstBuffer, m_GpuEditCountsBounds);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.BufferSize, m_GpuEditSelected.count);
            m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.UpdateEditData, out uint gsX, out _, out _);
            cmb.DispatchCompute(m_CSSplatUtilities, (int)KernelIndices.UpdateEditData, (int)((m_GpuEditSelected.count+gsX-1)/gsX), 1, 1);
            Graphics.ExecuteCommandBuffer(cmb);

            uint[] res = new uint[m_GpuEditCountsBounds.count];
            m_GpuEditCountsBounds.GetData(res);
            editSelectedSplats = res[0];
            editDeletedSplats = res[1];
            editCutSplats = res[2];
            Vector3 min = new Vector3(SortableUintToFloat(res[3]), SortableUintToFloat(res[4]), SortableUintToFloat(res[5]));
            Vector3 max = new Vector3(SortableUintToFloat(res[6]), SortableUintToFloat(res[7]), SortableUintToFloat(res[8]));
            Bounds bounds = default;
            bounds.SetMinMax(min, max);
            if (bounds.extents.sqrMagnitude < 0.01)
                bounds.extents = new Vector3(0.1f,0.1f,0.1f);
            editSelectedBounds = bounds;
        }

        void UpdateCutoutsBuffer()
        {
            int bufferSize = m_Cutouts?.Length ?? 0;
            if (bufferSize == 0)
                bufferSize = 1;
            if (m_GpuEditCutouts == null || m_GpuEditCutouts.count != bufferSize)
            {
                m_GpuEditCutouts?.Dispose();
                m_GpuEditCutouts = new GraphicsBuffer(GraphicsBuffer.Target.Structured, bufferSize, UnsafeUtility.SizeOf<GaussianCutout.ShaderData>()) { name = "GaussianCutouts" };
            }

            NativeArray<GaussianCutout.ShaderData> data = new(bufferSize, Allocator.Temp);
            if (m_Cutouts != null)
            {
                var matrix = transform.localToWorldMatrix;
                for (var i = 0; i < m_Cutouts.Length; ++i)
                {
                    data[i] = GaussianCutout.GetShaderData(m_Cutouts[i], matrix);
                }
            }

            m_GpuEditCutouts.SetData(data);
            data.Dispose();
        }

        bool EnsureEditingBuffers()
        {
            if (!HasValidAsset || !HasValidRenderSetup)
                return false;

            if (m_GpuEditSelected == null)
            {
                var target = GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource |
                             GraphicsBuffer.Target.CopyDestination;
                var size = (m_SplatCount + 31) / 32;
                m_GpuEditSelected = new GraphicsBuffer(target, size, 4) {name = "GaussianSplatSelected"};
                m_GpuEditSelectedMouseDown = new GraphicsBuffer(target, size, 4) {name = "GaussianSplatSelectedInit"};
                m_GpuEditDeleted = new GraphicsBuffer(target, size, 4) {name = "GaussianSplatDeleted"};
                m_GpuEditCountsBounds = new GraphicsBuffer(target, 3 + 6, 4) {name = "GaussianSplatEditData"}; // selected count, deleted bound, cut count, float3 min, float3 max
                ClearGraphicsBuffer(m_GpuEditSelected);
                ClearGraphicsBuffer(m_GpuEditSelectedMouseDown);
                ClearGraphicsBuffer(m_GpuEditDeleted);
            }
            return m_GpuEditSelected != null;
        }

        public void EditStoreSelectionMouseDown()
        {
            if (!EnsureEditingBuffers()) return;
            Graphics.CopyBuffer(m_GpuEditSelected, m_GpuEditSelectedMouseDown);
        }

        public void EditStorePosMouseDown()
        {
            if (m_GpuEditPosMouseDown == null)
            {
                m_GpuEditPosMouseDown = new GraphicsBuffer(m_GpuPosData.target | GraphicsBuffer.Target.CopyDestination, m_GpuPosData.count, m_GpuPosData.stride) {name = "GaussianSplatEditPosMouseDown"};
            }
            Graphics.CopyBuffer(m_GpuPosData, m_GpuEditPosMouseDown);
        }
        public void EditStoreOtherMouseDown()
        {
            if (m_GpuEditOtherMouseDown == null)
            {
                m_GpuEditOtherMouseDown = new GraphicsBuffer(m_GpuOtherData.target | GraphicsBuffer.Target.CopyDestination, m_GpuOtherData.count, m_GpuOtherData.stride) {name = "GaussianSplatEditOtherMouseDown"};
            }
            Graphics.CopyBuffer(m_GpuOtherData, m_GpuEditOtherMouseDown);
        }

        public void EditUpdateSelection(Vector2 rectMin, Vector2 rectMax, Camera cam, bool subtract)
        {
            if (!EnsureEditingBuffers()) return;

            Graphics.CopyBuffer(m_GpuEditSelectedMouseDown, m_GpuEditSelected);

            var tr = transform;
            Matrix4x4 matView = cam.worldToCameraMatrix;
            Matrix4x4 matProj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);
            Matrix4x4 matO2W = tr.localToWorldMatrix;
            Matrix4x4 matW2O = tr.worldToLocalMatrix;
            int screenW = cam.pixelWidth, screenH = cam.pixelHeight;
            Vector4 screenPar = new Vector4(screenW, screenH, 0, 0);
            Vector4 camPos = cam.transform.position;

            using var cmb = new CommandBuffer { name = "SplatSelectionUpdate" };
            SetAssetDataOnCS(cmb, KernelIndices.SelectionUpdate);

            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixVP, matProj * matView);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixMV, matView * matO2W);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixP, matProj);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, matO2W);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, matW2O);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.VecScreenParams, screenPar);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.VecWorldSpaceCameraPos, camPos);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_SelectionRect", new Vector4(rectMin.x, rectMax.y, rectMax.x, rectMin.y));
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SelectionMode, subtract ? 0 : 1);

            DispatchUtilsAndExecute(cmb, KernelIndices.SelectionUpdate, m_SplatCount);
            UpdateEditCountsAndBounds();
        }

        public void EditTranslateSelection(Vector3 localSpacePosDelta)
        {
            if (!EnsureEditingBuffers()) return;

            using var cmb = new CommandBuffer { name = "SplatTranslateSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.TranslateSelection);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionDelta, localSpacePosDelta);

            DispatchUtilsAndExecute(cmb, KernelIndices.TranslateSelection, m_SplatCount);
            UpdateEditCountsAndBounds();
            editModified = true;
        }

        public void EditRotateSelection(Vector3 localSpaceCenter, Matrix4x4 localToWorld, Matrix4x4 worldToLocal, Quaternion rotation)
        {
            if (!EnsureEditingBuffers()) return;
            if (m_GpuEditPosMouseDown == null || m_GpuEditOtherMouseDown == null) return; // should have captured initial state

            using var cmb = new CommandBuffer { name = "SplatRotateSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.RotateSelection);

            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.RotateSelection, Props.SplatPosMouseDown, m_GpuEditPosMouseDown);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.RotateSelection, Props.SplatOtherMouseDown, m_GpuEditOtherMouseDown);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionCenter, localSpaceCenter);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, localToWorld);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, worldToLocal);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionDeltaRot, new Vector4(rotation.x, rotation.y, rotation.z, rotation.w));

            DispatchUtilsAndExecute(cmb, KernelIndices.RotateSelection, m_SplatCount);
            UpdateEditCountsAndBounds();
            editModified = true;
        }


        public void EditScaleSelection(Vector3 localSpaceCenter, Matrix4x4 localToWorld, Matrix4x4 worldToLocal, Vector3 scale)
        {
            if (!EnsureEditingBuffers()) return;
            if (m_GpuEditPosMouseDown == null) return; // should have captured initial state

            using var cmb = new CommandBuffer { name = "SplatScaleSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.ScaleSelection);

            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.ScaleSelection, Props.SplatPosMouseDown, m_GpuEditPosMouseDown);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionCenter, localSpaceCenter);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, localToWorld);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, worldToLocal);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionDelta, scale);

            DispatchUtilsAndExecute(cmb, KernelIndices.ScaleSelection, m_SplatCount);
            UpdateEditCountsAndBounds();
            editModified = true;
        }

        public void EditDeleteSelected()
        {
            if (!EnsureEditingBuffers()) return;
            UnionGraphicsBuffers(m_GpuEditDeleted, m_GpuEditSelected);
            EditDeselectAll();
            UpdateEditCountsAndBounds();
            if (editDeletedSplats != 0)
                editModified = true;
        }

        public void EditSelectAll()
        {
            if (!EnsureEditingBuffers()) return;
            using var cmb = new CommandBuffer { name = "SplatSelectAll" };
            SetAssetDataOnCS(cmb, KernelIndices.SelectAll);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.SelectAll, Props.DstBuffer, m_GpuEditSelected);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.BufferSize, m_GpuEditSelected.count);
            DispatchUtilsAndExecute(cmb, KernelIndices.SelectAll, m_GpuEditSelected.count);
            UpdateEditCountsAndBounds();
        }

        public void EditDeselectAll()
        {
            if (!EnsureEditingBuffers()) return;
            ClearGraphicsBuffer(m_GpuEditSelected);
            UpdateEditCountsAndBounds();
        }

        public void EditInvertSelection()
        {
            if (!EnsureEditingBuffers()) return;

            using var cmb = new CommandBuffer { name = "SplatInvertSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.InvertSelection);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.InvertSelection, Props.DstBuffer, m_GpuEditSelected);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.BufferSize, m_GpuEditSelected.count);
            DispatchUtilsAndExecute(cmb, KernelIndices.InvertSelection, m_GpuEditSelected.count);
            UpdateEditCountsAndBounds();
        }

        public bool EditExportData(GraphicsBuffer dstData, bool bakeTransform)
        {
            if (!EnsureEditingBuffers()) return false;

            int flags = 0;
            var tr = transform;
            Quaternion bakeRot = tr.localRotation;
            Vector3 bakeScale = tr.localScale;

            if (bakeTransform)
                flags = 1;

            using var cmb = new CommandBuffer { name = "SplatExportData" };
            SetAssetDataOnCS(cmb, KernelIndices.ExportData);
            cmb.SetComputeIntParam(m_CSSplatUtilities, "_ExportTransformFlags", flags);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_ExportTransformRotation", new Vector4(bakeRot.x, bakeRot.y, bakeRot.z, bakeRot.w));
            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_ExportTransformScale", bakeScale);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, tr.localToWorldMatrix);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.ExportData, "_ExportBuffer", dstData);

            DispatchUtilsAndExecute(cmb, KernelIndices.ExportData, m_SplatCount);
            return true;
        }

        public void EditSetSplatCount(int newSplatCount)
        {
            if (newSplatCount <= 0 || newSplatCount > GaussianSplatAsset.kMaxSplats)
            {
                Debug.LogError($"Invalid new splat count: {newSplatCount}");
                return;
            }
            if (asset.chunkData != null)
            {
                Debug.LogError("Only splats with VeryHigh quality can be resized");
                return;
            }
            if (newSplatCount == splatCount)
                return;

            int posStride = (int)(asset.posData.dataSize / asset.splatCount);
            int otherStride = (int)(asset.otherData.dataSize / asset.splatCount);
            int shStride = (int) (asset.shData.dataSize / asset.splatCount);

            // create new GPU buffers
            var newPosData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, newSplatCount * posStride / 4, 4) { name = "GaussianPosData" };
            var newOtherData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, newSplatCount * otherStride / 4, 4) { name = "GaussianOtherData" };
            var newSHData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, newSplatCount * shStride / 4, 4) { name = "GaussianSHData" };

            // new texture is a RenderTexture so we can write to it from a compute shader
            var (texWidth, texHeight) = GaussianSplatAsset.CalcTextureSize(newSplatCount);
            var texFormat = GaussianSplatAsset.ColorFormatToGraphics(asset.colorFormat);
            var newColorData = new RenderTexture(texWidth, texHeight, texFormat, GraphicsFormat.None) { name = "GaussianColorData", enableRandomWrite = true };
            newColorData.Create();

            // selected/deleted buffers
            var selTarget = GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource | GraphicsBuffer.Target.CopyDestination;
            var selSize = (newSplatCount + 31) / 32;
            var newEditSelected = new GraphicsBuffer(selTarget, selSize, 4) {name = "GaussianSplatSelected"};
            var newEditSelectedMouseDown = new GraphicsBuffer(selTarget, selSize, 4) {name = "GaussianSplatSelectedInit"};
            var newEditDeleted = new GraphicsBuffer(selTarget, selSize, 4) {name = "GaussianSplatDeleted"};
            ClearGraphicsBuffer(newEditSelected);
            ClearGraphicsBuffer(newEditSelectedMouseDown);
            ClearGraphicsBuffer(newEditDeleted);

            var newGpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, newSplatCount, kGpuViewDataSize);
            InitSortBuffers(newSplatCount);

            // copy existing data over into new buffers
            EditCopySplats(transform, newPosData, newOtherData, newSHData, newColorData, newEditDeleted, newSplatCount, 0, 0, m_SplatCount);

            // use the new buffers and the new splat count
            m_GpuPosData.Dispose();
            m_GpuOtherData.Dispose();
            m_GpuSHData.Dispose();
            DestroyImmediate(m_GpuColorData);
            m_GpuView.Dispose();

            m_GpuEditSelected?.Dispose();
            m_GpuEditSelectedMouseDown?.Dispose();
            m_GpuEditDeleted?.Dispose();

            m_GpuPosData = newPosData;
            m_GpuOtherData = newOtherData;
            m_GpuSHData = newSHData;
            m_GpuColorData = newColorData;
            m_GpuView = newGpuView;
            m_GpuEditSelected = newEditSelected;
            m_GpuEditSelectedMouseDown = newEditSelectedMouseDown;
            m_GpuEditDeleted = newEditDeleted;

            DisposeBuffer(ref m_GpuEditPosMouseDown);
            DisposeBuffer(ref m_GpuEditOtherMouseDown);

            m_SplatCount = newSplatCount;
            editModified = true;
        }

        public void EditCopySplatsInto(GaussianSplatRenderer dst, int copySrcStartIndex, int copyDstStartIndex, int copyCount)
        {
            EditCopySplats(
                dst.transform,
                dst.m_GpuPosData, dst.m_GpuOtherData, dst.m_GpuSHData, dst.m_GpuColorData, dst.m_GpuEditDeleted,
                dst.splatCount,
                copySrcStartIndex, copyDstStartIndex, copyCount);
            dst.editModified = true;
        }

        public void EditCopySplats(
            Transform dstTransform,
            GraphicsBuffer dstPos, GraphicsBuffer dstOther, GraphicsBuffer dstSH, Texture dstColor,
            GraphicsBuffer dstEditDeleted,
            int dstSize,
            int copySrcStartIndex, int copyDstStartIndex, int copyCount)
        {
            if (!EnsureEditingBuffers()) return;

            Matrix4x4 copyMatrix = dstTransform.worldToLocalMatrix * transform.localToWorldMatrix;
            Quaternion copyRot = copyMatrix.rotation;
            Vector3 copyScale = copyMatrix.lossyScale;

            using var cmb = new CommandBuffer { name = "SplatCopy" };
            SetAssetDataOnCS(cmb, KernelIndices.CopySplats);

            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CopySplats, "_CopyDstPos", dstPos);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CopySplats, "_CopyDstOther", dstOther);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CopySplats, "_CopyDstSH", dstSH);
            cmb.SetComputeTextureParam(m_CSSplatUtilities, (int)KernelIndices.CopySplats, "_CopyDstColor", dstColor);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CopySplats, "_CopyDstEditDeleted", dstEditDeleted);

            cmb.SetComputeIntParam(m_CSSplatUtilities, "_CopyDstSize", dstSize);
            cmb.SetComputeIntParam(m_CSSplatUtilities, "_CopySrcStartIndex", copySrcStartIndex);
            cmb.SetComputeIntParam(m_CSSplatUtilities, "_CopyDstStartIndex", copyDstStartIndex);
            cmb.SetComputeIntParam(m_CSSplatUtilities, "_CopyCount", copyCount);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_CopyTransformRotation", new Vector4(copyRot.x, copyRot.y, copyRot.z, copyRot.w));
            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_CopyTransformScale", copyScale);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, "_CopyTransformMatrix", copyMatrix);

            DispatchUtilsAndExecute(cmb, KernelIndices.CopySplats, copyCount);
        }

        void DispatchUtilsAndExecute(CommandBuffer cmb, KernelIndices kernel, int count)
        {
            m_CSSplatUtilities.GetKernelThreadGroupSizes((int)kernel, out uint gsX, out _, out _);
            cmb.DispatchCompute(m_CSSplatUtilities, (int)kernel, (int)((count + gsX - 1)/gsX), 1, 1);
            Graphics.ExecuteCommandBuffer(cmb);
        }

        public GraphicsBuffer GpuEditDeleted => m_GpuEditDeleted;


        void SaveMatrixToFile(string fileName, Matrix4x4 matrix)
    {
        string filePath = fileName;

        try
        {
            using (StreamWriter writer = new StreamWriter(filePath))
            {
                for (int i = 0; i < 4; i++)
                {
                    writer.WriteLine($"{matrix[i, 0]}, {matrix[i, 1]}, {matrix[i, 2]}, {matrix[i, 3]}");
                }
            }
            Debug.Log($"{fileName} saved to {filePath}");
        }
        catch (IOException ex)
        {
            Debug.LogError($"Failed to save {fileName}: {ex.Message}");
        }
    }

    }



    
}
