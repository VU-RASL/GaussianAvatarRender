using System.Collections.Generic;
using GaussianSplatting.Runtime;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

public sealed class ARGroundPlacement : MonoBehaviour
{
    [SerializeField] Transform avatarRoot;
    [SerializeField] Camera arCamera;
    [SerializeField] Transform[] extraGroundedObjects;
    [SerializeField] float distanceInFrontOfCamera = 2.0f;
    [SerializeField, Range(0.6f, 1.2f)] float shoulderTouchDistanceInMeters = 0.8f;
    [SerializeField] float floorHeightOffset;
    [SerializeField] float estimatedHeadHeight = 1.6f;
    [SerializeField] float retrySeconds = 8.0f;
    [SerializeField] bool faceCamera = true;
    [SerializeField] bool configureXROriginForAR = true;
    [SerializeField] bool keepGroundDetectionRunning;

    static readonly List<ARRaycastHit> s_Hits = new();

    XROrigin xrOrigin;
    ARPlaneManager planeManager;
    ARRaycastManager raycastManager;
    float startTime;
    bool configuredXROrigin;
    bool placedOnce;
    bool placedOnDetectedGround;
    bool useXRTrackingFloor;
    bool isARMode;
    bool useTouchPlacement;
    float headTrackedSince = -1;
    float nextTouchAnchorAttempt;
    InputDevice headDevice;
    PoseController touchPose;
    Transform touchMesh, touchGaussian, touchLeftShoulder, touchRightShoulder;

    bool hasManualChairPlacement;
    Transform manuallyPlacedAvatarRoot;
    Transform calibratedChairAvatarRoot;
    PoseController chairPoseController;
    Transform chairMesh, chairGaussian, chairPelvis;
    Vector3 chairGroundContactLocal, chairForwardLocal;
    bool chairCalibrationReady;
    string chairPlacementDetail = "Waiting for the avatar geometry.";

    public Transform AvatarRoot
    {
        get { ResolveAvatarReference(); return avatarRoot; }
    }

    public bool CanPlaceAvatarAtChairPose => TryGetChairGeometry(out _);
    public string ChairPlacementDetail => chairPlacementDetail;
    public bool ReadyForShoulderTouch => !useTouchPlacement || placedOnce;
    bool HasManualChairPlacement => hasManualChairPlacement &&
        manuallyPlacedAvatarRoot != null && manuallyPlacedAvatarRoot == avatarRoot;

    // Places the existing body at the chair footprint; it does not change its pose.
    // All input validation and calibration finish before any scene transform changes.
    public bool TryPlaceAvatarAtChairPose(Pose chairWorldPose, float chairHeight)
    {
        if (!TryNormalizeFiniteRotation(chairWorldPose.rotation, out Quaternion chairRotation) ||
            !Finite(chairWorldPose.position) || !Finite(chairHeight) || chairHeight <= 0)
            return ChairPlacementUnavailable("Invalid chair position, rotation, or height.");
        if (!TryGetChairGeometry(out Vector3[] vertices))
            return false;
        if (!chairCalibrationReady && !TryCalibrateChairContact(vertices))
            return false;

        Pose normalizedChairPose = new(chairWorldPose.position, chairRotation);
        if (!TryCalculateChairRootPose(normalizedChairPose, chairHeight, chairGroundContactLocal,
                chairForwardLocal, avatarRoot.lossyScale, out Pose rootPose))
            return ChairPlacementUnavailable("The chair heading or avatar scale cannot produce a valid placement.");

        CommitChairRootPose(rootPose);
        return true;
    }

    // Capture the standing placement reference before a manual pose can change
    // the body's foot height. Keep the root exactly where the user placed it.
    public bool TryPreparePoseChangePlacement()
    {
        if (!TryGetChairGeometry(out Vector3[] vertices)) return false;
        if (!chairCalibrationReady && !TryCalibrateChairContact(vertices)) return false;
        hasManualChairPlacement = true;
        manuallyPlacedAvatarRoot = avatarRoot;
        placedOnce = true;
        placedOnDetectedGround = true;
        chairPlacementDetail = "Avatar placement preserved for manual pose switching.";
        return true;
    }

