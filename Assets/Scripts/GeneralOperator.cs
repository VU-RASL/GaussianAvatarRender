using System;
using UnityEngine;

public enum GeneralBuildMode
{
    VR,
    AR,
}

[ExecuteAlways]
public sealed class GeneralOperator : MonoBehaviour
{
    [SerializeField] bool buildVR = true;
    [SerializeField] bool buildAR;
    [SerializeField] bool showHeadsetFps = false;
    [SerializeField] bool runPerformanceProtocol = false;
    [SerializeField] bool useQuestEnvironmentDepth = false;
    [SerializeField] bool useQuestTrackedHandOcclusion = true;

    public bool BuildVR => buildVR;
    public bool BuildAR => buildAR;
    public bool ShowHeadsetFps => showHeadsetFps;
    public bool RunPerformanceProtocol => runPerformanceProtocol;
    public bool UseQuestEnvironmentDepth => useQuestEnvironmentDepth;
    public bool UseQuestTrackedHandOcclusion => useQuestTrackedHandOcclusion;
    public GeneralBuildMode Mode => buildAR ? GeneralBuildMode.AR : GeneralBuildMode.VR;

    public void SetMode(GeneralBuildMode mode)
    {
        buildVR = mode == GeneralBuildMode.VR;
        buildAR = mode == GeneralBuildMode.AR;
        ApplySceneModeComponents();
    }

    public static GeneralBuildMode GetSceneMode()
    {
        var generalOperator = FindObjectOfType<GeneralOperator>(true);
        return generalOperator != null ? generalOperator.Mode : GeneralBuildMode.VR;
    }

    public static bool GetScenePerformanceProtocolEnabled()
    {
        var generalOperator = FindObjectOfType<GeneralOperator>(true);
        return generalOperator != null && generalOperator.RunPerformanceProtocol;
    }

    public static bool GetSceneHeadsetFpsEnabled()
    {
        var generalOperator = FindObjectOfType<GeneralOperator>(true);
        return generalOperator != null && generalOperator.ShowHeadsetFps;
    }

    public static bool GetSceneEnvironmentDepthEnabled()
    {
        var generalOperator = FindObjectOfType<GeneralOperator>(true);
        return generalOperator != null && generalOperator.Mode == GeneralBuildMode.AR &&
               generalOperator.UseQuestEnvironmentDepth;
    }

    public static bool GetSceneTrackedHandOcclusionEnabled()
    {
        var generalOperator = FindObjectOfType<GeneralOperator>(true);
        return generalOperator != null && generalOperator.Mode == GeneralBuildMode.AR &&
               generalOperator.UseQuestEnvironmentDepth && generalOperator.UseQuestTrackedHandOcclusion;
    }

    void Reset()
    {
        SetMode(GeneralBuildMode.VR);
    }

    void Awake()
    {
        ApplySceneModeComponents();
    }

    void OnEnable()
    {
        ApplySceneModeComponents();
    }

    void OnValidate()
    {
        if (!buildVR && !buildAR)
            SetMode(GeneralBuildMode.VR);
        else if (buildVR && buildAR)
            SetMode(GeneralBuildMode.AR);
        else
            ApplySceneModeComponents();
    }

    public void ApplySceneModeComponents()
    {
        bool enableAR = Mode == GeneralBuildMode.AR;
        bool enableARFoundation = enableAR && !QuestARBootstrap.IsOculusLoaderConfigured();
        SetComponentsEnabled("UnityEngine.XR.ARFoundation.ARSession, Unity.XR.ARFoundation", enableARFoundation);
        SetComponentsEnabled("UnityEngine.XR.ARFoundation.ARCameraManager, Unity.XR.ARFoundation", enableARFoundation);
        SetComponentsEnabled("UnityEngine.XR.ARFoundation.ARCameraBackground, Unity.XR.ARFoundation", enableARFoundation);
        SetComponentsEnabled("UnityEngine.XR.ARFoundation.AROcclusionManager, Unity.XR.ARFoundation", false);
        SetComponentsEnabled("UnityEngine.XR.ARFoundation.ARPlaneManager, Unity.XR.ARFoundation", enableARFoundation);
        SetComponentsEnabled("UnityEngine.XR.ARFoundation.ARRaycastManager, Unity.XR.ARFoundation", enableARFoundation);
        SetComponentsEnabled(typeof(ARGroundPlacement), enableAR);
        SetComponentsEnabled(typeof(ARPoseControlPanel), false);
    }

    static void SetComponentsEnabled(string typeName, bool enabled)
    {
        Type type = Type.GetType(typeName);
        if (type == null || !typeof(Behaviour).IsAssignableFrom(type))
            return;

        foreach (var behaviour in FindObjectsOfType(type, true))
            ((Behaviour)behaviour).enabled = enabled;
    }

    static void SetComponentsEnabled(Type type, bool enabled)
    {
        if (type == null || !typeof(Behaviour).IsAssignableFrom(type))
            return;

        foreach (var behaviour in FindObjectsOfType(type, true))
            ((Behaviour)behaviour).enabled = enabled;
    }
}
