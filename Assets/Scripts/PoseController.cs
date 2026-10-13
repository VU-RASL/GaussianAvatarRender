using UnityEngine;
using System;
using System.Collections.Generic;
using System.IO;
using Unity.Mathematics;

[DefaultExecutionOrder(50)]
// [ExecuteInEditMode]
public class PoseController : MonoBehaviour
{   
#if DEVELOPMENT_BUILD || UNITY_EDITOR
    public static long QuestBakeTicks;
    public static int QuestBakeCount;
#endif
    public Transform debug;


    [SerializeField] public SMPLX smplx; // Reference to the SMPL-X model
    public bool no_pose = false;
    public bool a_pose = false;
    public bool using_custom = false;
    public bool visualable_mesh = true;
    public float poseSwitchTime = 3f;   // Time in seconds to switch poses
    [SerializeField, Range(1, 4)] int questVertexBakeInterval = 2;
    public float[] customPose;
    // public Material smplxMaterial;     // Assign the material with your custom shader

    public ComputeBuffer vertexBuffer; // GPU buffer to store vertex positions
    private Transform[] joints;         // Array to store SMPL-X joint transforms
    private readonly string[] customJointNames = new string[]
    {
        "pelvis", "left_hip", "right_hip", "spine1", "left_knee", "right_knee", "spine2",
        "left_ankle", "right_ankle", "spine3", "left_foot", "right_foot", "neck",
        "left_collar", "right_collar", "head", "left_shoulder", "right_shoulder",
        "left_elbow", "right_elbow", "left_wrist", "right_wrist", "jaw",
        "left_eye_smplhf", "right_eye_smplhf", "left_index1", "left_index2",
        "left_index3", "left_middle1", "left_middle2", "left_middle3", "left_pinky1",
        "left_pinky2", "left_pinky3", "left_ring1", "left_ring2", "left_ring3",
        "left_thumb1", "left_thumb2", "left_thumb3", "right_index1", "right_index2",
        "right_index3", "right_middle1", "right_middle2", "right_middle3",
        "right_pinky1", "right_pinky2", "right_pinky3", "right_ring1", "right_ring2",
        "right_ring3", "right_thumb1", "right_thumb2", "right_thumb3"
    };
    private bool isTPose = true;        // Toggle between poses
    private SkinnedMeshRenderer smr;    // Reference to SkinnedMeshRenderer
    private Animator[] smplxAnimators;
    private Coroutine customPoseCoroutine;
    private float[] activeCustomPose;
    private float initialAnimatorSpeed = 1.0f;
    private bool previousUsingCustom;
    private bool previousNoPose;
    private bool previousAPose;
    private bool previousVisualableMesh;
    private Mesh bakedMesh;
    private Vector3[] currentVertices;
    private Vector3 initialSmplxLocalPosition;
    private Quaternion initialSmplxLocalRotation;
    private Vector3 initialSmplxLocalScale;
    private Vector3 initialMeshLocalPosition;
    private Quaternion initialMeshLocalRotation;
    private Vector3 initialMeshLocalScale;
    private bool hasInitialTransforms;
    private int lastVertexBakeFrame = -1;
    private readonly List<Vector3> bakedVertexList = new List<Vector3>();
    [SerializeField] public HahaImporter hahaImporter;

    public int3[] faces;
    public int[] g2f;
    public float3[] offsets;
    public float4[] rotations;
    public float3[] scales;

    ComputeBuffer facebuffer;
    ComputeBuffer gaussianToFaceBuffer;
    ComputeBuffer haha_xyzBuffer;
    ComputeBuffer haha_scalingBuffer;
    ComputeBuffer haha_rotationBuffer;

    public bool NoPose => no_pose;
    public bool APose => a_pose;
    public event Action<PoseController> BeforeVertexBake;
    bool vertexBakeRequested;
    static readonly int[] SittingRequiredJoints = { 0, 1, 2, 4, 5, 7, 8, 10, 11, 13, 14, 16, 17, 18, 19 };
    bool isSittingPose;
    bool sittingPoseBakePending;
    Transform sittingPelvis;
    Vector3 sittingPelvisBaseLocalPosition;
    Vector3 sittingPelvisOffsetInMesh;
    float sittingPelvisLowering;
    float[] sittingPoseValues;
    string sittingPoseDetail = "Sitting pose has not been requested.";

    public bool IsSittingPose => isSittingPose;
    public bool CanToggleSittingPose => SittingRuntimeReady(out _);
    public string SittingPoseDetail => SittingRuntimeReady(out string waiting) ? sittingPoseDetail : waiting;

    bool SittingRuntimeReady(out string detail)
    {
        if (!isActiveAndEnabled || smplx == null || smr == null || bakedMesh == null ||
            !hasInitialTransforms || joints == null || joints.Length != customJointNames.Length ||
            vertexBuffer == null || !vertexBuffer.IsValid() || currentVertices == null || currentVertices.Length == 0)
        { detail = "Waiting for the avatar joints and first vertex bake."; return false; }
        foreach (int index in SittingRequiredJoints)
            if (joints[index] == null)
            { detail = "The sitting pose requires the complete pelvis, leg, and arm skeleton."; return false; }
        if (sittingPoseBakePending)
        { detail = "Waiting for the sitting/No Pose change to reach the next vertex bake."; return false; }
        detail = "";
        return true;
    }

    public bool TryApplySittingPose()
    {
        if (!SittingRuntimeReady(out string detail)) { sittingPoseDetail = detail; return false; }
        if (isSittingPose) return true;
        return ApplySittingPoseCore();
    }

    public bool TryRestoreNoPose()
    {
        if (!SittingRuntimeReady(out string detail)) { sittingPoseDetail = detail; return false; }
        ApplyCleanPose(); // The existing exact No Pose path, not the previous animation or A pose.
        sittingPoseDetail = "Restored the existing No Pose state.";
        sittingPoseBakePending = true;
        RequestImmediateVertexBake();
        return true;
    }

