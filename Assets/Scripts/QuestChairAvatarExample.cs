// Explicit X/X/Y/Y demonstration: reveal T1 before placing the avatar; no live chair observation.
using System;
using System.IO;
using UnityEngine;
using UnityEngine.XR;

[DefaultExecutionOrder(250)]
[DisallowMultipleComponent]
[RequireComponent(typeof(QuestSavedRoomMesh))]
public sealed class QuestChairAvatarExample : MonoBehaviour
{
    [SerializeField] QuestSavedRoomMesh reader;
    [SerializeField] ARGroundPlacement groundPlacement;
    PoseController poseController;
    Transform poseControllerAvatarRoot;
    [SerializeField, Min(0f)] float avatarMoveSpeed = 0.5f;
    [SerializeField, Range(0.05f, 0.5f)] float leftStickDeadzone = 0.2f;
    Camera headCamera;

    enum Target { None, T0, T1 }
    Target selectedTarget;
    InputEdges inputEdges;
    StickInput stickInput;
    TriggerInput triggerInput;
    float rawLeftTrigger;
    bool leftTriggerAvailable, leftTriggerTracked, triggerInputReady;
    int poseToggleAttempts, poseToggleCount, failedPoseToggleCount;
    string lastPoseAction = "none", lastPoseToggleError = "";
    Vector2 rawLeftStick, pendingMoveStick;
    Vector3 manualTargetOffset;
    bool leftStickAvailable, headTracked, movementInputReady;
    int movementFrames;
    float distanceMoved;
    string movementError = "";
    bool applicationFocused;
    bool applicationPaused;
    bool leftDeviceValid;
    bool hasPreviousLeftDevice;
    InputDevice previousLeftDevice;
    ulong leftDeviceGeneration;
    bool leftDeviceTracked;
    bool leftButtonsAvailable;
    bool inputReady;
    bool hasAppliedPose;
    Pose lastAppliedPose;
    float lastAppliedHeight;
    float nextStatusWriteTime;
    float nextFollowAttemptTime;
    int t0Actions;
    int t1Actions;
    int t1RevealActions;
    int lastRevealFrame = -1;
    int actionAttempts;
    int failedActions;
    int roomFollowUpdates;
    string lastAction = "none";
    bool lastActionSucceeded;
    string lastActionError = "";
    string followError = "";
    string lastStatusWriteError = "";

    const string Source = "hardcoded_chair_avatar_example_not_live_observation";
    public string CurrentTargetLabel => selectedTarget == Target.None ? "NONE" : selectedTarget.ToString();
    public int T0ActionCount => t0Actions;
    public int T1ActionCount => t1Actions;
    public int T1RevealActionCount => t1RevealActions;
    bool SuppressAvatarMovementThisFrame => lastRevealFrame == Time.frameCount;
    public int FailedActionCount => failedActions;
    public int PoseToggleCount => poseToggleCount;
    public int FailedPoseToggleCount => failedPoseToggleCount;

    void OnEnable()
    {
        applicationFocused = Application.isFocused;
        applicationPaused = false;
        inputEdges.Reset();
        stickInput.Reset();
        triggerInput.Reset();
        triggerInputReady = false;
        pendingMoveStick = Vector2.zero;
        movementInputReady = false;
        hasAppliedPose = false;
        nextStatusWriteTime = nextFollowAttemptTime = 0f;
        EnsureReferences();
    }

    void OnDisable()
    {
        inputEdges.Reset();
        stickInput.Reset();
        triggerInput.Reset();
        triggerInputReady = false;
        pendingMoveStick = Vector2.zero;
        movementInputReady = false;
        inputReady = false;
    }

    void OnApplicationFocus(bool focused)
    {
        applicationFocused = focused;
        inputEdges.Reset();
        stickInput.Reset();
        triggerInput.Reset();
        triggerInputReady = false;
        pendingMoveStick = Vector2.zero;
        movementInputReady = false;
        inputReady = false;
    }

    void OnApplicationPause(bool paused)
    {
        applicationPaused = paused;
        inputEdges.Reset();
        stickInput.Reset();
        triggerInput.Reset();
        triggerInputReady = false;
        pendingMoveStick = Vector2.zero;
        movementInputReady = false;
        inputReady = false;
    }

    bool HasFocus
    {
        get
        {
            bool focused = applicationFocused && !applicationPaused && Application.isFocused;
#if UNITY_ANDROID && !UNITY_EDITOR
            focused &= OVRPlugin.hasVrFocus;
#endif
            return focused;
        }
    }

    void EnsureReferences()
    {
        if (reader == null) reader = GetComponent<QuestSavedRoomMesh>();
        if (groundPlacement == null) groundPlacement = FindObjectOfType<ARGroundPlacement>(true);
        if (headCamera == null) headCamera = Camera.main;
        Transform avatar = groundPlacement != null ? groundPlacement.AvatarRoot : null;
        if (poseControllerAvatarRoot != avatar || poseController == null)
        {
            poseControllerAvatarRoot = avatar;
            poseController = avatar != null ? avatar.GetComponentInChildren<PoseController>(true) : null;
        }
    }

    void LateUpdate()
    {
        EnsureReferences();
        PollLeftInput();
        if (HasFocus && !SuppressAvatarMovementThisFrame) FollowSelectedRoomPose();
        ApplyLeftStickMovement();
        WriteStatus(false);
    }

