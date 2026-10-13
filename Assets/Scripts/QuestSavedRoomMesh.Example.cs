// Explicit hardcoded example only; this component does not observe physical chair motion.
using System;
using UnityEngine;
#if UNITY_EDITOR
using UnityEngine.Rendering;
#endif

public sealed partial class QuestSavedRoomMesh
{
    GameObject chairExampleMarker;
    Material chairExampleMarkerMaterial;
    TextMesh chairExampleMarkerLabel;
    TextMesh chairExampleInitialLabel;
    bool chairExampleMarkerFailed;
    // Per reader/app session; never saved to PlayerPrefs or reset by marker recreation.
    bool chairExampleTargetRevealed;

    public bool ChairExampleTargetRevealed => chairExampleTargetRevealed;

    // First Y reveals only the reference target. It never applies a memory pose.
    public bool TryRevealChairExampleTarget()
    {
        if (chairGeometryMesh == null || chairMarkerMesh == null || meshMaterial == null ||
            !TryGetChairExampleWorldPose(true, out _)) return false;
        bool previouslyRevealed = chairExampleTargetRevealed;
        chairExampleTargetRevealed = true;
        UpdateChairExampleMarker();
        if (chairExampleMarker != null && chairExampleMarkerLabel != null) return true;
        // A failed marker creation must not advance the controller's two-stage action.
        chairExampleTargetRevealed = previouslyRevealed;
        if (chairExampleMarker != null) chairExampleMarker.SetActive(chairSegmentationRevealed && previouslyRevealed);
        return false;
    }

    // T0 and T1 are immutable in the saved room-anchor frame, independent of memory pose.
    public bool TryGetChairExampleWorldPose(bool updated, out Pose worldPose)
    {
        worldPose = default;
        if (!TryGetChairInitialPoseInRoom(out var initial) || chairRoomTransform == null) return false;
        Pose local = updated ? HardcodedChairTarget(initial) : initial;
        Vector3 position = chairRoomTransform.TransformPoint(local.position);
        Quaternion rotation = chairRoomTransform.rotation * local.rotation;
        if (!ValidChairPose(position, rotation)) return false;
        worldPose = new Pose(position, rotation);
        return true;
    }

    // Target marker only: creation/relocalization must never update chair memory.
    internal void UpdateChairExampleMarker()
    {
        if (!TryGetChairInitialPoseInRoom(out var initial) || chairRoomTransform == null || chairGeometryMesh == null || chairMarkerMesh == null)
        { DisposeChairExampleMarker(); return; }
        if (chairExampleMarker != null && chairExampleMarker.transform.parent != chairRoomTransform) DisposeChairExampleMarker();
        if (chairExampleMarkerFailed) return;
        try
        {
            Pose target = HardcodedChairTarget(initial);
            if (chairExampleMarker == null)
            {
                chairExampleMarkerMaterial = new Material(meshMaterial) { name = "Chair T1 target cyan" };
                chairExampleMarkerMaterial.SetColor("_Color", new Color(0.08f, 1f, 1f, 1f));
                chairExampleMarker = new GameObject("T1 target marker - not chair memory");
                // Gate the complete subtree, including the label. Mesh visibility
                // toggles change renderer.enabled and cannot bypass this first-Y gate.
                chairExampleMarker.SetActive(chairSegmentationRevealed && chairExampleTargetRevealed);
                chairExampleMarker.layer = chairRoomTransform.gameObject.layer;
                chairExampleMarker.transform.SetParent(chairRoomTransform, false);
                // These meshes belong to the chair baseline. This marker only shares them.
                AddChairRenderer(chairExampleMarker, chairGeometryMesh, chairExampleMarkerMaterial);
                var arrow = new GameObject("T1 bounds and forward arrow");
                arrow.layer = chairExampleMarker.layer;
                arrow.transform.SetParent(chairExampleMarker.transform, false);
                AddChairRenderer(arrow, chairMarkerMesh, chairExampleMarkerMaterial);
                chairExampleMarkerLabel = CreateChairExampleLabel(chairExampleMarker.transform,
                    "T1", new Color(0.08f, 1f, 1f, 1f));
                ApplyMeshVisibility();
            }
            chairExampleMarker.transform.localPosition = target.position;
            chairExampleMarker.transform.localRotation = target.rotation;
            chairExampleMarker.SetActive(chairSegmentationRevealed && chairExampleTargetRevealed);
            // T0 belongs to the immutable room reference, not to the movable chair.
            if (chairExampleInitialLabel == null)
                chairExampleInitialLabel = CreateChairExampleLabel(chairRoomTransform,
                    "T0", new Color(1f, 0.48f, 0.08f, 1f));
            if (chairExampleInitialLabel != null)
            {
                chairExampleInitialLabel.transform.localPosition = initial.position + initial.rotation *
                    new Vector3(0, ChairBoundsSize.y * 0.5f + 0.22f, 0);
                chairExampleInitialLabel.transform.localRotation = initial.rotation;
                chairExampleInitialLabel.GetComponent<MeshRenderer>().enabled = showMeshes && chairSegmentationRevealed;
                if (headCamera != null) chairExampleInitialLabel.transform.rotation = headCamera.transform.rotation;
            }
            // Central presentation gating also suppresses the pre-A T1 subtree.
            // At overlapping T1, it keeps only the cyan label to avoid z-fighting.
            ApplyChairPresentationVisibility();
            if (chairExampleMarkerLabel != null && headCamera != null) chairExampleMarkerLabel.transform.rotation = headCamera.transform.rotation;
        }
        catch (Exception error)
        {
            DisposeChairExampleMarker(); chairExampleMarkerFailed = true;
            Debug.LogWarning("CHAIR_EXAMPLE markers unavailable: " + error.Message);
        }
    }