    bool ApplySittingPoseCore()
    {
        try
        {
            // Start from the same neutral body and joint positions as No Pose.
            // Neither this path nor the built-in pose overwrites serialized customPose.
            ApplyCleanPose();
            if (!TryMeasureVisibleFloor(out float standingFloor))
                throw new InvalidOperationException("The standing body has no finite baked geometry.");
            float[] pose = GenerateSittingPose();
            ApplyStableCustomPose(pose, false);
            if (!TryMeasureVisibleFloor(out float bentFloor))
                throw new InvalidOperationException("The seated body has no finite baked geometry.");
            float lowering = standingFloor - bentFloor;
            if (float.IsNaN(lowering) || float.IsInfinity(lowering) || lowering > 0.02f || lowering < -1.5f)
                throw new InvalidOperationException("The seated floor compensation is outside the supported range.");
            sittingPelvis = joints[0];
            sittingPelvisBaseLocalPosition = sittingPelvis.localPosition;
            // BakeMesh is mesh-local and Gaussian rendering reverses the baked X
            // reflection. A mesh-local bone translation therefore moves the visible
            // body; moving SMPLX's parent would cancel during the bake.
            sittingPelvisOffsetInMesh = transform.InverseTransformVector(Vector3.up * lowering);
            sittingPelvisLowering = lowering;
            sittingPoseValues = pose;
            isSittingPose = true;
            ApplySittingPelvisOffset();
            sittingPoseDetail = "Hardcoded sitting pose applied; pelvis lowered " + (-lowering).ToString("F3") +
                " m to preserve the original foot contact. Avatar root and custom pose data are unchanged.";
            sittingPoseBakePending = true;
            RequestImmediateVertexBake();
            return true;
        }
        catch (Exception error)
        {
            ApplyCleanPose();
            RequestImmediateVertexBake();
            sittingPoseDetail = "Sitting pose rejected; restored No Pose: " + error.Message;
            return false;
        }
    }

    float[] GenerateSittingPose()
    {
        float[] pose = new float[customJointNames.Length * 3];
        // This interface is Unity local Euler DEGREES, not SMPL axis-angle radians.
        // The actual male-model bind/template has downward thigh/shin vectors and
        // +Z toe vectors. -90 X sends thighs forward; +90 at knees returns shins down.
        SetCustomJointEuler(pose, "left_hip", new Vector3(-90f, 0, 0));
        SetCustomJointEuler(pose, "right_hip", new Vector3(-90f, 0, 0));
        SetCustomJointEuler(pose, "left_knee", new Vector3(90f, 0, 0));
        SetCustomJointEuler(pose, "right_knee", new Vector3(90f, 0, 0));
        // Left arm extends toward -X in Unity after the SMPL X conversion.
        // Mirrored Z/Y angles lower both arms and let the forearms rest forward.
        SetCustomJointEuler(pose, "left_collar", new Vector3(0, 0, 5f));
        SetCustomJointEuler(pose, "right_collar", new Vector3(0, 0, -5f));
        SetCustomJointEuler(pose, "left_shoulder", new Vector3(0, 0, 75f));
        SetCustomJointEuler(pose, "right_shoulder", new Vector3(0, 0, -75f));
        SetCustomJointEuler(pose, "left_elbow", new Vector3(0, 60f, 0));
        SetCustomJointEuler(pose, "right_elbow", new Vector3(0, -60f, 0));
        return pose;
    }

    bool TryMeasureVisibleFloor(out float floor)
    {
        floor = float.PositiveInfinity;
        if (smr == null || bakedMesh == null) return false;
        smr.BakeMesh(bakedMesh);
        bakedMesh.GetVertices(bakedVertexList);
        foreach (Vector3 vertex in bakedVertexList)
        {
            // This is the unreflected BakeMesh output, before UpdateVertexBuffer.
            Vector3 visible = transform.TransformPoint(vertex);
            if (float.IsNaN(visible.y) || float.IsInfinity(visible.y)) return false;
            floor = Mathf.Min(floor, visible.y);
        }
        return !float.IsInfinity(floor);
    }

    void ApplySittingPelvisOffset()
    {
        if (!isSittingPose || sittingPelvis == null || sittingPelvis.parent == null || smr == null) return;
        Vector3 sourceWorldOffset = smr.transform.TransformVector(sittingPelvisOffsetInMesh);
        Vector3 parentLocalOffset = sittingPelvis.parent.InverseTransformVector(sourceWorldOffset);
        sittingPelvis.localPosition = sittingPelvisBaseLocalPosition + parentLocalOffset;
    }

    void ClearSittingPoseState()
    {
        if (isSittingPose && sittingPelvis != null)
            sittingPelvis.localPosition = sittingPelvisBaseLocalPosition;
        if (isSittingPose) RequestImmediateVertexBake();
        isSittingPose = false;
        sittingPoseBakePending = false;
        sittingPelvis = null;
        sittingPelvisOffsetInMesh = Vector3.zero;
        sittingPoseValues = null;
    }

#if UNITY_EDITOR
    [Serializable] sealed class SittingJointSample
    {
        public string name;
        public Vector3 standing, sitting;
    }
    [Serializable] sealed class SittingValidationRecord
    {
        public bool passed;
        public string report, sourceModel;
        public float standingFloorY, sittingFloorY, pelvisLowering, sittingHipHeight, restoredVertexError, repeatedVertexError;
        public Vector3[] standingVertices, sittingVertices;
        public SittingJointSample[] joints;
    }

