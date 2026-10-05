using System.Collections.Generic;
using GaussianSplatting.Runtime;
using UnityEngine;
using UnityEngine.XR;

// A single shoulder touch switches the avatar from its clean startup pose to
// its existing Animator-driven Pose 1. Tracking is independent of depth occlusion.
[DefaultExecutionOrder(100)]
public sealed class QuestAvatarShoulderTouch : MonoBehaviour
{
    [SerializeField, Range(0.04f, 0.16f)] float shoulderRadiusMeters = 0.09f;
    [SerializeField, Range(0.005f, 0.06f)] float releaseMarginMeters = 0.025f;
    [SerializeField, Range(0.02f, 0.3f)] float contactHoldSeconds = 0.08f;

    sealed class RunState { public bool startupApplied, triggered; }
    static readonly Dictionary<PoseController, RunState> Runs = new Dictionary<PoseController, RunState>();
    readonly HandPoints left = new HandPoints(OVRPlugin.Hand.HandLeft, "QuestShoulderTouchHandLeft");
    readonly HandPoints right = new HandPoints(OVRPlugin.Hand.HandRight, "QuestShoulderTouchHandRight");
    readonly ContactGate leftGate = new ContactGate();
    readonly ContactGate rightGate = new ContactGate();
    PoseController pose;
    OVRCameraRig rig;
    ARGroundPlacement placement;
    RunState state;
    Transform gaussianTransform, meshTransform, leftShoulder, rightShoulder;
    InputDevice head;
    bool initialized, paused, warned, firstTrackingReported, setupReported;
    double nextSetupAttempt, nextDiagnosticTime;
    string trackingWaitReason = "not_initialized";

    public bool HasAppliedStartupPose => state != null && state.startupApplied;
    public bool HasTriggered => state != null && state.triggered;
    public bool LeftArmed => leftGate.Armed;
    public bool RightArmed => rightGate.Armed;
    public bool HasObservedTrackedHand => firstTrackingReported;
    public string TrackingWaitReason => trackingWaitReason;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetRunState() => Runs.Clear();

    public bool Initialize(PoseController controller, OVRCameraRig cameraRig)
    {
        Shutdown();
        if (controller == null || cameraRig == null || cameraRig.trackingSpace == null)
            return false;
        var gaussian = controller.GetComponent<GaussianSplatRenderer>();
        if (gaussian == null)
        {
            Debug.LogWarning("Shoulder touch requires PoseController on the visible Gaussian renderer.", this);
            return false;
        }
        pose = controller;
        rig = cameraRig;
        placement = FindObjectOfType<ARGroundPlacement>();
        gaussianTransform = gaussian.transform;
        if (!Runs.TryGetValue(pose, out state))
        {
            state = new RunState();
            Runs.Add(pose, state);
        }
        head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
        nextSetupAttempt = 0;
        warned = false;
        firstTrackingReported = false;
        setupReported = false;
        nextDiagnosticTime = 0;
        trackingWaitReason = "waiting_for_pose";
        initialized = true;
        enabled = !state.triggered;
        return true;
    }

    public void Shutdown()
    {
        initialized = false;
        ReleaseHands();
        leftGate.Reset();
        rightGate.Reset();
        pose = null;
        rig = null;
        placement = null;
        gaussianTransform = meshTransform = leftShoulder = rightShoulder = null;
        // Deliberately keep the run state: focus changes and XR reconstruction
        // must not reset the avatar or repeat the one-shot reaction.
    }

    void OnApplicationPause(bool value)
    {
        paused = value;
        if (value) ClearContact();
    }

    void OnApplicationFocus(bool focused)
    {
        if (!focused) ClearContact();
    }

    void OnDisable()
    {
        ClearContact();
        ReleaseHands();
    }

    void OnDestroy() => Shutdown();