    void PollLeftInput()
    {
        InputDevice left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
        leftDeviceValid = left.isValid;
        if (leftDeviceValid)
        {
            if (!hasPreviousLeftDevice || !left.Equals(previousLeftDevice))
            {
                leftDeviceGeneration++;
                previousLeftDevice = left;
                hasPreviousLeftDevice = true;
            }
        }
        else hasPreviousLeftDevice = false;
        bool x = false, y = false;
        leftButtonsAvailable = leftDeviceValid &&
            left.TryGetFeatureValue(CommonUsages.primaryButton, out x) &&
            left.TryGetFeatureValue(CommonUsages.secondaryButton, out y);
        leftDeviceTracked = leftDeviceValid &&
            (!left.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) || tracked);
        bool ready = TryGetReadyTarget(false, out _, out _, out _);
        // The preview needs only a valid saved target; the avatar can still be loading.
        bool revealReady = reader != null && !reader.ChairExampleTargetRevealed && TryGetRevealReady(out _);
        inputReady = HasFocus && leftButtonsAvailable && leftDeviceTracked && (ready || revealReady);
        int edge = inputEdges.Sample(inputReady, leftDeviceGeneration, x, y);
        if (edge == 1) TryPlaceAvatarAtT0();
        else if (edge == 2) TryRevealOrPlaceAvatarAtT1();

        rawLeftStick = Vector2.zero;
        leftStickAvailable = leftDeviceValid && left.TryGetFeatureValue(CommonUsages.primary2DAxis, out rawLeftStick);
        InputDevice head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
        headTracked = head.isValid && head.TryGetFeatureValue(CommonUsages.isTracked, out bool headIsTracked) && headIsTracked;
        bool controllerTracked = leftDeviceValid && left.TryGetFeatureValue(CommonUsages.isTracked, out bool leftIsTracked) && leftIsTracked;
        movementInputReady = HasFocus && ready && leftStickAvailable && controllerTracked && headTracked &&
            headCamera != null && headCamera.isActiveAndEnabled;
        // A successful X/Y resets this gate before the stick sample, so a held
        // stick cannot displace the just-selected target in the same frame.
        pendingMoveStick = stickInput.Sample(movementInputReady, leftDeviceGeneration, rawLeftStick, leftStickDeadzone);

