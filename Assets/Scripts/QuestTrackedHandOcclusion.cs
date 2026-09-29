using System;
using System.Collections.Generic;
using Meta.XR.EnvironmentDepth;
using UnityEngine;
using UnityEngine.XR;

// Replaces native hand depth only while BOTH tracked SDK meshes are usable.
// The SDK's RemoveHands setter has no acknowledgement of the applied texture state.
[DefaultExecutionOrder(21000)]
public sealed class QuestTrackedHandOcclusion : MonoBehaviour
{
    const double RetrySeconds = 0.5;
    const double AcquireSeconds = 0.2;
    const double InitializationTimeoutSeconds = 2;
    readonly HandData left = new HandData(OVRPlugin.Hand.HandLeft, "QuestTrackedHandLeft");
    readonly HandData right = new HandData(OVRPlugin.Hand.HandRight, "QuestTrackedHandRight");
    EnvironmentDepthManager manager;
    OVRCameraRig rig;
    InputDevice head;
    bool initialized, paused, bound, invalidGeometry, warned;
    double nextRetry, usableSince = -1, pendingSince = -1;

    // Reports the requested replacement and enabled meshes, not a native removal acknowledgement.
    public bool IsReplacementActive { get; private set; }

    public bool Initialize(EnvironmentDepthManager depthManager, OVRCameraRig cameraRig)
    {
        Shutdown();
        if (depthManager == null || cameraRig == null || cameraRig.trackingSpace == null)
            return false;
        manager = depthManager;
        rig = cameraRig;
        head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
        nextRetry = 0;
        invalidGeometry = warned = false;
        initialized = true;
        SetReplacement(false);
        return true;
    }

    public void Shutdown()
    {
        initialized = false;
        SetReplacement(false);
        ReleaseHands();
        manager = null;
        rig = null;
    }

    void OnApplicationPause(bool value)
    {
        paused = value;
        if (value) SetReplacement(false);
    }

    void OnApplicationFocus(bool focused)
    {
        if (!focused) SetReplacement(false);
    }

    void OnDisable()
    {
        SetReplacement(false);
        // SDK Update can enable its renderer independently. Destroy/deactivate
        // the owned instances when this final LateUpdate gate cannot run.
        ReleaseHands();
    }

    void OnDestroy() => Shutdown();

    void LateUpdate()
    {
        if (!initialized) return;
        if (!ContextReady())
        {
            SetReplacement(false);
            return;
        }

        double now = Time.realtimeSinceStartupAsDouble;
        if (!bound)
        {
            SetReplacement(false);
            if (!invalidGeometry && now >= nextRetry)
            {
                nextRetry = now + RetrySeconds;
                TryPrepareHands();
                left.SetEnabled(false);
                right.SetEnabled(false);
            }
            return;
        }

        if (!left.Usable(rig.trackingSpace) || !right.Usable(rig.trackingSpace))
        {
            SetReplacement(false);
            return;
        }

        // This only debounces reacquisition. SDK poses continue updating directly;
        // no pose history, interpolation, or smoothing is introduced.
        if (usableSince < 0) usableSince = now;
        bool ready = now - usableSince >= AcquireSeconds;
        left.SetEnabled(ready);
        right.SetEnabled(ready);
        if (ready)
        {
            if (!manager.RemoveHands) manager.RemoveHands = true;
            IsReplacementActive = true;
        }
    }