    void LateUpdate()
    {
        if (!initialized || state == null || state.triggered || pose == null) return;

        // Applying No Pose before PoseController.Start would make it cache an
        // Animator speed of zero. A populated vertex buffer proves Start ran.
        if (!state.startupApplied)
        {
            if (!PoseReady())
            {
                trackingWaitReason = "pose_not_initialized";
                ReportDevelopmentState();
                return;
            }
            pose.ApplyCleanPose();
            pose.RequestImmediateVertexBake();
            state.startupApplied = true;
            Debug.Log("GSAC shoulder touch: startup No Pose applied.", this);
        }

        if (!TrackingContextReady())
        {
            ClearContact();
            ReportDevelopmentState();
            return;
        }

        double now = Time.realtimeSinceStartupAsDouble;
        if (meshTransform == null || leftShoulder == null || rightShoulder == null)
        {
            trackingWaitReason = "shoulder_anchors_pending";
            if (now < nextSetupAttempt)
            {
                ReportDevelopmentState();
                return;
            }
            nextSetupAttempt = now + 0.5;
            if (!CacheShoulders())
            {
                WarnOnce("The active SMPL-X shoulder bones are not available; touch detection is waiting.");
                ReportDevelopmentState();
                return;
            }
        }

        if ((!left.Created || !right.Created) && now >= nextSetupAttempt)
        {
            nextSetupAttempt = now + 0.5;
            if (!left.Created && !left.Create(rig.trackingSpace))
                WarnOnce("Left hand tracking Resources prefab is unavailable.");
            if (!right.Created && !right.Create(rig.trackingSpace))
                WarnOnce("Right hand tracking Resources prefab is unavailable.");
        }

        Vector3 leftCenter = MapMeshWorldPointToGaussianWorld(meshTransform, gaussianTransform, leftShoulder.position);
        Vector3 rightCenter = MapMeshWorldPointToGaussianWorld(meshTransform, gaussianTransform, rightShoulder.position);
        if (!Finite(leftCenter) || !Finite(rightCenter))
        {
            trackingWaitReason = "shoulder_centers_nonfinite";
            ClearContact();
            ReportDevelopmentState();
            return;
        }
        trackingWaitReason = "ready";
        ReportDevelopmentSetup(leftCenter, rightCenter);

        bool leftTouch = SampleHand(left, leftGate, leftCenter, rightCenter, now);
        bool rightTouch = SampleHand(right, rightGate, leftCenter, rightCenter, now);
        ReportDevelopmentState();
        if (!leftTouch && !rightTouch) return;

        state.triggered = true;
        pose.ApplyPose1();
        pose.RequestImmediateVertexBake();
        Debug.Log("GSAC shoulder touch: " + (leftTouch ? "left" : "right") + " hand triggered existing Pose 1.", this);
        ReleaseHands();
        enabled = false;
    }

    bool PoseReady()
    {
        if (pose.smplx == null || !pose.isActiveAndEnabled || pose.vertexBuffer == null || !pose.vertexBuffer.IsValid())
            return false;
        var vertices = pose.GetCurrentVertices();
        return vertices != null && vertices.Length > 0;
    }