    // Uses a hidden clone of the scene's actual shaped SMPL-X mesh, without GPU
    // buffers or editing the source scene. The normal runtime core is shared.
    public static bool RunSittingPoseSmokeTest(out string report)
    {
        report = "Sitting pose validation did not complete.";
        var record = new SittingValidationRecord();
        GameObject root = null;
        PoseController fixture = null;
        SkinnedMeshRenderer renderer = null;
        Mesh copiedMesh = null, sourceMesh = null;
        try
        {
            PoseController source = null;
            GameObject avatar = GameObject.Find("Avatar");
            if (avatar != null) source = avatar.GetComponentInChildren<PoseController>(true);
            if (source == null || source.smplx == null)
                throw new InvalidOperationException("Open the actual avatar scene before sitting-pose validation.");
            if (source.hahaImporter == null || !source.hahaImporter.EnsureLoaded() || source.hahaImporter.data == null)
                throw new InvalidOperationException("The scene avatar shape data is unavailable.");
            record.sourceModel = source.smplx.name;
            root = new GameObject("SittingPoseValidation") { hideFlags = HideFlags.HideAndDontSave };
            GameObject model = Instantiate(source.smplx.gameObject, root.transform, false);
            renderer = model.GetComponentInChildren<SkinnedMeshRenderer>(true);
            if (renderer == null || renderer.sharedMesh == null)
                throw new InvalidOperationException("The real SMPL-X clone has no skinned mesh.");
            sourceMesh = renderer.sharedMesh;
            copiedMesh = Instantiate(sourceMesh);
            renderer.sharedMesh = copiedMesh;
            // SMPLX caches its renderer using an active-child lookup in Awake.
            // Initialize only this isolated model before disabling the fixture host.
            model.GetComponent<SMPLX>().Awake();
            root.SetActive(false);
            var gaussian = new GameObject("VisibleGaussianCoordinates");
            gaussian.transform.SetParent(root.transform, false);
            gaussian.transform.localPosition = source.transform.localPosition;
            gaussian.transform.localRotation = source.transform.localRotation;
            gaussian.transform.localScale = source.transform.localScale;
            fixture = gaussian.AddComponent<PoseController>();
            fixture.smplx = model.GetComponent<SMPLX>();
            fixture.smr = renderer;
            fixture.bakedMesh = new Mesh { name = "SittingValidationBake" };
            fixture.smplx.Awake();
            fixture.smplx.betas = (float[])source.hahaImporter.data.betas.Clone();
            fixture.smplx.ResetBodyPose();
            fixture.smplx.SetBetaShapes();
            fixture.CacheInitialTransforms();
            fixture.CacheAnimators();
            fixture.InitializeJoints();
            fixture.customPose = new float[fixture.customJointNames.Length * 3];
            fixture.customPose[3] = 12.25f;
            fixture.customPose[18] = -7.5f;
            float[] savedCustom = fixture.customPose;
            float[] savedCustomValues = (float[])savedCustom.Clone();
            fixture.ApplyCleanPose();
            Vector3[] standing = fixture.ValidationVisibleVertices();
            record.standingFloorY = ValidationFloor(standing);
            record.standingVertices = ValidationSample(standing);
            int[] jointIndices = { 0, 1, 2, 4, 5, 7, 8, 10, 11, 12, 15, 16, 17, 18, 19, 20, 21 };
            record.joints = new SittingJointSample[jointIndices.Length];
            for (int i = 0; i < jointIndices.Length; ++i)
                record.joints[i] = new SittingJointSample {
                    name = fixture.customJointNames[jointIndices[i]], standing = fixture.ValidationJoint(jointIndices[i]) };
            Pose rootBefore = new Pose(root.transform.position, root.transform.rotation);
            Vector3 rootScale = root.transform.localScale, gaussianBefore = gaussian.transform.localPosition;
            if (!fixture.ApplySittingPoseCore()) throw new InvalidOperationException(fixture.sittingPoseDetail);
            Vector3[] sitting = fixture.ValidationVisibleVertices();
            record.sittingVertices = ValidationSample(sitting);
            record.sittingFloorY = ValidationFloor(sitting);
            record.pelvisLowering = -fixture.sittingPelvisLowering;
            record.sittingHipHeight = (fixture.ValidationJoint(1).y + fixture.ValidationJoint(2).y) * 0.5f - record.sittingFloorY;
            for (int i = 0; i < jointIndices.Length; ++i) record.joints[i].sitting = fixture.ValidationJoint(jointIndices[i]);
            if (Mathf.Abs(record.sittingFloorY - record.standingFloorY) > 0.0002f ||
                record.pelvisLowering < 0.15f || record.pelvisLowering > 0.8f ||
                record.sittingHipHeight < 0.25f || record.sittingHipHeight > 0.8f)
                throw new InvalidOperationException("Seated floor or hip height is implausible: floor error=" +
                    (record.sittingFloorY - record.standingFloorY) + ", lowering=" + record.pelvisLowering + ", hip height=" + record.sittingHipHeight);
            foreach (int side in new[] { 0, 1 })
            {
                Vector3 hip = fixture.ValidationJoint(1 + side), knee = fixture.ValidationJoint(4 + side);
                Vector3 ankle = fixture.ValidationJoint(7 + side);
                Vector3 forward = gaussian.transform.forward;
                if (Vector3.Dot((knee - hip).normalized, forward) < 0.9f ||
                    Vector3.Dot((ankle - knee).normalized, Vector3.down) < 0.9f ||
                    Vector3.Angle(hip - knee, ankle - knee) < 70f || Vector3.Angle(hip - knee, ankle - knee) > 110f)
                    throw new InvalidOperationException("Actual-model thigh/shin direction or knee bend is invalid on side " + side);
                Vector3 shoulder = fixture.ValidationJoint(16 + side), elbow = fixture.ValidationJoint(18 + side);
                Vector3 wrist = fixture.ValidationJoint(20 + side);
                if (shoulder.y - wrist.y < 0.2f || Vector3.Dot(wrist - elbow, forward) <= 0f)
                    throw new InvalidOperationException("Actual-model arm direction is invalid on side " + side);
            }
            for (int i = 0; i < 3; ++i)
            {
                fixture.ApplyCustomPose(fixture.activeCustomPose);
                record.repeatedVertexError = Mathf.Max(record.repeatedVertexError,
                    ValidationMaxError(sitting, fixture.ValidationVisibleVertices()));
            }
            if (record.repeatedVertexError > 0.00005f)
                throw new InvalidOperationException("Repeated sitting pose accumulated a pelvis offset.");
            fixture.ApplyCleanPose();
            record.restoredVertexError = ValidationMaxError(standing, fixture.ValidationVisibleVertices());
            if (!fixture.NoPose || fixture.IsSittingPose || fixture.a_pose || fixture.using_custom ||
                record.restoredVertexError > 0.00005f)
                throw new InvalidOperationException("No Pose did not restore the exact original baked body: " + record.restoredVertexError);
            if (!ReferenceEquals(savedCustom, fixture.customPose)) throw new InvalidOperationException("Sitting replaced the user's custom pose array.");
            for (int i = 0; i < savedCustom.Length; ++i)
                if (savedCustom[i] != savedCustomValues[i]) throw new InvalidOperationException("Sitting modified the user's custom pose values.");
            if (root.transform.position != rootBefore.position || root.transform.rotation != rootBefore.rotation ||
                root.transform.localScale != rootScale || gaussian.transform.localPosition != gaussianBefore)
                throw new InvalidOperationException("Sitting moved the Avatar root or Gaussian object instead of only the pelvis.");
            record.passed = true;
            report = "Actual SMPL-X sitting passed: hips forward, knees bent, arms lowered, floor error=" +
                Mathf.Abs(record.sittingFloorY - record.standingFloorY).ToString("G5") +
                " m, pelvis lowering=" + record.pelvisLowering.ToString("F3") + " m, hip height=" + record.sittingHipHeight.ToString("F3") +
                " m, exact No Pose vertex error=" + record.restoredVertexError.ToString("G5") +
                " m; custom pose and root unchanged. Geometry: Logs/sitting-pose-validation.json";
            return true;
        }
        catch (Exception error)
        {
            report = "Sitting validation failed: " + error.Message;
            return false;
        }
        finally
        {
            record.report = report;
            Directory.CreateDirectory("Logs");
            File.WriteAllText("Logs/sitting-pose-validation.json", JsonUtility.ToJson(record, false));
            if (fixture != null)
            {
                fixture.ClearSittingPoseState();
                if (fixture.bakedMesh != null) DestroyImmediate(fixture.bakedMesh);
                fixture.bakedMesh = null;
            }
            if (renderer != null && renderer.sharedMesh != null && renderer.sharedMesh != sourceMesh && renderer.sharedMesh != copiedMesh)
                DestroyImmediate(renderer.sharedMesh);
            if (copiedMesh != null) DestroyImmediate(copiedMesh);
            if (root != null) DestroyImmediate(root);
        }
    }