    bool ContextReady()
    {
        if (paused || !Application.isFocused || manager == null || !manager.isActiveAndEnabled ||
            !manager.IsDepthAvailable || rig == null || rig.trackingSpace == null) return false;
        if (!head.isValid) head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
        return head.isValid && head.TryGetFeatureValue(CommonUsages.userPresence, out bool present) && present &&
            head.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) && tracked;
    }

    void TryPrepareHands()
    {
        try
        {
            if (!left.Created || !right.Created)
            {
                if (!left.NativeGeometryReady() || !right.NativeGeometryReady()) return;
                if (!left.Create(rig.trackingSpace) || !right.Create(rig.trackingSpace))
                {
                    ReleaseHands();
                    WarnOnce("Inactive tracked hand Resources prefabs or their depth materials are unavailable.");
                }
                else pendingSince = Time.realtimeSinceStartupAsDouble;
                // Allow SDK Start/Update to initialize and bind before retrying.
                return;
            }

            var leftResult = left.Bind();
            var rightResult = right.Bind();
            if (leftResult == BindResult.Invalid || rightResult == BindResult.Invalid)
            {
                invalidGeometry = true;
                ReleaseHands();
                WarnOnce("Tracked hand mesh weights or bound bone indices are invalid; native hands remain enabled.");
                return;
            }
            bound = leftResult == BindResult.Ready && rightResult == BindResult.Ready;
            if (bound) pendingSince = -1;
            else if (pendingSince >= 0 && Time.realtimeSinceStartupAsDouble - pendingSince >= InitializationTimeoutSeconds)
            {
                // OVRMesh only retries a failed Awake mesh fetch in the Editor.
                // Recreate pending instances so Android can recover from a transient fetch failure.
                ReleaseHands();
                WarnOnce("Tracked hand initialization timed out; retrying while native hands remain enabled.");
            }
        }
        catch (Exception error)
        {
            ReleaseHands();
            WarnOnce("Tracked hand initialization failed; native hands remain enabled. " + error.Message);
        }
    }

    void SetReplacement(bool enabled)
    {
        left.SetEnabled(enabled);
        right.SetEnabled(enabled);
        if (manager != null && manager.RemoveHands != enabled) manager.RemoveHands = enabled;
        IsReplacementActive = enabled;
        if (!enabled) usableSince = -1;
    }

    void ReleaseHands()
    {
        left.Dispose();
        right.Dispose();
        bound = false;
        usableSince = -1;
        pendingSince = -1;
    }

    void WarnOnce(string message)
    {
        if (warned) return;
        warned = true;
        Debug.LogWarning("GSAC tracked hand occlusion: " + message, this);
    }

    enum BindResult { Pending, Ready, Invalid }

    sealed class HandData
    {
        readonly OVRPlugin.Hand side;
        readonly string resource;
        OVRPlugin.HandState nativeState = new OVRPlugin.HandState();
        OVRPlugin.Skeleton2 nativeSkeleton = new OVRPlugin.Skeleton2();
        GameObject root;
        OVRHand hand;
        OVRSkeleton skeleton;
        OVRMesh mesh;
        OVRMeshRenderer binder;
        SkinnedMeshRenderer renderer;
        IList<OVRBone> bones;
        bool weightsChecked, weightsValid;

        public bool Created => root != null;

        public HandData(OVRPlugin.Hand handSide, string resourceName)
        {
            side = handSide;
            resource = resourceName;
        }

        public bool NativeGeometryReady()
        {
            if (!OVRManager.OVRManagerinitialized || ControllerInput() ||
                !OVRPlugin.GetHandState(OVRPlugin.Step.Render, side, ref nativeState) ||
                (nativeState.Status & OVRPlugin.HandStatus.HandTracked) == 0 ||
                nativeState.HandConfidence != OVRPlugin.TrackingConfidence.High ||
                nativeState.FingerConfidences == null || nativeState.FingerConfidences.Length < 5) return false;
            for (int i = 0; i < 5; ++i)
                if (nativeState.FingerConfidences[i] != OVRPlugin.TrackingConfidence.High) return false;
            return OVRPlugin.GetSkeleton2((OVRPlugin.SkeletonType)(int)side, ref nativeSkeleton) &&
                nativeSkeleton.NumBones > 0 &&
                OVRPlugin.GetMesh((OVRPlugin.MeshType)(int)side, out var nativeMesh) &&
                nativeMesh.NumVertices > 0 && nativeMesh.NumIndices > 0;
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
            mesh = root.GetComponent<OVRMesh>();
            binder = root.GetComponent<OVRMeshRenderer>();
            renderer = root.GetComponent<SkinnedMeshRenderer>();
            if (hand == null || skeleton == null || mesh == null || binder == null || renderer == null ||
                renderer.sharedMaterial == null || renderer.sharedMaterial.renderQueue != 1900) return false;
            renderer.quality = SkinQuality.Bone4;
            renderer.enabled = false;
            root.SetActive(true);
            return true;
        }

        public BindResult Bind()
        {
            if (mesh == null || skeleton == null || !mesh.IsInitialized || !skeleton.IsInitialized)
                return BindResult.Pending;
            if (!binder.IsInitialized) binder.ForceRebind();
            SetEnabled(false);
            if (!binder.IsInitialized || mesh.Mesh == null || mesh.Mesh.vertexCount <= 0)
                return BindResult.Pending;
            bones = skeleton.Bones;
            if (!weightsChecked)
            {
                weightsChecked = true;
                weightsValid = ValidWeights();
            }
            return weightsValid ? BindResult.Ready : BindResult.Invalid;
        }

        bool ValidWeights()
        {
            var boundBones = renderer.bones;
            if (boundBones == null || boundBones.Length == 0) return false;
            for (int i = 0; i < boundBones.Length; ++i) if (boundBones[i] == null) return false;
            BoneWeight[] weights = mesh.Mesh.boneWeights;
            if (weights.Length != mesh.Mesh.vertexCount) return false;
            foreach (BoneWeight weight in weights)
            {
                float sum = weight.weight0 + weight.weight1 + weight.weight2 + weight.weight3;
                if (!ValidInfluence(weight.weight0, weight.boneIndex0, boundBones.Length) ||
                    !ValidInfluence(weight.weight1, weight.boneIndex1, boundBones.Length) ||
                    !ValidInfluence(weight.weight2, weight.boneIndex2, boundBones.Length) ||
                    !ValidInfluence(weight.weight3, weight.boneIndex3, boundBones.Length) ||
                    !Finite(sum) || Mathf.Abs(sum - 1) > 0.01f) return false;
            }
            return true;
        }

        static bool ValidInfluence(float weight, int index, int count) =>
            Finite(weight) && weight >= 0 && (weight == 0 || (index >= 0 && index < count));

        bool ControllerInput() => (OVRInput.GetActiveController() & OVRInput.Controller.Touch) != 0 ||
            OVRInput.AreHandPosesGeneratedByControllerData(OVRPlugin.Step.Render, (OVRInput.Hand)(int)side) ||
            OVRInput.GetControllerIsInHandState((OVRInput.Hand)(int)side) == OVRInput.ControllerInHandState.ControllerInHand;

        public bool Usable(Transform parent)
        {
            if (root == null || root.transform.parent != parent || hand == null || !hand.isActiveAndEnabled ||
                !hand.IsDataValid || !hand.IsTracked || !hand.IsDataHighConfidence || skeleton == null ||
                !skeleton.isActiveAndEnabled || !skeleton.IsDataValid || !skeleton.IsDataHighConfidence ||
                !weightsValid || ControllerInput()) return false;
            for (int i = 0; i < 5; ++i)
                if (hand.GetFingerConfidence((OVRHand.HandFinger)i) != OVRHand.TrackingConfidence.High) return false;
            if (!Finite(hand.HandScale) || hand.HandScale <= 0 || !Finite(root.transform.localPosition) ||
                !Finite(root.transform.localScale) || !Finite(root.transform.localRotation)) return false;
            if (!ReferenceEquals(bones, skeleton.Bones)) bones = skeleton.Bones;
            if (bones == null || bones.Count == 0) return false;
            for (int i = 0; i < bones.Count; ++i)
            {
                var bone = bones[i]?.Transform;
                if (bone == null || !Finite(bone.localPosition) || !Finite(bone.localRotation)) return false;
            }
            return true;
        }

        public void SetEnabled(bool value)
        {
            if (renderer != null) renderer.enabled = value;
        }

        public void Dispose()
        {
            SetEnabled(false);
            if (root != null) root.SetActive(false);
            if (mesh != null && mesh.Mesh != null) Destroy(mesh.Mesh);
            if (root != null) Destroy(root);
            root = null;
            hand = null;
            skeleton = null;
            mesh = null;
            binder = null;
            renderer = null;
            bones = null;
            weightsChecked = weightsValid = false;
        }

        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        static bool Finite(Quaternion value) => Finite(value.x) && Finite(value.y) && Finite(value.z) && Finite(value.w);
    }
}
