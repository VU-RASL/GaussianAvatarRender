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
        bool examplePassed = QuestSavedRoomMesh.RunHardcodedChairExampleSmokeTest(meshJson, chairJson, out string exampleSmoke);
        bool placementPassed = ARGroundPlacement.RunChairPlacementSmokeTest(out string placementSmoke);
        bool inputPassed = QuestChairAvatarExample.RunChairAvatarInputSmokeTest(out string inputSmoke);
        bool presentationPassed = QuestSavedRoomMesh.RunMeshPresentationSmokeTest(meshJson, chairJson, out string presentationSmoke);
        Directory.CreateDirectory("Logs");
        File.WriteAllText("Logs/chair-baseline-validation.json", JsonUtility.ToJson(new Result {
            validatedUtc = DateTime.UtcNow.ToString("o"), sourceMesh = roomPath,
            sourceChair = chairPath, validation = validation, smokeTest = smoke,
            hardcodedExampleSmokeTest = exampleSmoke,
            avatarPlacementSmokeTest = placementSmoke, controllerInputSmokeTest = inputSmoke, meshPresentationSmokeTest = presentationSmoke,
            passed = valid && passed && examplePassed && placementPassed && inputPassed && presentationPassed, headsetTested = false
        }, true));
        if (!passed) throw new BuildFailedException("Chair baseline runtime smoke check failed: " + smoke);
        if (!examplePassed) throw new BuildFailedException("Hardcoded chair example smoke check failed: " + exampleSmoke);
        if (!placementPassed) throw new BuildFailedException("Avatar chair placement check failed: " + placementSmoke);
        if (!inputPassed) throw new BuildFailedException("Chair controller input check failed: " + inputSmoke);
        if (!presentationPassed) throw new BuildFailedException("Mesh presentation check failed: " + presentationSmoke);
        Debug.Log("AIM2_MESH_PRESENTATION_VALIDATED " + presentationSmoke);
        Debug.Log("AIM2_AVATAR_PLACEMENT_VALIDATED " + placementSmoke);
        Debug.Log("AIM2_CHAIR_INPUT_VALIDATED " + inputSmoke);
        Debug.Log("AIM2_CHAIR_BASELINE_VALIDATED " + validation + " | " + smoke);
        Debug.Log("AIM2_CHAIR_EXAMPLE_VALIDATED " + exampleSmoke);
    }

    [Serializable] sealed class Result
    {
        public string validatedUtc, sourceMesh, sourceChair, validation, smokeTest, hardcodedExampleSmokeTest;
        public string avatarPlacementSmokeTest, controllerInputSmokeTest, meshPresentationSmokeTest;
        public bool passed, headsetTested;
    }
}
