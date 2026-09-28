#if UNITY_EDITOR
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using UnityEditor;
using UnityEditor.Android;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using Unity.XR.Oculus;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;

[CustomEditor(typeof(GeneralOperator))]
public sealed class GeneralOperatorEditor : Editor
{
    SerializedProperty buildVR;
    SerializedProperty buildAR;
    SerializedProperty showHeadsetFps;
    SerializedProperty runPerformanceProtocol;
    SerializedProperty useQuestEnvironmentDepth;

    void OnEnable()
    {
        buildVR = serializedObject.FindProperty("buildVR");
        buildAR = serializedObject.FindProperty("buildAR");
        showHeadsetFps = serializedObject.FindProperty("showHeadsetFps");
        runPerformanceProtocol = serializedObject.FindProperty("runPerformanceProtocol");
        useQuestEnvironmentDepth = serializedObject.FindProperty("useQuestEnvironmentDepth");
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        bool oldVR = buildVR.boolValue;
        bool oldAR = buildAR.boolValue;

        EditorGUILayout.LabelField("Build Mode", EditorStyles.boldLabel);
        bool newVR = EditorGUILayout.ToggleLeft("VR", oldVR);
        bool newAR = EditorGUILayout.ToggleLeft("AR", oldAR);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Headset Debug", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(showHeadsetFps, new GUIContent("Show FPS count"));

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Performance Protocol", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(runPerformanceProtocol, new GUIContent("Run on app start"));

        GeneralBuildMode? requestedMode = null;
        if (newVR != oldVR && newVR)
            requestedMode = GeneralBuildMode.VR;
        else if (newAR != oldAR && newAR)
            requestedMode = GeneralBuildMode.AR;
        else if (!newVR && !newAR)
            requestedMode = GeneralBuildMode.VR;

        if (requestedMode.HasValue)
        {
            buildVR.boolValue = requestedMode.Value == GeneralBuildMode.VR;
            buildAR.boolValue = requestedMode.Value == GeneralBuildMode.AR;
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Real-world Occlusion", EditorStyles.boldLabel);
        using (new EditorGUI.DisabledScope(!buildAR.boolValue || EditorApplication.isPlaying))
        {
            EditorGUILayout.PropertyField(useQuestEnvironmentDepth, new GUIContent(
                "Use Quest environment depth (experimental)",
                "AR builds only. Uses the installed Oculus XR 4.2 depth provider instead of OpenXR. " +
                "Placement uses the XR floor height rather than detected AR planes. " +
                "Selects Multiview, which the Oculus environment depth provider requires. " +
                "Verify both eyes and performance on the headset."));
        }
        if (buildAR.boolValue && useQuestEnvironmentDepth.boolValue)
        {
            EditorGUILayout.HelpBox(
                "Experimental Oculus XR depth profile: real hands and surfaces can occlude supported objects. " +
                "AR plane detection is replaced by XR floor placement. Oculus Multiview is required because " +
                "the depth provider cannot produce depth frames in Multi Pass. Check both eyes and compare headset " +
                "performance before relying on it. Turn this off and rebuild to return to the OpenXR baseline.",
                MessageType.Warning);
        }

        if (serializedObject.ApplyModifiedProperties())
        {
            foreach (Object selectedTarget in targets)
            {
                var generalOperator = (GeneralOperator)selectedTarget;
                if (!EditorApplication.isPlaying)
                    GeneralOperatorOpenXRUtility.ApplyProfile(generalOperator);
                generalOperator.ApplySceneModeComponents();
                EditorUtility.SetDirty(generalOperator);
                EditorSceneManager.MarkSceneDirty(generalOperator.gameObject.scene);
            }
        }
    }
}

public sealed class GeneralOperatorBuildProcessor : IPreprocessBuildWithReport, IProcessSceneWithReport
{
    // Select the provider before XR Management and Oculus serialize their settings.
    public int callbackOrder => -1000;

    public void OnPreprocessBuild(BuildReport report)
    {
        if (report.summary.platform != BuildTarget.Android)
            return;

        GeneralOperator generalOperator = GeneralOperatorOpenXRUtility.FindLoadedOperator();
        GeneralOperatorOpenXRUtility.ApplyProfile(generalOperator);
        GeneralOperatorOpenXRUtility.EnsureShaderIncluded("GSAC/Headset FPS Overlay");
        GeneralBuildMode mode = generalOperator != null ? generalOperator.Mode : GeneralBuildMode.VR;
        Debug.Log($"General Operator build mode for Android: {mode}");
    }