    TextMesh CreateChairExampleLabel(Transform parent, string text, Color color)
    {
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font == null) return null;
        var label = new GameObject(text + " reference label");
        label.layer = parent.gameObject.layer;
        label.transform.SetParent(parent, false);
        label.transform.localPosition = new Vector3(0, ChairBoundsSize.y * 0.5f + 0.22f, 0);
        var textMesh = label.AddComponent<TextMesh>();
        textMesh.text = text;
        textMesh.font = font;
        textMesh.fontSize = 48;
        textMesh.characterSize = 0.006f;
        textMesh.anchor = TextAnchor.MiddleCenter;
        textMesh.alignment = TextAlignment.Center;
        textMesh.color = color;
        var renderer = label.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = font.material;
        renderer.enabled = showMeshes && chairSegmentationRevealed;
        return textMesh;
    }

    internal void DisposeChairExampleMarker()
    {
        // Keep the session reveal flag across relocalization/recreation and X resets.
        // Never destroy chairGeometryMesh, chairMarkerMesh, or the built-in font material.
        DestroyChairOwned(chairExampleMarker);
        if (chairExampleInitialLabel != null) DestroyChairOwned(chairExampleInitialLabel.gameObject);
        DestroyChairOwned(chairExampleMarkerMaterial);
        chairExampleMarker = null; chairExampleMarkerMaterial = null; chairExampleMarkerLabel = null;
        chairExampleInitialLabel = null;
        chairExampleMarkerFailed = false;
    }
    public string ChairExampleStateLabel
    {
        get
        {
            if (!TryGetChairInitialPoseInRoom(out var initial)) return "CHAIR: LOADING";
            var actual = new Pose(chairObject.transform.localPosition, chairObject.transform.localRotation);
            if (ChairExamplePoseMatches(actual, initial)) return "CHAIR: INITIAL";
            if (ChairExamplePoseMatches(actual, HardcodedChairTarget(initial))) return "CHAIR: UPDATED";
            return "CHAIR: CUSTOM";
        }
    }

    public bool TryApplyHardcodedChairExample()
    {
        if (!TryGetChairInitialPoseInRoom(out var initial)) return false;
        Pose target = HardcodedChairTarget(initial);
        if (!TryUpdateChairPoseInRoom(target.position, target.rotation)) return false;
        chairExampleTargetRevealed = true;
        chairPoseSource = "hardcoded_chair_example_not_live_observation";
        chairReferenceDetail = "Hardcoded example: 0.9 m along the initial chair forward direction, then 90 degrees left (-90 degrees) around its own up axis. No physical-chair movement was observed.";
        WriteChairReferenceStatus();
        if (chairExampleMarker != null) UpdateChairExampleMarker();
        return true;
    }

    public bool TryResetChairExample()
    {
        if (!TryGetChairInitialPoseInRoom(out var initial)) return false;
        if (!TryUpdateChairPoseInRoom(initial.position, initial.rotation)) return false;
        chairPoseSource = "restored_user_annotated_baseline_not_live_observation";
        chairReferenceDetail = "Restored the immutable initial user-annotated chair pose. No physical-chair movement was observed.";
        WriteChairReferenceStatus();
        if (chairExampleMarker != null) UpdateChairExampleMarker();
        return true;
    }

    static Pose HardcodedChairTarget(Pose initial)
    {
        // The mesh anchor's local Y is not necessarily vertical. Use the initial
        // chair frame for both operations, never the current (possibly moved) pose.
        return new Pose(initial.position + initial.rotation * new Vector3(0f, 0f, 0.9f),
            initial.rotation * Quaternion.Euler(0f, -90f, 0f));
    }

    static bool ChairExamplePoseMatches(Pose a, Pose b)
    {
        if (!ValidChairPose(a.position, a.rotation) || !ValidChairPose(b.position, b.rotation)) return false;
        return (a.position - b.position).sqrMagnitude <= 0.00000001f &&
            Mathf.Abs(Quaternion.Dot(a.rotation.normalized, b.rotation.normalized)) >= 0.999999f;
    }