        rawLeftTrigger = 0f;
        leftTriggerAvailable = leftDeviceValid && left.TryGetFeatureValue(CommonUsages.trigger, out rawLeftTrigger) &&
            Finite(rawLeftTrigger) && rawLeftTrigger >= 0f && rawLeftTrigger <= 1f;
        if (!leftTriggerAvailable) rawLeftTrigger = 0f;
        leftTriggerTracked = controllerTracked;
        // Sitting does not depend on room-anchor loading or a selected T0/T1.
        // Initialization/pending bakes disarm input until another neutral release.
        triggerInputReady = HasFocus && leftTriggerAvailable && leftTriggerTracked && TryGetPoseToggleReady(out _);
        if (triggerInput.Sample(triggerInputReady, leftDeviceGeneration, rawLeftTrigger))
            TryToggleSittingPose();
    }

    bool TryGetPoseToggleReady(out string detail)
    {
        EnsureReferences();
        if (poseController == null || !poseController.isActiveAndEnabled)
        {
            detail = "Waiting for the active avatar PoseController.";
            return false;
        }
        if (!poseController.CanToggleSittingPose)
        {
            detail = "Waiting for pose initialization or the pending pose bake: " + poseController.SittingPoseDetail;
            return false;
        }
        if (!poseController.IsSittingPose && (groundPlacement == null || !groundPlacement.CanPlaceAvatarAtChairPose))
        {
            detail = groundPlacement == null ? "Waiting for ARGroundPlacement before entering sitting." :
                "Waiting to preserve standing placement before sitting: " + groundPlacement.ChairPlacementDetail;
            return false;
        }
        detail = "";
        return true;
    }

    // Toggle actual body state, preserving chair memory, target, root and manual
    // movement offset. Exiting uses PoseController's exact existing No Pose path.
    public bool TryToggleSittingPose()
    {
        poseToggleAttempts++;
        triggerInput.Reset();
        triggerInputReady = false;
        if (!TryGetPoseToggleReady(out string detail)) return RejectPoseToggle(detail);
        bool wasSitting = poseController.IsSittingPose;
        lastPoseAction = wasSitting ? "restore_no_pose" : "apply_hardcoded_sitting";
        try
        {
            // Cache the standing contact and suppress startup placement before
            // changing vertices, including a first sit made before any X press.
            if (!wasSitting && !groundPlacement.TryPreparePoseChangePlacement())
                return RejectPoseToggle("Standing placement preparation rejected: " + groundPlacement.ChairPlacementDetail);
            bool applied = wasSitting ? poseController.TryRestoreNoPose() : poseController.TryApplySittingPose();
            if (!applied) return RejectPoseToggle("Pose toggle rejected: " + poseController.SittingPoseDetail);
        }
        catch (Exception error)
        {
            return RejectPoseToggle("Pose toggle failed: " + error.Message);
        }
        poseToggleCount++;
        lastPoseToggleError = "";
        WriteStatus(true);
        return true;
    }

    bool RejectPoseToggle(string detail)
    {
        failedPoseToggleCount++;
        lastPoseToggleError = string.IsNullOrEmpty(detail) ? "The avatar pose is not ready to toggle." : detail;
        WriteStatus(true);
        return false;
    }

    void ApplyLeftStickMovement()
    {
        if (SuppressAvatarMovementThisFrame || !movementInputReady || pendingMoveStick.sqrMagnitude <= 0f || !HasFocus || groundPlacement == null || headCamera == null)
            return;
        if (selectedTarget != Target.None && (!hasAppliedPose || !string.IsNullOrEmpty(followError)))
        { stickInput.Reset(); return; }
        Vector3 delta = CalculateHorizontalMovement(pendingMoveStick, headCamera.transform.rotation,
            avatarMoveSpeed, Mathf.Min(Time.unscaledDeltaTime, 0.05f));
        if (delta.sqrMagnitude <= 0f) return;
        // Follow has run first, so the offset is recorded against the latest
        // selected chair heading. Only this separate offset changes; T0/T1 and
        // the reader's remembered chair pose remain untouched.
        Vector3 nextOffset = manualTargetOffset;
        if (selectedTarget != Target.None && hasAppliedPose)
            nextOffset += Quaternion.Inverse(HorizontalHeading(lastAppliedPose.rotation)) * delta;
        nextOffset.y = 0;
        if (!Finite(nextOffset.x) || !Finite(nextOffset.z))
        { movementError = "The avatar movement offset is invalid."; stickInput.Reset(); return; }
        if (!groundPlacement.TryMoveAvatarHorizontal(delta))
        {
            movementError = groundPlacement.ChairPlacementDetail;
            stickInput.Reset();
            return;
        }
        manualTargetOffset = nextOffset;
        movementFrames++;
        distanceMoved += delta.magnitude;
        movementError = "";
    }

    static Quaternion HorizontalHeading(Quaternion rotation)
    {
        Vector3 forward = Vector3.ProjectOnPlane(rotation * Vector3.forward, Vector3.up);
        if (forward.sqrMagnitude < 0.000001f)
        {
            Vector3 right = Vector3.ProjectOnPlane(rotation * Vector3.right, Vector3.up);
            forward = Vector3.Cross(right, Vector3.up);
        }
        return Quaternion.LookRotation(forward.normalized, Vector3.up);
    }

    static Vector3 CalculateHorizontalMovement(Vector2 stick, Quaternion headRotation, float speed, float seconds)
    {
        if (!Finite(stick.x) || !Finite(stick.y) || !Finite(speed) || !Finite(seconds) ||
            speed <= 0f || seconds <= 0f || !ValidPose(new Pose(Vector3.zero, headRotation))) return Vector3.zero;
        stick = Vector2.ClampMagnitude(stick, 1f);
        Vector3 delta = HorizontalHeading(headRotation) * new Vector3(stick.x, 0f, stick.y) * (speed * seconds);
        delta.y = 0;
        return delta;
    }

    static Pose WithManualOffset(Pose target, Vector3 horizontalOffset)
    {
        horizontalOffset.y = 0;
        Vector3 worldOffset = HorizontalHeading(target.rotation) * horizontalOffset;
        worldOffset.y = 0;
        return new Pose(target.position + worldOffset, target.rotation);
    }

    // Explicit placement APIs keep their original semantics. Physical Y input
    // reveals the T1 preview first; subsequent Y presses use the placement API.
    public bool TryPlaceAvatarAtT0() => TrySelectTarget(false);
    public bool TryPlaceAvatarAtT1() => TrySelectTarget(true);

    public bool TryRevealOrPlaceAvatarAtT1()
    {
        EnsureReferences();
        if (reader != null && reader.ChairExampleTargetRevealed)
            return TryPlaceAvatarAtT1();

        actionAttempts++;
        lastAction = "T1_REVEAL";
        lastActionSucceeded = false;
        // Preserve selection, manual offset and follow state. Even if room
        // relocalization or a held stick arrives now, this preview frame cannot
        // move the avatar; the stick must return to neutral before moving again.
        SuppressMovementForReveal();
        if (!TryGetRevealReady(out string detail)) return RejectAction(detail);
        try
        {
            if (!reader.TryRevealChairExampleTarget())
                return RejectAction("T1 preview could not be revealed; wait for the saved room and target mesh.");
        }
        catch (Exception error)
        {
            return RejectAction("T1 preview reveal failed: " + error.Message);
        }
        t1RevealActions++;
        lastActionSucceeded = true;
        lastActionError = "";
        WriteStatus(true);
        return true;
    }

    void SuppressMovementForReveal()
    {
        lastRevealFrame = Time.frameCount;
        stickInput.Reset();
        pendingMoveStick = Vector2.zero;
    }

    bool TryGetRevealReady(out string detail)
    {
        EnsureReferences();
        if (reader == null || !reader.ChairReferenceInitialized)
        {
            detail = reader == null ? "Waiting for QuestSavedRoomMesh on this GameObject." :
                "Waiting for the matching saved chair reference: " + reader.ChairReferenceDetail;
            return false;
        }
        if (!reader.TryGetChairExampleWorldPose(true, out Pose target) || !ValidPose(target))
        {
            detail = "Waiting for a valid room-localized T1 preview pose.";
            return false;
        }
        detail = "";
        return true;
    }

    bool TrySelectTarget(bool updated)
    {
        actionAttempts++;
        lastAction = updated ? "T1" : "T0";
        lastActionSucceeded = false;
        if (!TryGetReadyTarget(updated, out Pose desired, out float height, out string detail))
            return RejectAction(detail);

        Transform avatar = groundPlacement.AvatarRoot;
        if (avatar == null) return RejectAction("AvatarRoot is unavailable; wait for avatar initialization.");
        Pose previousAvatar = new Pose(avatar.position, avatar.rotation);
        try
        {
            // Place first: a placement rejection must never move chair memory.
            if (!groundPlacement.TryPlaceAvatarAtChairPose(desired, height))
            {
                avatar.SetPositionAndRotation(previousAvatar.position, previousAvatar.rotation);
                return RejectAction("Avatar placement rejected: " + groundPlacement.ChairPlacementDetail);
            }

            bool committed = updated ? reader.TryApplyHardcodedChairExample() : reader.TryResetChairExample();
            if (!committed)
            {
                avatar.SetPositionAndRotation(previousAvatar.position, previousAvatar.rotation);
                return RejectAction("Chair pose update rejected; avatar pose restored. " + reader.ChairReferenceDetail);
            }
        }
        catch (Exception error)
        {
            if (avatar != null) avatar.SetPositionAndRotation(previousAvatar.position, previousAvatar.rotation);
            return RejectAction("Chair/avatar action failed; avatar pose restored: " + error.Message);
        }

        selectedTarget = updated ? Target.T1 : Target.T0;
        manualTargetOffset = Vector3.zero;
        stickInput.Reset();
        pendingMoveStick = Vector2.zero;
        movementError = "";
        if (updated) t1Actions++; else t0Actions++;
        lastAppliedPose = desired;
        lastAppliedHeight = height;
        hasAppliedPose = true;
        lastActionSucceeded = true;
        lastActionError = followError = "";
        nextFollowAttemptTime = 0f;
        WriteStatus(true);
        return true;
    }

    bool RejectAction(string detail)
    {
        failedActions++;
        lastActionError = string.IsNullOrEmpty(detail) ? "Chair/avatar action was not ready." : detail;
        WriteStatus(true);
        return false;
    }

    bool TryGetReadyTarget(bool updated, out Pose desired, out float height, out string detail)
    {
        desired = default;
        height = 0f;
        EnsureReferences();
        if (reader == null || !reader.ChairReferenceInitialized)
        {
            detail = reader == null ? "Waiting for QuestSavedRoomMesh on this GameObject." :
                "Waiting for the matching saved chair reference: " + reader.ChairReferenceDetail;
            return false;
        }
        if (!reader.TryGetChairExampleWorldPose(updated, out desired) || !ValidPose(desired))
        {
            detail = "Waiting for a valid room-localized chair example pose.";
            return false;
        }
        height = reader.ChairBoundsSize.y;
        if (!Finite(height) || height <= 0f)
        {
            detail = "The saved chair height is missing or invalid.";
            return false;
        }
        if (groundPlacement == null || !groundPlacement.CanPlaceAvatarAtChairPose)
        {
            detail = groundPlacement == null ? "Waiting for ARGroundPlacement and AvatarRoot." :
                "Waiting for avatar placement readiness: " + groundPlacement.ChairPlacementDetail;
            return false;
        }
        detail = "";
        return true;
    }

    void FollowSelectedRoomPose()
    {
        if (SuppressAvatarMovementThisFrame || selectedTarget == Target.None || Time.realtimeSinceStartup < nextFollowAttemptTime) return;
        if (!TryGetReadyTarget(selectedTarget == Target.T1, out Pose desired, out float height, out string detail))
        {
            followError = detail;
            return;
        }
        // Keep the chosen target attached to the room, without applying root-offset
        // corrections repeatedly to an unchanged pose or updating chair memory.
        if (hasAppliedPose && !PoseChanged(lastAppliedPose, desired) && Mathf.Abs(lastAppliedHeight - height) <= 0.001f)
        {
            followError = "";
            return;
        }
        Transform avatar = groundPlacement.AvatarRoot;
        if (avatar == null) { followError = "AvatarRoot disappeared while following the selected room pose."; return; }
        Pose previousAvatar = new Pose(avatar.position, avatar.rotation);
        try
        {
            if (!groundPlacement.TryPlaceAvatarAtChairPose(WithManualOffset(desired, manualTargetOffset), height))
            {
                avatar.SetPositionAndRotation(previousAvatar.position, previousAvatar.rotation);
                followError = "Room-follow placement rejected: " + groundPlacement.ChairPlacementDetail;
                nextFollowAttemptTime = Time.realtimeSinceStartup + 1f;
                return;
            }
            lastAppliedPose = desired;
            lastAppliedHeight = height;
            hasAppliedPose = true;
            roomFollowUpdates++;
            followError = "";
        }
        catch (Exception error)
        {
            if (avatar != null) avatar.SetPositionAndRotation(previousAvatar.position, previousAvatar.rotation);
            followError = "Room-follow placement failed: " + error.Message;
            nextFollowAttemptTime = Time.realtimeSinceStartup + 1f;
        }
    }

    static bool PoseChanged(Pose a, Pose b) =>
        (a.position - b.position).sqrMagnitude > 0.000001f || Quaternion.Angle(a.rotation, b.rotation) > 0.1f;
    static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    static bool ValidPose(Pose pose)
    {
        Vector3 p = pose.position;
        Quaternion q = pose.rotation;
        return Finite(p.x) && Finite(p.y) && Finite(p.z) && Finite(q.x) && Finite(q.y) && Finite(q.z) && Finite(q.w) &&
            Mathf.Abs(Quaternion.Dot(q, q) - 1f) < 0.002f;
    }

    [Serializable]
    sealed class Status
    {
        public int schemaVersion = 4;
        public string source = Source;
        public string updatedUtc, currentTarget, state, waitingReason, inputWaitReason, error;
        public bool hardcoded = true, liveChairObservation = false;
        public bool appFocused, appPaused, leftDeviceValid, leftDeviceTracked, leftButtonsAvailable, inputReady;
        public bool chairReferenceReady, avatarPlacementReady;
        public int actionAttempts, t0ActionCount, t1ActionCount, t1RevealActionCount, failedActionCount, roomFollowUpdateCount;
        public bool t1TargetRevealed, avatarMovementSuppressedForReveal;
        public string nextYAction;
        public string lastAction, lastActionError, roomFollowError, placementDetail;
        public bool lastActionSucceeded, hasT0WorldPose, hasT1WorldPose, hasChairWorldPose, hasAvatarWorldPose;
        public Pose t0WorldPose, t1WorldPose, chairWorldPose, avatarWorldPose;
        public bool leftStickAvailable, headTracked, movementInputReady, movementArmed;
        public Vector2 leftStick;
        public Vector3 manualTargetOffset;
        public float avatarMoveSpeed, leftStickDeadzone, distanceMoved;
        public int movementFrames;
        public string movementWaitReason, movementError;
        public bool leftTriggerAvailable, leftTriggerTracked, triggerInputReady, triggerArmed;
        public float leftTrigger;
        public bool poseToggleReady, sittingPoseActive;
        public string poseState, poseDetail, poseToggleWaitReason, lastPoseAction, lastPoseToggleError;
        public int poseToggleAttempts, poseToggleCount, failedPoseToggleCount;
    }

    void WriteStatus(bool force)
    {
        // Editor smoke tests can exercise the public methods without touching
        // the user's persistent device-status directory.
        if (!Application.isPlaying || (!force && Time.realtimeSinceStartup < nextStatusWriteTime)) return;
        nextStatusWriteTime = Time.realtimeSinceStartup + 1f;
        bool ready = TryGetReadyTarget(selectedTarget == Target.T1, out _, out _, out string waiting);
        bool revealed = reader != null && reader.ChairExampleTargetRevealed;
        bool revealReady = !revealed && TryGetRevealReady(out _);
        bool poseReady = TryGetPoseToggleReady(out string poseWaiting);
        Status status = new Status
        {
            updatedUtc = DateTime.UtcNow.ToString("O"), currentTarget = CurrentTargetLabel,
            state = !ready ? (revealReady ? "ready_to_reveal_T1" : "waiting") :
                !string.IsNullOrEmpty(followError) ? "placement_error" :
                revealed && selectedTarget != Target.T1 ? "T1_revealed_waiting_for_Y" :
                selectedTarget == Target.None ? "ready_waiting_for_X_or_Y" : "selected",
            waitingReason = waiting, error = !string.IsNullOrEmpty(followError) ? followError : lastActionError,
            inputWaitReason = !HasFocus ? "Restore app focus, then release X/Y and center the left stick." :
                !leftDeviceValid ? "Connect the left controller." :
                !leftDeviceTracked ? "Restore left-controller tracking." :
                !leftButtonsAvailable ? "Left-controller X/Y button input is unavailable." :
                !ready && !revealReady ? waiting : "",
            appFocused = HasFocus, appPaused = applicationPaused,
            leftDeviceValid = leftDeviceValid, leftDeviceTracked = leftDeviceTracked,
            leftButtonsAvailable = leftButtonsAvailable, inputReady = inputReady,
            leftStickAvailable = leftStickAvailable, headTracked = headTracked,
            movementInputReady = movementInputReady, movementArmed = stickInput.Armed,
            leftStick = rawLeftStick, manualTargetOffset = manualTargetOffset,
            avatarMoveSpeed = avatarMoveSpeed, leftStickDeadzone = leftStickDeadzone,
            distanceMoved = distanceMoved, movementFrames = movementFrames, movementError = movementError,
            movementWaitReason = !HasFocus ? "Restore app focus and center the left stick." :
                !leftDeviceValid ? "Connect the left controller and center the stick." :
                !leftDeviceTracked || !headTracked ? "Restore headset and left-controller tracking." :
                !leftStickAvailable ? "Left-controller stick input is unavailable." :
                !ready ? waiting : !stickInput.Armed ? "Center the left stick to enable avatar movement." : "",
            leftTriggerAvailable = leftTriggerAvailable, leftTriggerTracked = leftTriggerTracked,
            triggerInputReady = triggerInputReady, triggerArmed = triggerInput.Armed, leftTrigger = rawLeftTrigger,
            poseToggleReady = poseReady, sittingPoseActive = poseController != null && poseController.IsSittingPose,
            poseState = poseController == null ? "UNAVAILABLE" : poseController.IsSittingPose ? "SITTING" :
                poseController.NoPose ? "NO_POSE" : "OTHER",
            poseDetail = poseController != null ? poseController.SittingPoseDetail : "PoseController unavailable.",
            poseToggleWaitReason = !HasFocus ? "Restore app focus and fully release the left trigger." :
                !leftDeviceValid ? "Connect the left controller and release its trigger." :
                !leftTriggerTracked ? "Restore left-controller tracking and release its trigger." :
                !leftTriggerAvailable ? "Left-controller analog trigger input is unavailable." :
                !poseReady ? poseWaiting : !triggerInput.Armed ? "Fully release the left trigger before the next pose toggle." : "",
            poseToggleAttempts = poseToggleAttempts, poseToggleCount = poseToggleCount,
            failedPoseToggleCount = failedPoseToggleCount, lastPoseAction = lastPoseAction,
            lastPoseToggleError = lastPoseToggleError,
            chairReferenceReady = reader != null && reader.ChairReferenceInitialized,
            avatarPlacementReady = groundPlacement != null && groundPlacement.CanPlaceAvatarAtChairPose,
            actionAttempts = actionAttempts, t0ActionCount = t0Actions, t1ActionCount = t1Actions,
            t1RevealActionCount = t1RevealActions, t1TargetRevealed = revealed,
            avatarMovementSuppressedForReveal = SuppressAvatarMovementThisFrame,
            nextYAction = revealed ? "place_avatar_at_T1" : "reveal_T1_only",
            failedActionCount = failedActions, roomFollowUpdateCount = roomFollowUpdates,
            lastAction = lastAction, lastActionSucceeded = lastActionSucceeded, lastActionError = lastActionError,
            roomFollowError = followError, placementDetail = groundPlacement != null ? groundPlacement.ChairPlacementDetail : "ARGroundPlacement unavailable."
        };
        if (reader != null)
        {
            status.hasT0WorldPose = reader.TryGetChairExampleWorldPose(false, out status.t0WorldPose);
            status.hasT1WorldPose = reader.TryGetChairExampleWorldPose(true, out status.t1WorldPose);
            status.hasChairWorldPose = reader.TryGetChairPose(out status.chairWorldPose);
        }
        Transform avatar = groundPlacement != null ? groundPlacement.AvatarRoot : null;
        status.hasAvatarWorldPose = avatar != null;
        if (avatar != null) status.avatarWorldPose = new Pose(avatar.position, avatar.rotation);
        string path = Path.Combine(Application.persistentDataPath, "chair-avatar-example-status.json");
        string temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Application.persistentDataPath);
            File.WriteAllText(temporary, JsonUtility.ToJson(status, true));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
            lastStatusWriteError = "";
        }
        catch (Exception error)
        {
            if (lastStatusWriteError != error.Message)
                Debug.LogWarning("CHAIR_AVATAR_EXAMPLE status write failed: " + error.Message, this);
            lastStatusWriteError = error.Message;
        }
    }

    // A tracked, ready controller must cross neutral before its next press.
    // Separate thresholds prevent analog jitter from repeatedly toggling a pose.
    struct TriggerInput
    {
        bool seeded;
        ulong deviceId;
        public bool Armed { get; private set; }
        public void Reset() { seeded = false; Armed = false; }
        public bool Sample(bool available, ulong id, float value)
        {
            if (!available || !Finite(value) || value < 0f || value > 1f)
            { Reset(); return false; }
            if (!seeded || deviceId != id)
            { seeded = true; deviceId = id; Armed = false; }
            if (value <= 0.25f) { Armed = true; return false; }
            if (!Armed || value < 0.75f) return false;
            Armed = false;
            return true;
        }
    }

    // Every interruption or controller change requires an observed neutral stick.
    struct StickInput
    {
        bool seeded;
        ulong deviceId;
        public bool Armed { get; private set; }
        public void Reset() { seeded = false; Armed = false; }
        public Vector2 Sample(bool available, ulong id, Vector2 value, float deadzone)
        {
            if (!available || !Finite(value.x) || !Finite(value.y) || !Finite(deadzone))
            { Reset(); return Vector2.zero; }
            if (!seeded || deviceId != id)
            { seeded = true; deviceId = id; Armed = false; }
            deadzone = Mathf.Clamp(deadzone, 0.05f, 0.5f);
            float magnitude = value.magnitude;
            if (magnitude <= deadzone) { Armed = true; return Vector2.zero; }
            if (!Armed) return Vector2.zero;
            float strength = Mathf.Clamp01((magnitude - deadzone) / (1f - deadzone));
            return value.normalized * strength;
        }
    }
    // Seeding a newly ready or reconnected input with its current held state
    // means a release and a new press are always needed after interruptions.
    struct InputEdges
    {
        bool seeded, previousX, previousY;
        ulong deviceId;
        public void Reset() { seeded = false; }
        public int Sample(bool available, ulong id, bool x, bool y)
        {
            if (!available) { Reset(); return 0; }
            if (!seeded || deviceId != id)
            {
                seeded = true; deviceId = id; previousX = x; previousY = y;
                return 0;
            }
            bool xEdge = x && !previousX, yEdge = y && !previousY;
            previousX = x; previousY = y;
            if (x && y) return 0; // Ignore ambiguous simultaneous inputs.
            return xEdge ? 1 : yEdge ? 2 : 0;
        }
    }

