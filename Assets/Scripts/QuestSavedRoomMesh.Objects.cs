// Saved Scene cuboids are annotations, not live tracking of movable furniture.
// This partial reads existing data only. It does not assign chair semantics.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

public sealed partial class QuestSavedRoomMesh
{
    readonly List<OVRAnchor> objectAnchors = new List<OVRAnchor>();
    readonly List<OVRAnchor> roomAnchors = new List<OVRAnchor>();
    bool objectAnchorOwnerDisposed;
    SavedObjectExport savedObjectExport;
    const double ObjectOperationTimeout = 30;

    IEnumerator ReadSavedObjects()
    {
        savedObjectExport = new SavedObjectExport {
            runId = status.runId, capturedUtc = DateTime.UtcNow.ToString("o"), state = "starting",
            coordinateSystem = "Unity session world coordinates in meters; Y is up. Bounds center and full size are in Scene-anchor local coordinates. Matrices are row-major; worldPoint = matrix * (x,y,z,1). Anchor matrix maps anchor-local coordinates; box matrix maps centered box coordinates measured in meters (it does not scale a unit cube). Corners use bit 0 for positive X, bit 1 for positive Y, bit 2 for positive Z; unset bits use negative half-size. Session world is not a persistent cross-session frame.",
            source = "Existing saved Scene cuboid annotations. Anchor localization does not track movement of the physical furniture. Labels are SDK classifications; no chair identity is inferred. Room names are unknown."
        };
        status.discoveredBoxes = status.exportedBoxes = status.failedBoxes = 0;
        status.objectQueryResult = "NotStarted";
        status.objectFile = Path.Combine(Path.GetFileName(runDirectory), "scene-objects.json").Replace('\\', '/');
        var iterator = ReadSavedObjectsCore();
        while (true)
        {
            bool more = false;
            object next = null;
            try { more = iterator.MoveNext(); if (more) next = iterator.Current; }
            catch (Exception error)
            {
                savedObjectExport.state = "failed";
                savedObjectExport.detail = error.ToString();
                Debug.LogError("AVATAR_ROOM_OBJECTS " + error);
            }
            if (!more) break;
            yield return next;
        }
        (iterator as IDisposable)?.Dispose();
        WriteSavedObjects();
        WriteStatus();
    }

    IEnumerator ReadSavedObjectsCore()
    {
        Directory.CreateDirectory(runDirectory);
        WriteSavedObjects();
#if !UNITY_ANDROID || UNITY_EDITOR
        savedObjectExport.state = "unsupported_platform";
        savedObjectExport.detail = "Saved Scene cuboids must be read on the Quest headset.";
        yield break;
#else
        if (headCamera == null || !UnityEngine.Android.Permission.HasUserAuthorizedPermission(ScenePermission))
        {
            savedObjectExport.state = "not_ready";
            savedObjectExport.detail = "A tracked camera and granted Spatial Data permission are required.";
            yield break;
        }
        savedObjectExport.trackingOrigin = OVRPlugin.GetTrackingOriginType().ToString();
        SetState("objects_querying", "Reading saved cuboid annotations. This does not track moving furniture.");
        var queryResult = new SavedAnchorQueryResult();
        var query = QuerySavedObjectAnchors<OVRBounded3D>(objectAnchors, queryResult);
        while (query.MoveNext()) yield return query.Current;
        savedObjectExport.queryResult = status.objectQueryResult = queryResult.result;
        savedObjectExport.queryApi = queryResult.api;
        savedObjectExport.queryDetail = queryResult.detail;
        if (!queryResult.success)
        {
            savedObjectExport.state = "query_failed";
            savedObjectExport.detail = queryResult.detail;
            yield break;
        }
        savedObjectExport.queryCandidateCount = objectAnchors.Count;
        objectAnchors.Sort((a, b) => StringComparer.Ordinal.Compare(a.Uuid.ToString(), b.Uuid.ToString()));
        var boxAnchors = new List<OVRAnchor>();
        foreach (var candidate in objectAnchors)
        {
            if (IsGlobalSavedMesh(candidate)) savedObjectExport.excludedSceneMeshUuids.Add(candidate.Uuid.ToString());
            else boxAnchors.Add(candidate);
        }
        status.discoveredBoxes = boxAnchors.Count;
        for (int i = 0; i < boxAnchors.Count; i++)
        {
            savedObjectExport.objects.Add(new SavedObjectRecord {
                displayId = "B" + (i + 1).ToString("D02"),
                anchorUuid = boxAnchors[i].Uuid.ToString(), state = "discovered"
            });
        }
        savedObjectExport.state = "reading_objects";
        WriteSavedObjects();
        for (int i = 0; i < boxAnchors.Count; i++)
        {
            var record = savedObjectExport.objects[i];
            SetState("objects_localizing", "Reading saved box " + record.displayId + " (" + record.anchorUuid + ").");
            var readOne = ReadOneSavedObject(boxAnchors[i], record);
            while (true)
            {
                bool more = false;
                object next = null;
                try { more = readOne.MoveNext(); if (more) next = readOne.Current; }
                catch (Exception error) { record.state = "read_failed"; record.detail = error.Message; }
                if (!more) break;
                yield return next;
            }
            (readOne as IDisposable)?.Dispose();
            if (record.worldTransformValid) status.exportedBoxes++;
            else status.failedBoxes++;
            WriteSavedObjects();
            WriteStatus();
            yield return null;
        }
        SetState("objects_querying_rooms", "Reading saved room-container membership; user room names are not inferred.");
        var roomResult = new SavedAnchorQueryResult();
        var roomQuery = QuerySavedObjectAnchors<OVRRoomLayout>(roomAnchors, roomResult);
        while (roomQuery.MoveNext()) yield return roomQuery.Current;
        savedObjectExport.roomQueryResult = roomResult.result;
        savedObjectExport.roomQueryApi = roomResult.api;
        if (roomResult.success)
        {
            roomAnchors.Sort((a, b) => StringComparer.Ordinal.Compare(a.Uuid.ToString(), b.Uuid.ToString()));
            foreach (var roomAnchor in roomAnchors) ReadSavedRoomMembership(roomAnchor);
        }
        else savedObjectExport.roomQueryDetail = roomResult.detail;
        savedObjectExport.state = status.discoveredBoxes == 0 ? "no_objects" :
            status.failedBoxes > 0 ? "partial" : "complete";
#endif
    }