    public void OnProcessScene(Scene scene, BuildReport report)
    {
        if (!BuildPipeline.isBuildingPlayer || report == null || report.summary.platform != BuildTarget.Android)
            return;

        XRGeneralSettings settings = XRGeneralSettingsPerBuildTarget
            .XRGeneralSettingsForBuildTarget(BuildTargetGroup.Android);
        XRManagerSettings manager = settings != null ? settings.Manager : null;
        string[] loaders = manager != null
            ? manager.activeLoaders.Select(loader => loader != null ? loader.GetType().FullName : "<null>").ToArray()
            : new string[0];

        // Validate the actual serialized build scene, not the currently open
        // editor scene used by preprocessing. Unsaved flags must never select
        // one provider while the player silently loads another scene profile.
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (GeneralOperator generalOperator in root.GetComponentsInChildren<GeneralOperator>(true))
            {
                bool depthRequested = generalOperator.Mode == GeneralBuildMode.AR &&
                                      generalOperator.UseQuestEnvironmentDepth;
                string expected = depthRequested
                    ? "Unity.XR.Oculus.OculusLoader" : "UnityEngine.XR.OpenXR.OpenXRLoader";
                if (loaders.Length != 1 || loaders[0] != expected)
                {
                    throw new BuildFailedException(
                        $"GSAC build profile mismatch in scene '{scene.path}' on '{generalOperator.name}': " +
                        $"serialized mode={generalOperator.Mode}, environment depth requested={depthRequested}, " +
                        $"requires exactly one Android loader '{expected}', but selected [{string.Join(", ", loaders)}]. " +
                        "Save the intended General Operator settings in the scene, apply that build profile, " +
                        "and rebuild. The build was stopped without saving or modifying the scene.");
                }

                if (depthRequested &&
                    (!EditorBuildSettings.TryGetConfigObject<OculusSettings>("Unity.XR.Oculus.Settings", out var oculusSettings) ||
                     oculusSettings == null ||
                     oculusSettings.m_StereoRenderingModeAndroid != OculusSettings.StereoRenderingModeAndroid.Multiview))
                {
                    throw new BuildFailedException(
                        $"GSAC environment depth in scene '{scene.path}' requires Oculus Multiview. " +
                        "The Oculus XR depth provider cannot produce depth frames in Multi Pass. " +
                        "Apply the General Operator build profile and rebuild.");
                }
            }
        }
    }
}

public sealed class QuestAndroidManifestPostprocessor : IPostGenerateGradleAndroidProject
{
    const string AndroidNamespace = "http://schemas.android.com/apk/res/android";

    public int callbackOrder => 10000;

    public void OnPostGenerateGradleAndroidProject(string path)
    {
        string manifestPath = Path.Combine(path, "src", "main", "AndroidManifest.xml");
        if (!File.Exists(manifestPath))
            return;

        var document = new XmlDocument();
        document.Load(manifestPath);

        XmlElement application = document.SelectSingleNode("/manifest/application") as XmlElement;
        if (application == null)
            return;

        bool changed = false;
        if (application.HasAttribute("label", AndroidNamespace))
        {
            application.RemoveAttribute("label", AndroidNamespace);
            changed = true;
        }

        if (application.HasAttribute("icon", AndroidNamespace))
        {
            application.RemoveAttribute("icon", AndroidNamespace);
            changed = true;
        }

        if (changed)
            document.Save(manifestPath);
    }
}

public static class GeneralOperatorOpenXRUtility
{
    const string MetaQuestFeatureId = "com.unity.openxr.feature.metaquest";
    const string MetaArSessionFeatureId = "com.unity.openxr.feature.arfoundation-meta-session";
    const string MetaArCameraFeatureId = "com.unity.openxr.feature.arfoundation-meta-camera";
    const string MetaArPlaneFeatureId = "com.unity.openxr.feature.arfoundation-meta-plane";
    const string MetaArRaycastFeatureId = "com.unity.openxr.feature.arfoundation-meta-raycast";
    const string MetaArOcclusionFeatureId = "com.unity.openxr.feature.arfoundation-meta-occlusion";
    const string UnityHandTrackingFeatureId = "com.unity.openxr.feature.input.handtracking";

