using System.Collections;
using System.Collections.Generic;

using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering;

public class ScreenSpaceOutlines : ScriptableRendererFeature 
{

    [System.Serializable]
    class ScreenSpaceOutlineSettings {

        [Header("General Outline Settings")]
        public Color outlineColor = Color.black;
        [Range(0.0f, 20.0f)]
        public float outlineScale = 1.0f;
        
        [Header("Depth Settings")]
        [Range(0.0f, 100.0f)]
        public float depthThreshold = 1.5f;
        [Range(0.0f, 500.0f)]
        public float robertsCrossMultiplier = 100.0f;

        [Header("Normal Settings")]
        [Range(0.0f, 1.0f)]
        public float normalThreshold = 0.4f;

        [Header("Depth Normal Relation Settings")]
        [Range(0.0f, 2.0f)]
        public float steepAngleThreshold = 0.2f;
        [Range(0.0f, 500.0f)]
        public float steepAngleMultiplier = 25.0f;

    }

    [System.Serializable]
    class ViewSpaceNormalsTextureSettings {

        [Header("General Scene View Space Normal Texture Settings")]
        public RenderTextureFormat colorFormat;
        public int depthBufferBits = 16;
        public FilterMode filterMode;
        public Color backgroundColor = Color.black;

        [Header("View Space Normal Texture Object Draw Settings")]
        public PerObjectData perObjectData;
        public bool enableDynamicBatching;
        public bool enableInstancing;

    }

    class ViewSpaceNormalsTexturePass : ScriptableRenderPass
    {
        const string ProfilerTag = "Outlines/View-Space Normals Pass";
        
        ViewSpaceNormalsTextureSettings normalsTextureSettings;
        FilteringSettings filteringSettings;
        FilteringSettings occluderFilteringSettings;

        readonly List<ShaderTagId> shaderTagIdList;
        readonly Material normalsMaterial;
        readonly Material occludersMaterial;

        readonly RenderTargetHandle normals;

        public ViewSpaceNormalsTexturePass(RenderPassEvent renderPassEvent, LayerMask layerMask, LayerMask occluderLayerMask, ViewSpaceNormalsTextureSettings settings) {
            this.renderPassEvent = renderPassEvent;
            this.normalsTextureSettings = settings;
            filteringSettings = new FilteringSettings(RenderQueueRange.opaque, layerMask);
            occluderFilteringSettings = new FilteringSettings(RenderQueueRange.opaque, occluderLayerMask);

            shaderTagIdList = new List<ShaderTagId> {
                new ShaderTagId("UniversalForward"),
                new ShaderTagId("UniversalForwardOnly"),
                new ShaderTagId("LightweightForward"),
                new ShaderTagId("SRPDefaultUnlit")
            };

            normals.Init("_SceneViewSpaceNormals");
            normalsMaterial = new Material(Shader.Find("Hidden/ViewSpaceNormals"));

            occludersMaterial = new Material(Shader.Find("Hidden/UnlitColor"));
            occludersMaterial.SetColor("_Color", normalsTextureSettings.backgroundColor);
        }

        public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor) {
            RenderTextureDescriptor normalsTextureDescriptor = cameraTextureDescriptor;
            normalsTextureDescriptor.colorFormat = normalsTextureSettings.colorFormat;
            normalsTextureDescriptor.depthBufferBits = normalsTextureSettings.depthBufferBits;
            cmd.GetTemporaryRT(normals.id, normalsTextureDescriptor, normalsTextureSettings.filterMode);

            ConfigureTarget(normals.Identifier());
            ConfigureClear(ClearFlag.All, normalsTextureSettings.backgroundColor);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) {
            if (!normalsMaterial || !occludersMaterial)
                return;

            CommandBuffer cmd = CommandBufferPool.Get();
            using (new ProfilingScope(cmd, new ProfilingSampler(ProfilerTag))) {
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

                DrawingSettings drawSettings = CreateDrawingSettings(shaderTagIdList, ref renderingData, renderingData.cameraData.defaultOpaqueSortFlags);
                drawSettings.perObjectData = normalsTextureSettings.perObjectData;
                drawSettings.enableDynamicBatching = normalsTextureSettings.enableDynamicBatching;
                drawSettings.enableInstancing = normalsTextureSettings.enableInstancing;
                drawSettings.overrideMaterial = normalsMaterial;

                DrawingSettings occluderSettings = drawSettings;
                occluderSettings.overrideMaterial = occludersMaterial;

                context.DrawRenderers(renderingData.cullResults, ref drawSettings, ref filteringSettings);
                context.DrawRenderers(renderingData.cullResults, ref occluderSettings, ref occluderFilteringSettings);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd) {
            cmd.ReleaseTemporaryRT(normals.id);
        }

    }