    // Left-stick movement changes only this avatar's horizontal world position.
    // Lock startup placement even when the user has not selected T0/T1 yet.
    public bool TryMoveAvatarHorizontal(Vector3 worldDelta)
    {
        if (!Finite(worldDelta))
            return ChairPlacementUnavailable("The requested avatar movement is invalid.");
        worldDelta.y = 0;
        if (worldDelta.sqrMagnitude <= 0) return true;
        if (!TryGetChairGeometry(out _)) return false;
        Vector3 nextPosition = avatarRoot.position + worldDelta;
        nextPosition.y = avatarRoot.position.y;
        if (!Finite(nextPosition))
            return ChairPlacementUnavailable("The requested avatar position is invalid.");
        avatarRoot.position = nextPosition;
        hasManualChairPlacement = true;
        manuallyPlacedAvatarRoot = avatarRoot;
        placedOnce = true;
        placedOnDetectedGround = true;
        chairPlacementDetail = "Avatar moved with the left stick; height, facing, and body pose preserved.";
        return true;
    }
    void CommitChairRootPose(Pose rootPose)
    {
        avatarRoot.SetPositionAndRotation(rootPose.position, rootPose.rotation);
        hasManualChairPlacement = true;
        manuallyPlacedAvatarRoot = avatarRoot;
        placedOnce = true;
        placedOnDetectedGround = true;
        chairPlacementDetail = "Avatar placed at the selected chair footprint; current body pose preserved.";
    }

    void ResolveAvatarReference()
    {
        if (avatarRoot != null) return;
        var avatar = GameObject.Find("Avatar");
        if (avatar != null) avatarRoot = avatar.transform;
    }

    bool ChairPlacementUnavailable(string detail)
    {
        chairPlacementDetail = detail;
        return false;
    }

    bool TryGetChairGeometry(out Vector3[] vertices)
    {
        vertices = null;
        ResolveAvatarReference();
        if (avatarRoot == null || !avatarRoot.gameObject.activeInHierarchy)
            return ChairPlacementUnavailable("The avatar root is not active.");
        // A mistaken reference must never move the headset or its tracking origin.
        Camera camera = arCamera != null ? arCamera : Camera.main;
        XROrigin origin = xrOrigin != null ? xrOrigin : FindObjectOfType<XROrigin>(true);
        if ((camera != null && camera.transform.IsChildOf(avatarRoot)) ||
            (origin != null && origin.transform.IsChildOf(avatarRoot)))
            return ChairPlacementUnavailable("The avatar root must be separate from the XR origin and camera.");
        if (!PositiveFiniteScale(avatarRoot.lossyScale))
            return ChairPlacementUnavailable("The avatar requires a finite, positive scale.");
        // The scene avatar is unparented. Uniformly scaled parents are also safe;
        // nonuniform ancestor scale can shear the body when world yaw changes.
        for (Transform parent = avatarRoot.parent; parent != null; parent = parent.parent)
        {
            Vector3 scale = parent.localScale;
            if (!PositiveFiniteScale(scale) ||
                Mathf.Abs(scale.x - scale.y) > 0.0001f || Mathf.Abs(scale.x - scale.z) > 0.0001f)
                return ChairPlacementUnavailable("The avatar needs uniformly scaled parent transforms.");
        }

        if (calibratedChairAvatarRoot != avatarRoot || chairPoseController == null)
        {
            chairCalibrationReady = false;
            calibratedChairAvatarRoot = avatarRoot;
            chairPoseController = avatarRoot.GetComponentInChildren<PoseController>();
            chairMesh = chairGaussian = chairPelvis = null;
        }
        if (chairPoseController == null || !chairPoseController.isActiveAndEnabled ||
            chairPoseController.smplx == null || chairPoseController.vertexBuffer == null ||
            !chairPoseController.vertexBuffer.IsValid())
            return ChairPlacementUnavailable("Waiting for the avatar pose and vertex buffer.");
        vertices = chairPoseController.GetCurrentVertices();
        if (vertices == null || vertices.Length == 0)
            return ChairPlacementUnavailable("Waiting for the first avatar vertex bake.");

        if (chairMesh == null || chairGaussian == null || chairPelvis == null)
        {
            chairCalibrationReady = false;
            var mesh = chairPoseController.smplx.GetComponentInChildren<SkinnedMeshRenderer>();
            var gaussian = chairPoseController.GetComponent<GaussianSplatRenderer>();
            if (mesh == null || gaussian == null || !gaussian.isActiveAndEnabled)
                return ChairPlacementUnavailable("Waiting for the visible Gaussian avatar and source mesh.");
            chairMesh = mesh.transform;
            chairGaussian = gaussian.transform;
            foreach (Transform bone in chairPoseController.smplx.GetComponentsInChildren<Transform>(true))
                if (bone.name == "pelvis") { chairPelvis = bone; break; }
        }
        if (chairPelvis == null || !chairMesh.IsChildOf(avatarRoot) || !chairGaussian.IsChildOf(avatarRoot))
            return ChairPlacementUnavailable("The avatar pelvis and geometry must belong to the selected root.");
        if (!HasManualChairPlacement)
            chairPlacementDetail = "Avatar geometry ready for chair placement.";
        return true;
    }

