using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

// Uses the captured real mesh to exercise the same chair initialization path
// before packaging. No headset, Scene permission, or synthetic room is needed.
public static class Aim2ChairBaselineEditor
{
    [MenuItem("Gaussian Splatting/Aim 2/Validate Chair Baseline")]
    public static void Validate()
    {
        const string roomPath = "Assets/Aim2/ChairBaseline/ReferenceRoomMesh.json";
        const string chairPath = "Assets/Resources/Aim2/Chair_01.json";
        if (!File.Exists(roomPath) || !File.Exists(chairPath))
            throw new BuildFailedException("The captured room mesh and Chair_01 reference are required.");
        string meshJson = File.ReadAllText(roomPath);
        string chairJson = File.ReadAllText(chairPath);
        bool valid = QuestSavedRoomMesh.ValidateChairReferenceJson(meshJson, chairJson, out string validation);
        if (!valid) throw new BuildFailedException("Chair baseline validation failed: " + validation);
        bool passed = QuestSavedRoomMesh.RunChairReferenceSmokeTest(meshJson, chairJson, out string smoke);
        Directory.CreateDirectory("Logs");
        File.WriteAllText("Logs/chair-baseline-validation.json", JsonUtility.ToJson(new Result {
            validatedUtc = DateTime.UtcNow.ToString("o"), sourceMesh = roomPath,
            sourceChair = chairPath, validation = validation, smokeTest = smoke,
            passed = valid && passed, headsetTested = false
        }, true));
        if (!passed) throw new BuildFailedException("Chair baseline runtime smoke check failed: " + smoke);
        Debug.Log("AIM2_CHAIR_BASELINE_VALIDATED " + validation + " | " + smoke);
    }

    [Serializable] sealed class Result
    {
        public string validatedUtc, sourceMesh, sourceChair, validation, smokeTest;
        public bool passed, headsetTested;
    }
}