    public static GeneralBuildMode FindLoadedOperatorMode()
    {
        GeneralOperator generalOperator = FindLoadedOperator();
        return generalOperator != null ? generalOperator.Mode : GeneralBuildMode.VR;
    }

    public static GeneralOperator FindLoadedOperator()
    {
        foreach (GeneralOperator generalOperator in Resources.FindObjectsOfTypeAll<GeneralOperator>())
        {
            if (generalOperator == null)
                continue;

            if (!generalOperator.gameObject.scene.IsValid())
                continue;

            return generalOperator;
        }

        return null;
    }

    [MenuItem("Gaussian Splatting/Apply General Operator Build Mode")]
    public static void ApplyCurrentMode()
    {
        ApplyProfile(FindLoadedOperator());
    }

    public static void ApplyProfile(GeneralOperator generalOperator)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            return;

        GeneralBuildMode mode = generalOperator != null ? generalOperator.Mode : GeneralBuildMode.VR;
        bool useDepth = mode == GeneralBuildMode.AR && generalOperator != null &&
                        generalOperator.UseQuestEnvironmentDepth;
        SelectAndroidLoader(useDepth);
        if (useDepth)
        {
            ConfigureOculusDepthSettings();
            EnsureShaderIncluded("GSAC/Quest Depth Occluded Color");
            EnsureShaderIncluded("Gaussian Splatting/Render Splats");
            Debug.LogWarning("GSAC experimental environment depth uses Oculus XR 4.2 with required Multiview rendering. " +
                             "The Oculus depth provider does not support Multi Pass. Validate both eyes, " +
                             "real-world occlusion and performance on the headset; AR placement uses XR floor height.");
        }
        else
        {
            // Keep the existing OpenXR feature configuration workflow for baseline builds.
            ApplyMode(mode);
        }
    }

    static void SelectAndroidLoader(bool useDepth)
    {
        XRGeneralSettings settings = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Android);
        XRManagerSettings manager = settings != null ? settings.Manager : null;
        if (manager == null)
            throw new BuildFailedException("GSAC could not find Android XR Management settings. Configure XR Plug-in Management first.");

        string loaderType = useDepth ? "Unity.XR.Oculus.OculusLoader" : "UnityEngine.XR.OpenXR.OpenXRLoader";
        // Assign first so a missing provider cannot leave the project without its previous loader.
        if (!manager.activeLoaders.Any(loader => loader != null && loader.GetType().FullName == loaderType) &&
            !XRPackageMetadataStore.AssignLoader(manager, loaderType, BuildTargetGroup.Android))
            throw new BuildFailedException("GSAC could not select the Android XR loader: " + loaderType);

        foreach (XRLoader loader in manager.activeLoaders.ToArray())
        {
            if (loader != null && loader.GetType().FullName != loaderType &&
                !XRPackageMetadataStore.RemoveLoader(manager, loader.GetType().FullName, BuildTargetGroup.Android))
                throw new BuildFailedException("GSAC could not remove the conflicting Android XR loader: " + loader.GetType().FullName);
        }
        if (manager.activeLoaders.Count != 1 || manager.activeLoaders[0] == null ||
            manager.activeLoaders[0].GetType().FullName != loaderType)
            throw new BuildFailedException("GSAC Android XR loader selection did not produce a single provider: " + loaderType);

        EditorUtility.SetDirty(manager);
        AssetDatabase.SaveAssets();
    }

    static void ConfigureOculusDepthSettings()
    {
        const string settingsKey = "Unity.XR.Oculus.Settings";
        if (!EditorBuildSettings.TryGetConfigObject<OculusSettings>(settingsKey, out var settings) || settings == null)
        {
            // Recover a dangling configuration reference using the existing asset's GUID.
            settings = AssetDatabase.LoadAssetAtPath<OculusSettings>("Assets/XR/Settings/OculusSettings.asset");
            if (settings == null)
                throw new BuildFailedException("GSAC could not find Assets/XR/Settings/OculusSettings.asset.");
            EditorBuildSettings.AddConfigObject(settingsKey, settings, true);
        }

        // The Oculus provider fails to create environment depth textures in Multi Pass.
        settings.m_StereoRenderingModeAndroid = OculusSettings.StereoRenderingModeAndroid.Multiview;
        // Preserve each eye's native projection; symmetric projection is an optional optimization.
        settings.SymmetricProjection = false;
        // Gaussian color rendering reuses opaque camera depth after switching color targets.
        // Oculus Vulkan discard optimization may invalidate that depth at the pass boundary.
        settings.OptimizeBufferDiscards = false;
        EditorUtility.SetDirty(settings);
        AssetDatabase.SaveAssets();
    }

    public static void ApplyMode(GeneralBuildMode mode)
    {
        UnityEditor.XR.OpenXR.Features.FeatureHelpers.RefreshFeatures(BuildTargetGroup.Android);

        var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
        if (settings == null)
            return;

        bool changed = false;
        foreach (OpenXRFeature feature in settings.GetFeatures())
        {
            if (feature == null)
                continue;

            string featureId = GetInternalString(feature, "featureIdInternal");
            string extensionStrings = GetInternalString(feature, "openxrExtensionStrings");
            bool arFeature = IsArFeature(featureId, extensionStrings);
            bool occlusionFeature = IsQuestOcclusionFeature(featureId, extensionStrings);
            bool shouldEnable =
                featureId == MetaQuestFeatureId ||
                (mode == GeneralBuildMode.AR && arFeature && !occlusionFeature);
            bool shouldDisable =
                (mode == GeneralBuildMode.VR && arFeature) ||
                occlusionFeature;

            if (shouldEnable && !feature.enabled)
            {
                feature.enabled = true;
                EditorUtility.SetDirty(feature);
                changed = true;
            }
            else if (shouldDisable && feature.enabled)
            {
                feature.enabled = false;
                EditorUtility.SetDirty(feature);
                changed = true;
            }
        }

        if (changed)
        {
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
        }
    }

    public static void EnsureShaderIncluded(string shaderName)
    {
        Shader shader = Shader.Find(shaderName);
        if (shader == null)
        {
            Debug.LogWarning($"Could not find shader '{shaderName}' to include in the Android build.");
            return;
        }

        UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
        UnityEngine.Object graphicsSettings = assets != null && assets.Length > 0 ? assets[0] : null;
        if (graphicsSettings == null)
        {
            Debug.LogWarning($"Could not load GraphicsSettings to include shader '{shaderName}'.");
            return;
        }

        var serializedSettings = new SerializedObject(graphicsSettings);
        SerializedProperty shaders = serializedSettings.FindProperty("m_AlwaysIncludedShaders");
        if (shaders == null || !shaders.isArray)
        {
            Debug.LogWarning($"Could not find always-included shader list for '{shaderName}'.");
            return;
        }

        for (int i = 0; i < shaders.arraySize; ++i)
        {
            if (shaders.GetArrayElementAtIndex(i).objectReferenceValue == shader)
                return;
        }

        int index = shaders.arraySize;
        shaders.InsertArrayElementAtIndex(index);
        shaders.GetArrayElementAtIndex(index).objectReferenceValue = shader;
        serializedSettings.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(graphicsSettings);
        AssetDatabase.SaveAssets();
    }

    static string GetInternalString(OpenXRFeature feature, string fieldName)
    {
        FieldInfo field = typeof(OpenXRFeature).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(feature) as string ?? string.Empty;
    }

    static bool IsArFeature(string featureId, string extensionStrings)
    {
        return featureId == MetaArSessionFeatureId ||
               featureId == MetaArCameraFeatureId ||
               featureId == MetaArPlaneFeatureId ||
               featureId == MetaArRaycastFeatureId ||
               featureId == MetaArOcclusionFeatureId ||
               IsQuestHandTrackingFeature(featureId, extensionStrings) ||
               IsOcclusionFeature(featureId);
    }

    static bool IsQuestOcclusionFeature(string featureId, string extensionStrings)
    {
        return featureId == MetaArOcclusionFeatureId ||
               IsOcclusionFeature(featureId) ||
               (!string.IsNullOrEmpty(extensionStrings) &&
                extensionStrings.IndexOf("occlusion", System.StringComparison.OrdinalIgnoreCase) >= 0);
    }

    static bool IsQuestHandTrackingFeature(string featureId, string extensionStrings)
    {
        return featureId == UnityHandTrackingFeatureId &&
               !string.IsNullOrEmpty(extensionStrings) &&
               extensionStrings.IndexOf("XR_EXT_hand_tracking", System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    static bool IsOcclusionFeature(string featureId)
    {
        return !string.IsNullOrEmpty(featureId) &&
               featureId.IndexOf("occlusion", System.StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
#endif
