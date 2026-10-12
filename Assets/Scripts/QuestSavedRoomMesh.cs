// Saved room mesh component for the existing avatar scene.
// Reuses QuestARBootstrap and the tracked XROrigin camera.
// Manifest must declare com.oculus.permission.USE_SCENE. No MRUK is required.
// Reads saved triangle meshes and object boxes. Never requests Space Setup, saves/deletes
// anchors, reads Environment Depth, or manufactures replacement geometry.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Collections;
using Unity.XR.CoreUtils;
using UnityEngine.Rendering;
using UnityEngine;
using UnityEngine.Android;
using UnityEngine.XR;

public sealed partial class QuestSavedRoomMesh : MonoBehaviour
{
    [SerializeField] Material meshMaterial;
    readonly List<Mesh> previewMeshes = new List<Mesh>();
    readonly List<GameObject> previewObjects = new List<GameObject>();
    readonly List<OVRLocatable> previewLocations = new List<OVRLocatable>();
    public string State => status != null ? status.state : "idle";
    const string ScenePermission = "com.oculus.permission.USE_SCENE";
    const double PermissionTimeout = 180;
    const double TrackingTimeout = 180;
    const double QueryTimeout = 60;
    const double LocalizationTimeout = 30;
    readonly List<OVRAnchor> anchors = new List<OVRAnchor>();
    ReaderStatus status;
    string statusPath, runDirectory;
    InputDevice head;
    Camera headCamera;
    TextMesh statusLabel;
    bool paused, finished;
    double lastStatusWrite;

    void OnApplicationPause(bool value) { paused = value; }