    Vector3 ValidationJoint(int index) => QuestAvatarShoulderTouch.MapMeshWorldPointToGaussianWorld(
        smr.transform, transform, joints[index].position);
    Vector3[] ValidationVisibleVertices()
    {
        smr.BakeMesh(bakedMesh);
        Vector3[] vertices = bakedMesh.vertices;
        for (int i = 0; i < vertices.Length; ++i) vertices[i] = transform.TransformPoint(vertices[i]);
        return vertices;
    }
    static float ValidationFloor(Vector3[] vertices)
    {
        float lowest = float.PositiveInfinity;
        foreach (Vector3 vertex in vertices) lowest = Mathf.Min(lowest, vertex.y);
        return lowest;
    }
    static Vector3[] ValidationSample(Vector3[] vertices)
    {
        var sampled = new Vector3[(vertices.Length + 2) / 3];
        for (int i = 0; i < sampled.Length; ++i) sampled[i] = vertices[i * 3];
        return sampled;
    }
    static float ValidationMaxError(Vector3[] a, Vector3[] b)
    {
        if (a.Length != b.Length) return float.PositiveInfinity;
        float maximum = 0;
        for (int i = 0; i < a.Length; ++i) maximum = Mathf.Max(maximum, Vector3.Distance(a[i], b[i]));
        return maximum;
    }
#endif
    void Reset()
    {
        AutoAssignReferences();
    }

    void OnValidate()
    {
        AutoAssignReferences();
    }

    void AutoAssignReferences()
    {
        if (hahaImporter == null)
        {
            hahaImporter = GetComponent<HahaImporter>();
        }

        if (smplx == null)
        {
            Transform root = transform.parent != null ? transform.parent : transform;
            smplx = root.GetComponentInChildren<SMPLX>();
        }
    }

    void Awake()
    {
        AutoAssignReferences();

        if (hahaImporter == null || !hahaImporter.EnsureLoaded())
        {
            Debug.LogError("HahaImporter is not assigned or failed to load avatar data.");
            return;
        }

        smplx.Awake();
        if (using_custom)
        {
            CacheAnimators();
            DisableAnimatorsForCustomPose();
        }

        // Debug.Log(hahaImporter.data.betas);
        

        for (int i = 0; i < SMPLX.NUM_BETAS; i++)
        {
            smplx.betas[i] = hahaImporter.data.betas[i];
        }
        smplx.ResetBodyPose();
        smplx.SetBetaShapes();

        Debug.Log("ResetTPose");


    }

