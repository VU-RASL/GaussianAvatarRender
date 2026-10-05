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

    public bool ReadyForShoulderTouch => !useTouchPlacement || placedOnce;

    void OnEnable()
    {
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
        if (!isARMode)
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
        if (!configureXROriginForAR || configuredXROrigin || xrOrigin == null)
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