    IEnumerator Start()
    {
        InitializeMeshVisibility();
        statusPath = Path.Combine(Application.persistentDataPath, "scene-mesh-status.json");
        string runId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture);
        runDirectory = Path.Combine(Application.persistentDataPath, "scene-mesh-" + runId);
        status = new ReaderStatus {
            runId = runId, outputDirectory = Path.GetFileName(runDirectory),
            queryScope = "Existing OVRTriangleMesh anchors discoverable by this app. Only successfully localized meshes are exported. Current-room membership is not verified; anchors are never merged.",
            source = "Meta Scene API persisted triangle mesh; not Environment Depth or semantic box geometry"
        };
        var iterator = ReadSceneSnapshot();
        // Catch errors from coroutine steps as well as synchronous file/SDK work.
        while (true)
        {
            object next = null;
            bool more = false;
            try { more = iterator.MoveNext(); if (more) next = iterator.Current; }
            catch (Exception error)
            {
                status.error = error.ToString();
                SetState("failed", error.Message);
                finished = true;
            }
            if (!more) break;
            yield return next;
        }
        finished = true;
    }

    IEnumerator ReadSceneSnapshot()
    {
        // Drive the two iterators here so Start's exception handler also covers
        // their MoveNext calls. Mesh and object exports share one Unity session.
        var meshReader = ReadExistingMeshes();
        while (meshReader.MoveNext()) yield return meshReader.Current;
        if (headCamera == null || !status.permissionGranted || !HeadReady()) yield break;
        var objectReader = ReadSavedObjects();
        while (objectReader.MoveNext()) yield return objectReader.Current;
        bool anyGeometry = status.exportedMeshes > 0 || status.exportedBoxes > 0;
        bool allExported = status.exportedMeshes > 0 && status.exportedMeshes == status.discoveredAnchors &&
                           status.objectQueryResult == "Success" && status.failedBoxes == 0;
        SetState(anyGeometry ? (allExported ? "complete" : "partial") : "no_geometry",
            "Saved " + status.exportedMeshes + " mesh(es) and " + status.exportedBoxes +
            " object box(es). These describe Space Setup annotations, not live object tracking.");
    }
    IEnumerator ReadExistingMeshes()
    {
        SetState("starting", "Reading existing saved Scene meshes. No room scan will be started.");
#if !UNITY_ANDROID || UNITY_EDITOR
        SetState("unsupported_platform", "Run this reader on the Quest headset; Editor output cannot establish a real saved room mesh.");
        yield break;
#else
        status.permissionGranted = Permission.HasUserAuthorizedPermission(ScenePermission);
        SetState("waiting_for_runtime", "Waiting for the avatar scene's Meta XR runtime.");
        double runtimeDeadline = Now + 60;
        while (!OVRManager.OVRManagerinitialized && Now < runtimeDeadline)
        { Heartbeat(); yield return null; }
        if (!OVRManager.OVRManagerinitialized)
        { SetState("runtime_unavailable", "The avatar's Oculus AR profile did not initialize. Apply General Operator Build Mode and rebuild."); yield break; }
        var origin = FindObjectOfType<XROrigin>();
        headCamera = origin != null ? origin.Camera : null;
        if (headCamera == null)
        { SetState("configuration_error", "The avatar scene needs an XROrigin with a tracked Camera."); yield break; }
        CreateStatusLabel();
        if (!Permission.HasUserAuthorizedPermission(ScenePermission))
        {
            SetState("waiting_for_permission", "Please respond to the headset's Spatial Data permission prompt.");
            // QuestARBootstrap owns the normal system permission request.
            // Wait here so the two components never race to open the prompt.
            double deadline = Now + PermissionTimeout;
            while (!Permission.HasUserAuthorizedPermission(ScenePermission) && Now < deadline)
            { Heartbeat(); yield return null; }
            if (!Permission.HasUserAuthorizedPermission(ScenePermission))
            {
                SetState("permission_timeout",
                    "Spatial Data permission was not granted. No mesh was read; change permission in headset settings and restart to retry.");
                yield break;
            }
        }
        status.permissionGranted = true;
        SetState("waiting_for_tracking", "Wear the headset in the saved room and keep this app in the foreground.");
        double trackingDeadline = Now + TrackingTimeout;
        double stableSince = -1;
        while (Now < trackingDeadline)
        {
            if (!HeadReady()) stableSince = -1;
            else if (stableSince < 0) stableSince = Now;
            if (stableSince >= 0 && Now - stableSince >= 2) break;
            Heartbeat(); yield return null;
        }
        if (!HeadReady() || stableSince < 0 || Now - stableSince < 2)
        { SetState("tracking_timeout", "No stable worn, tracked, foreground headset within the timeout. No mesh was read."); yield break; }
        Vector3 scale = headCamera.transform.lossyScale;
        if ((scale - Vector3.one).sqrMagnitude > 0.000001f)
        { SetState("configuration_error", "Use unit scale on the camera rig and its parents so exported world positions are in meters."); yield break; }
        Directory.CreateDirectory(runDirectory);
        SetState("querying_existing_meshes", "Querying existing triangle mesh anchors. Current-room membership is not inferred from this query.");
        var query = OVRAnchor.FetchAnchorsAsync(anchors,
            new OVRAnchor.FetchOptions { SingleComponentType = typeof(OVRTriangleMesh) });
        double queryDeadline = Now + QueryTimeout;
        while (!query.IsCompleted && Now < queryDeadline) { Heartbeat(); yield return null; }
        if (!query.IsCompleted)
        {
            // The native request may finish later. Release its returned runtime
            // handles without persisting, erasing, or exporting any scene data.
            query.ContinueWith(result => {
                foreach (var lateAnchor in anchors) lateAnchor.Dispose();
                anchors.Clear();
            });
            SetState("query_timeout", "Scene mesh discovery did not finish. No mesh was exported.");
            yield break;
        }
        var queryResult = query.GetResult();
        status.queryResult = queryResult.Status.ToString();
        if (!queryResult.Success)
        {
            SetState("querying_compatible_api", "Scene discovery failed (" + status.queryResult + "); trying the SDK's local Scene query.");
            foreach (var unusedAnchor in anchors) unusedAnchor.Dispose();
            anchors.Clear();
#pragma warning disable CS0618
            var compatibleQuery = OVRAnchor.FetchAnchorsAsync<OVRTriangleMesh>(anchors, OVRSpace.StorageLocation.Local, maxResults: 100, timeout: 30.0);
#pragma warning restore CS0618
            double compatibleDeadline = Now + QueryTimeout;
            while (!compatibleQuery.IsCompleted && Now < compatibleDeadline) { Heartbeat(); yield return null; }
            if (!compatibleQuery.IsCompleted)
            {
                compatibleQuery.ContinueWith(result => { foreach (var lateAnchor in anchors) lateAnchor.Dispose(); anchors.Clear(); });
                SetState("query_timeout", "Compatible Scene query timed out. No mesh was exported.");
                yield break;
            }
            bool compatibleSuccess = compatibleQuery.GetResult();
            status.queryResult += compatibleSuccess ? "; local_query_success" : "; local_query_failed";
            if (!compatibleSuccess)
            { SetState("query_failed", "Both Scene query methods failed: " + status.queryResult); yield break; }
        }
        status.discoveredAnchors = anchors.Count;
        if (anchors.Count == 0)
        {
            SetState("no_mesh", "Scene API returned no existing triangle mesh anchors accessible to this app. This does not by itself prove that no room setup exists. No scan was started.");
            yield break;
        }
        foreach (var anchor in anchors)
        {
            var record = new AnchorRecord { anchorUuid = anchor.Uuid.ToString(), state = "discovered", currentRoomMembership = "not_verified" };
            status.anchors.Add(record);
            SetState("localizing", "Localizing mesh anchor " + record.anchorUuid + ". Other saved rooms may be present in the query.");
            if (!anchor.TryGetComponent(out OVRLocatable locatable) || !anchor.TryGetComponent(out OVRTriangleMesh triangleMesh))
            { record.state = "missing_required_component"; WriteStatus(); continue; }
            var enable = locatable.SetEnabledAsync(true, LocalizationTimeout);
            double anchorDeadline = Now + LocalizationTimeout;
            while (!enable.IsCompleted && Now < anchorDeadline) { Heartbeat(); yield return null; }
            if (!enable.IsCompleted)
            { record.state = "localization_enable_timeout"; enable.Dispose(); WriteStatus(); continue; }
            if (!enable.GetResult())
            { record.state = "localization_enable_failed"; WriteStatus(); continue; }
            bool localized = false;
            Matrix4x4 localToWorld = Matrix4x4.identity;
            OVRLocatable.TrackingSpacePose pose = default;
            anchorDeadline = Now + LocalizationTimeout;
            while (Now < anchorDeadline)
            {
                if (HeadReady() && locatable.TryGetSceneAnchorPose(out pose) &&
                    pose.Position.HasValue && pose.Rotation.HasValue)
                {
                    Vector3? worldPosition = pose.ComputeWorldPosition(headCamera);
                    Quaternion? worldRotation = pose.ComputeWorldRotation(headCamera);
                    if (worldPosition.HasValue && worldRotation.HasValue && Finite(worldPosition.Value) && Finite(worldRotation.Value))
                    {
                        localToWorld = Matrix4x4.TRS(worldPosition.Value, worldRotation.Value, Vector3.one);
                        localized = true; break;
                    }
                }
                Heartbeat(); yield return null;
            }
            if (!localized)
            { record.state = "not_localized"; record.detail = "No valid anchor pose while headset tracking was available; geometry was not read or exported."; WriteStatus(); continue; }
            record.localized = true;
            record.posePositionTracked = pose.IsPositionTracked;
            record.poseRotationTracked = pose.IsRotationTracked;
            SetState("exporting", "Exporting localized saved mesh " + record.anchorUuid + ". Current-room membership remains unverified.");
            try
            {
                ExportMesh(triangleMesh, record, pose, localToWorld);
                status.exportedMeshes++;
                previewLocations.Add(locatable);
            }
            catch (Exception error)
            { record.state = "export_failed"; record.detail = error.Message; status.failedExports++; }
            WriteStatus();
            yield return null;
        }
        // Multiple saved rooms are kept separate. Do not overlay unverified rooms.
        if (previewObjects.Count == 1) previewObjects[0].SetActive(true);
        int skipped = status.discoveredAnchors - status.exportedMeshes;
        SetState(status.exportedMeshes == 0 ? "no_localized_mesh_exported" : skipped == 0 ? "meshes_exported" : "meshes_partial",
            "Exported " + status.exportedMeshes + " separate localized mesh anchor(s); " + skipped + " not exported. Current-room membership is not verified. See individual UUID records.");
#endif
    }

    void ExportMesh(OVRTriangleMesh triangleMesh, AnchorRecord record, OVRLocatable.TrackingSpacePose pose, Matrix4x4 localToWorld)
    {
        if (!triangleMesh.TryGetCounts(out int vertexCount, out int triangleCount) || vertexCount < 3 || triangleCount < 1)
            throw new InvalidOperationException("Scene API did not return a non-empty triangle mesh.");
        int indexCount = checked(triangleCount * 3);
        // Bound unexpected native counts before allocating native and managed copies.
        if (vertexCount > 1000000 || triangleCount > 2000000) throw new InvalidOperationException("Unexpectedly large Scene mesh; export stopped before allocation.");
        using (var vertices = new NativeArray<Vector3>(vertexCount, Allocator.Temp))
        using (var indices = new NativeArray<int>(indexCount, Allocator.Temp))
        {
            if (!triangleMesh.TryGetMesh(vertices, indices)) throw new InvalidOperationException("Scene API triangle mesh read failed.");
            for (int i = 0; i < vertexCount; i++) if (!Finite(vertices[i])) throw new InvalidOperationException("Mesh contains non-finite vertices.");
            for (int i = 0; i < indexCount; i++) if (indices[i] < 0 || indices[i] >= vertexCount) throw new InvalidOperationException("Mesh contains an out-of-range triangle index.");
            string stem = "mesh-" + record.anchorUuid;
            var data = new MeshExport {
                anchorUuid = record.anchorUuid, capturedUtc = DateTime.UtcNow.ToString("o"),
                source = status.source, currentRoomMembership = "not_verified", worldTransformValid = true,
                posePositionTracked = pose.IsPositionTracked, poseRotationTracked = pose.IsRotationTracked,
                coordinateSystem = "Vertices: Unity Scene-anchor local coordinates, meters, as returned by SDK 67 OVRTriangleMesh.TryGetMesh. Indices have Unity winding. Matrix maps these local vertices into this Unity session world; not a persistent cross-session world frame.",
                matrixLayout = "row-major 16 floats; worldPoint = localToWorld * (x,y,z,1)",
                plyCoordinateSystem = "Right-handed preview world coordinates in meters: (Unity world x, Unity world y, -Unity world z). Triangle winding reversed. PLY is separate for this UUID; current-room membership unverified.",
                trackingOrigin = OVRPlugin.GetTrackingOriginType().ToString(),
                localToWorld = Flatten(localToWorld), trackingPosition = pose.Position.Value, trackingRotation = pose.Rotation.Value,
                cameraWorldPosition = headCamera.transform.position, cameraWorldRotation = headCamera.transform.rotation,
                vertexCount = vertexCount, triangleCount = triangleCount,
                vertices = vertices.ToArray(), triangles = indices.ToArray()
            };
            string jsonName = stem + ".json", plyName = stem + ".ply";
            string jsonPath = Path.Combine(runDirectory, jsonName), plyPath = Path.Combine(runDirectory, plyName);
            File.WriteAllText(jsonPath + ".tmp", JsonUtility.ToJson(data, false), new UTF8Encoding(false));
            using (var writer = new StreamWriter(plyPath + ".tmp", false, new UTF8Encoding(false)))
            {
                writer.WriteLine("ply\nformat ascii 1.0");
                writer.WriteLine("comment source Meta Scene API existing saved triangle mesh");
                writer.WriteLine("comment anchor_uuid " + record.anchorUuid);
                writer.WriteLine("comment current_room_membership NOT_VERIFIED");
                writer.WriteLine("comment coordinates meters RH preview: UnityWorldX UnityWorldY negativeUnityWorldZ");
                writer.WriteLine("element vertex " + vertexCount.ToString(CultureInfo.InvariantCulture));
                writer.WriteLine("property float x\nproperty float y\nproperty float z");
                writer.WriteLine("element face " + triangleCount.ToString(CultureInfo.InvariantCulture));
                writer.WriteLine("property list uchar int vertex_indices\nend_header");
                for (int i = 0; i < vertexCount; i++)
                {
                    Vector3 p = localToWorld.MultiplyPoint3x4(vertices[i]);
                    writer.WriteLine(F(p.x) + " " + F(p.y) + " " + F(-p.z));
                }
                for (int i = 0; i < indexCount; i += 3)
                    writer.WriteLine("3 " + indices[i] + " " + indices[i + 2] + " " + indices[i + 1]);
            }
            File.Move(jsonPath + ".tmp", jsonPath);
            File.Move(plyPath + ".tmp", plyPath);
            record.jsonFile = Path.Combine(status.outputDirectory, jsonName).Replace('\\', '/');
            record.plyFile = Path.Combine(status.outputDirectory, plyName).Replace('\\', '/');
            record.vertexCount = vertexCount; record.triangleCount = triangleCount;
            record.worldTransformValid = true;
            record.state = "exported";
            CreatePreview(data, localToWorld);
        }
    }

    void CreateStatusLabel()
    {
        var labelObject = new GameObject("Room Mesh Status");
        labelObject.transform.SetParent(headCamera.transform, false);
        labelObject.transform.localPosition = new Vector3(0, 0.28f, 1.4f);
        statusLabel = labelObject.AddComponent<TextMesh>();
        statusLabel.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        statusLabel.fontSize = 40;
        statusLabel.characterSize = 0.003f;
        statusLabel.anchor = TextAnchor.MiddleCenter;
        statusLabel.alignment = TextAlignment.Center;
        labelObject.GetComponent<MeshRenderer>().sharedMaterial = statusLabel.font.material;
        ApplyMeshVisibility();
    }

    void CreatePreview(MeshExport data, Matrix4x4 localToWorld)
    {
        var preview = new GameObject("Saved Room Mesh " + data.anchorUuid);
        preview.SetActive(false);
        preview.transform.SetParent(transform, false);
        preview.transform.SetPositionAndRotation(localToWorld.GetColumn(3), localToWorld.rotation);
        var mesh = new Mesh { name = preview.name, indexFormat = IndexFormat.UInt32 };
        mesh.vertices = data.vertices;
        var edges = new int[data.triangles.Length * 2];
        for (int i = 0; i < data.triangles.Length; i += 3)
        {
            int j = i * 2;
            edges[j] = data.triangles[i]; edges[j + 1] = data.triangles[i + 1];
            edges[j + 2] = data.triangles[i + 1]; edges[j + 3] = data.triangles[i + 2];
            edges[j + 4] = data.triangles[i + 2]; edges[j + 5] = data.triangles[i];
        }
        mesh.SetIndices(edges, MeshTopology.Lines, 0);
        mesh.RecalculateBounds();
        preview.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = preview.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = meshMaterial;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        previewMeshes.Add(mesh);
        previewObjects.Add(preview);
        InitializeChair(data, preview, mesh);
        ApplyMeshVisibility();
    }

    void LateUpdate()
    {
        PollMeshVisibilityInput();
        if (headCamera == null || !finished) return;
        for (int i = 0; i < previewLocations.Count; i++)
        {
            if (!previewLocations[i].TryGetSceneAnchorPose(out var pose)) continue;
            var position = pose.ComputeWorldPosition(headCamera);
            var rotation = pose.ComputeWorldRotation(headCamera);
            if (position.HasValue && rotation.HasValue && Finite(position.Value) && Finite(rotation.Value))
                previewObjects[i].transform.SetPositionAndRotation(position.Value, rotation.Value);
        }
    }

    bool HeadReady()
    {
        if (!head.isValid) head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
        bool tracked;
        if (!head.isValid || !head.TryGetFeatureValue(CommonUsages.isTracked, out tracked))
            tracked = OVRPlugin.GetNodePositionTracked(OVRPlugin.Node.Head) && OVRPlugin.GetNodeOrientationTracked(OVRPlugin.Node.Head);
        bool worn;
        if (!head.isValid || !head.TryGetFeatureValue(CommonUsages.userPresence, out worn))
            worn = OVRPlugin.userPresent;
        status.headTracked = tracked;
        status.headWorn = worn;
        status.hasVrFocus = OVRPlugin.hasVrFocus;
        return !paused && Application.isFocused && status.hasVrFocus && tracked && worn;
    }
    static double Now => Time.realtimeSinceStartupAsDouble;
    static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    static bool Finite(Vector3 p) => Finite(p.x) && Finite(p.y) && Finite(p.z);
    static bool Finite(Quaternion q) => Finite(q.x) && Finite(q.y) && Finite(q.z) && Finite(q.w);
    static string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);
    static float[] Flatten(Matrix4x4 matrix)
    {
        var result = new float[16];
        for (int row = 0; row < 4; row++) for (int col = 0; col < 4; col++) result[row * 4 + col] = matrix[row, col];
        return result;
    }
    void SetState(string state, string message)
    {
        status.state = state; status.message = message;
        if (statusLabel != null)
        {
            string display = state switch {
                "starting" or "waiting_for_runtime" => "Starting room mesh...\nPlease wait.",
                _ when state.StartsWith("objects_") => "Reading saved object boxes...\nPlease keep the headset on.",
                "waiting_for_permission" => "Allow Spatial Data\nwhen prompted.",
                "waiting_for_tracking" => "Keep the headset on\nand look around the room.",
                "querying_existing_meshes" or "querying_compatible_api" or "localizing" or "exporting" => "Reading the saved room mesh...\nPlease keep the headset on.",
                "meshes_exported" or "meshes_partial" => "Room mesh read.\nReading object boxes next...",
                "complete" or "partial" => "Saved geometry exported.\nYou can remove the headset.",
                "no_mesh" => "No saved mesh was returned.\nCheck the computer for details.",
                _ => "Mesh reading stopped.\nCheck the computer for next steps."
            };
            statusLabel.text = "Avatar Room Mesh\n\n" + display;
        }
        Debug.Log("AVATAR_ROOM_MESH " + state + ": " + message);
        WriteStatus();
    }
    void Heartbeat() { if (Now - lastStatusWrite >= 1) WriteStatus(); }
    void WriteStatus()
    {
        status.updatedUtc = DateTime.UtcNow.ToString("o");
        status.elapsedSeconds = Now;
        lastStatusWrite = Now;
        try { File.WriteAllText(statusPath, JsonUtility.ToJson(status, true), new UTF8Encoding(false)); }
        catch (Exception error) { Debug.LogError("AVATAR_ROOM_MESH status write failed: " + error.Message); }
    }
    void OnDestroy()
    {
        DisposeChairReference();
        DisposeObjectAnchors();
        foreach (var mesh in previewMeshes) Destroy(mesh);
        if (statusLabel != null) Destroy(statusLabel.gameObject);
        // Dispose destroys runtime handles only; it does not erase persisted scenes.
        foreach (var anchor in anchors) anchor.Dispose();
        anchors.Clear();
        if (status != null && !finished) { status.state = "interrupted"; status.message = "Reader stopped before completion."; WriteStatus(); }
    }
    void OnGUI()
    {
        if (GUI.Button(new Rect(20, MeshesVisible ? 210 : 20, 280, 44),
            MeshesVisible ? "Hide room + chair meshes" : "Show room + chair meshes")) ToggleMeshes();
        if (!MeshesVisible || status == null) return;
        GUI.Box(new Rect(20, 20, Math.Min(Screen.width - 40, 1000), 180),
            "Saved Scene Mesh Reader\n" + status.state + "\n" + status.message +
            "\nExported: " + status.exportedMeshes + " / " + status.discoveredAnchors +
            "\nNo room scan is started. Separate anchor files; current-room membership unverified.");
    }

    [Serializable] sealed class ReaderStatus
    {
        public string runId, updatedUtc, state, message, source, queryScope, queryResult, outputDirectory, error;
        public double elapsedSeconds;
        public bool permissionGranted, headTracked, headWorn, hasVrFocus;
        public int discoveredAnchors, exportedMeshes, failedExports;
        public string objectQueryResult, objectFile;
        public int discoveredBoxes, exportedBoxes, failedBoxes;
        public List<AnchorRecord> anchors = new List<AnchorRecord>();
    }
    [Serializable] sealed class AnchorRecord
    {
        public string anchorUuid, currentRoomMembership, state, detail, jsonFile, plyFile;
        public bool localized, posePositionTracked, poseRotationTracked, worldTransformValid;
        public int vertexCount, triangleCount;
    }
    [Serializable] sealed class MeshExport
    {
        public string anchorUuid, capturedUtc, source, currentRoomMembership, coordinateSystem, matrixLayout, plyCoordinateSystem, trackingOrigin;
        public bool worldTransformValid, posePositionTracked, poseRotationTracked;
        public int vertexCount, triangleCount;
        public float[] localToWorld;
        public Vector3 trackingPosition, cameraWorldPosition;
        public Quaternion trackingRotation, cameraWorldRotation;
        public Vector3[] vertices;
        public int[] triangles;
    }
}
