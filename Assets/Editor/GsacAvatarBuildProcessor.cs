using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

// References the existing JSON asset in the temporary build scene. No Resources
// copy or saved-scene edit is needed to include the selected avatar in a player.
public sealed class GsacAvatarBuildProcessor : IProcessSceneWithReport
{
    public int callbackOrder => 0;

    public void OnProcessScene(Scene scene, BuildReport report)
    {
        if (!BuildPipeline.isBuildingPlayer)
            return;

        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (HahaImporter importer in root.GetComponentsInChildren<HahaImporter>(true))
            {
                // FileUtil expects Unity-style separators even for absolute paths.
                string fullPath = Path.GetFullPath(importer.GetJsonPath()).Replace('\\', '/');
                string assetPath = FileUtil.GetProjectRelativePath(fullPath);
                TextAsset stateDict = AssetDatabase.LoadAssetAtPath<TextAsset>(assetPath);
                if (stateDict != null && !string.Equals(stateDict.name,
                        Path.GetFileNameWithoutExtension(fullPath), StringComparison.Ordinal))
                    stateDict = null;

                var serializedImporter = new SerializedObject(importer);
                serializedImporter.FindProperty("bundledStateDict").objectReferenceValue = stateDict;
                serializedImporter.ApplyModifiedPropertiesWithoutUndo();

                if (stateDict == null && importer.isActiveAndEnabled &&
                    Resources.Load<TextAsset>(importer.GetResourcePath()) == null)
                {
                    throw new BuildFailedException(
                        $"Avatar '{importer.objectName}' in scene '{scene.path}' has no state dictionary. " +
                        $"Import '{importer.GetJsonPath()}' as a JSON TextAsset, or provide " +
                        $"Resources/{importer.GetResourcePath()}.json before building.");
                }
            }
        }
    }
}