    void Start()
    {
        
        // Validate the SMPL-X reference
        if (smplx == null)
        {
            Debug.LogError("SMPLX object not assigned!");
            return;
        }

        // // Validate the shader material reference
        // if (smplxMaterial == null)
        // {
        //     Debug.LogError("Material not assigned!");
        //     return;
        // }

        // Get the SkinnedMeshRenderer component
        smr = smplx.GetComponentInChildren<SkinnedMeshRenderer>();
        CacheAnimators();
        if (smr == null)
        {
            Debug.LogError("SkinnedMeshRenderer not found on SMPLX object!");
            return;
        }
        bakedMesh = new Mesh { name = "PoseControllerBakedMesh" };
        CacheInitialTransforms();
        InitializeAnimator();

        // Initialize the joints array from the SMPL-X hierarchy
        InitializeJoints();
        // UpdateSMPLXBetas(hahaImporter.data.betas);
        // Initialize the GPU vertex buffer
        InitializeVertexBuffer();

        previousNoPose = no_pose;
        previousAPose = a_pose;
        previousUsingCustom = using_custom;
        previousVisualableMesh = visualable_mesh;
        ApplyMeshVisibility();

        if (no_pose)
        {
            a_pose = false;
            previousAPose = false;
            using_custom = false;
            previousUsingCustom = false;
            ApplyNoPoseRuntime();
        }
        else if (a_pose)
        {
            using_custom = false;
            previousUsingCustom = false;
            ApplyAPoseRuntime();
        }
        else
        {
            ApplyPoseDriverFromInspector();
        }

        // smplx.SetBodyPose(SMPLX.BodyPose.T);
        UpdateVertexBuffer();
        // GetBuffers();
    }

    void GetBuffers()
    {
        facebuffer = hahaImporter.GetFaceBuffer();
        gaussianToFaceBuffer = hahaImporter.GetGaussianToFaceBuffer();
        haha_xyzBuffer = hahaImporter.GetHahaXyzBuffer();
        haha_scalingBuffer = hahaImporter.GetHahaScalingBuffer();
        haha_rotationBuffer = hahaImporter.GetHahaRotationBuffer();

        faces = new int3[facebuffer.count];
        g2f = new int[gaussianToFaceBuffer.count];
        offsets = new float3[haha_xyzBuffer.count];
        rotations = new float4[haha_scalingBuffer.count];
        scales = new float3[haha_rotationBuffer.count];

        facebuffer.GetData(faces);
        gaussianToFaceBuffer.GetData(g2f);
        haha_xyzBuffer.GetData(offsets);
        haha_rotationBuffer.GetData(rotations);
        haha_scalingBuffer.GetData(scales);
    }

    void Update()
    {
        if (visualable_mesh != previousVisualableMesh)
        {
            previousVisualableMesh = visualable_mesh;
            ApplyMeshVisibility();
        }

        if (no_pose != previousNoPose)
        {
            previousNoPose = no_pose;
            if (no_pose)
            {
                a_pose = false;
                previousAPose = false;
                using_custom = false;
                previousUsingCustom = false;
                ApplyNoPoseRuntime();
            }
            else
            {
                ApplyPoseDriverFromInspector();
            }
            return;
        }

        if (a_pose != previousAPose)
        {
            previousAPose = a_pose;
            if (a_pose)
            {
                no_pose = false;
                previousNoPose = false;
                using_custom = false;
                previousUsingCustom = false;
                ApplyAPoseRuntime();
            }
            else
            {
                activeCustomPose = null;
                ApplyPoseDriverFromInspector();
            }
            return;
        }

        if (no_pose)
        {
            return;
        }

        if (a_pose)
        {
            if (using_custom)
            {
                using_custom = false;
                previousUsingCustom = false;
            }
            return;
        }

        if (using_custom == previousUsingCustom)
        {
            return;
        }

        ApplyPoseDriverFromInspector();
    }

    void LateUpdate()
    {
        if (!no_pose && a_pose)
        {
            DisableAnimatorsForCustomPose();

            if (activeCustomPose == null)
            {
                activeCustomPose = GenerateAPose();
            }

            ApplyCustomPose(activeCustomPose);
        }
        else if (!no_pose && using_custom)
        {
            DisableAnimatorsForCustomPose();

            if (activeCustomPose == null)
            {
                activeCustomPose = GetCustomPose();
            }

            ApplyCustomPose(activeCustomPose);
        }

        if (ShouldBakeVerticesThisFrame())
        {
            BeforeVertexBake?.Invoke(this);
            UpdateVertexBuffer();
            vertexBakeRequested = false;
            sittingPoseBakePending = false;
        }
    }

    bool ShouldBakeVerticesThisFrame()
    {
        if (vertexBakeRequested)
            return true;

#if UNITY_ANDROID && !UNITY_EDITOR
        if (currentVertices == null || currentVertices.Length == 0)
            return true;

        int interval = Mathf.Max(1, questVertexBakeInterval);
        return Time.frameCount != lastVertexBakeFrame && Time.frameCount % interval == 0;
#else
        return true;
#endif
    }

    public void RequestImmediateBake()
    {
        vertexBakeRequested = true;
    }

    public void RequestImmediateVertexBake()
    {
        RequestImmediateBake();
    }
    void InitializeJoints()
    {
        Transform[] childTransforms = smplx.GetComponentsInChildren<Transform>(true);
        Dictionary<string, Transform> transformFromName = new Dictionary<string, Transform>();
        foreach (Transform childTransform in childTransforms)
        {
            transformFromName[childTransform.name] = childTransform;
        }

        joints = new Transform[customJointNames.Length];
        for (int i = 0; i < customJointNames.Length; i++)
        {
            if (!transformFromName.TryGetValue(customJointNames[i], out joints[i]))
            {
                Debug.LogError($"SMPL-X joint not found: {customJointNames[i]}");
            }
        }
    }

    bool EnsurePoseRuntimeReady()
    {
        AutoAssignReferences();
        if (smplx == null)
        {
            Debug.LogError("SMPLX object not assigned!");
            return false;
        }

        if (smr == null)
        {
            smr = smplx.GetComponentInChildren<SkinnedMeshRenderer>();
        }

        if (smr == null)
        {
            Debug.LogError("SkinnedMeshRenderer not found on SMPLX object!");
            return false;
        }

        if (!hasInitialTransforms)
        {
            CacheInitialTransforms();
        }

        if (smplxAnimators == null)
        {
            CacheAnimators();
        }

        if (joints == null || joints.Length != customJointNames.Length)
        {
            InitializeJoints();
        }

        return joints != null;
    }

