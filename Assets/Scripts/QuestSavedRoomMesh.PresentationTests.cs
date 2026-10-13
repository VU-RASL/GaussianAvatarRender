#if UNITY_EDITOR
using System;
using UnityEngine;
using UnityEngine.Rendering;

public sealed partial class QuestSavedRoomMesh
{
    public static bool RunMeshPresentationSmokeTest(string meshJson, string chairJson, out string report)
    {
        GameObject owner = null;
        Mesh wireframe = null;
        Material material = null;
        QuestSavedRoomMesh reader = null;
        try
        {
            RequireChair(!Application.isPlaying, "Run presentation validation in edit mode.");
            var input = new MeshPresentationInput();
            RequireChair(input.Sample(true, true, false) == 0, "Held A on connection must not reveal.");
            input.Sample(true, false, false);
            RequireChair(input.Sample(true, true, true) == 1, "B must win simultaneous A/B.");
            RequireChair(input.Sample(true, true, false) == 0, "Held A leaked through the B action.");
            input.Sample(true, false, false);
            RequireChair(input.Sample(true, true, false) == 2, "Fresh A was not recognized.");
            RequireChair(input.Sample(true, true, false) == 0, "Held A repeated.");
            input.Sample(false, false, false);
            RequireChair(input.Sample(true, false, true) == 0, "Held B on focus return must not toggle.");
            input.Sample(true, false, false);
            RequireChair(input.Sample(true, false, true) == 1, "Fresh B after focus return failed.");

            var data = JsonUtility.FromJson<MeshExport>(meshJson);
            var definition = JsonUtility.FromJson<ChairDefinition>(chairJson);
            ValidateChairData(data, definition);
            var sourceMaterial = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>("Assets/Shaders/AvatarRoomMesh.mat");
            RequireChair(sourceMaterial != null, "The real blue room material is required.");
            material = new Material(sourceMaterial) { hideFlags = HideFlags.HideAndDontSave };
            Color blue = material.GetColor("_Color");
            RequireChair(blue.b > blue.r && blue.b > 0.5f, "Room mesh must retain its blue material.");
            owner = new GameObject("Mesh presentation validation") { hideFlags = HideFlags.HideAndDontSave };
            reader = owner.AddComponent<QuestSavedRoomMesh>();
            reader.meshMaterial = material;
            // Exercise runtime visibility application without changing PlayerPrefs/files.
            reader.meshVisibilityInitialized = true;
            RequireChair(!reader.MeshesVisible && !reader.ChairSegmentationRevealed, "New sessions must start hidden and unsegmented.");
            RequireChair(!reader.TryRevealChairSegmentation(), "A must reject an unavailable room.");
            var preview = new GameObject("Actual saved room fixture");
            preview.transform.SetParent(owner.transform, false);
            preview.transform.SetPositionAndRotation(new Vector3(1, 0.4f, -2), Quaternion.Euler(13, 28, -7));
            wireframe = new Mesh { indexFormat = IndexFormat.UInt32, hideFlags = HideFlags.HideAndDontSave };
            wireframe.vertices = data.vertices;
            int[] complete = ChairEdges(data.triangles);
            wireframe.SetIndices(complete, MeshTopology.Lines, 0);
            preview.AddComponent<MeshFilter>().sharedMesh = wireframe;
            var roomRenderer = preview.AddComponent<MeshRenderer>();
            roomRenderer.sharedMaterial = material;
            reader.previewObjects.Add(preview);
            RequireChair(reader.InitializeChairWithDefinition(data, definition, preview, wireframe), "Real room initialization failed.");
            reader.UpdateChairExampleMarker();
            reader.ApplyMeshVisibility();
            RequireChair(!reader.TryRevealChairSegmentation(), "A revealed segmentation while B was off.");
            AssertPresentationRenderersHidden(preview);
            RequireChair(IntArraysEqual(complete, wireframe.GetIndices(0)), "Hidden unsegmented room lost original chair triangles.");
            RequireChair(reader.TryGetChairPose(out var originalMemory), "Initial memory unavailable.");
            string originalSource = reader.chairPoseSource;
            for (int i = 0; i < 3; i++)
            {
                reader.showMeshes = true; reader.ApplyMeshVisibility(); reader.UpdateChairExampleMarker();
                RequireChair(roomRenderer.enabled && IntArraysEqual(complete, wireframe.GetIndices(0)), "B must show every original blue room edge before A.");
                foreach (var renderer in reader.chairObject.GetComponentsInChildren<MeshRenderer>(true))
                    RequireChair(!renderer.enabled, "Orange chair or bounds leaked before A.");
                RequireChair(!reader.chairExampleInitialLabel.GetComponent<MeshRenderer>().enabled, "T0 label leaked before A.");
                foreach (var renderer in reader.chairExampleMarker.GetComponentsInChildren<MeshRenderer>(true))
                    RequireChair(!renderer.enabled || !renderer.gameObject.activeInHierarchy, "T1 leaked before A/Y.");
                reader.showMeshes = false; reader.ApplyMeshVisibility(); reader.UpdateChairExampleMarker();
                AssertPresentationRenderersHidden(preview);
                RequireChair(!reader.TryRevealChairSegmentation(), "Hidden A changed the presentation phase.");
            }
            reader.showMeshes = true; reader.ApplyMeshVisibility();
            preview.SetActive(false);
            RequireChair(!reader.TryRevealChairSegmentation(), "A accepted an inactive room preview.");
            preview.SetActive(true);
            RequireChair(reader.TryRevealChairSegmentation() && reader.ChairSegmentationRevealed, "Visible A did not reveal the chair.");
            reader.UpdateChairExampleMarker();
            int[] segmented = wireframe.GetIndices(0);
            RequireChair(segmented.Length + reader.chairGeometryMesh.GetIndices(0).Length == complete.Length, "Revealed room/chair partition is incomplete or duplicated.");
            foreach (var renderer in reader.chairObject.GetComponentsInChildren<MeshRenderer>(true))
                RequireChair(renderer.enabled, "Revealed chair mesh/bounds/arrow is missing.");
            RequireChair(reader.chairExampleInitialLabel.GetComponent<MeshRenderer>().enabled, "Revealed T0 label is missing.");
            RequireChair(!reader.ChairExampleTargetRevealed && !reader.chairExampleMarker.activeInHierarchy, "A incorrectly revealed T1 before Y.");
            RequireChair(reader.TryRevealChairSegmentation() && IntArraysEqual(segmented, wireframe.GetIndices(0)), "Repeated A changed the partition.");
            RequireChair(reader.TryGetChairPose(out var afterA) && ChairExamplePoseMatches(originalMemory, afterA) && originalSource == reader.chairPoseSource, "Presentation reveal changed chair memory.");
            RequireChair(reader.TryRevealChairExampleTarget(), "First Y target reveal failed after A.");
            for (int i = 0; i < 2; i++)
            {
                reader.showMeshes = false; reader.ApplyMeshVisibility(); reader.UpdateChairExampleMarker();
                AssertPresentationRenderersHidden(preview);
                reader.showMeshes = true; reader.ApplyMeshVisibility(); reader.UpdateChairExampleMarker();
                RequireChair(reader.ChairSegmentationRevealed && reader.ChairExampleTargetRevealed && IntArraysEqual(segmented, wireframe.GetIndices(0)), "B erased a revealed phase.");
                RequireChair(reader.chairObject.GetComponent<MeshRenderer>().enabled && reader.chairExampleMarker.GetComponent<MeshRenderer>().enabled, "B did not restore eligible chair meshes.");
            }
            RequireChair(reader.TryApplyHardcodedChairExample(), "Second Y update failed.");
            reader.showMeshes = false; reader.ApplyMeshVisibility();
            reader.showMeshes = true; reader.ApplyMeshVisibility(); reader.UpdateChairExampleMarker();
            RequireChair(!reader.chairExampleMarker.GetComponent<MeshRenderer>().enabled && reader.chairExampleMarkerLabel.GetComponent<MeshRenderer>().enabled, "B lost existing T1 overlap suppression.");
            RequireChair(reader.TryResetChairExample() && reader.ChairSegmentationRevealed, "X erased segmentation reveal.");
            reader.DisposeChairExampleMarker(); reader.DisposeChairReference();
            RequireChair(IntArraysEqual(complete, wireframe.GetIndices(0)), "Cleanup failed to restore the original scan.");
            preview.transform.rotation = Quaternion.Euler(-8, 77, 9);
            RequireChair(reader.InitializeChairWithDefinition(data, definition, preview, wireframe), "Room recreation failed.");
            reader.ApplyMeshVisibility(); reader.UpdateChairExampleMarker();
            RequireChair(reader.ChairSegmentationRevealed && reader.ChairExampleTargetRevealed && IntArraysEqual(segmented, wireframe.GetIndices(0)) && reader.chairExampleInitialLabel.GetComponent<MeshRenderer>().enabled, "Recreation/relocalization erased reveal state.");
            report = "PASS: fresh hidden session; B shows complete blue saved scan; hidden/unavailable A rejected; A reveals exact chair partition without memory mutation; repeated A is stable; B/X/recreation preserve reveals; T1 first-Y and overlap rules retained; A/B press/release/focus gating.";
            return true;
        }
        catch (Exception error) { report = "FAIL: " + error.Message; return false; }
        finally
        {
            if (reader != null) { reader.DisposeChairExampleMarker(); reader.DisposeChairReference(); }
            DestroyChairOwned(owner); DestroyChairOwned(wireframe); DestroyChairOwned(material);
        }
    }
    static void AssertPresentationRenderersHidden(GameObject preview)
    {
        foreach (var renderer in preview.GetComponentsInChildren<MeshRenderer>(true))
            RequireChair(!renderer.enabled, "B off left a room/chair/label renderer enabled.");
    }
}
#endif
