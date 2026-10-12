// User-identified static chair reference; explicit pose updates are not live observations.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

public sealed partial class QuestSavedRoomMesh
{
    string chairReferenceState = "not_initialized", chairReferenceDetail = "Chair reference has not been initialized.";
    string chairPoseSource = "static_user_annotated_baseline", chairActualGeometryHash;
    ChairDefinition chairDefinition;
    Transform chairRoomTransform;
    GameObject chairObject;
    Mesh chairGeometryMesh, chairMarkerMesh, chairBackgroundMesh;
    Material chairGeometryMaterial, chairMarkerMaterial;
    int[] chairOriginalBackgroundIndices, chairSourceVertexIndices;
    Vector3 chairInitialPosition;
    Quaternion chairInitialRotation;
    Coroutine chairStatusCoroutine;
    public string ChairReferenceState => chairReferenceState;
    public string ChairReferenceDetail => chairReferenceDetail;
    public bool ChairReferenceInitialized => chairObject != null && chairRoomTransform != null && chairReferenceState == "initialized";
    public Vector3 ChairBoundsSize => chairDefinition != null ? chairDefinition.boundsSize : Vector3.zero;

    void InitializeChair(MeshExport data, GameObject preview, Mesh mesh)
    {
        if (ChairReferenceInitialized) return;
        try
        {
            var asset = Resources.Load<TextAsset>("Aim2/Chair_01");
            if (asset == null) { SetChairReferenceState("missing_reference", "Resources/Aim2/Chair_01.json was not found."); return; }
            InitializeChairWithDefinition(data, JsonUtility.FromJson<ChairDefinition>(asset.text), preview, mesh);
        }
        catch (Exception error) { SetChairReferenceState("failure", error.Message); }
    }