    void UpdateSMPLXBetas(float[] betas)
    {
        if (betas.Length != SMPLX.NUM_BETAS)
        {
            Debug.LogError($"Invalid betas length! Expected  {SMPLX.NUM_BETAS} values.");
            return;
        }

        // Update the betas array in the SMPLX class
        for (int i = 0; i < SMPLX.NUM_BETAS; i++)
        {
            smplx.betas[i] = betas[i];
        }

        // Apply the updated betas to the SMPLX model
        smplx.SetBetaShapes();
        Debug.Log("Updated and applied new betas to SMPLX model.");
    }
    void InitializeVertexBuffer()
    {
        // Create the GPU buffer for vertices
        Mesh mesh = smr.sharedMesh;
        if (mesh == null)
        {
            Debug.LogError("Shared mesh not found in SkinnedMeshRenderer!");
            return;
        }

        vertexBuffer = new ComputeBuffer(mesh.vertexCount, sizeof(float) * 3);

        // Assign the buffer to the material
        // smplxMaterial.SetBuffer("_VertexBuffer", vertexBuffer);
        Debug.Log("Initialized GPU buffer for vertex positions and assigned it to the material.");
    }

    System.Collections.IEnumerator AnimatePose()
    {
        while (true)
        {
            activeCustomPose = GetCustomPose();

            isTPose = !isTPose; // Toggle pose state
            float delay = poseSwitchTime > 0.0f ? poseSwitchTime : 1.0f;
            yield return new WaitForSeconds(delay);
        }
    }

    void ApplyTPose()
    {
        foreach (var joint in joints)
        {
            if (joint == null)
            {
                continue;
            }

            joint.localRotation = Quaternion.identity; // Reset to default (T-pose)
        }
        Debug.Log("Applied T-pose to SMPL-X.");
    }

    float[] GetCustomPose()
    {
        if (customPose != null && customPose.Length == joints.Length * 3)
        {
            return customPose;
        }

        return GenerateCustomPose(isTPose);
    }

    float[] GenerateCustomPose(bool tPose)
    {
        float[] pose = new float[joints.Length * 3];
        if (tPose)
        {
            return pose;
        }

        SetCustomJointEuler(pose, "left_collar", new Vector3(0.0f, 0.0f, 10.0f));
        SetCustomJointEuler(pose, "left_shoulder", new Vector3(0.0f, 0.0f, 35.0f));
        SetCustomJointEuler(pose, "right_collar", new Vector3(0.0f, 0.0f, -10.0f));
        SetCustomJointEuler(pose, "right_shoulder", new Vector3(0.0f, 0.0f, -35.0f));
        return pose;
    }

    float[] GenerateAPose()
    {
        float[] pose = new float[joints.Length * 3];
        SetCustomJointEuler(pose, "left_collar", new Vector3(0.0f, 0.0f, 12.0f));
        SetCustomJointEuler(pose, "left_shoulder", new Vector3(0.0f, 0.0f, 55.0f));
        SetCustomJointEuler(pose, "left_elbow", new Vector3(0.0f, 0.0f, 4.0f));
        SetCustomJointEuler(pose, "right_collar", new Vector3(0.0f, 0.0f, -12.0f));
        SetCustomJointEuler(pose, "right_shoulder", new Vector3(0.0f, 0.0f, -55.0f));
        SetCustomJointEuler(pose, "right_elbow", new Vector3(0.0f, 0.0f, -4.0f));
        return pose;
    }

    void SetCustomJointEuler(float[] pose, string jointName, Vector3 eulerAngles)
    {
        for (int i = 0; i < joints.Length; i++)
        {
            if (joints[i] == null)
            {
                continue;
            }

            if (joints[i].name != jointName)
            {
                continue;
            }

            pose[i * 3 + 0] = eulerAngles.x;
            pose[i * 3 + 1] = eulerAngles.y;
            pose[i * 3 + 2] = eulerAngles.z;
            return;
        }
    }

    public void SetCustomPose(float[] pose)
    {
        ClearSittingPoseState();
        customPose = pose;
        activeCustomPose = pose;
    }

    public void ApplyCleanPose()
    {
        if (!EnsurePoseRuntimeReady())
            return;

        no_pose = true;
        previousNoPose = true;
        a_pose = false;
        previousAPose = false;
        using_custom = false;
        previousUsingCustom = false;
        ApplyNoPoseRuntime();
        Debug.Log("Applied no-pose clean state.");
    }

    public void ApplyTAPose()
    {
        if (!EnsurePoseRuntimeReady())
            return;

        no_pose = false;
        previousNoPose = false;
        a_pose = false;
        previousAPose = false;
        float[] pose = customPose != null && customPose.Length == joints.Length * 3
            ? customPose
            : GenerateCustomPose(false);

        ApplyStableCustomPose(pose);
        Debug.Log("Applied T-A pose from PoseController custom state.");
    }

    public void ApplyAPose()
    {
        if (!EnsurePoseRuntimeReady())
            return;

        no_pose = false;
        previousNoPose = false;
        a_pose = true;
        previousAPose = true;
        using_custom = false;
        previousUsingCustom = false;
        ApplyAPoseRuntime();
        Debug.Log("Applied A-pose from PoseController.");
    }

    public void ApplyPose1()
    {
        if (!EnsurePoseRuntimeReady())
            return;

        no_pose = false;
        previousNoPose = false;
        a_pose = false;
        previousAPose = false;
        StopCustomPoseCoroutine();
        using_custom = false;
        previousUsingCustom = false;
        activeCustomPose = null;
        SetAnimatorDrivenMode(true);

        if (smplxAnimators != null)
        {
            for (int i = 0; i < smplxAnimators.Length; ++i)
            {
                Animator animator = smplxAnimators[i];
                if (animator != null && animator.enabled)
                    animator.Update(0.0f);
            }
        }

        Debug.Log("Applied pose 1 from Animator.");
    }