#if UNITY_EDITOR
    public static bool RunHardcodedChairExampleSmokeTest(string meshJson, string chairJson, out string report)
    {
        GameObject owner = null;
        Mesh wireframe = null;
        Material material = null;
        QuestSavedRoomMesh reader = null;
        try
        {
            RequireChair(!Application.isPlaying, "Run the chair example smoke test in edit mode.");
            var data = JsonUtility.FromJson<MeshExport>(meshJson);
            var definition = JsonUtility.FromJson<ChairDefinition>(chairJson);
            ValidateChairData(data, definition);
            var shader = Shader.Find("GSAC/Saved Room Wireframe");
            RequireChair(shader != null, "Saved Room Wireframe shader unavailable.");
            material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            owner = new GameObject("Hardcoded chair example smoke test") { hideFlags = HideFlags.HideAndDontSave };
            reader = owner.AddComponent<QuestSavedRoomMesh>();
            reader.meshMaterial = material;
            reader.chairSegmentationRevealed = true; // Legacy X/Y coverage starts after the A reveal.
            var cameraObject = new GameObject("Temporary label-facing camera");
            cameraObject.transform.SetParent(owner.transform, false);
            cameraObject.transform.rotation = Quaternion.Euler(8f, -35f, 0);
            reader.headCamera = cameraObject.AddComponent<Camera>();
            RequireChair(reader.ChairExampleStateLabel == "CHAIR: LOADING" && !reader.TryApplyHardcodedChairExample() && !reader.TryResetChairExample() && !reader.TryGetChairExampleWorldPose(false, out _) && !reader.TryGetChairExampleWorldPose(true, out _), "Unavailable chair must safely reject updates and pose requests.");
            reader.UpdateChairExampleMarker(); RequireChair(reader.chairExampleMarker == null, "Unavailable chair created a marker.");
            RequireChair(!reader.TryRevealChairExampleTarget() && !reader.ChairExampleTargetRevealed, "Unavailable reveal/apply advanced the T1 stage.");
            var preview = new GameObject("Temporary real room fixture") { hideFlags = HideFlags.HideAndDontSave };
            preview.transform.SetParent(owner.transform, false);
            preview.transform.localPosition = new Vector3(1.2f, 0.7f, -2.4f);
            preview.transform.localRotation = Quaternion.Euler(17f, 63f, -11f);
            wireframe = new Mesh { indexFormat = IndexFormat.UInt32, hideFlags = HideFlags.HideAndDontSave };
            wireframe.vertices = data.vertices;
            wireframe.SetIndices(ChairEdges(data.triangles), MeshTopology.Lines, 0);
            preview.AddComponent<MeshFilter>().sharedMesh = wireframe;
            preview.AddComponent<MeshRenderer>().sharedMaterial = material;
            RequireChair(reader.InitializeChairWithDefinition(data, definition, preview, wireframe), "Fixture initialization failed: " + reader.ChairReferenceDetail);
            reader.previewObjects.Add(preview);
            reader.showMeshes = false; // no PlayerPrefs/file changes in this editor test
            reader.ApplyMeshVisibility();
            int[] background = wireframe.GetIndices(0);
            string geometryHash = ChairGeometryHash(wireframe.vertices, data.triangles);
            RequireChair(reader.TryGetChairInitialPoseInRoom(out var initial), "Initial reference unavailable.");
            RequireChair(reader.ChairExampleStateLabel == "CHAIR: INITIAL", "Initial label incorrect.");
            RequireChair(reader.TryGetChairPose(out var memoryBeforeMarker), "Initial memory unavailable.");
            string sourceBeforeMarker = reader.chairPoseSource;
            reader.UpdateChairExampleMarker();
            RequireChair(reader.chairExampleMarker != null, "T1 marker was not created.");
            RequireChair(reader.chairExampleInitialLabel != null && reader.chairExampleInitialLabel.text == "T0", "T0 label was not created.");
            Vector3 initialLabelLocal = initial.position + initial.rotation * new Vector3(0, reader.ChairBoundsSize.y * 0.5f + 0.22f, 0);
            RequireChair((reader.chairExampleInitialLabel.transform.position - preview.transform.TransformPoint(initialLabelLocal)).sqrMagnitude < 0.00000001f, "T0 label is not above the immutable T0 bounds.");
            RequireChair(!reader.chairExampleInitialLabel.GetComponent<MeshRenderer>().enabled, "T0 label ignored hidden meshes.");
            RequireChair(Mathf.Abs(Quaternion.Dot(reader.chairExampleInitialLabel.transform.rotation, reader.headCamera.transform.rotation)) > 0.999999f, "T0 label does not face the head camera.");
            Vector3 originalT0LabelPosition = reader.chairExampleInitialLabel.transform.position;
            RequireChair(reader.chairExampleMarker.GetComponent<MeshFilter>().sharedMesh == reader.chairGeometryMesh, "Marker does not share chair geometry.");
            RequireChair(reader.TryGetChairPose(out var memoryAfterMarker) && ChairExamplePoseMatches(memoryBeforeMarker, memoryAfterMarker) && reader.chairPoseSource == sourceBeforeMarker, "Creating marker changed memory.");
            RequireChair(reader.TryGetChairExampleWorldPose(false, out var originalT0), "Immutable T0 unavailable.");
            RequireChair(reader.TryGetChairExampleWorldPose(true, out var originalT1), "Immutable T1 unavailable.");
            RequireChair(ChairExamplePoseMatches(originalT0, new Pose(preview.transform.TransformPoint(initial.position), preview.transform.rotation * initial.rotation)), "T0 does not use the initial reference.");
            RequireChair(ChairExamplePoseMatches(new Pose(reader.chairExampleMarker.transform.position, reader.chairExampleMarker.transform.rotation), originalT1), "Target marker is not at T1.");
            foreach (var renderer in reader.chairExampleMarker.GetComponentsInChildren<MeshRenderer>(true)) RequireChair(!renderer.enabled, "Marker creation turned hidden meshes on.");
            RequireChair(!reader.chairExampleTargetRevealed && !reader.chairExampleMarker.activeSelf, "T1 was revealed before the first successful Y.");
            reader.showMeshes = true;
            reader.ApplyMeshVisibility(); // Exercise the same renderer path used by right B.
            RequireChair(!reader.chairExampleMarker.activeInHierarchy, "Mesh toggle revealed T1 before Y.");
            foreach (var renderer in reader.chairExampleMarker.GetComponentsInChildren<MeshRenderer>(true))
                RequireChair(!renderer.gameObject.activeInHierarchy, "A T1 renderer escaped the first-Y gate.");
            RequireChair(reader.chairExampleInitialLabel.gameObject.activeInHierarchy && reader.chairExampleInitialLabel.GetComponent<MeshRenderer>().enabled, "Initial T0 label should remain visible.");
            RequireChair(reader.TryResetChairExample() && !reader.chairExampleTargetRevealed && !reader.chairExampleMarker.activeSelf, "X before Y revealed the target.");
            reader.DisposeChairExampleMarker();
            reader.UpdateChairExampleMarker();
            RequireChair(!reader.chairExampleMarker.activeSelf && !reader.chairExampleTargetRevealed, "Marker recreation revealed T1 before Y.");
            reader.showMeshes = false;
            reader.ApplyMeshVisibility();

            // Independent expected frame composition, not a world/mesh-Y rotation.
            Vector3 expectedPosition = initial.position + (initial.rotation * Vector3.forward) * 0.9f;
            Quaternion expectedRotation = initial.rotation * Quaternion.AngleAxis(-90f, Vector3.up);
            // First Y creates/reveals the target only; the orange memory stays at T0.
            var memoryBeforeReveal = new Pose(reader.chairObject.transform.localPosition, reader.chairObject.transform.localRotation);
            string sourceBeforeReveal = reader.chairPoseSource;
            string detailBeforeReveal = reader.chairReferenceDetail;
            reader.DisposeChairExampleMarker();
            reader.showMeshes = true;
            reader.ApplyMeshVisibility();
            RequireChair(reader.TryRevealChairExampleTarget() && reader.ChairExampleTargetRevealed && reader.chairExampleMarker.activeInHierarchy, "First Y did not reveal/create T1.");
            RequireChair(reader.chairExampleMarkerLabel != null && reader.chairExampleMarkerLabel.text == "T1", "Reveal omitted the T1 label.");
            foreach (var renderer in reader.chairExampleMarker.GetComponentsInChildren<MeshRenderer>(true))
                RequireChair(renderer.enabled && renderer.gameObject.activeInHierarchy, "Reveal did not show all T1 ghost/bounds/label renderers.");
            RequireChair(reader.ChairExampleStateLabel == "CHAIR: INITIAL" && reader.chairObject.transform.localPosition.Equals(memoryBeforeReveal.position) && reader.chairObject.transform.localRotation.Equals(memoryBeforeReveal.rotation), "Reveal moved chair memory from T0.");
            RequireChair(reader.chairPoseSource == sourceBeforeReveal && reader.chairReferenceDetail == detailBeforeReveal, "Reveal changed the memory pose source/detail.");
            RequireChair(IntArraysEqual(background, wireframe.GetIndices(0)) && ChairGeometryHash(wireframe.vertices, data.triangles) == geometryHash, "Reveal changed background geometry.");
            RequireChair(reader.TryGetChairExampleWorldPose(false, out var revealedT0) && ChairExamplePoseMatches(revealedT0, originalT0) && reader.TryGetChairExampleWorldPose(true, out var revealedT1) && ChairExamplePoseMatches(revealedT1, originalT1), "Reveal changed immutable T0/T1.");
            var firstRevealMarker = reader.chairExampleMarker;
            RequireChair(reader.TryRevealChairExampleTarget() && reader.chairExampleMarker == firstRevealMarker && reader.chairObject.transform.localPosition.Equals(memoryBeforeReveal.position) && reader.chairPoseSource == sourceBeforeReveal, "Repeated reveal was not idempotent.");
            RequireChair(reader.TryResetChairExample() && reader.ChairExampleTargetRevealed && reader.chairExampleMarker.activeSelf, "X after reveal reset the two-stage state.");
            reader.showMeshes = false;
            reader.ApplyMeshVisibility();
            reader.UpdateChairExampleMarker();
            RequireChair(reader.TryApplyHardcodedChairExample(), "Hardcoded apply failed.");
            RequireChair(reader.ChairExampleStateLabel == "CHAIR: UPDATED", "Updated label incorrect.");
            RequireChair(reader.chairExampleTargetRevealed && reader.chairExampleMarker.activeSelf, "Later memory update lost the target reveal.");
            foreach (var renderer in reader.chairExampleMarker.GetComponentsInChildren<MeshRenderer>(true))
                RequireChair(!renderer.enabled, "Y changed the hidden-mesh preference.");
            reader.showMeshes = true;
            reader.ApplyMeshVisibility();
            reader.UpdateChairExampleMarker();
            foreach (var renderer in reader.chairExampleMarker.GetComponentsInChildren<MeshRenderer>(true))
            {
                bool isLabel = reader.chairExampleMarkerLabel != null && renderer.gameObject == reader.chairExampleMarkerLabel.gameObject;
                RequireChair(renderer.enabled == isLabel, "At T1, only the target label should remain visible.");
            }
            RequireChair(reader.chairExampleInitialLabel.GetComponent<MeshRenderer>().enabled, "T0 label was hidden while meshes were visible at T1.");
            RequireChair((reader.chairExampleInitialLabel.transform.position - originalT0LabelPosition).sqrMagnitude < 0.00000001f, "T0 label moved with memory to T1.");
            RequireChair(reader.TryResetChairExample(), "Marker visibility reset failed.");
            RequireChair(reader.chairExampleTargetRevealed && reader.chairExampleMarker.activeSelf, "X after first Y lost the session target reveal.");
            foreach (var renderer in reader.chairExampleMarker.GetComponentsInChildren<MeshRenderer>(true))
                RequireChair(renderer.enabled, "Returning to T0 did not restore the T1 ghost.");
            RequireChair(reader.TryApplyHardcodedChairExample(), "Marker visibility reapply failed.");
            reader.showMeshes = false;
            reader.ApplyMeshVisibility();
            reader.UpdateChairExampleMarker();
            RequireChair(reader.chairObject.transform.localPosition == expectedPosition && ChairExamplePoseMatches(new Pose(reader.chairObject.transform.localPosition, reader.chairObject.transform.localRotation), new Pose(expectedPosition, expectedRotation)), "Target is not based on the initial chair frame.");
            RequireChair(reader.TryGetChairPose(out var world), "Updated world pose unavailable.");
            RequireChair(ChairExamplePoseMatches(world, new Pose(preview.transform.TransformPoint(expectedPosition), preview.transform.rotation * expectedRotation)), "Target world pose does not follow arbitrary room transform.");
            Pose firstApplied = new Pose(reader.chairObject.transform.localPosition, reader.chairObject.transform.localRotation);
            RequireChair(reader.TryApplyHardcodedChairExample(), "Repeated apply failed.");
            RequireChair(reader.chairObject.transform.localPosition.Equals(firstApplied.position) && reader.chairObject.transform.localRotation.Equals(firstApplied.rotation), "Repeated apply accumulated movement.");
            RequireChair(reader.TryGetChairExampleWorldPose(false, out var afterApplyT0) && ChairExamplePoseMatches(afterApplyT0, originalT0), "Applying memory moved immutable T0.");
            RequireChair(reader.TryGetChairExampleWorldPose(true, out var afterApplyT1) && ChairExamplePoseMatches(afterApplyT1, originalT1), "Applying memory moved immutable T1.");

            // Relocalizing the parent must not alter the saved chair-local target.
            preview.transform.SetPositionAndRotation(new Vector3(-0.4f, 1.6f, 3.2f), Quaternion.Euler(-23f, 121f, 8f));
            RequireChair(reader.TryGetChairPose(out var relocatedWorld), "Relocalized pose unavailable.");
            RequireChair(ChairExamplePoseMatches(relocatedWorld, new Pose(preview.transform.TransformPoint(expectedPosition), preview.transform.rotation * expectedRotation)), "Target failed to follow parent relocalization.");
            reader.UpdateChairExampleMarker();
            RequireChair(reader.TryGetChairExampleWorldPose(true, out var relocatedT1) && ChairExamplePoseMatches(relocatedT1, relocatedWorld), "Immutable T1 failed to relocalize.");
            RequireChair(reader.TryGetChairExampleWorldPose(false, out var relocatedT0) && ChairExamplePoseMatches(relocatedT0, new Pose(preview.transform.TransformPoint(initial.position), preview.transform.rotation * initial.rotation)), "Immutable T0 failed to relocalize.");
            RequireChair(ChairExamplePoseMatches(new Pose(reader.chairExampleMarker.transform.position, reader.chairExampleMarker.transform.rotation), relocatedT1), "Marker failed to follow parent relocalization.");
            RequireChair((reader.chairExampleInitialLabel.transform.position - preview.transform.TransformPoint(initialLabelLocal)).sqrMagnitude < 0.00000001f, "T0 label failed to follow room relocalization.");
            RequireChair(!reader.chairExampleInitialLabel.GetComponent<MeshRenderer>().enabled, "T0 label ignored mesh visibility after relocalization.");
            for (int repeat = 0; repeat < 3; repeat++)
            {
                RequireChair(reader.TryResetChairExample(), "Repeated T0 reset failed.");
                RequireChair(reader.TryGetChairPose(out var resetPose) && ChairExamplePoseMatches(resetPose, relocatedT0), "Repeated T0 moved the baseline.");
                RequireChair(reader.TryApplyHardcodedChairExample(), "Repeated T1 apply failed.");
                RequireChair(reader.TryGetChairPose(out var appliedPose) && ChairExamplePoseMatches(appliedPose, relocatedT1), "Repeated T1 moved the target.");
            }
            RequireChair(reader.TryUpdateChairPoseInRoom(initial.position + new Vector3(0.19f, -0.11f, 0.23f), initial.rotation) && reader.ChairExampleStateLabel == "CHAIR: CUSTOM", "Custom pose label incorrect.");
            RequireChair(reader.TryApplyHardcodedChairExample() && ChairExamplePoseMatches(new Pose(reader.chairObject.transform.localPosition, reader.chairObject.transform.localRotation), new Pose(expectedPosition, expectedRotation)), "Apply from a custom pose changed the fixed target.");
            RequireChair(reader.TryResetChairExample() && reader.ChairExampleStateLabel == "CHAIR: INITIAL", "Reset failed.");
            RequireChair(reader.chairObject.transform.localPosition.Equals(initial.position) && reader.chairObject.transform.localRotation.Equals(initial.rotation), "Reset did not restore the exact initial local pose.");
            RequireChair(reader.TryGetChairInitialPoseInRoom(out var unchanged), "Initial reference lost.");
            RequireChair(unchanged.position.Equals(initial.position) && unchanged.rotation.Equals(initial.rotation), "Initial reference was mutated.");
            RequireChair(IntArraysEqual(background, wireframe.GetIndices(0)) && ChairGeometryHash(wireframe.vertices, data.triangles) == geometryHash, "Background geometry changed.");
            RequireChair(!reader.MeshesVisible, "Example changed the visibility preference.");
            foreach (var renderer in preview.GetComponentsInChildren<MeshRenderer>(true)) RequireChair(!renderer.enabled, "Example changed renderer visibility.");
            RequireChair(reader.chairPoseSource == "restored_user_annotated_baseline_not_live_observation", "Reset source is not explicit.");
            reader.DisposeChairExampleMarker();
            reader.UpdateChairExampleMarker();
            RequireChair(reader.chairExampleTargetRevealed && reader.chairExampleMarker.activeSelf, "Marker recreation lost the first-Y reveal.");
            foreach (var renderer in preview.GetComponentsInChildren<MeshRenderer>(true)) RequireChair(!renderer.enabled, "Recreated marker ignored hidden meshes.");
            var disposedT0Label = reader.chairExampleInitialLabel;
            reader.DisposeChairExampleMarker();
            RequireChair(disposedT0Label == null && reader.chairExampleInitialLabel == null, "T0 label cleanup failed.");
            RequireChair(reader.chairGeometryMesh != null && reader.chairMarkerMesh != null, "Marker cleanup destroyed shared meshes.");
            reader.DisposeChairReference();
            RequireChair(!reader.TryApplyHardcodedChairExample() && !reader.TryResetChairExample() && reader.ChairExampleStateLabel == "CHAIR: LOADING", "Disposed chair accepted an update.");
            report = "PASS: real fixture; initial-frame 0.9 m forward/90 degree left turn; idempotent target; exact reset; custom/unavailable labels; frozen reference/background; arbitrary parent transform/relocalization; preserved hidden renderers; stable T0/T1; marker without memory mutation; T1 geometry hidden only at overlap with label retained; fixed T0 label with camera facing, visibility and relocalization; first-Y reveal leaves memory/source/geometry unchanged; later apply suppresses overlap; reveal gate survives mesh toggles, X replay and marker recreation; safe shared-mesh cleanup.";
            return true;
        }
        catch (Exception error) { report = "FAIL: " + error.Message; return false; }
        finally
        {
            if (reader != null) { reader.DisposeChairExampleMarker(); reader.DisposeChairReference(); }
            DestroyChairOwned(owner); DestroyChairOwned(wireframe); DestroyChairOwned(material);
        }
    }
#endif
}