    // Register completion immediately: late results release runtime handles even
    // after a timeout or destruction. No persisted Scene anchors are erased.
    IEnumerator QuerySavedObjectAnchors<T>(List<OVRAnchor> destination, SavedAnchorQueryResult result)
        where T : struct, IOVRAnchorComponent<T>
    {
        var candidates = new List<OVRAnchor>();
        bool done = false, expired = false;
        result.api = "Scene discovery";
        var query = OVRAnchor.FetchAnchorsAsync(candidates,
            new OVRAnchor.FetchOptions { SingleComponentType = typeof(T) });
        query.ContinueWith(completion => {
            done = true;
            if (expired || objectAnchorOwnerDisposed) { DisposeSavedObjectList(candidates); return; }
            result.success = completion.Success;
            result.result = completion.Success ? "Success" : completion.Status.ToString();
            if (completion.Success) { destination.AddRange(candidates); candidates.Clear(); }
            else DisposeSavedObjectList(candidates);
        });
        double deadline = Now + ObjectOperationTimeout;
        while (!done && Now < deadline) { Heartbeat(); yield return null; }
        if (!done)
        {
            expired = true;
            result.result = "Timeout";
            result.detail = "Scene discovery did not complete within 30 seconds; any late handles will be released.";
            yield break;
        }
        if (result.success || objectAnchorOwnerDisposed) yield break;
        string discoveryFailure = result.result;
        candidates = new List<OVRAnchor>();
        done = expired = false;
        result.api = "Compatible local Scene query";
#pragma warning disable CS0618
        var compatible = OVRAnchor.FetchAnchorsAsync<T>(candidates, OVRSpace.StorageLocation.Local, maxResults: 100, timeout: ObjectOperationTimeout);
#pragma warning restore CS0618
        compatible.ContinueWith(success => {
            done = true;
            if (expired || objectAnchorOwnerDisposed) { DisposeSavedObjectList(candidates); return; }
            result.success = success;
            result.result = success ? "Success" : "Failed";
            result.detail = "Primary discovery returned " + discoveryFailure + "; compatible local query " + (success ? "succeeded" : "failed") + ". Local query maximum: 100 anchors.";
            if (success) { destination.AddRange(candidates); candidates.Clear(); }
            else DisposeSavedObjectList(candidates);
        });
        deadline = Now + ObjectOperationTimeout;
        while (!done && Now < deadline) { Heartbeat(); yield return null; }
        if (!done)
        {
            expired = true;
            result.result = "Timeout";
            result.detail = "Primary discovery returned " + discoveryFailure + "; compatible query timed out after 30 seconds. Any late handles will be released.";
        }
    }