    void ApplyStableCustomPose(float[] pose, bool storeCustomPose = true)
    {
        no_pose = false;
        previousNoPose = false;
        a_pose = false;
        previousAPose = false;
        StopCustomPoseCoroutine();
        using_custom = true;
        previousUsingCustom = true;
        if (storeCustomPose) customPose = pose;
        activeCustomPose = pose;
        SetAnimatorDrivenMode(false);
        DisableAnimatorsForCustomPose();
        ApplyCustomPose(activeCustomPose);
    }

    void ApplyAPoseRuntime()
    {
        StopCustomPoseCoroutine();
        activeCustomPose = GenerateAPose();
        SetAnimatorDrivenMode(false);
        DisableAnimatorsForCustomPose();
        ApplyCustomPose(activeCustomPose);
    }

    void ApplyPoseDriverFromInspector()
    {
        StopCustomPoseCoroutine();
        previousUsingCustom = using_custom;
        SetAnimatorDrivenMode(!using_custom);

        if (using_custom)
        {
            activeCustomPose = GetCustomPose();
            ApplyCustomPose(activeCustomPose);
            customPoseCoroutine = StartCoroutine(AnimatePose());
        }
        else
        {
            activeCustomPose = null;
        }
    }

    void ApplyNoPoseRuntime()
    {
        StopCustomPoseCoroutine();
        activeCustomPose = null;
        isTPose = true;
        SetAnimatorDrivenMode(false);
        ApplyTPose();
        smplx.ResetBodyPose();
        smplx.UpdatePoseCorrectives();
        smplx.UpdateJointPositions(false);
    }

    float[] GeneratePose1()
    {
        float[] pose = new float[joints.Length * 3];
        SetCustomJointEuler(pose, "left_collar", new Vector3(0.0f, 0.0f, -60.0f));
        SetCustomJointEuler(pose, "left_shoulder", new Vector3(0.0f, 0.0f, 35.0f));
        SetCustomJointEuler(pose, "right_collar", new Vector3(0.0f, 0.0f, 60.0f));
        SetCustomJointEuler(pose, "right_shoulder", new Vector3(0.0f, 0.0f, -35.0f));
        SetCustomJointEuler(pose, "right_hip", new Vector3(0.0f, 30.0f, 0.0f));
        SetCustomJointEuler(pose, "left_hip", new Vector3(0.0f, -30.0f, 0.0f));
        SetCustomJointEuler(pose, "left_knee", new Vector3(30.0f, 30.0f, 0.0f));
        return pose;
    }

    void StopCustomPoseCoroutine()
    {
        if (customPoseCoroutine == null)
            return;

        StopCoroutine(customPoseCoroutine);
        customPoseCoroutine = null;
    }

    public void ApplyCustomPose(float[] customPose)
    {
        if (customPose == null || customPose.Length != joints.Length * 3)
        {
            Debug.LogError($"Invalid custom pose array! Expected {joints.Length * 3} values.");
            return;
        }

        if (isSittingPose && !ReferenceEquals(customPose, sittingPoseValues)) ClearSittingPoseState();
        for (int i = 0; i < joints.Length; i++)
        {
            if (joints[i] == null)
            {
                continue;
            }

            // Extract rotation for each joint
            Vector3 rotation = new Vector3(
                customPose[i * 3 + 0], // X rotation
                customPose[i * 3 + 1], // Y rotation
                customPose[i * 3 + 2]  // Z rotation
            );

            joints[i].localEulerAngles = rotation; // Apply rotation
        }
        smplx.UpdatePoseCorrectives();
        smplx.UpdateJointPositions(false);
        // SMPLX may lock the pelvis height during UpdateJointPositions; apply the
        // seated compensation afterwards, on every LateUpdate, before vertex bake.
        ApplySittingPelvisOffset();
    }

    void CacheAnimators()
    {
        smplxAnimators = smplx != null ? smplx.GetComponentsInChildren<Animator>(true) : null;
    }

    void SetAnimatorDrivenMode(bool animatorDriven)
    {
        ClearSittingPoseState();
        if (smplxAnimators != null)
        {
            for (int i = 0; i < smplxAnimators.Length; ++i)
            {
                Animator animator = smplxAnimators[i];
                if (animator == null)
                {
                    continue;
                }

                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.applyRootMotion = false;
                animator.speed = animatorDriven ? initialAnimatorSpeed : 0.0f;
                animator.enabled = animatorDriven;
            }
        }

        RestoreInitialTransforms();
    }

    void DisableAnimatorsForCustomPose()
    {
        if (smplxAnimators == null)
        {
            return;
        }

        for (int i = 0; i < smplxAnimators.Length; ++i)
        {
            Animator animator = smplxAnimators[i];
            if (animator == null)
            {
                continue;
            }

            animator.speed = 0.0f;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.enabled = false;
        }
    }

    void ApplyMeshVisibility()
    {
        if (smr == null)
        {
            return;
        }

        smr.enabled = true;
        smr.updateWhenOffscreen = true;
        smr.forceRenderingOff = !visualable_mesh;
    }

    void CacheInitialTransforms()
    {
        if (smplx == null || smr == null)
        {
            return;
        }

        Transform smplxTransform = smplx.transform;
        initialSmplxLocalPosition = smplxTransform.localPosition;
        initialSmplxLocalRotation = smplxTransform.localRotation;
        initialSmplxLocalScale = smplxTransform.localScale;

        Transform meshTransform = smr.transform;
        initialMeshLocalPosition = meshTransform.localPosition;
        initialMeshLocalRotation = meshTransform.localRotation;
        initialMeshLocalScale = meshTransform.localScale;
        hasInitialTransforms = true;
    }

    void RestoreInitialTransforms()
    {
        if (!hasInitialTransforms || smplx == null || smr == null)
        {
            return;
        }

        Transform smplxTransform = smplx.transform;
        smplxTransform.localPosition = initialSmplxLocalPosition;
        smplxTransform.localRotation = initialSmplxLocalRotation;
        smplxTransform.localScale = initialSmplxLocalScale;

        Transform meshTransform = smr.transform;
        meshTransform.localPosition = initialMeshLocalPosition;
        meshTransform.localRotation = initialMeshLocalRotation;
        meshTransform.localScale = initialMeshLocalScale;
    }