    bool TryCalibrateChairContact(Vector3[] vertices)
    {
        Vector3 pelvisWorld = QuestAvatarShoulderTouch.MapMeshWorldPointToGaussianWorld(
            chairMesh, chairGaussian, chairPelvis.position);
        Vector3 pelvisLocal = avatarRoot.InverseTransformPoint(pelvisWorld);
        Vector3 forwardLocal = avatarRoot.InverseTransformVector(chairGaussian.TransformVector(Vector3.forward));
        forwardLocal.y = 0;
        if (!Finite(pelvisLocal) || !Finite(forwardLocal) || forwardLocal.sqrMagnitude < 0.000001f)
            return ChairPlacementUnavailable("The visible avatar body anchor is invalid.");

        float lowestY = float.PositiveInfinity;
        foreach (Vector3 bakedVertex in vertices)
        {
            // GetCurrentVertices contains PoseController's baked X reflection.
            // The Gaussian rendering path reverses it; match the visible body.
            Vector3 visibleVertex = new(-bakedVertex.x, bakedVertex.y, bakedVertex.z);
            Vector3 local = avatarRoot.InverseTransformPoint(chairGaussian.TransformPoint(visibleVertex));
            if (!Finite(local))
                return ChairPlacementUnavailable("The avatar vertex bake contains invalid positions.");
            lowestY = Mathf.Min(lowestY, local.y);
        }
        if (!Finite(lowestY))
            return ChairPlacementUnavailable("The visible avatar has no valid ground contact.");

        // Cache in root-local coordinates, never as a previous world-space foot.
        // Selection and relocalization reuse this calibration without accumulating offsets.
        chairGroundContactLocal = new Vector3(pelvisLocal.x, lowestY, pelvisLocal.z);
        chairForwardLocal = forwardLocal.normalized;
        chairCalibrationReady = true;
        return true;
    }

    static bool TryCalculateChairRootPose(Pose chairPose, float chairHeight, Vector3 contactLocal,
        Vector3 forwardLocal, Vector3 worldScale, out Pose rootPose)
    {
        rootPose = default;
        if (!TryNormalizeFiniteRotation(chairPose.rotation, out Quaternion chairRotation) ||
            !Finite(chairPose.position) || !Finite(chairHeight) || chairHeight <= 0 ||
            !Finite(contactLocal) || !Finite(forwardLocal) || !PositiveFiniteScale(worldScale))
            return false;
        Vector3 targetForward = Vector3.ProjectOnPlane(chairRotation * Vector3.forward, Vector3.up);
        Vector3 scaledForward = Vector3.ProjectOnPlane(Vector3.Scale(forwardLocal, worldScale), Vector3.up);
        if (!Finite(targetForward) || !Finite(scaledForward) ||
            targetForward.sqrMagnitude < 0.000001f || scaledForward.sqrMagnitude < 0.000001f)
            return false;
        Quaternion rotation = Quaternion.LookRotation(targetForward, Vector3.up) *
            Quaternion.Inverse(Quaternion.LookRotation(scaledForward, Vector3.up));
        Vector3 floorPoint = chairPose.position - (chairRotation * Vector3.up) * (chairHeight * 0.5f);
        Vector3 position = floorPoint - rotation * Vector3.Scale(contactLocal, worldScale);
        if (!Finite(position) || !TryNormalizeFiniteRotation(rotation, out rotation))
            return false;
        rootPose = new Pose(position, rotation);
        return true;
    }