    // Device and editor validation share this transaction. Failure preserves the original wireframe.
    bool InitializeChairWithDefinition(MeshExport data, ChairDefinition definition, GameObject preview, Mesh mesh)
    {
        if (ChairReferenceInitialized) return false;
        GameObject stagedObject = null;
        Mesh stagedGeometry = null, stagedMarker = null;
        Material stagedMaterial = null, stagedMarkerMaterial = null;
        int[] originalIndices = null;
        bool backgroundChanged = false;
        try
        {
            chairDefinition = definition;
            chairActualGeometryHash = ValidateChairData(data, definition);
            RequireChair(preview != null && mesh != null, "The matching preview and mesh are required.");
            RequireChair(meshMaterial != null && meshMaterial.HasProperty("_Color"), "The wireframe material must expose _Color.");
            RequireChair((preview.transform.lossyScale - Vector3.one).sqrMagnitude <= 0.000001f, "The room preview must have unit world scale.");
            var filter = preview.GetComponent<MeshFilter>();
            RequireChair(filter != null && filter.sharedMesh == mesh && mesh.vertexCount == data.vertexCount, "The mesh must belong to the matching room preview.");
            RequireChair(mesh.subMeshCount == 1 && mesh.GetTopology(0) == MeshTopology.Lines, "The preview must have one wireframe submesh.");
            originalIndices = mesh.GetIndices(0);
            RequireChair(IntArraysEqual(originalIndices, ChairEdges(data.triangles)), "The preview must contain the complete original wireframe.");
            var selected = new bool[data.triangleCount];
            foreach (int triangle in definition.selectedTriangleIndices) selected[triangle] = true;
            var selectedTriangles = new List<int>();
            var backgroundTriangles = new List<int>();
            var vertexMap = new Dictionary<int, int>();
            var sourceIndices = new List<int>();
            var localVertices = new List<Vector3>();
            Quaternion inverse = Quaternion.Inverse(definition.initialRotationMesh);
            for (int triangle = 0; triangle < data.triangleCount; triangle++)
            {
                for (int corner = 0; corner < 3; corner++)
                {
                    int sourceIndex = data.triangles[triangle * 3 + corner];
                    if (!selected[triangle]) { backgroundTriangles.Add(sourceIndex); continue; }
                    if (!vertexMap.TryGetValue(sourceIndex, out int mapped))
                    {
                        mapped = localVertices.Count; vertexMap.Add(sourceIndex, mapped); sourceIndices.Add(sourceIndex);
                        Vector3 vertex = inverse * (data.vertices[sourceIndex] - definition.initialPositionMesh);
                        RequireChair(Finite(vertex), "Chair-local vertex is not finite."); localVertices.Add(vertex);
                    }
                    selectedTriangles.Add(mapped);
                }
            }
            int[] backgroundEdges = ChairEdges(backgroundTriangles.ToArray());
            stagedGeometry = new Mesh { name = "Chair_01 selected saved triangles", indexFormat = IndexFormat.UInt32 };
            stagedGeometry.SetVertices(localVertices);
            stagedGeometry.SetIndices(ChairEdges(selectedTriangles.ToArray()), MeshTopology.Lines, 0);
            stagedGeometry.RecalculateBounds();
            stagedMarker = CreateChairMarker(definition.boundsSize);
            stagedMaterial = new Material(meshMaterial) { name = "Chair_01 annotated geometry" };
            stagedMaterial.SetColor("_Color", new Color(1f, 0.48f, 0.08f, 1f));
            stagedMarkerMaterial = new Material(meshMaterial) { name = "Chair_01 bounds and +Z front direction" };
            stagedMarkerMaterial.SetColor("_Color", new Color(1f, 0.95f, 0.18f, 1f));
            stagedObject = new GameObject("Chair_01"); stagedObject.SetActive(false); stagedObject.layer = preview.layer;
            stagedObject.transform.SetParent(preview.transform, false);
            stagedObject.transform.localPosition = definition.initialPositionMesh;
            stagedObject.transform.localRotation = definition.initialRotationMesh;
            AddChairRenderer(stagedObject, stagedGeometry, stagedMaterial);
            var marker = new GameObject("Bounds and +Z front direction"); marker.layer = preview.layer;
            marker.transform.SetParent(stagedObject.transform, false); AddChairRenderer(marker, stagedMarker, stagedMarkerMaterial);
            backgroundChanged = true;
            mesh.SetIndices(backgroundEdges, MeshTopology.Lines, 0); mesh.RecalculateBounds();
            stagedObject.SetActive(true);
            chairObject = stagedObject; chairGeometryMesh = stagedGeometry; chairMarkerMesh = stagedMarker;
            chairGeometryMaterial = stagedMaterial; chairMarkerMaterial = stagedMarkerMaterial;
            chairRoomTransform = preview.transform; chairBackgroundMesh = mesh;
            chairOriginalBackgroundIndices = originalIndices; chairSourceVertexIndices = sourceIndices.ToArray();
            chairInitialPosition = definition.initialPositionMesh; chairInitialRotation = definition.initialRotationMesh;
            chairPoseSource = "static_user_annotated_baseline";
            SetChairReferenceState("initialized", "User-identified static chair reference loaded. Heading is " + (definition.orientationConfirmed ? "user-confirmed." : "inferred and unconfirmed."));
            if (Application.isPlaying && isActiveAndEnabled) chairStatusCoroutine = StartCoroutine(WriteChairStatusPeriodically());
            return true;
        }
        catch (Exception error)
        {
            if (backgroundChanged && mesh != null && originalIndices != null)
            {
                try { mesh.SetIndices(originalIndices, MeshTopology.Lines, 0); mesh.RecalculateBounds(); }
                catch (Exception restoreError) { Debug.LogError("CHAIR_REFERENCE background restore failed: " + restoreError.Message); }
            }
            DestroyChairOwned(stagedObject); DestroyChairOwned(stagedGeometry); DestroyChairOwned(stagedMarker);
            DestroyChairOwned(stagedMaterial); DestroyChairOwned(stagedMarkerMaterial);
            chairObject = null; chairRoomTransform = null; chairBackgroundMesh = null;
            chairGeometryMesh = chairMarkerMesh = null; chairGeometryMaterial = chairMarkerMaterial = null;
            chairOriginalBackgroundIndices = chairSourceVertexIndices = null;
            SetChairReferenceState(error is ChairReferenceMismatch ? "mismatch" : "failure", error.Message);
            return false;
        }
    }
    public bool TryGetChairPose(out Pose worldPose)
    {
        worldPose = default;
        if (!ChairReferenceInitialized) return false;
        if (!ValidChairPose(chairObject.transform.position, chairObject.transform.rotation)) return false;
        worldPose = new Pose(chairObject.transform.position, chairObject.transform.rotation); return true;
    }
    public bool TryGetChairInitialPoseInRoom(out Pose initialPose)
    {
        initialPose = default;
        if (!ChairReferenceInitialized) return false;
        initialPose = new Pose(chairInitialPosition, chairInitialRotation); return true;
    }
    // Room-local = saved Scene mesh anchor local frame, meters. Reference/background stay frozen.
    public bool TryUpdateChairPoseInRoom(Vector3 roomLocalPosition, Quaternion roomLocalRotation)
    {
        if (!ChairReferenceInitialized) return false;
        if (!ValidChairPose(roomLocalPosition, roomLocalRotation))
        {
            chairReferenceDetail = "Rejected manual/simulated pose: position must be finite and rotation normalized.";
            WriteChairReferenceStatus(); return false;
        }
        chairObject.transform.localPosition = roomLocalPosition; chairObject.transform.localRotation = roomLocalRotation;
        chairPoseSource = "explicit_manual_or_simulated_pose_api_not_live_observation";
        chairReferenceDetail = "Explicit manual/simulated pose input; no physical-chair movement observed by this component.";
        Debug.Log("CHAIR_REFERENCE manual/simulated Chair_01 pose update; not a live observation.");
        WriteChairReferenceStatus(); return true;
    }
    static string ValidateChairData(MeshExport data, ChairDefinition definition)
    {
        RequireChair(data != null && definition != null, "Mesh export and chair reference are required.");
        RequireChair(definition.schemaVersion == 1, "Unsupported chair schemaVersion.");
        RequireChair(definition.objectId == "Chair_01" && definition.semanticLabel == "Chair" && definition.identityConfirmed, "Chair_01 identity must be user-confirmed.");
        RequireChair(!string.IsNullOrWhiteSpace(definition.orientationSource) && !string.IsNullOrWhiteSpace(definition.sourceRunId), "Orientation source and source run are required.");
        RequireChair(Guid.TryParse(definition.sourceMeshAnchorUuid, out var expectedUuid), "Reference mesh UUID is invalid.");
        RequireChair(Guid.TryParse(data.anchorUuid, out var actualUuid), "Exported mesh UUID is invalid.");
        if (expectedUuid != actualUuid) throw new ChairReferenceMismatch("Reference belongs to a different Scene mesh anchor.");
        RequireChair(Guid.TryParse(definition.sourceObjectAnchorUuid, out _), "Contextual source-object anchor UUID is required.");
        RequireChair(data.vertices != null && data.triangles != null && data.vertexCount >= 3 && data.triangleCount >= 1, "Mesh geometry is missing.");
        RequireChair(data.vertices.Length == data.vertexCount && data.triangles.Length == checked(data.triangleCount * 3), "Mesh counts do not match arrays.");
        if (definition.sourceVertexCount != data.vertexCount || definition.sourceTriangleCount != data.triangleCount)
            throw new ChairReferenceMismatch("Saved mesh counts changed; reference not applied.");
        foreach (Vector3 vertex in data.vertices) RequireChair(Finite(vertex), "Non-finite mesh vertex.");
        foreach (int index in data.triangles) RequireChair(index >= 0 && index < data.vertexCount, "Mesh vertex index out of range.");
        RequireChair(IsChairHash(definition.meshGeometrySha256), "meshGeometrySha256 must contain 64 hexadecimal characters.");
        string hash = ChairGeometryHash(data.vertices, data.triangles);
        if (!StringComparer.OrdinalIgnoreCase.Equals(hash, definition.meshGeometrySha256))
            throw new ChairReferenceMismatch("Mesh geometry hash differs; original wireframe preserved. Actual SHA256: " + hash);
        RequireChair(ValidChairPose(definition.initialPositionMesh, definition.initialRotationMesh), "Reference pose must be finite with normalized rotation.");
        RequireChair(Finite(definition.boundsSize) && definition.boundsSize.x > 0 && definition.boundsSize.y > 0 && definition.boundsSize.z > 0, "Bounds must have finite positive full dimensions.");
        RequireChair(definition.selectedTriangleIndices != null && definition.selectedTriangleIndices.Length > 0 && definition.selectedTriangleIndices.Length < data.triangleCount, "Select a non-empty proper subset of triangle ordinals.");
        var seen = new HashSet<int>();
        foreach (int triangle in definition.selectedTriangleIndices) RequireChair(triangle >= 0 && triangle < data.triangleCount && seen.Add(triangle), "Triangle ordinals must be unique and in range.");
        return hash;
    }
    static bool ValidChairPose(Vector3 position, Quaternion rotation)
    {
        if (!Finite(position) || !Finite(rotation)) return false;
        float norm = rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w;
        return Finite(norm) && Mathf.Abs(norm - 1f) <= 0.0001f;
    }
    static bool IsChairHash(string value)
    {
        if (value == null || value.Length != 64) return false;
        foreach (char c in value) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
        return true;
    }
    static string ChairGeometryHash(Vector3[] vertices, int[] indices)
    {
        // BinaryWriter emits little-endian float32 and int32, matching the baseline hash.
        using (var bytes = new MemoryStream())
        {
            using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
            {
                foreach (Vector3 vertex in vertices) { writer.Write(vertex.x); writer.Write(vertex.y); writer.Write(vertex.z); }
                foreach (int index in indices) writer.Write(index);
            }
            bytes.Position = 0;
            using (var sha = SHA256.Create())
            {
                var text = new StringBuilder(64);
                foreach (byte value in sha.ComputeHash(bytes)) text.Append(value.ToString("x2"));
                return text.ToString();
            }
        }
    }
    static void RequireChair(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    static int[] ChairEdges(int[] triangles)
    {
        var edges = new int[checked(triangles.Length * 2)];
        for (int i = 0; i < triangles.Length; i += 3)
        {
            int j = i * 2;
            edges[j] = triangles[i]; edges[j + 1] = triangles[i + 1];
            edges[j + 2] = triangles[i + 1]; edges[j + 3] = triangles[i + 2];
            edges[j + 4] = triangles[i + 2]; edges[j + 5] = triangles[i];
        }
        return edges;
    }
    static bool IntArraysEqual(int[] a, int[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
    static Mesh CreateChairMarker(Vector3 size)
    {
        var vertices = new Vector3[12]; Vector3 half = size * 0.5f;
        for (int i = 0; i < 8; i++) vertices[i] = new Vector3((i & 1) == 0 ? -half.x : half.x, (i & 2) == 0 ? -half.y : half.y, (i & 4) == 0 ? -half.z : half.z);
        float length = Mathf.Max(size.z * 0.75f, 0.25f), y = half.y + 0.06f, arrowHead = Mathf.Min(length * 0.25f, 0.12f);
        vertices[8] = new Vector3(0, y, 0); vertices[9] = new Vector3(0, y, length);
        vertices[10] = new Vector3(-arrowHead, y, length - arrowHead); vertices[11] = new Vector3(arrowHead, y, length - arrowHead);
        var edges = new List<int>(30);
        for (int i = 0; i < 8; i++) for (int bit = 1; bit <= 4; bit *= 2) if ((i & bit) == 0) { edges.Add(i); edges.Add(i | bit); }
        edges.AddRange(new[] { 8, 9, 9, 10, 9, 11 });
        var mesh = new Mesh { name = "Chair_01 bounds and +Z front direction" };
        mesh.vertices = vertices; mesh.SetIndices(edges.ToArray(), MeshTopology.Lines, 0); mesh.RecalculateBounds(); return mesh;
    }
    static void AddChairRenderer(GameObject target, Mesh mesh, Material material)
    {
        target.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = target.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
    }
    void SetChairReferenceState(string state, string detail)
    {
        chairReferenceState = state; chairReferenceDetail = detail;
        Debug.Log("CHAIR_REFERENCE " + state + ": " + detail); WriteChairReferenceStatus();
    }
    IEnumerator WriteChairStatusPeriodically()
    {
        while (ChairReferenceInitialized) { yield return new WaitForSecondsRealtime(1f); WriteChairReferenceStatus(); }
    }
    void WriteChairReferenceStatus()
    {
        if (string.IsNullOrEmpty(runDirectory)) return; // editor smoke test writes no artifacts
        try
        {
            var report = new ChairRuntimeStatus {
                runId = status != null ? status.runId : "", updatedUtc = DateTime.UtcNow.ToString("o"),
                state = chairReferenceState, detail = chairReferenceDetail, objectId = "Chair_01",
                currentPoseSource = chairPoseSource, liveObservation = false,
                worldFrame = "Current Unity session world via saved Scene mesh anchor; relocalization can change world coordinates. No physical-chair tracking occurs.",
                referenceFrame = "Saved Scene mesh anchor local frame, meters; initial reference frozen. Chair local +Y is up; +Z is forward; confirmation recorded separately.",
                actualMeshGeometrySha256 = chairActualGeometryHash
            };
            if (chairDefinition != null)
            {
                report.sourceRunId = chairDefinition.sourceRunId; report.sourceMeshAnchorUuid = chairDefinition.sourceMeshAnchorUuid;
                report.sourceObjectAnchorUuid = chairDefinition.sourceObjectAnchorUuid; report.semanticLabel = chairDefinition.semanticLabel;
                report.identityConfirmed = chairDefinition.identityConfirmed; report.orientationConfirmed = chairDefinition.orientationConfirmed;
                report.orientationSource = chairDefinition.orientationSource; report.expectedMeshGeometrySha256 = chairDefinition.meshGeometrySha256;
                report.referencePositionMesh = chairDefinition.initialPositionMesh; report.referenceRotationMesh = chairDefinition.initialRotationMesh;
                report.boundsSize = chairDefinition.boundsSize; report.selectedTriangleCount = chairDefinition.selectedTriangleIndices != null ? chairDefinition.selectedTriangleIndices.Length : 0;
            }
            if (ChairReferenceInitialized)
            {
                report.referencePositionMesh = chairInitialPosition; report.referenceRotationMesh = chairInitialRotation;
                report.referencePositionWorld = chairRoomTransform.TransformPoint(chairInitialPosition);
                report.referenceRotationWorld = chairRoomTransform.rotation * chairInitialRotation;
                report.currentPositionMesh = chairObject.transform.localPosition; report.currentRotationMesh = chairObject.transform.localRotation;
                if (TryGetChairPose(out var pose)) { report.currentWorldPoseAvailable = true; report.currentPositionWorld = pose.position; report.currentRotationWorld = pose.rotation; }
                report.previewVisible = chairObject.activeInHierarchy && chairObject.GetComponent<MeshRenderer>().enabled;
            }
            Directory.CreateDirectory(runDirectory);
            string path = Path.Combine(runDirectory, "chair-reference-status.json");
            File.WriteAllText(path + ".tmp", JsonUtility.ToJson(report, true), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path); File.Move(path + ".tmp", path);
        }
        catch (Exception error) { Debug.LogError("CHAIR_REFERENCE status write failed: " + error.Message); }
    }
    void DisposeChairReference()
    {
        if (chairStatusCoroutine != null) { StopCoroutine(chairStatusCoroutine); chairStatusCoroutine = null; }
        if (chairBackgroundMesh != null && chairOriginalBackgroundIndices != null)
        {
            try { chairBackgroundMesh.SetIndices(chairOriginalBackgroundIndices, MeshTopology.Lines, 0); chairBackgroundMesh.RecalculateBounds(); }
            catch (Exception error) { Debug.LogError("CHAIR_REFERENCE cleanup restore failed: " + error.Message); }
        }
        DestroyChairOwned(chairObject); DestroyChairOwned(chairGeometryMesh); DestroyChairOwned(chairMarkerMesh);
        DestroyChairOwned(chairGeometryMaterial); DestroyChairOwned(chairMarkerMaterial);
        chairObject = null; chairRoomTransform = null; chairBackgroundMesh = null;
        chairGeometryMesh = chairMarkerMesh = null; chairGeometryMaterial = chairMarkerMaterial = null;
        chairOriginalBackgroundIndices = chairSourceVertexIndices = null; chairReferenceState = "not_initialized";
    }
    static void DestroyChairOwned(UnityEngine.Object item)
    {
        if (item == null) return;
#if UNITY_EDITOR
        if (!Application.isPlaying) { UnityEngine.Object.DestroyImmediate(item); return; }
#endif
        UnityEngine.Object.Destroy(item);
    }
#if UNITY_EDITOR
    public static bool ValidateChairReferenceJson(string meshJson, string chairJson, out string report)
    {
        try
        {
            var data = JsonUtility.FromJson<MeshExport>(meshJson); var definition = JsonUtility.FromJson<ChairDefinition>(chairJson);
            string hash = ValidateChairData(data, definition);
            report = "Validated Chair_01: " + definition.selectedTriangleIndices.Length + "/" + data.triangleCount + " selected triangles; SHA256 " + hash + ". Identity user-confirmed; heading " + (definition.orientationConfirmed ? "confirmed." : "inferred/unconfirmed."); return true;
        }
        catch (Exception error) { report = error.Message; return false; }
    }
    public static bool RunChairReferenceSmokeTest(string meshJson, string chairJson, out string report)
    {
        GameObject owner = null; Mesh wireframe = null; Material material = null; QuestSavedRoomMesh reader = null;
        try
        {
            RequireChair(!Application.isPlaying, "Run the smoke test in edit mode without a headset.");
            var data = JsonUtility.FromJson<MeshExport>(meshJson); var definition = JsonUtility.FromJson<ChairDefinition>(chairJson);
            ValidateChairData(data, definition);
            var shader = Shader.Find("GSAC/Saved Room Wireframe"); RequireChair(shader != null, "Saved Room Wireframe shader unavailable.");
            material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            owner = new GameObject("Chair reference editor smoke test") { hideFlags = HideFlags.HideAndDontSave };
            reader = owner.AddComponent<QuestSavedRoomMesh>(); reader.meshMaterial = material;
            var preview = new GameObject("Temporary matching saved room") { hideFlags = HideFlags.HideAndDontSave };
            preview.transform.SetParent(owner.transform, false);
            preview.transform.localPosition = new Vector3(1.2f, 0.7f, -2.4f); preview.transform.localRotation = Quaternion.Euler(0f, 37f, 0f);
            wireframe = new Mesh { indexFormat = IndexFormat.UInt32, hideFlags = HideFlags.HideAndDontSave }; wireframe.vertices = data.vertices;
            int[] fullEdges = ChairEdges(data.triangles); wireframe.SetIndices(fullEdges, MeshTopology.Lines, 0);
            preview.AddComponent<MeshFilter>().sharedMesh = wireframe;
            RequireChair(reader.InitializeChairWithDefinition(data, definition, preview, wireframe), "Initialization failed: " + reader.ChairReferenceDetail);
            RequireChair(reader.chairGeometryMesh.GetIndices(0).Length == definition.selectedTriangleIndices.Length * 6, "Selected partition count mismatch.");
            int[] background = wireframe.GetIndices(0);
            RequireChair(background.Length + reader.chairGeometryMesh.GetIndices(0).Length == fullEdges.Length, "Face partition does not cover source mesh.");
            RequireChair(reader.TryGetChairPose(out var initialWorld), "Initial world pose unavailable.");
            AssertChairWorldPose(initialWorld, preview.transform, definition.initialPositionMesh, definition.initialRotationMesh);
            Vector3[] extracted = reader.chairGeometryMesh.vertices;
            for (int i = 0; i < extracted.Length; i++) RequireChair(Vector3.Distance(reader.chairObject.transform.TransformPoint(extracted[i]), preview.transform.TransformPoint(data.vertices[reader.chairSourceVertexIndices[i]])) < 0.0001f, "Extracted geometry shifted in world space.");
            RequireChair(reader.TryGetChairInitialPoseInRoom(out var frozen), "Initial reference unavailable.");
            Vector3 movedPosition = frozen.position + new Vector3(0.35f, 0.1f, -0.2f);
            Quaternion movedRotation = Quaternion.AngleAxis(25f, Vector3.up) * frozen.rotation;
            RequireChair(reader.TryUpdateChairPoseInRoom(movedPosition, movedRotation), "Explicit pose update failed.");
            RequireChair(reader.TryGetChairPose(out var movedWorld), "Updated world pose unavailable.");
            AssertChairWorldPose(movedWorld, preview.transform, movedPosition, movedRotation);
            RequireChair(reader.TryGetChairInitialPoseInRoom(out var afterMove), "Frozen reference disappeared.");
            RequireChair(afterMove.position == frozen.position && Quaternion.Angle(afterMove.rotation, frozen.rotation) < 0.001f, "Initial reference changed during update.");
            RequireChair(IntArraysEqual(background, wireframe.GetIndices(0)) && ChairGeometryHash(wireframe.vertices, data.triangles) == definition.meshGeometrySha256.ToLowerInvariant(), "Background changed during update.");
            RequireChair(!reader.TryUpdateChairPoseInRoom(new Vector3(float.NaN, 0, 0), Quaternion.identity), "Non-finite pose accepted.");
            RequireChair(!reader.TryUpdateChairPoseInRoom(Vector3.zero, new Quaternion(0, 0, 0, 0)), "Zero quaternion accepted.");
            RequireChair(reader.TryGetChairPose(out var afterInvalid), "Pose lost after rejected update.");
            AssertChairWorldPose(afterInvalid, preview.transform, movedPosition, movedRotation);
            RequireChair(reader.TryUpdateChairPoseInRoom(frozen.position, frozen.rotation), "Reset to initial reference failed.");
            RequireChair(reader.TryGetChairPose(out var resetWorld), "Reset world pose unavailable.");
            AssertChairWorldPose(resetWorld, preview.transform, frozen.position, frozen.rotation);
            reader.DisposeChairReference(); RequireChair(IntArraysEqual(fullEdges, wireframe.GetIndices(0)), "Cleanup did not restore original background.");
            var changed = JsonUtility.FromJson<MeshExport>(meshJson); changed.vertices[0].x += 0.125f;
            RequireChair(!reader.InitializeChairWithDefinition(changed, definition, preview, wireframe) && reader.ChairReferenceState == "mismatch", "Changed mesh hash accepted.");
            RequireChair(IntArraysEqual(fullEdges, wireframe.GetIndices(0)) && preview.transform.childCount == 0, "Rejected reference changed background or left objects behind.");
            report = "PASS: exact mesh UUID/hash; " + definition.selectedTriangleIndices.Length + " selected and " + (data.triangleCount - definition.selectedTriangleIndices.Length) + " background faces; initial pose/geometry alignment; manual move/reset; frozen reference/background; invalid pose rejection; changed-hash rejection without mutation."; return true;
        }
        catch (Exception error) { report = "FAIL: " + error.Message; return false; }
        finally
        {
            if (reader != null) reader.DisposeChairReference();
            DestroyChairOwned(owner); DestroyChairOwned(wireframe); DestroyChairOwned(material);
        }
    }
    static void AssertChairWorldPose(Pose actual, Transform room, Vector3 position, Quaternion rotation)
    {
        RequireChair(Vector3.Distance(actual.position, room.TransformPoint(position)) < 0.0001f && Quaternion.Angle(actual.rotation, room.rotation * rotation) < 0.02f, "World pose does not match room-local pose.");
    }
#endif
    sealed class ChairReferenceMismatch : Exception { public ChairReferenceMismatch(string message) : base(message) { } }
    [Serializable] sealed class ChairDefinition
    {
        public int schemaVersion, sourceVertexCount, sourceTriangleCount;
        public string objectId, semanticLabel, orientationSource, sourceRunId, sourceMeshAnchorUuid, sourceObjectAnchorUuid, meshGeometrySha256;
        public bool identityConfirmed, orientationConfirmed;
        public Vector3 initialPositionMesh, boundsSize;
        public Quaternion initialRotationMesh;
        public int[] selectedTriangleIndices;
    }
    [Serializable] sealed class ChairRuntimeStatus
    {
        public int schemaVersion = 1, selectedTriangleCount;
        public string runId, updatedUtc, state, detail, objectId, semanticLabel, currentPoseSource, worldFrame, referenceFrame;
        public string sourceRunId, sourceMeshAnchorUuid, sourceObjectAnchorUuid, orientationSource, expectedMeshGeometrySha256, actualMeshGeometrySha256;
        public bool identityConfirmed, orientationConfirmed, liveObservation, currentWorldPoseAvailable, previewVisible;
        public Vector3 referencePositionMesh, referencePositionWorld, currentPositionMesh, currentPositionWorld, boundsSize;
        public Quaternion referenceRotationMesh, referenceRotationWorld, currentRotationMesh, currentRotationWorld;
    }
}