    class ScreenSpaceOutlinePass : ScriptableRenderPass {

        readonly Material screenSpaceOutlineMaterial;

        RenderTargetIdentifier cameraColorTarget;

        RenderTargetIdentifier temporaryBuffer;
        int temporaryBufferID = Shader.PropertyToID("_TemporaryBuffer");

        public ScreenSpaceOutlinePass(RenderPassEvent renderPassEvent, ScreenSpaceOutlineSettings settings) {
            this.renderPassEvent = renderPassEvent;

            screenSpaceOutlineMaterial = new Material(Shader.Find("Hidden/Outlines"));
            screenSpaceOutlineMaterial.SetColor("_OutlineColor", settings.outlineColor);
            screenSpaceOutlineMaterial.SetFloat("_OutlineScale", settings.outlineScale);

            screenSpaceOutlineMaterial.SetFloat("_DepthThreshold", settings.depthThreshold);
            screenSpaceOutlineMaterial.SetFloat("_RobertsCrossMultiplier", settings.robertsCrossMultiplier);

            screenSpaceOutlineMaterial.SetFloat("_NormalThreshold", settings.normalThreshold);

            screenSpaceOutlineMaterial.SetFloat("_SteepAngleThreshold", settings.steepAngleThreshold);
            screenSpaceOutlineMaterial.SetFloat("_SteepAngleMultiplier", settings.steepAngleMultiplier);
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData) {
            RenderTextureDescriptor temporaryTargetDescriptor = renderingData.cameraData.cameraTargetDescriptor;
            temporaryTargetDescriptor.depthBufferBits = 0;
            cmd.GetTemporaryRT(temporaryBufferID, temporaryTargetDescriptor, FilterMode.Bilinear);
            temporaryBuffer = new RenderTargetIdentifier(temporaryBufferID);

            cameraColorTarget = renderingData.cameraData.renderer.cameraColorTarget;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) {
            if (!screenSpaceOutlineMaterial)
                return;

            CommandBuffer cmd = CommandBufferPool.Get();
            using (new ProfilingScope(cmd, new ProfilingSampler("ScreenSpaceOutlines"))) {

                Blit(cmd, cameraColorTarget, temporaryBuffer);
                Blit(cmd, temporaryBuffer, cameraColorTarget, screenSpaceOutlineMaterial);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd) {
            cmd.ReleaseTemporaryRT(temporaryBufferID);
        }

    }

    [SerializeField] RenderPassEvent renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
    [SerializeField] LayerMask outlinesLayerMask;
    [SerializeField] LayerMask outlinesOccluderLayerMask;
    
    [SerializeField] ScreenSpaceOutlineSettings outlineSettings = new ScreenSpaceOutlineSettings();
    [SerializeField] ViewSpaceNormalsTextureSettings viewSpaceNormalsTextureSettings = new ViewSpaceNormalsTextureSettings();

    ViewSpaceNormalsTexturePass viewSpaceNormalsTexturePass;
    ScreenSpaceOutlinePass screenSpaceOutlinePass;
    
    public override void Create() {
        if (renderPassEvent < RenderPassEvent.BeforeRenderingPrePasses)
        {
            Debug.LogWarning($"This pass cannot be run earlier than " +
                $"{RenderPassEvent.BeforeRenderingPrePasses}");
            renderPassEvent = RenderPassEvent.BeforeRenderingPrePasses;
        }

        viewSpaceNormalsTexturePass = new ViewSpaceNormalsTexturePass(renderPassEvent, outlinesLayerMask, outlinesOccluderLayerMask, viewSpaceNormalsTextureSettings);
        screenSpaceOutlinePass = new ScreenSpaceOutlinePass(renderPassEvent, outlineSettings);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData) {
        renderer.EnqueuePass(viewSpaceNormalsTexturePass);
        renderer.EnqueuePass(screenSpaceOutlinePass);
    }

}