    static bool TryNormalizeFiniteRotation(Quaternion value, out Quaternion normalized)
    {
        normalized = Quaternion.identity;
        if (!Finite(value.x) || !Finite(value.y) || !Finite(value.z) || !Finite(value.w)) return false;
        double norm = System.Math.Sqrt((double)value.x * value.x + (double)value.y * value.y +
            (double)value.z * value.z + (double)value.w * value.w);
        if (norm < 0.000001 || double.IsInfinity(norm) || double.IsNaN(norm)) return false;
        normalized = new Quaternion((float)(value.x / norm), (float)(value.y / norm),
            (float)(value.z / norm), (float)(value.w / norm));
        return true;
    }

    static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    static bool PositiveFiniteScale(Vector3 value) => Finite(value) &&
        value.x > 0.000001f && value.y > 0.000001f && value.z > 0.000001f;

#if UNITY_EDITOR
    public static bool RunChairPlacementSmokeTest(out string report)
    {
        if (!ValidateChairPlacementMathForEditor(out report)) return false;
        GameObject host = null, body = null;
        try
        {
            host = new GameObject("ChairPlacementValidationHost") { hideFlags = HideFlags.HideAndDontSave };
            host.SetActive(false); // Do not run startup placement or configure the scene XR origin.
            var placement = host.AddComponent<ARGroundPlacement>();
            body = new GameObject("ChairPlacementValidationBody") { hideFlags = HideFlags.HideAndDontSave };
            placement.avatarRoot = body.transform;
            body.transform.SetPositionAndRotation(new Vector3(5, 2, -3), Quaternion.Euler(12, 37, -8));
            Vector3 originalScale = new(1.5f, 0.8f, 1.2f);
            body.transform.localScale = originalScale;
            var mesh = new GameObject("SourceMesh").transform;
            mesh.SetParent(body.transform, false);
            mesh.localPosition = new Vector3(-0.4f, 0.7f, 0.2f);
            var gaussian = new GameObject("VisibleGaussian").transform;
            gaussian.SetParent(body.transform, false);
            gaussian.localPosition = new Vector3(1.047f, 0, 0);
            gaussian.localRotation = Quaternion.Euler(0, 23, 0);
            var pelvis = new GameObject("pelvis").transform;
            pelvis.SetParent(mesh, false);
            pelvis.localPosition = new Vector3(0.1f, 0.75f, 0.05f);
            placement.chairMesh = mesh;
            placement.chairGaussian = gaussian;
            placement.chairPelvis = pelvis;
            Vector3[] reflectedVertices = { new(-0.1f, -0.82f, 0.3f), new(0.2f, 0.9f, -0.1f) };
            if (!placement.TryCalibrateChairContact(reflectedVertices))
            { report = placement.ChairPlacementDetail; return false; }
            Vector3 mappedPelvis = gaussian.localPosition + gaussian.localRotation * pelvis.localPosition;
            Vector3 expectedLocalContact = new(mappedPelvis.x, -0.82f, mappedPelvis.z);
            if (Vector3.Distance(placement.chairGroundContactLocal, expectedLocalContact) > 0.0001f)
            { report = "Visible geometry calibration failed for a tilted, offset, scaled root."; return false; }

            Pose t0 = new(new Vector3(3.2f, 1.1f, -2.4f), Quaternion.Euler(0, 71, 0));
            Pose t1 = new(new Vector3(-1.4f, 0.9f, 2.8f), Quaternion.Euler(0, -38, 0));
            Pose firstRoot = default;
            Pose[] selections = { t0, t0, t1, t0 };
            float maxContactError = 0, maxHeadingDelta = 0, maxLegacyHeadingAngle = 0;
            for (int i = 0; i < selections.Length; ++i)
            {
                if (!TryCalculateChairRootPose(selections[i], 0.9f, placement.chairGroundContactLocal,
                    placement.chairForwardLocal, body.transform.lossyScale, out Pose rootPose))
                { report = "A T0/T1/reset selection failed."; return false; }
                placement.CommitChairRootPose(rootPose);
                if (i == 0) firstRoot = rootPose;
                Vector3 expectedFloor = selections[i].position - Vector3.up * 0.45f;
                Vector3 actualForward = gaussian.TransformVector(Vector3.forward);
                Vector3 expectedForward = selections[i].rotation * Vector3.forward;
                float contactError = Vector3.Distance(body.transform.TransformPoint(expectedLocalContact), expectedFloor);
                // acos(dot) loses precision near 1: even parallel float vectors can
                // report about 0.02 degrees. Chord distance is stable near zero;
                // 1e-4 corresponds to about 0.0057 degrees, stricter than 0.01.
                float headingDelta = Vector3.Distance(actualForward.normalized, expectedForward.normalized);
                float legacyHeadingAngle = Vector3.Angle(actualForward, expectedForward);
                float scaleDelta = Vector3.Distance(body.transform.localScale, originalScale);
                maxContactError = Mathf.Max(maxContactError, contactError);
                maxHeadingDelta = Mathf.Max(maxHeadingDelta, headingDelta);
                maxLegacyHeadingAngle = Mathf.Max(maxLegacyHeadingAngle, legacyHeadingAngle);
                if (contactError > 0.0001f || headingDelta > 0.0001f || scaleDelta > 0.000001f)
                {
                    report = "Selection failed: iteration=" + i + ", contactDistance=" + contactError.ToString("G9") +
                        ", headingDelta=" + headingDelta.ToString("G9") + ", legacyHeadingAngle=" + legacyHeadingAngle.ToString("G9") +
                        ", scaleDelta=" + scaleDelta.ToString("G9") + ", localScale=" + body.transform.localScale.ToString("F8") +
                        ", lossyScale=" + body.transform.lossyScale.ToString("F8") + ", actualForward=" + actualForward.ToString("F8") +
                        ", expectedForward=" + expectedForward.ToString("F8") + ", cachedForward=" + placement.chairForwardLocal.ToString("F8");
                    return false;
                }
            }
            if (Vector3.Distance(body.transform.position, firstRoot.position) > 0.0001f)
            { report = "Reset to T0 accumulated a position offset."; return false; }
            Vector3 beforePosition = body.transform.position;
            Quaternion beforeRotation = body.transform.rotation;
            if (placement.TryPlaceAvatarAtChairPose(new Pose(t0.position, new Quaternion(float.NaN, 0, 0, 1)), 0.9f) ||
                body.transform.position != beforePosition || body.transform.rotation != beforeRotation)
            { report = "Invalid input mutated the actual root."; return false; }
            placement.OnEnable();
            placement.Update();
            placement.EnsureReferences();
            placement.ConfigureXROriginForAR();
            placement.PlaceAvatarOnGround();
            if (!placement.HasManualChairPlacement || !placement.chairCalibrationReady ||
                body.transform.position != beforePosition || body.transform.rotation != beforeRotation ||
                placement.configuredXROrigin)
            { report = "Startup or re-enable overwrote the manual placement."; return false; }
            report = "Chair placement passed: visible calibration under root tilt/scale, T0/repeat/T1/reset, " +
                "heading, unchanged scale, invalid input atomicity, and manual placement surviving startup/re-enable. " +
                "maxContactDistance=" + maxContactError.ToString("G9") + ", maxHeadingDelta=" + maxHeadingDelta.ToString("G9") +
                ", maxLegacyHeadingAngle=" + maxLegacyHeadingAngle.ToString("G9");
            return true;
        }
        catch (System.Exception error)
        {
            report = "Chair placement validation exception: " + error.Message;
            return false;
        }
        finally
        {
            if (host != null) DestroyImmediate(host);
            if (body != null) DestroyImmediate(body);
        }
    }
    // Called by the editor validation runner without loading a model or GPU buffer.
    public static bool ValidateChairPlacementMathForEditor(out string detail)
    {
        Vector3 contact = new(1.047f, -0.82f, 0.31f);
        Vector3 scale = new(1.5f, 0.8f, 1.2f);
        Vector3 heading = Quaternion.Euler(0, 23, 0) * Vector3.forward;
        Pose chair = new(new Vector3(3.2f, 1.1f, -2.4f), Quaternion.Euler(0, 71, 0));
        if (!TryCalculateChairRootPose(chair, 0.9f, contact, heading, scale, out Pose placed))
        { detail = "Offset/scaled avatar placement failed."; return false; }
        Vector3 actualContact = Matrix4x4.TRS(placed.position, placed.rotation, scale).MultiplyPoint3x4(contact);
        Vector3 expectedContact = new(3.2f, 0.65f, -2.4f);
        Vector3 actualHeading = placed.rotation * Vector3.Scale(heading, scale);
        if (Vector3.Distance(actualContact, expectedContact) > 0.0001f ||
            Vector3.Distance(actualHeading.normalized, (chair.rotation * Vector3.forward).normalized) > 0.0001f)
        { detail = "Visible contact or heading did not match the chair."; return false; }
        for (int i = 0; i < 100; ++i)
        {
            if (!TryCalculateChairRootPose(chair, 0.9f, contact, heading, scale, out Pose repeated) ||
                Vector3.Distance(repeated.position, placed.position) > 0.000001f ||
                Quaternion.Angle(repeated.rotation, placed.rotation) > 0.01f)
            { detail = "Repeated placement drifted."; return false; }
        }
        Pose relocalized = new(chair.position + new Vector3(-0.8f, 0.12f, 1.9f), Quaternion.Euler(0, -38, 0));
        if (!TryCalculateChairRootPose(relocalized, 0.9f, contact, heading, scale, out Pose moved) ||
            Vector3.Distance(Matrix4x4.TRS(moved.position, moved.rotation, scale).MultiplyPoint3x4(contact),
                relocalized.position - Vector3.up * 0.45f) > 0.0001f)
        { detail = "Relocalized placement did not preserve the local contact."; return false; }
        Pose invalid = new(chair.position, new Quaternion(float.NaN, 0, 0, 1));
        if (TryCalculateChairRootPose(invalid, 0.9f, contact, heading, scale, out _) ||
            TryCalculateChairRootPose(new Pose(chair.position, new Quaternion(0, 0, 0, 0)),
                0.9f, contact, heading, scale, out _))
        { detail = "Invalid chair rotations were accepted."; return false; }
        detail = "Chair placement math passed: scaled offset, visible heading, repeated selection, relocalization, invalid rotation.";
        return true;
    }
#endif