#if UNITY_EDITOR
    public static bool RunChairAvatarInputSmokeTest(out string report)
    {
        var edges = new InputEdges();
        int[] observed = {
            edges.Sample(true, 1, true, false), edges.Sample(true, 1, false, false),
            edges.Sample(true, 1, true, false), edges.Sample(true, 1, true, false),
            edges.Sample(true, 1, false, false), edges.Sample(true, 1, true, false),
            edges.Sample(true, 1, false, false), edges.Sample(true, 1, false, true),
            edges.Sample(false, 1, false, true), edges.Sample(true, 1, false, true),
            edges.Sample(true, 1, false, false), edges.Sample(true, 1, true, false),
            edges.Sample(true, 2, true, false), edges.Sample(true, 2, false, false),
            edges.Sample(true, 2, true, true), edges.Sample(true, 2, true, false)
        };
        int[] expected = { 0, 0, 1, 0, 0, 1, 0, 2, 0, 0, 0, 1, 0, 0, 0, 0 };
        for (int i = 0; i < expected.Length; i++)
        {
            if (observed[i] != expected[i]) { report = "Input edge case " + i + " failed."; return false; }
        }
        edges.Reset(); // The same reset used by OnApplicationFocus/OnApplicationPause.
        if (edges.Sample(true, 2, true, false) != 0 || edges.Sample(true, 2, false, false) != 0 ||
            edges.Sample(true, 2, false, false) != 0)
        {
            report = "Focus reseed or unchanged-left/right-only input case failed.";
            return false;
        }
        edges.Reset();
        if (edges.Sample(true, 3, false, false) != 0 || edges.Sample(true, 3, false, true) != 2 ||
            edges.Sample(true, 3, false, true) != 0 || edges.Sample(true, 3, false, false) != 0 ||
            edges.Sample(true, 3, false, true) != 2 || edges.Sample(true, 3, false, true) != 0)
        { report = "Reveal and placement require two distinct Y presses; a held Y must not advance twice."; return false; }
        var trigger = new TriggerInput();
        bool[] triggerObserved = {
            trigger.Sample(true, 1, 1f), trigger.Sample(true, 1, 0.3f),
            trigger.Sample(true, 1, 0.25f), trigger.Sample(true, 1, 0.74f),
            trigger.Sample(true, 1, 0.75f), trigger.Sample(true, 1, 1f),
            trigger.Sample(true, 1, 0.3f), trigger.Sample(true, 1, 0.76f),
            trigger.Sample(true, 1, 0.25f), trigger.Sample(true, 1, 0.75f),
            // Untracked, unavailable, focus lost or bake pending all disarm.
            trigger.Sample(false, 1, 0f), trigger.Sample(true, 1, 1f),
            trigger.Sample(true, 1, 0f), trigger.Sample(true, 2, 1f),
            trigger.Sample(true, 2, 0f), trigger.Sample(true, 2, float.NaN),
            trigger.Sample(true, 2, 1f), trigger.Sample(true, 2, 0f),
            trigger.Sample(true, 2, 0.75f), trigger.Sample(true, 2, 1f)
        };
        bool[] triggerExpected = { false, false, false, false, true, false, false, false, false, true,
            false, false, false, false, false, false, false, false, true, false };
        for (int i = 0; i < triggerExpected.Length; i++)
        {
            if (triggerObserved[i] != triggerExpected[i])
            { report = "Trigger hysteresis/neutral-rearm case " + i + " failed."; return false; }
        }
        trigger.Sample(true, 2, 0f);
        trigger.Reset(); // The same reset used for focus/pause and pose actions.
        if (trigger.Sample(true, 2, 1f) || trigger.Armed || trigger.Sample(true, 2, 0f) ||
            !trigger.Armed || trigger.Sample(true, 2, 0f) || !trigger.Sample(true, 2, 0.75f) ||
            trigger.Sample(true, 2, 1f))
        { report = "Trigger focus/action reseed or unchanged-left/right-only sample failed."; return false; }
        var stick = new StickInput();
        if (stick.Sample(true, 1, Vector2.up, 0.2f) != Vector2.zero || stick.Armed ||
            stick.Sample(true, 1, Vector2.zero, 0.2f) != Vector2.zero || !stick.Armed ||
            stick.Sample(true, 1, new Vector2(0.1f, 0.1f), 0.2f) != Vector2.zero)
        { report = "Stick startup-neutral or radial deadzone case failed."; return false; }
        Vector2 half = stick.Sample(true, 1, new Vector2(0, 0.6f), 0.2f);
        Vector2 diagonal = stick.Sample(true, 1, Vector2.one, 0.2f);
        if (Vector2.Distance(half, new Vector2(0, 0.5f)) > 0.000001f || Mathf.Abs(diagonal.magnitude - 1f) > 0.000001f)
        { report = "Stick radial scaling or diagonal clamp failed."; return false; }
        if (stick.Sample(false, 1, Vector2.up, 0.2f) != Vector2.zero ||
            stick.Sample(true, 1, Vector2.up, 0.2f) != Vector2.zero || stick.Armed)
        { report = "Stick moved while unavailable or immediately after tracking/focus return."; return false; }
        stick.Sample(true, 1, Vector2.zero, 0.2f);
        if (stick.Sample(true, 2, Vector2.up, 0.2f) != Vector2.zero || stick.Armed)
        { report = "Reconnected controller bypassed neutral rearming."; return false; }
        stick.Sample(true, 2, Vector2.zero, 0.2f);
        stick.Reset(); // A successful X/Y uses this same reset before its stick sample.
        if (stick.Sample(true, 2, Vector2.up, 0.2f) != Vector2.zero || stick.Armed)
        { report = "Held stick displaced an X/Y target before returning to neutral."; return false; }
        stick.Sample(true, 2, Vector2.zero, 0.2f);
        if (stick.Sample(true, 2, new Vector2(float.NaN, 0), 0.2f) != Vector2.zero || stick.Armed)
        { report = "Invalid stick input was accepted."; return false; }

        Quaternion head = Quaternion.Euler(25, 90, 0);
        Vector3 forwardStep = CalculateHorizontalMovement(Vector2.up, head, 0.5f, 0.02f);
        Vector3 rightStep = CalculateHorizontalMovement(Vector2.right, head, 0.5f, 0.02f);
        Vector3 diagonalStep = CalculateHorizontalMovement(Vector2.one, head, 0.5f, 0.02f);
        if (Vector3.Distance(forwardStep, new Vector3(0.01f, 0, 0)) > 0.000001f ||
            Vector3.Distance(rightStep, new Vector3(0, 0, -0.01f)) > 0.000001f ||
            Mathf.Abs(diagonalStep.magnitude - 0.01f) > 0.000001f || diagonalStep.y != 0 ||
            CalculateHorizontalMovement(Vector2.up, head, 0.5f, 0f) != Vector3.zero)
        { report = "Head-relative horizontal movement, speed, or diagonal clamp failed."; return false; }
        Pose tiltedTarget = new Pose(new Vector3(10, 3, 20), Quaternion.Euler(12, 90, 0));
        Vector3 offset = new Vector3(1, 0, 2);
        Pose followed = WithManualOffset(tiltedTarget, offset);
        if (Vector3.Distance(followed.position, new Vector3(12, 3, 19)) > 0.00001f ||
            followed.rotation != tiltedTarget.rotation || followed.position.y != tiltedTarget.position.y ||
            WithManualOffset(tiltedTarget, Vector3.zero).position != tiltedTarget.position)
        { report = "Room-follow offset changed height/facing or failed fixed-target reset."; return false; }
        Vector3 recovered = Quaternion.Inverse(HorizontalHeading(tiltedTarget.rotation)) *
            (followed.position - tiltedTarget.position);
        if (Vector3.Distance(recovered, offset) > 0.00001f)
        { report = "Manual offset was not preserved in the relocalized horizontal target frame."; return false; }
        GameObject revealFixture = null;
        try
        {
            revealFixture = new GameObject("ChairRevealInputValidation") { hideFlags = HideFlags.HideAndDontSave };
            revealFixture.SetActive(false);
            var example = revealFixture.AddComponent<QuestChairAvatarExample>();
            example.selectedTarget = Target.T0;
            example.manualTargetOffset = new Vector3(0.7f, 0, -0.4f);
            example.hasAppliedPose = true;
            example.lastAppliedPose = tiltedTarget;
            example.lastAppliedHeight = 0.9f;
            example.nextFollowAttemptTime = 23f;
            example.followError = "preserve existing follow state";
            example.t0Actions = 2;
            example.t1Actions = 0;
            example.stickInput.Sample(true, 1, Vector2.zero, 0.2f);
            example.pendingMoveStick = example.stickInput.Sample(true, 1, Vector2.up, 0.2f);
            // Exercise the same reveal-frame guard used by the Y-only branch.
            // Both consumers must exit without resetting selection or follow state.
            example.SuppressMovementForReveal();
            example.FollowSelectedRoomPose();
            example.ApplyLeftStickMovement();
            if (!example.SuppressAvatarMovementThisFrame || example.selectedTarget != Target.T0 ||
                example.manualTargetOffset != new Vector3(0.7f, 0, -0.4f) || !example.hasAppliedPose ||
                example.lastAppliedPose.position != tiltedTarget.position || example.lastAppliedPose.rotation != tiltedTarget.rotation ||
                example.lastAppliedHeight != 0.9f || example.nextFollowAttemptTime != 23f ||
                example.followError != "preserve existing follow state" || example.roomFollowUpdates != 0 ||
                example.t0Actions != 2 || example.t1Actions != 0 || example.pendingMoveStick != Vector2.zero ||
                example.stickInput.Armed || example.stickInput.Sample(true, 1, Vector2.up, 0.2f) != Vector2.zero)
            { report = "Reveal-only frame changed placement/follow state or allowed held-stick movement."; return false; }
        }
        finally
        {
            if (revealFixture != null) DestroyImmediate(revealFixture);
        }
        report = "Input passed: X/X/Y/Y edges, reveal-only state preservation, focus/tracking/reconnect neutral rearm, same-frame target reset, " +
            "left-trigger 0.75/0.25 hysteresis and pending-pose reseed, radial deadzone, diagonal clamp, " +
            "0.5 m/s head-relative horizontal movement, and tilted-room offset preservation.";
        return true;
    }
#endif
}
