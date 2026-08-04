using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(GsacFaceTrajectoryPlayer))]
public sealed class GsacFaceTrajectoryPlayerEditor : Editor
{
    static readonly string[] EmotionNames =
    {
        "happiness",
        "sadness",
        "anger",
        "fear",
        "surprise",
        "disgust"
    };

    SerializedProperty faceControllerProperty;
    SerializedProperty trajectoryJsonProperty;
    SerializedProperty trajectoryFilePathProperty;
    SerializedProperty emotionNameProperty;
    SerializedProperty loadOnStartProperty;
    SerializedProperty playOnStartProperty;
    SerializedProperty loopProperty;
    SerializedProperty playbackSpeedProperty;
    SerializedProperty useUnscaledTimeProperty;
    SerializedProperty statusProperty;

    void OnEnable()
    {
        faceControllerProperty = serializedObject.FindProperty("faceController");
        trajectoryJsonProperty = serializedObject.FindProperty("trajectoryJson");
        trajectoryFilePathProperty = serializedObject.FindProperty("trajectoryFilePath");
        emotionNameProperty = serializedObject.FindProperty("emotionName");
        loadOnStartProperty = serializedObject.FindProperty("loadOnStart");
        playOnStartProperty = serializedObject.FindProperty("playOnStart");
        loopProperty = serializedObject.FindProperty("loop");
        playbackSpeedProperty = serializedObject.FindProperty("playbackSpeed");
        useUnscaledTimeProperty = serializedObject.FindProperty("useUnscaledTime");
        statusProperty = serializedObject.FindProperty("status");
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        EditorGUILayout.PropertyField(faceControllerProperty);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Mira Emotion Loop", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Select one emotion checkbox, then enter Play Mode. The selected trajectory loops until you stop Play Mode or uncheck it.", MessageType.Info);

        string selectedEmotion = emotionNameProperty.stringValue;
        bool autoPlay = playOnStartProperty.boolValue;
        string clickedEmotion = null;

        for (int i = 0; i < EmotionNames.Length; ++i)
        {
            string emotion = EmotionNames[i];
            bool selected = autoPlay && selectedEmotion == emotion;
            bool nextSelected = EditorGUILayout.ToggleLeft(ObjectNames.NicifyVariableName(emotion), selected);
            if (nextSelected != selected)
                clickedEmotion = nextSelected ? emotion : string.Empty;
        }

        if (clickedEmotion != null)
        {
            if (string.IsNullOrEmpty(clickedEmotion))
            {
                playOnStartProperty.boolValue = false;
                if (Application.isPlaying)
                    StopPlayer();
            }
            else
            {
                emotionNameProperty.stringValue = clickedEmotion;
                loadOnStartProperty.boolValue = true;
                playOnStartProperty.boolValue = true;
                loopProperty.boolValue = true;
                if (Application.isPlaying)
                    PlaySelectedEmotion(clickedEmotion);
            }
        }

        EditorGUILayout.Space();
        EditorGUILayout.PropertyField(trajectoryJsonProperty);
        EditorGUILayout.PropertyField(trajectoryFilePathProperty);
        EditorGUILayout.PropertyField(loadOnStartProperty);
        EditorGUILayout.PropertyField(playOnStartProperty);
        EditorGUILayout.PropertyField(loopProperty);
        EditorGUILayout.PropertyField(playbackSpeedProperty);
        EditorGUILayout.PropertyField(useUnscaledTimeProperty);
        EditorGUILayout.PropertyField(statusProperty);

        serializedObject.ApplyModifiedProperties();
    }

    void PlaySelectedEmotion(string emotion)
    {
        foreach (Object selectedTarget in targets)
        {
            var player = selectedTarget as GsacFaceTrajectoryPlayer;
            if (player == null)
                continue;

            player.LoadTrajectory();
            player.SelectEmotion(emotion);
            player.Play();
            EditorUtility.SetDirty(player);
        }
    }

    void StopPlayer()
    {
        foreach (Object selectedTarget in targets)
        {
            var player = selectedTarget as GsacFaceTrajectoryPlayer;
            if (player == null)
                continue;

            player.StopAndReset();
            EditorUtility.SetDirty(player);
        }
    }
}