    void OnEnable()
    {
        ResolveAvatarReference();
        if (HasManualChairPlacement)
        {
            placedOnce = true;
            placedOnDetectedGround = true;
            StopGroundDetection();
            return;
        }
        startTime = Time.time;
        configuredXROrigin = false;
        placedOnce = false;
        placedOnDetectedGround = false;
        isARMode = GeneralOperator.GetSceneMode() == GeneralBuildMode.AR;
        useXRTrackingFloor = QuestARBootstrap.IsOculusLoaderConfigured();
        useTouchPlacement = isARMode && useXRTrackingFloor && GeneralOperator.GetSceneShoulderTouchPoseEnabled();
        headTrackedSince = -1;
        nextTouchAnchorAttempt = 0;
        touchPose = null;
        touchMesh = touchGaussian = touchLeftShoulder = touchRightShoulder = null;
        headDevice = InputDevices.GetDeviceAtXRNode(XRNode.Head);
        EnsureReferences();
        PlaceAvatarOnGround();
    }

    void Update()
    {
        if (HasManualChairPlacement || !isARMode)
            return;

        if (placedOnDetectedGround || (useTouchPlacement && placedOnce))
            return;

        if (placedOnce && Time.time - startTime > retrySeconds)
            return;

        EnsureReferences();
        PlaceAvatarOnGround();
    }