    bool IsGlobalSavedMesh(OVRAnchor candidate)
    {
        foreach (var meshAnchor in anchors) if (meshAnchor.Uuid == candidate.Uuid) return true;
        // Component presence does not establish GLOBAL_MESH identity. Saved object
        // anchors can pass the TriangleMesh component-status check as well; exclude
        // only a known exported mesh UUID or an explicit SceneMesh classification.
        try
        {
            if (candidate.TryGetComponent(out OVRSemanticLabels semantic))
            {
                var labels = new List<OVRSemanticLabels.Classification>();
                semantic.GetClassifications(labels);
                return labels.Contains(OVRSemanticLabels.Classification.SceneMesh);
            }
        }
        catch (Exception) { /* ReadOneSavedObject will persist any label-read error. */ }
        return false;
    }
    IEnumerator ReadOneSavedObject(OVRAnchor anchor, SavedObjectRecord record)
    {
        if (!anchor.TryGetComponent(out OVRBounded3D bounded) || !anchor.TryGetComponent(out OVRLocatable locatable))
        { record.state = "missing_required_component"; yield break; }
        Bounds bounds = bounded.BoundingBox;
        record.boundsCenterLocal = bounds.center;
        record.boundsSize = bounds.size;
        if (!Finite(bounds.center) || !Finite(bounds.size) || bounds.size.x <= 0 || bounds.size.y <= 0 || bounds.size.z <= 0)
        { record.state = "invalid_bounds"; record.detail = "Bounds must have finite center and finite positive full dimensions."; yield break; }
        try
        {
            if (anchor.TryGetComponent(out OVRSemanticLabels semantic))
            {
                var classifications = new List<OVRSemanticLabels.Classification>();
                semantic.GetClassifications(classifications);
                var labels = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var label in classifications) labels.Add(label.ToString());
                record.labels = new string[labels.Count]; labels.CopyTo(record.labels);
                record.labelsState = "read";
            }
            else record.labelsState = "unavailable";
        }
        catch (Exception error) { record.labelsState = "read_failed"; record.detail = "Labels: " + error.Message; }
        record.state = "localizing";
        bool enableDone = false, enabled = false;
        var enable = locatable.SetEnabledAsync(true, ObjectOperationTimeout);
        enable.ContinueWith(success => { enabled = success; enableDone = true; });
        double deadline = Now + ObjectOperationTimeout;
        while (!enableDone && Now < deadline) { Heartbeat(); yield return null; }
        if (!enableDone) { record.state = "localization_enable_timeout"; yield break; }
        if (!enabled) { record.state = "localization_enable_failed"; yield break; }
        deadline = Now + ObjectOperationTimeout;
        while (Now < deadline)
        {
            if (HeadReady() && locatable.TryGetSceneAnchorPose(out var pose) && pose.Position.HasValue && pose.Rotation.HasValue)
            {
                var position = pose.ComputeWorldPosition(headCamera);
                var rotation = pose.ComputeWorldRotation(headCamera);
                if (position.HasValue && rotation.HasValue && Finite(position.Value) && Finite(rotation.Value))
                {
                    var anchorMatrix = Matrix4x4.TRS(position.Value, rotation.Value, Vector3.one);
                    Vector3 center = anchorMatrix.MultiplyPoint3x4(bounds.center);
                    var boxMatrix = Matrix4x4.TRS(center, rotation.Value, Vector3.one);
                    var corners = new Vector3[8];
                    for (int corner = 0; corner < 8; corner++)
                    {
                        Vector3 offset = new Vector3((corner & 1) == 0 ? -bounds.extents.x : bounds.extents.x,
                            (corner & 2) == 0 ? -bounds.extents.y : bounds.extents.y,
                            (corner & 4) == 0 ? -bounds.extents.z : bounds.extents.z);
                        corners[corner] = boxMatrix.MultiplyPoint3x4(offset);
                        if (!Finite(corners[corner])) throw new InvalidOperationException("Transformed box corner is not finite.");
                    }
                    if (!Finite(center)) throw new InvalidOperationException("Transformed box center is not finite.");
                    record.anchorPositionWorld = position.Value;
                    record.anchorRotationWorld = record.boxRotationWorld = rotation.Value;
                    record.boxCenterWorld = center;
                    record.anchorLocalToWorld = Flatten(anchorMatrix);
                    record.boxLocalToWorld = Flatten(boxMatrix);
                    record.cornersWorld = corners;
                    record.posePositionTracked = pose.IsPositionTracked;
                    record.poseRotationTracked = pose.IsRotationTracked;
                    record.worldTransformValid = true;
                    record.state = "exported";
                    yield break;
                }
            }
            Heartbeat(); yield return null;
        }
        record.state = "not_localized";
        record.detail = "No valid box anchor pose while the headset was worn, tracked and focused within 30 seconds.";
    }

    void ReadSavedRoomMembership(OVRAnchor roomAnchor)
    {
        var room = new SavedRoomRecord { roomUuid = roomAnchor.Uuid.ToString(), state = "reading", name = "unknown" };
        savedObjectExport.rooms.Add(room);
        try
        {
            if (roomAnchor.TryGetComponent(out OVRRoomLayout layout) && layout.TryGetRoomLayout(out var ceiling, out var floor, out var walls))
            {
                room.ceilingUuid = ceiling.ToString(); room.floorUuid = floor.ToString();
                room.wallUuids = Array.ConvertAll(walls ?? Array.Empty<Guid>(), value => value.ToString());
                Array.Sort(room.wallUuids, StringComparer.Ordinal);
            }
            if (!roomAnchor.TryGetComponent(out OVRAnchorContainer container))
            { room.state = "container_unavailable"; return; }
            var children = new HashSet<string>(StringComparer.Ordinal);
            foreach (var child in container.Uuids) children.Add(child.ToString());
            room.childAnchorUuids = new string[children.Count]; children.CopyTo(room.childAnchorUuids);
            Array.Sort(room.childAnchorUuids, StringComparer.Ordinal);
            var meshChildren = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var meshAnchor in anchors) if (children.Contains(meshAnchor.Uuid.ToString())) meshChildren.Add(meshAnchor.Uuid.ToString());
            room.meshAnchorUuids = new string[meshChildren.Count]; meshChildren.CopyTo(room.meshAnchorUuids);
            foreach (var box in savedObjectExport.objects)
            {
                if (!children.Contains(box.anchorUuid)) continue;
                var memberships = new List<string>(box.roomUuids) { room.roomUuid };
                memberships.Sort(StringComparer.Ordinal);
                box.roomUuids = memberships.ToArray();
            }
            room.state = "read";
        }
        catch (Exception error) { room.state = "read_failed"; room.detail = error.Message; }
    }

    void WriteSavedObjects()
    {
        if (savedObjectExport == null) return;
        savedObjectExport.updatedUtc = DateTime.UtcNow.ToString("o");
        try
        {
            Directory.CreateDirectory(runDirectory);
            string path = Path.Combine(runDirectory, "scene-objects.json");
            File.WriteAllText(path + ".tmp", JsonUtility.ToJson(savedObjectExport, true), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(path + ".tmp", path);
        }
        catch (Exception error) { Debug.LogError("AVATAR_ROOM_OBJECTS export write failed: " + error.Message); }
    }

    void DisposeObjectAnchors()
    {
        objectAnchorOwnerDisposed = true;
        DisposeSavedObjectList(objectAnchors);
        DisposeSavedObjectList(roomAnchors);
    }

    static void DisposeSavedObjectList(List<OVRAnchor> values)
    {
        foreach (var value in values) value.Dispose();
        values.Clear();
    }

    sealed class SavedAnchorQueryResult
    {
        public bool success;
        public string result = "NotStarted", api, detail;
    }
    [Serializable] sealed class SavedObjectExport
    {
        public int schemaVersion = 1;
        public int queryCandidateCount;
        public string runId, capturedUtc, updatedUtc, state, detail, queryResult = "NotStarted", queryApi, queryDetail;
        public string roomQueryResult = "NotStarted", roomQueryApi, roomQueryDetail, trackingOrigin, coordinateSystem, source;
        public List<SavedObjectRecord> objects = new List<SavedObjectRecord>();
        public List<SavedRoomRecord> rooms = new List<SavedRoomRecord>();
        public List<string> excludedSceneMeshUuids = new List<string>();
    }
    [Serializable] sealed class SavedObjectRecord
    {
        public string displayId, anchorUuid, state, detail, labelsState;
        public string[] labels = Array.Empty<string>(), roomUuids = Array.Empty<string>();
        public Vector3 boundsCenterLocal, boundsSize, anchorPositionWorld, boxCenterWorld;
        public Quaternion anchorRotationWorld, boxRotationWorld;
        public float[] anchorLocalToWorld = Array.Empty<float>(), boxLocalToWorld = Array.Empty<float>();
        public Vector3[] cornersWorld = Array.Empty<Vector3>();
        public bool worldTransformValid, posePositionTracked, poseRotationTracked;
    }
    [Serializable] sealed class SavedRoomRecord
    {
        public string roomUuid, name, state, detail, ceilingUuid, floorUuid;
        public string[] wallUuids = Array.Empty<string>(), childAnchorUuids = Array.Empty<string>(), meshAnchorUuids = Array.Empty<string>();
    }
}