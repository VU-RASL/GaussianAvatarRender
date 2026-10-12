using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class AvatarRoomMeshEditor
{
    const string ScenePath = "Assets/Scenes/GSAvatarRenderer.unity";

    [MenuItem("Gaussian Splatting/Aim 2/Add Saved Room Mesh to Avatar Scene")]
    public static void Configure()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Stop Play mode before changing the scene.");
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        var scene = EditorSceneManager.OpenScene(ScenePath);
        var op = UnityEngine.Object.FindObjectOfType<GeneralOperator>(true);
        if (op == null || op.Mode != GeneralBuildMode.AR || !op.UseQuestEnvironmentDepth)
            throw new BuildFailedException("Use the existing avatar Oculus AR profile before reading the saved mesh.");
        var reader = UnityEngine.Object.FindObjectOfType<QuestSavedRoomMesh>(true);
        if (reader == null) reader = new GameObject("Aim 2 Saved Room Mesh").AddComponent<QuestSavedRoomMesh>();
        const string materialPath = "Assets/Shaders/AvatarRoomMesh.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
        if (material == null)
        {
            var shader = Shader.Find("GSAC/Saved Room Wireframe");
            if (shader == null) throw new BuildFailedException("Saved room mesh shader was not imported.");
            material = new Material(shader);
            AssetDatabase.CreateAsset(material, materialPath);
        }
        var serialized = new SerializedObject(reader);
        serialized.FindProperty("meshMaterial").objectReferenceValue = material;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        GeneralOperatorOpenXRUtility.ApplyProfile(op);
        var config = OVRProjectConfig.CachedProjectConfig;
        config.sceneSupport = OVRProjectConfig.FeatureSupport.Required;
        OVRProjectConfig.CommitProjectConfig(config);
        AssetDatabase.SaveAssets();
        Debug.Log("AVATAR_ROOM_MESH_CONFIGURED existing scene=" + ScenePath);
    }

    [MenuItem("Gaussian Splatting/Aim 2/Build Avatar with Mesh Visibility Toggle")]
    public static void Build()
    {
        Aim2ChairBaselineEditor.Validate();
        Configure();
        string previousVersion = PlayerSettings.bundleVersion;
        int previousCode = PlayerSettings.Android.bundleVersionCode;
        AndroidSdkVersions previousTarget = PlayerSettings.Android.targetSdkVersion;
        try
        {
            // Keep the original avatar package and signing; install over the mesh reader build.
            PlayerSettings.Android.bundleVersionCode = Math.Max(previousCode, 14);
            PlayerSettings.bundleVersion = "0.14";
            PlayerSettings.Android.targetSdkVersion = (AndroidSdkVersions)32;
            Directory.CreateDirectory("Builds");
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
                scenes = new[] { ScenePath },
                locationPathName = "Builds/avatar-mesh-toggle.apk",
                target = BuildTarget.Android,
                options = BuildOptions.Development
            });
            Debug.Log("AVATAR_ROOM_MESH_BUILD " + report.summary.result + " bytes=" + report.summary.totalSize);
            if (report.summary.result != BuildResult.Succeeded) throw new BuildFailedException("Avatar mesh build failed.");
        }
        finally
        {
            PlayerSettings.bundleVersion = previousVersion;
            PlayerSettings.Android.bundleVersionCode = previousCode;
            PlayerSettings.Android.targetSdkVersion = previousTarget;
        }
    }
}