    void EnsureReferences()
    {
        if (HasManualChairPlacement) return;
        if (avatarRoot == null)
        {
            var avatar = GameObject.Find("Avatar");
            if (avatar != null)
                avatarRoot = avatar.transform;
        }

        if (arCamera == null)
            arCamera = Camera.main;

        if (xrOrigin == null)
            xrOrigin = FindObjectOfType<XROrigin>(true);

        if (xrOrigin == null)
            return;

        ConfigureXROriginForAR();

        // Oculus XR has no ARFoundation plane/raycast provider. Its floor tracking
        // origin supplies placement height; environment depth only handles occlusion.
        if (useXRTrackingFloor)
        {
            StopGroundDetection();
            return;
        }

        if (planeManager == null)
            planeManager = xrOrigin.GetComponent<ARPlaneManager>() ?? xrOrigin.gameObject.AddComponent<ARPlaneManager>();

        if (raycastManager == null)
            raycastManager = xrOrigin.GetComponent<ARRaycastManager>() ?? xrOrigin.gameObject.AddComponent<ARRaycastManager>();

        planeManager.requestedDetectionMode = PlaneDetectionMode.Horizontal;
        planeManager.enabled = true;
        raycastManager.enabled = true;
    }

    void ConfigureXROriginForAR()
    {
        if (HasManualChairPlacement || !configureXROriginForAR || configuredXROrigin || xrOrigin == null)
            return;

        xrOrigin.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        xrOrigin.transform.localScale = Vector3.one;
        xrOrigin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.Floor;
        xrOrigin.CameraYOffset = 0.0f;

        if (xrOrigin.CameraFloorOffsetObject != null)
        {
            Transform offset = xrOrigin.CameraFloorOffsetObject.transform;
            offset.localPosition = new Vector3(offset.localPosition.x, 0.0f, offset.localPosition.z);
            offset.localRotation = Quaternion.identity;
            offset.localScale = Vector3.one;
        }

        configuredXROrigin = true;
    }

