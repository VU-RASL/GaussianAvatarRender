using UnityEngine;
using System;
using System.Collections.Generic;
using System.IO;
using Unity.Mathematics;

[DefaultExecutionOrder(50)]
// [ExecuteInEditMode]
public class PoseController : MonoBehaviour
{   
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
        Transform[] childTransforms = smplx.GetComponentsInChildren<Transform>();
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

    void ApplyStableCustomPose(float[] pose)
    {
        no_pose = false;
        previousNoPose = false;
        a_pose = false;
        previousAPose = false;
        StopCustomPoseCoroutine();
        using_custom = true;
        previousUsingCustom = true;
        customPose = pose;
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
    }

    void CacheAnimators()
    {
        smplxAnimators = smplx != null ? smplx.GetComponentsInChildren<Animator>(true) : null;
    }

    void SetAnimatorDrivenMode(bool animatorDriven)
    {
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