    bool TrackingContextReady()
    {
        if (placement != null && !placement.ReadyForShoulderTouch) return WaitFor("placement_pending");
        if (paused) return WaitFor("application_paused");
        if (!Application.isFocused) return WaitFor("application_unfocused");
        if (rig == null) return WaitFor("camera_rig_missing");
        if (rig.trackingSpace == null) return WaitFor("tracking_space_missing");
        if (!OVRManager.OVRManagerinitialized) return WaitFor("ovr_manager_uninitialized");
        if (!head.isValid) head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
        if (!head.isValid) return WaitFor("head_device_unavailable");
        if (!head.TryGetFeatureValue(CommonUsages.userPresence, out bool present)) return WaitFor("head_presence_unavailable");
        if (!present) return WaitFor("head_not_present");
        if (!head.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked)) return WaitFor("head_tracking_flag_unavailable");
        if (!tracked) return WaitFor("head_not_tracked");
        trackingWaitReason = "ready";
        return true;
    }

    bool WaitFor(string reason)
    {
        trackingWaitReason = reason;
        return false;
    }

    bool CacheShoulders()
    {
        if (!PoseReady()) return false;
        var renderer = pose.smplx.GetComponentInChildren<SkinnedMeshRenderer>();
        if (renderer == null) return false;
        var transforms = pose.smplx.GetComponentsInChildren<Transform>(true);
        foreach (var candidate in transforms)
        {
            if (candidate.name == "left_shoulder") leftShoulder = candidate;
            else if (candidate.name == "right_shoulder") rightShoulder = candidate;
        }
        if (leftShoulder == null || rightShoulder == null) return false;
        meshTransform = renderer.transform;
        return true;
    }

    // PoseController reflects baked X, then Test_Updater's Android path (and
    // TestShader) reflects the final Gaussian centroid X again. The body-anchor
    // position therefore has no net X reflection before the Gaussian transform.
    public static Vector3 MapMeshWorldPointToGaussianWorld(Transform mesh, Transform gaussian, Vector3 point)
    {
        return gaussian.TransformPoint(mesh.InverseTransformPoint(point));
    }

    // Bits 0..4 select independently confident fingertips. Bit 5 is the palm,
    // supported by the index and pinky bases; unrelated fingers do not veto it.
    public static int SelectConfidentPointMask(bool thumbHigh, bool indexHigh, bool middleHigh, bool ringHigh, bool pinkyHigh)
    {
        int mask = (thumbHigh ? 1 : 0) | (indexHigh ? 2 : 0) | (middleHigh ? 4 : 0) |
            (ringHigh ? 8 : 0) | (pinkyHigh ? 16 : 0);
        if (indexHigh && pinkyHigh) mask |= 32;
        return mask;
    }

    bool SampleHand(HandPoints hand, ContactGate gate, Vector3 firstCenter, Vector3 secondCenter, double now)
    {
        bool valid = hand.TryDistanceSquared(rig.trackingSpace, firstCenter, secondCenter, out float distanceSquared);
        if (valid && !firstTrackingReported)
        {
            firstTrackingReported = true;
            Debug.Log("GSAC shoulder touch: first confident " + hand.SideLabel + " hand is available.", this);
        }
        float radius = Mathf.Max(0.001f, shoulderRadiusMeters);
        float releaseRadius = radius + Mathf.Max(0, releaseMarginMeters);
        return gate.Sample(valid, valid && distanceSquared <= radius * radius,
            valid && distanceSquared > releaseRadius * releaseRadius, now, contactHoldSeconds);
    }

    [System.Diagnostics.Conditional("DEVELOPMENT_BUILD"), System.Diagnostics.Conditional("UNITY_EDITOR")]
    void ReportDevelopmentSetup(Vector3 leftCenter, Vector3 rightCenter)
    {
        if (setupReported) return;
        setupReported = true;
        Vector3 headPosition = rig != null && rig.centerEyeAnchor != null ? rig.centerEyeAnchor.position : Vector3.zero;
        Debug.Log("GSAC shoulder touch setup: left=" + leftCenter.ToString("F3") +
            " right=" + rightCenter.ToString("F3") + " head=" + headPosition.ToString("F3") +
            " radius_m=" + shoulderRadiusMeters.ToString("F3") +
            " hold_s=" + contactHoldSeconds.ToString("F3"), this);
    }

    [System.Diagnostics.Conditional("DEVELOPMENT_BUILD"), System.Diagnostics.Conditional("UNITY_EDITOR")]
    void ReportDevelopmentState()
    {
        double now = Time.realtimeSinceStartupAsDouble;
        if (now < nextDiagnosticTime) return;
        nextDiagnosticTime = now + 1;
        Debug.Log("GSAC shoulder touch pending: wait=" + trackingWaitReason +
            " frame=" + Time.frameCount + " left={" + left.DiagnosticSummary(leftGate.Armed) +
            ",invalid_resets=" + leftGate.InvalidTrackingResetCount +
            ",dwell_s=" + (leftGate.ContactStartedAt >= 0 ? now - leftGate.ContactStartedAt : 0).ToString("F3") +
            "} right={" + right.DiagnosticSummary(rightGate.Armed) +
            ",invalid_resets=" + rightGate.InvalidTrackingResetCount +
            ",dwell_s=" + (rightGate.ContactStartedAt >= 0 ? now - rightGate.ContactStartedAt : 0).ToString("F3") + "}", this);
    }

    void ClearContact()
    {
        leftGate.Reset();
        rightGate.Reset();
    }

    void ReleaseHands()
    {
        left.Dispose();
        right.Dispose();
    }

    void WarnOnce(string message)
    {
        if (warned) return;
        warned = true;
        Debug.LogWarning("GSAC shoulder touch: " + message, this);
    }

    // Pure contact gate, independent of XR and rendering, for deterministic tests.
    public sealed class ContactGate
    {
        public bool Armed { get; private set; }
        public bool Fired { get; private set; }
        public int InvalidTrackingResetCount { get; private set; }
        public double ContactStartedAt => contactStarted;
        double contactStarted = -1;

        public bool Sample(bool tracked, bool touching, bool outsideReleaseBoundary, double now, float holdSeconds)
        {
            if (Fired) return false;
            if (!tracked || double.IsNaN(now) || double.IsInfinity(now))
            {
                if (Armed || contactStarted >= 0) ++InvalidTrackingResetCount;
                Reset();
                return false;
            }
            if (!Armed)
            {
                if (outsideReleaseBoundary && !touching) Armed = true;
                return false;
            }
            if (!touching)
            {
                contactStarted = -1;
                return false;
            }
            if (contactStarted < 0 || now < contactStarted) contactStarted = now;
            if (now - contactStarted < Mathf.Max(0.02f, holdSeconds)) return false;
            Fired = true;
            Armed = false;
            return true;
        }

        public void Reset()
        {
            Armed = false;
            contactStarted = -1;
        }
    }

    sealed class HandPoints
    {
        readonly OVRPlugin.Hand side;
        readonly string resource;
        readonly Transform[] tips = new Transform[5];
        GameObject root;
        OVRHand hand;
        OVRSkeleton skeleton;
        Transform wrist, indexBase, pinkyBase;
        bool pointsCached;
        string rejectionReason = "not_sampled";
        int confidenceMask, sampleMask, lastSampleFrame = -1;
        float lastDistanceSquared = float.PositiveInfinity;
        public bool Created => root != null;
        public string SideLabel => side == OVRPlugin.Hand.HandLeft ? "left" : "right";

        public HandPoints(OVRPlugin.Hand handSide, string resourceName)
        {
            side = handSide;
            resource = resourceName;
        }

        public bool Create(Transform parent)
        {
            if (Created) return true;
            var prefab = Resources.Load<GameObject>(resource);
            if (prefab == null || prefab.activeSelf) return false;
            root = Instantiate(prefab, parent, false);
            root.transform.localPosition = Vector3.zero;
            root.transform.localRotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;
            hand = root.GetComponent<OVRHand>();
            skeleton = root.GetComponent<OVRSkeleton>();
            if (hand == null || skeleton == null ||
                skeleton.GetSkeletonType() != (OVRSkeleton.SkeletonType)(int)side ||
                root.GetComponent<OVRMesh>() != null || root.GetComponent<OVRMeshRenderer>() != null ||
                root.GetComponentInChildren<Renderer>(true) != null)
            {
                Dispose();
                return false;
            }
            hand.RayHelper = null;
            root.SetActive(true);
            return true;
        }

        public bool TryDistanceSquared(Transform parent, Vector3 first, Vector3 second, out float minimum)
        {
            minimum = float.PositiveInfinity;
            lastDistanceSquared = minimum;
            lastSampleFrame = Time.frameCount;
            confidenceMask = sampleMask = 0;
            if (root == null) return Reject("hand_instance_missing");
            if (root.transform.parent != parent) return Reject("tracking_parent_changed");
            if (hand == null || !hand.isActiveAndEnabled) return Reject("hand_component_disabled_or_missing");
            if (!hand.IsDataValid) return Reject("hand_data_invalid");
            if (!hand.IsTracked) return Reject("hand_not_tracked");
            if (!hand.IsDataHighConfidence) return Reject("hand_confidence_low");
            if (skeleton == null || !skeleton.isActiveAndEnabled) return Reject("skeleton_disabled_or_missing");
            if (!skeleton.IsInitialized) return Reject("skeleton_uninitialized");
            if (!skeleton.IsDataValid) return Reject("skeleton_data_invalid");
            if (!skeleton.IsDataHighConfidence) return Reject("skeleton_confidence_low");
            if (!Finite(hand.HandScale) || hand.HandScale <= 0) return Reject("hand_scale_invalid");
            if (OVRInput.AreHandPosesGeneratedByControllerData(OVRPlugin.Step.Render, (OVRInput.Hand)(int)side))
                return Reject("controller_driven_pose");
            if (OVRInput.GetControllerIsInHandState((OVRInput.Hand)(int)side) == OVRInput.ControllerInHandState.ControllerInHand)
                return Reject("controller_in_hand");

            confidenceMask = SelectConfidentPointMask(
                hand.GetFingerConfidence(OVRHand.HandFinger.Thumb) == OVRHand.TrackingConfidence.High,
                hand.GetFingerConfidence(OVRHand.HandFinger.Index) == OVRHand.TrackingConfidence.High,
                hand.GetFingerConfidence(OVRHand.HandFinger.Middle) == OVRHand.TrackingConfidence.High,
                hand.GetFingerConfidence(OVRHand.HandFinger.Ring) == OVRHand.TrackingConfidence.High,
                hand.GetFingerConfidence(OVRHand.HandFinger.Pinky) == OVRHand.TrackingConfidence.High);
            if (confidenceMask == 0) return Reject("no_confident_contact_points");
            if (!pointsCached && !CachePoints()) return Reject("skeleton_contact_bones_pending");
            if (!Finite(root.transform.position) || !Finite(root.transform.rotation) ||
                !Finite(root.transform.lossyScale)) return Reject("hand_root_nonfinite");

            if ((confidenceMask & 32) != 0 && Position(wrist, out Vector3 wristPoint) &&
                Position(indexBase, out Vector3 indexPoint) && Position(pinkyBase, out Vector3 pinkyPoint))
            {
                Vector3 palm = (wristPoint + indexPoint + pinkyPoint) / 3f;
                minimum = Mathf.Min((palm - first).sqrMagnitude, (palm - second).sqrMagnitude);
                sampleMask |= 32;
            }
            for (int i = 0; i < tips.Length; ++i)
            {
                if ((confidenceMask & (1 << i)) == 0 || !Position(tips[i], out Vector3 point)) continue;
                minimum = Mathf.Min(minimum, Mathf.Min((point - first).sqrMagnitude, (point - second).sqrMagnitude));
                sampleMask |= 1 << i;
            }
            if (sampleMask == 0 || !Finite(minimum)) return Reject("no_finite_confident_contact_points");
            lastDistanceSquared = minimum;
            rejectionReason = "ready";
            return true;
        }

        bool Reject(string reason)
        {
            rejectionReason = reason;
            return false;
        }

        public string DiagnosticSummary(bool armed)
        {
            return "reason=" + rejectionReason + ",sample_frame=" + lastSampleFrame +
                ",tracked=" + (hand != null && hand.IsTracked) +
                ",hand_high=" + (hand != null && hand.IsDataHighConfidence) +
                ",confidence_mask=" + confidenceMask + ",sample_mask=" + sampleMask +
                ",min_m=" + Mathf.Sqrt(lastDistanceSquared).ToString("F3") + ",armed=" + armed;
        }

        bool CachePoints()
        {
            var bones = skeleton.Bones;
            if (bones == null || bones.Count == 0) return false;
            for (int i = 0; i < bones.Count; ++i)
            {
                var bone = bones[i];
                if (bone == null) continue;
                switch (bone.Id)
                {
                    case OVRSkeleton.BoneId.Hand_WristRoot: wrist = bone.Transform; break;
                    case OVRSkeleton.BoneId.Hand_Index1: indexBase = bone.Transform; break;
                    case OVRSkeleton.BoneId.Hand_Pinky1: pinkyBase = bone.Transform; break;
                    case OVRSkeleton.BoneId.Hand_ThumbTip: tips[0] = bone.Transform; break;
                    case OVRSkeleton.BoneId.Hand_IndexTip: tips[1] = bone.Transform; break;
                    case OVRSkeleton.BoneId.Hand_MiddleTip: tips[2] = bone.Transform; break;
                    case OVRSkeleton.BoneId.Hand_RingTip: tips[3] = bone.Transform; break;
                    case OVRSkeleton.BoneId.Hand_PinkyTip: tips[4] = bone.Transform; break;
                }
            }
            pointsCached = wrist != null || indexBase != null || pinkyBase != null;
            for (int i = 0; i < tips.Length; ++i) pointsCached |= tips[i] != null;
            return pointsCached;
        }

        static bool Position(Transform target, out Vector3 value)
        {
            value = target != null ? target.position : Vector3.zero;
            return target != null && Finite(value) && Finite(target.rotation);
        }

        public void Dispose()
        {
            if (root != null)
            {
                root.SetActive(false);
                Destroy(root);
            }
            root = null;
            hand = null;
            skeleton = null;
            wrist = indexBase = pinkyBase = null;
            for (int i = 0; i < tips.Length; ++i) tips[i] = null;
            pointsCached = false;
        }
    }

    static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    static bool Finite(Quaternion value) => Finite(value.x) && Finite(value.y) && Finite(value.z) && Finite(value.w);
}