    void PlaceAvatarOnGround()
    {
        if (HasManualChairPlacement) return;
        if (avatarRoot == null || arCamera == null)
            return;
        if (useTouchPlacement && (placedOnce || !CanPlaceTouchAvatar()))
            return;

        Vector3 cameraForward = Vector3.ProjectOnPlane(arCamera.transform.forward, Vector3.up);
        if (cameraForward.sqrMagnitude < 0.001f)
            cameraForward = Vector3.ProjectOnPlane(arCamera.transform.up, Vector3.up);
        if (cameraForward.sqrMagnitude < 0.001f)
            cameraForward = Vector3.forward;
        cameraForward.Normalize();

        float distance = useTouchPlacement ? Mathf.Clamp(shoulderTouchDistanceInMeters, 0.6f, 1.2f) : distanceInFrontOfCamera;
        Vector3 target = arCamera.transform.position + cameraForward * distance;
        bool foundGround = TryGetDetectedGround(target, out float groundY);
        if (!foundGround)
            groundY = EstimateFallbackGroundY();

        Quaternion rotation = avatarRoot.rotation;
        if (faceCamera)
            rotation = Quaternion.LookRotation(-cameraForward, Vector3.up);

        float placementGroundY = groundY + floorHeightOffset;
        avatarRoot.SetPositionAndRotation(new Vector3(target.x, placementGroundY, target.z), rotation);
        AlignRendererBoundsToGround(avatarRoot, placementGroundY);
        if (useTouchPlacement)
        {
            // The visible Gaussian body is offset from Avatar's root. Align its
            // actual shoulder midpoint after yaw and floor placement.
            Vector3 center = QuestAvatarShoulderTouch.MapMeshWorldPointToGaussianWorld(
                touchMesh, touchGaussian, (touchLeftShoulder.position + touchRightShoulder.position) * 0.5f);
            Vector3 correction = target - center;
            correction.y = 0;
            avatarRoot.position += correction;
            Debug.Log("GSAC shoulder placement: visible shoulders centered " + distance.ToString("F2") +
                      " m ahead; world position locked.", this);
        }
        AlignExtraObjectsToGround(placementGroundY);
        placedOnce = true;
        placedOnDetectedGround |= foundGround;
        if (placedOnDetectedGround && !keepGroundDetectionRunning)
            StopGroundDetection();
    }

    bool CanPlaceTouchAvatar()
    {
        if (!headDevice.isValid) headDevice = InputDevices.GetDeviceAtXRNode(XRNode.Head);
        bool tracked = Application.isFocused && headDevice.isValid &&
            headDevice.TryGetFeatureValue(CommonUsages.userPresence, out bool present) && present &&
            headDevice.TryGetFeatureValue(CommonUsages.isTracked, out bool isTracked) && isTracked;
        if (!tracked)
        {
            headTrackedSince = -1;
            return false;
        }
        if (headTrackedSince < 0) headTrackedSince = Time.unscaledTime;
        if (Time.unscaledTime - headTrackedSince < 0.35f)
            return false;

        if (touchPose == null || touchMesh == null || touchGaussian == null ||
            touchLeftShoulder == null || touchRightShoulder == null)
        {
            if (Time.unscaledTime < nextTouchAnchorAttempt) return false;
            nextTouchAnchorAttempt = Time.unscaledTime + 0.5f;
            touchPose = avatarRoot.GetComponentInChildren<PoseController>();
            if (touchPose == null || touchPose.smplx == null ||
                touchPose.vertexBuffer == null || !touchPose.vertexBuffer.IsValid())
                return false;
            var vertices = touchPose.GetCurrentVertices();
            if (vertices == null || vertices.Length == 0) return false;
            var mesh = touchPose.smplx.GetComponentInChildren<SkinnedMeshRenderer>();
            var gaussian = touchPose.GetComponent<GaussianSplatRenderer>();
            if (mesh == null || gaussian == null) return false;
            touchMesh = mesh.transform;
            touchGaussian = gaussian.transform;
            foreach (var bone in touchPose.smplx.GetComponentsInChildren<Transform>(true))
            {
                if (bone.name == "left_shoulder") touchLeftShoulder = bone;
                else if (bone.name == "right_shoulder") touchRightShoulder = bone;
            }
        }
        return touchMesh != null && touchGaussian != null &&
               touchLeftShoulder != null && touchRightShoulder != null;
    }