    void InitializeAnimator()
    {
        if (smplxAnimators == null || smplxAnimators.Length == 0 || smplxAnimators[0] == null)
        {
            return;
        }

        Animator primaryAnimator = smplxAnimators[0];
        bool wasEnabled = primaryAnimator.enabled;
        initialAnimatorSpeed = primaryAnimator.speed;

        primaryAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        primaryAnimator.applyRootMotion = false;
        primaryAnimator.enabled = true;
        primaryAnimator.Update(0.0f);
        primaryAnimator.speed = wasEnabled ? initialAnimatorSpeed : 0.0f;
        primaryAnimator.enabled = !using_custom && wasEnabled;
        RestoreInitialTransforms();
    }

    // float[] GenerateRandomPose()
    // {
    //     float[] randomPose = new float[joints.Length * 3];

    //     // Generate random rotations for all joints
    //     for (int i = 0; i < joints.Length; i++)
    //     {
    //         randomPose[i * 3 + 0] = Random.Range(-30f, 30f); // X rotation
    //         randomPose[i * 3 + 1] = Random.Range(-30f, 30f); // Y rotation
    //         randomPose[i * 3 + 2] = Random.Range(-30f, 30f); // Z rotation
    //     }

    //     Debug.Log($"Generated pose length: {randomPose.Length}");
    //     return randomPose;
    // }
    // [System.Serializable]
    // public class FaceIndexData
    // {
    //     public int FaceIndex;
    //     public int[] VertexIndices;
    // }

    // // Class to hold the list of face data for JSON serialization
    // [System.Serializable]
    // public class FaceIndexDataList
    // {
    //     public List<FaceIndexData> Faces;
    // }
    void UpdateVertexBuffer()
    {
        if (smr == null)
        {
            Debug.LogError("SkinnedMeshRenderer not initialized!");
            return;
        }

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        long bakeStarted = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        smr.updateWhenOffscreen = true;
        smr.BakeMesh(bakedMesh);


        bakedMesh.GetVertices(bakedVertexList);
        if (currentVertices == null || currentVertices.Length != bakedVertexList.Count)
            currentVertices = new Vector3[bakedVertexList.Count];

        for (int i = 0; i < bakedVertexList.Count; i++)
        {
            // vertices[i] += smr.gameObject.transform.position; 
            // vertices[i] += debug.position; 
            Vector3 vertex = bakedVertexList[i];
            vertex.x = -vertex.x;
            currentVertices[i] = vertex;
        }
        lastVertexBakeFrame = Time.frameCount;
            // Specify the file path
        // string filePath = Application.dataPath + "/UnityVertices.txt";

        // // Write vertices to the file
        // using (StreamWriter writer = new StreamWriter(filePath))
        // {
        //     foreach (Vector3 vertex in vertices)
        //     {
        //         writer.WriteLine($"{vertex.x} {vertex.y} {vertex.z}");
        //     }
        // }

        // Debug.Log($"Vertices saved to {filePath}");

        
        // Upload the vertex positions to the GPU buffer
        if (vertexBuffer != null)
        {
            vertexBuffer.SetData(currentVertices);
            // Debug.Log("Uploaded new vertex positions to GPU.");
        }
        else
        {
            Debug.LogError("Vertex buffer is not initialized!");
        }
#if DEVELOPMENT_BUILD || UNITY_EDITOR
        QuestBakeTicks += System.Diagnostics.Stopwatch.GetTimestamp() - bakeStarted;
        ++QuestBakeCount;
#endif



        // // Get the triangles (face indices)
        // int[] triangles = bakedMesh.triangles;

        // // Create a list to store face-to-vertex index mappings
        // List<FaceIndexData> faceToVertexIndices = new List<FaceIndexData>();

        // // Iterate through the triangles (each face has 3 indices)
        // for (int i = 0; i < triangles.Length; i += 3)
        // {
        //     // Get the indices of the vertices for this face
        //     int v0 = triangles[i];
        //     int v1 = triangles[i + 1];
        //     int v2 = triangles[i + 2];

        //     // Add the face index and its vertex indices to the list
        //     faceToVertexIndices.Add(new FaceIndexData
        //     {
        //         FaceIndex = i / 3,
        //         VertexIndices = new int[] { v0, v1, v2 }
        //     });
        // }

        // // Convert to JSON and save to file
        // string json = JsonUtility.ToJson(new FaceIndexDataList { Faces = faceToVertexIndices }, true);
        // string path = Path.Combine(Application.dataPath, "FaceToVertexIndices.json");
        // File.WriteAllText(path, json);

        // Debug.Log($"Face-to-vertex indices mapping saved to {path}");
    }

    void OnDestroy()
    {
        ClearSittingPoseState();
        // Clean up GPU resources
        if (vertexBuffer != null)
        {
            vertexBuffer.Dispose();
            vertexBuffer = null;
        }
        if (facebuffer != null)
        {
            facebuffer.Dispose();
            facebuffer = null;
        }
        if (gaussianToFaceBuffer != null)
        {
            gaussianToFaceBuffer.Dispose();
            gaussianToFaceBuffer = null;
        }
        if (haha_xyzBuffer != null)
        {
            haha_xyzBuffer.Dispose();
            haha_xyzBuffer = null;
        }
        if (haha_scalingBuffer != null)
        {
            haha_scalingBuffer.Dispose();
            haha_scalingBuffer = null;
        }
        if (haha_rotationBuffer != null)
        {
            haha_rotationBuffer.Dispose();
            haha_rotationBuffer = null;
        }
        if (bakedMesh != null)
        {
            Destroy(bakedMesh);
            bakedMesh = null;
        }

    }

    public ComputeBuffer GetVertexBuffer()
    {
        return vertexBuffer;
    }

    public Vector3[] GetCurrentVertices()
    {
        return currentVertices;
    }
}