    void StopGroundDetection()
    {
        if (planeManager != null)
            planeManager.enabled = false;
        if (raycastManager != null)
            raycastManager.enabled = false;
    }

    bool TryGetDetectedGround(Vector3 target, out float groundY)
    {
        groundY = 0.0f;
        if (useXRTrackingFloor)
            return false;
        float rayStartY = Mathf.Max(target.y, arCamera.transform.position.y) + 0.25f;
        Ray downRay = new(new Vector3(target.x, rayStartY, target.z), Vector3.down);

        if (raycastManager != null &&
            raycastManager.Raycast(downRay, s_Hits, TrackableType.PlaneWithinPolygon | TrackableType.PlaneWithinBounds | TrackableType.PlaneWithinInfinity))
        {
            foreach (ARRaycastHit hit in s_Hits)
            {
                var plane = hit.trackable as ARPlane;
                if (plane == null || !IsFloorLikePlane(plane))
                    continue;

                if (!IsBelowHeadset(plane.center.y))
                    continue;

                groundY = hit.pose.position.y;
                return true;
            }
        }

        if (planeManager == null)
            return false;

        ARPlane bestPlane = null;
        float bestDistance = float.MaxValue;
        foreach (ARPlane plane in planeManager.trackables)
        {
            if (!IsFloorLikePlane(plane))
                continue;

            if (!IsBelowHeadset(plane.center.y))
                continue;

            float distance = Vector2.SqrMagnitude(new Vector2(plane.center.x - target.x, plane.center.z - target.z));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestPlane = plane;
            }
        }

        if (bestPlane == null)
            return false;

        groundY = bestPlane.center.y;
        return true;
    }

    bool IsFloorLikePlane(ARPlane plane)
    {
        if (plane == null)
            return false;

        if (plane.classification == PlaneClassification.Ceiling ||
            plane.classification == PlaneClassification.Table ||
            plane.classification == PlaneClassification.Seat)
            return false;

        if (plane.classification == PlaneClassification.Floor)
            return true;

        return plane.alignment == PlaneAlignment.HorizontalUp &&
               Vector3.Dot(plane.normal, Vector3.up) > 0.85f;
    }

    bool IsBelowHeadset(float y)
    {
        if (arCamera == null)
            return true;

        return y < arCamera.transform.position.y - 0.25f;
    }

    float EstimateFallbackGroundY()
    {
        if (arCamera == null)
            return 0.0f;

        if (xrOrigin != null && xrOrigin.CameraInOriginSpaceHeight > 0.25f)
            return arCamera.transform.position.y - xrOrigin.CameraInOriginSpaceHeight;

        return arCamera.transform.position.y - estimatedHeadHeight;
    }

    void AlignExtraObjectsToGround(float groundY)
    {
        if (extraGroundedObjects == null)
            return;

        foreach (Transform extraObject in extraGroundedObjects)
        {
            if (extraObject == null)
                continue;

            if (HasBeenPlacedByUser(extraObject))
                continue;

            AlignRendererBoundsToGround(extraObject, groundY);
        }
    }

    static bool HasBeenPlacedByUser(Transform target)
    {
        if (target == null)
            return false;

        GrabbableTestBall grabbable = target.GetComponent<GrabbableTestBall>();
        return grabbable != null && grabbable.HasUserPlaced;
    }

    static void AlignRendererBoundsToGround(Transform target, float groundY)
    {
        if (target == null)
            return;

        Renderer[] renderers = target.GetComponentsInChildren<Renderer>(true);
        bool hasBounds = false;
        Bounds bounds = default;

        foreach (Renderer renderer in renderers)
        {
            if (renderer == null || !renderer.enabled)
                continue;

            if (!hasBounds)
            {
                bounds = renderer.bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(renderer.bounds);
            }
        }

        if (!hasBounds)
            return;

        float yCorrection = groundY - bounds.min.y;
        target.position += Vector3.up * yCorrection;
    }
}
