using UnityEngine;
using GaussianSplatting.Runtime;
using System;
using Unity.Mathematics;
using System.IO;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Profiling;
using Stopwatch = System.Diagnostics.Stopwatch;

[DefaultExecutionOrder(100)]
public class TestShaderWithBuffer : MonoBehaviour
{
    public Transform debug;

    public ComputeShader testShader;
    public HahaImporter hahaImporter;

    public PoseController poseController;
    [SerializeField] private GaussianSplatRenderer gaussianRenderer;
    [SerializeField] private bool forceCpuSplatUpdate;
    [SerializeField, Range(1, 4)] private int questCpuUpdateInterval = 2;
    [SerializeField] private bool useOptimizedQuestCpuUpdate = true;

    public bool UseOptimizedQuestCpuUpdate
    {
        get => useOptimizedQuestCpuUpdate;
        set => useOptimizedQuestCpuUpdate = value;
    }

    public static long QuestComputeTicks;
    public static long QuestUploadTicks;
    public static int QuestComputeCount;
    public static int QuestUploadCount;

    static readonly ProfilerMarker GaussianUpdateMarker = new ProfilerMarker("GSAC.GaussianUpdate");
    static readonly ProfilerMarker GaussianComputeMarker = new ProfilerMarker("GSAC.GaussianCompute");
    static readonly ProfilerMarker GaussianUploadMarker = new ProfilerMarker("GSAC.GaussianUpload");
    
    // GPU Buffers
    private ComputeBuffer gaussianToFaceBuffer;
    private ComputeBuffer haha_xyzBuffer;
    private ComputeBuffer haha_rotationBuffer;
    private ComputeBuffer haha_scalingBuffer;
    private ComputeBuffer vertexBuffer;
    private ComputeBuffer faceBuffer;
    public ComputeBuffer TBuffer;
    private ComputeBuffer RBuffer;
    private ComputeBuffer kBuffer;
    private int calcFacesKernelHandle;
    private int mapGaussiansKernelHandle;
    private bool isInitialized = false;
    private int[] cpuGaussianToFace;
    private int3[] cpuFaces;
    private float3[] cpuOffsets;
    private float4[] cpuRotations;
    private uint[] cpuPositionData;
    private Vector3[] cpuSortPositions;
    private uint[] cpuOtherData;
    private int cpuOtherStrideWords;
    private GraphicsBuffer questCpuPositionBuffer;
    private int lastQuestCpuUpdateFrame = -1;
    private NativeArray<int> nativeGaussianToFace;
    private NativeArray<int3> nativeFaces;
    private NativeArray<float3> nativeOffsets;
    private NativeArray<float4> nativeRotations;
    private NativeArray<Vector3> nativeVertices;
    private NativeArray<uint> nativePositionData;
    private NativeArray<Vector3> nativeSortPositions;
    private NativeArray<uint> nativeOtherData;
    private bool lastUpdateUsedOptimizedPath;
    private bool optimizedFailureLogged;

    bool UseQuestCpuSplatUpdate
    {
        get
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return true;
#else
            return forceCpuSplatUpdate;
#endif
        }
    }

    bool UseDirectQuestPositionBuffer
    {
        get
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return true;
#else
            return false;
#endif
        }
    }

    public struct GaussianData
    {
        public Vector4 rotation;  // Quaternion rotation (x, y, z, w)
        public Vector3 scaling;   // Scaling vector (x, y, z)
        public float shIndex;
    }

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

        if (poseController == null)
        {
            poseController = GetComponent<PoseController>();
        }

        if (gaussianRenderer == null)
        {
            gaussianRenderer = GetComponent<GaussianSplatRenderer>();
        }
    }

    void Start()
    {
        AutoAssignReferences();

        if (hahaImporter == null || poseController == null)
        {
            Debug.LogError("HahaImporter or PoseController not assigned! Drag them in the Inspector.");
            return;
        }

        if (gaussianRenderer == null)
        {
            Debug.LogError("GaussianSplatRenderer not assigned!");
            return;
        }

        Invoke(nameof(InitializeBuffers), 0.1f);
    }

    void InitializeBuffers()
    {
        if (gaussianRenderer.asset == null)
        {
            Invoke(nameof(InitializeBuffers), 0.1f);
            return;
        }
        if (gaussianRenderer.asset.posFormat != GaussianSplatAsset.VectorFormat.Float32)
        {
            Debug.LogError("Quest runtime Gaussian animation currently requires Float32/VeryHigh position data to avoid CPU readbacks.");
            enabled = false;
            return;
        }
        if (gaussianRenderer.m_GpuPosData == null || gaussianRenderer.m_GpuOtherData == null)
        {
            Invoke(nameof(InitializeBuffers), 0.1f);
            return;
        }
        if (UseQuestCpuSplatUpdate)
        {
            int questGaussianCount = gaussianRenderer.asset.splatCount;
            if (!InitializeQuestCpuUpdateData(questGaussianCount))
                return;
            isInitialized = true;
            ExecuteQuestCpuUpdate();
            return;
        }

        gaussianToFaceBuffer = hahaImporter.GetGaussianToFaceBuffer();
        haha_xyzBuffer = hahaImporter.GetHahaXyzBuffer();
        haha_rotationBuffer = hahaImporter.GetHahaRotationBuffer();
        // haha_scalingBuffer = hahaImporter.GetHahaScalingBuffer();
        float3[] haha_scaling = hahaImporter.GetHAHAScaling();
        faceBuffer = hahaImporter.GetFaceBuffer();
        vertexBuffer = poseController.GetVertexBuffer();

        if (gaussianToFaceBuffer == null || haha_xyzBuffer == null || faceBuffer == null || vertexBuffer == null)
        {
            Debug.LogError("One or more buffers are null! Ensure HahaImporter and PoseController are initialized.");
            return;
        }

        int faceCount = faceBuffer.count;
        int gaussianCount = gaussianToFaceBuffer.count;

        haha_scalingBuffer = new ComputeBuffer(gaussianCount, sizeof(float) * 3);
        haha_scalingBuffer.SetData(haha_scaling);
        // haha_scalingBuffer.GetData()
        // SaveHahaScalingToTxt();
        TBuffer = new ComputeBuffer(gaussianCount, sizeof(float) * 3);
        RBuffer = new ComputeBuffer(gaussianCount, sizeof(float) * 4);
        kBuffer = new ComputeBuffer(gaussianCount, sizeof(float));
        isInitialized = true;
        // SaveVertexBufferAsTxt();
        InitializeComputeShader();
    
    }

#if UNITY_EDITOR
void SaveHahaScalingToTxt()
{
    if (haha_scalingBuffer == null)
    {
        Debug.LogError("HahaScalingBuffer is null! Ensure it is initialized.");
        return;
    }

    string filePath = "Assets/GaussianAssets/HahaScaling.txt";

    try
    {
        int count = haha_scalingBuffer.count;
        float3[] data = new float3[count];
        haha_scalingBuffer.GetData(data); // Fetch actual buffer data

        using (StreamWriter writer = new StreamWriter(filePath))
        {
            writer.WriteLine("HahaScaling (Scaling Vectors):");
            foreach (var item in data)
            {
                writer.WriteLine($"{item.x} {item.y} {item.z}"); // Write x, y, z for each scaling vector
            }
        }

        Debug.Log($"HahaScalingBuffer saved to: {filePath}");
    }
    catch (Exception ex)
    {
        Debug.LogError($"Error saving HahaScalingBuffer: {ex.Message}");
    }
}


    void SaveVertexBufferAsTxt()
    {
        if (vertexBuffer == null)
        {
            Debug.LogError("VertexBuffer is null! Ensure the PoseController is initialized and the VertexBuffer is set.");
            return;
        }

        string vertexFilePath = "Assets/GaussianAssets/VertexBuffer.txt";

        try
        {
            // Read vertex data from the buffer
            int vertexCount = vertexBuffer.count;
            Vector3[] vertices = new Vector3[vertexCount];
            vertexBuffer.GetData(vertices);

            // Write the vertices to the file
            using (StreamWriter writer = new StreamWriter(vertexFilePath))
            {
                foreach (var vertex in vertices)
                {
                    writer.WriteLine($"{vertex.x} {vertex.y} {vertex.z}");
                }
            }

            Debug.Log($"VertexBuffer saved to: {vertexFilePath}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error saving VertexBuffer: {ex.Message}");
        }
    }
#endif

    void InitializeComputeShader()
    {
        if (!isInitialized) return;

        calcFacesKernelHandle = testShader.FindKernel("CalcFacesTransform");
        mapGaussiansKernelHandle = testShader.FindKernel("MapGaussiansToFaces");

        testShader.SetBuffer(calcFacesKernelHandle, "FaceBuffer", faceBuffer);
        testShader.SetBuffer(calcFacesKernelHandle, "VertexBuffer", vertexBuffer);
        testShader.SetBuffer(calcFacesKernelHandle, "TBuffer", TBuffer);
        testShader.SetBuffer(calcFacesKernelHandle, "RBuffer", RBuffer);
        testShader.SetBuffer(calcFacesKernelHandle, "kBuffer", kBuffer);
        testShader.SetBuffer(calcFacesKernelHandle,  "GaussianToFaceBuffer", gaussianToFaceBuffer);
        // testShader.SetBuffer(calcFacesKernelHandle, "tempBuffer", tempBuffer);



        testShader.SetBuffer(mapGaussiansKernelHandle, "GaussianToFaceBuffer", gaussianToFaceBuffer);
        testShader.SetBuffer(mapGaussiansKernelHandle, "HahaXyzBuffer", haha_xyzBuffer);
        testShader.SetBuffer(mapGaussiansKernelHandle, "HahaRotationBuffer", haha_rotationBuffer);
        testShader.SetBuffer(mapGaussiansKernelHandle, "HahaScalingBuffer", haha_scalingBuffer);
        testShader.SetBuffer(mapGaussiansKernelHandle, "TBuffer", TBuffer);
        testShader.SetBuffer(mapGaussiansKernelHandle, "RBuffer", RBuffer);
        testShader.SetBuffer(mapGaussiansKernelHandle, "kBuffer", kBuffer);
        testShader.SetBuffer(mapGaussiansKernelHandle, "SplatPosOutput", gaussianRenderer.m_GpuPosData);
        testShader.SetBuffer(mapGaussiansKernelHandle, "SplatOtherOutput", gaussianRenderer.m_GpuOtherData);
        int otherStride = GaussianSplatAsset.GetOtherSizeNoSHIndex(gaussianRenderer.asset.scaleFormat);
        if (gaussianRenderer.asset.shFormat > GaussianSplatAsset.SHFormat.Norm6)
        {
            otherStride += 2;
        }
        testShader.SetInt("_SplatOtherStride", otherStride);

        ExecuteShader();
    }
    void Update()
    {
    }

    void LateUpdate()
    {
        if (UseQuestCpuSplatUpdate && ShouldRunQuestCpuUpdateThisFrame())
        {
            ExecuteQuestCpuUpdate();
            return;
        }

        if (UseQuestCpuSplatUpdate || !isInitialized || poseController == null || testShader == null)
            return;

        vertexBuffer = poseController.GetVertexBuffer();
        if (vertexBuffer == null)
            return;

        testShader.SetBuffer(calcFacesKernelHandle, "VertexBuffer", vertexBuffer);
        ExecuteShader();
    }

    bool ShouldRunQuestCpuUpdateThisFrame()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        int interval = Mathf.Max(1, questCpuUpdateInterval);
        return lastQuestCpuUpdateFrame < 0 || Time.frameCount % interval == 0;
#else
        return true;
#endif
    }
    void ExecuteShader()
    {
        if (!isInitialized) return;

        int faceThreadGroups = Mathf.CeilToInt(gaussianToFaceBuffer.count / 64.0f);
        testShader.Dispatch(calcFacesKernelHandle, faceThreadGroups, 1, 1);

        int gaussianThreadGroups = Mathf.CeilToInt(gaussianToFaceBuffer.count / 64.0f);
        testShader.Dispatch(mapGaussiansKernelHandle, gaussianThreadGroups, 1, 1);

        UpdateGaussianRenderer();
        // CreateOtherDataAsset();
        // SaveTBufferToTxt();
        // SaveRBufferToTxt();
        // SaveKBufferToTxt();
        // DebugFaceBuffer();
        // DebugGaussianToFaceBuffer();
    }
#if UNITY_EDITOR
void DebugGaussianToFaceBuffer()
{
    if (gaussianToFaceBuffer == null)
    {
        Debug.LogError("GaussianToFaceBuffer is null!");
        return;
    }

    int gaussianCount = gaussianToFaceBuffer.count;
    int[] gaussianToFaceData = new int[gaussianCount];
    gaussianToFaceBuffer.GetData(gaussianToFaceData);

    using (StreamWriter writer = new StreamWriter("Assets/GaussianToFaceBuffer.txt"))
    {
        for (int i = 0; i < gaussianToFaceData.Length; i++)
        {
            writer.WriteLine($"Gaussian {i} -> Face {gaussianToFaceData[i]}");
        }
    }

    Debug.Log("GaussianToFaceBuffer saved to file.");
}
void SaveTBufferToTxt()
{
    if (TBuffer == null)
    {
        Debug.LogError("TBuffer is null! Ensure it is initialized.");
        return;
    }

    string filePath = "Assets/GaussianAssets/TBuffer.txt";

    try
    {
        int count = TBuffer.count;
        Vector3[] data = new Vector3[count];
        TBuffer.GetData(data); // Fetch actual buffer data

        using (StreamWriter writer = new StreamWriter(filePath))
        {
            foreach (var item in data)
            {
                writer.WriteLine($"{item.x} {item.y} {item.z}"); // Write x, y, z for each row
            }
        }

        Debug.Log($"TBuffer saved to: {filePath}");
    }
    catch (Exception ex)
    {
        Debug.LogError($"Error saving TBuffer: {ex.Message}");
    }
}

void SaveRBufferToTxt()
{
    if (RBuffer == null)
    {
        Debug.LogError("RBuffer is null! Ensure it is initialized.");
        return;
    }

    string filePath = "Assets/GaussianAssets/RBuffer.txt";

    try
    {
        int count = RBuffer.count;
        Quaternion[] data = new Quaternion[count];
        RBuffer.GetData(data); // Fetch actual buffer data

        using (StreamWriter writer = new StreamWriter(filePath))
        {
            writer.WriteLine("RBuffer (Rotation Quaternions):");
            foreach (var item in data)
            {
                writer.WriteLine($"{item.x} {item.y} {item.z} {item.w}"); // Write quaternion x, y, z, w
            }
        }

        Debug.Log($"RBuffer saved to: {filePath}");
    }
    catch (Exception ex)
    {
        Debug.LogError($"Error saving RBuffer: {ex.Message}");
    }
}

void SaveKBufferToTxt()
{
    if (kBuffer == null)
    {
        Debug.LogError("kBuffer is null! Ensure it is initialized.");
        return;
    }

    string filePath = "Assets/GaussianAssets/kBuffer.txt";

    try
    {
        int count = kBuffer.count;
        float[] data = new float[count];
        kBuffer.GetData(data); // Fetch actual buffer data

        using (StreamWriter writer = new StreamWriter(filePath))
        {
            writer.WriteLine("kBuffer (Scaling Factors):");
            foreach (var item in data)
            {
                writer.WriteLine($"{item}"); // Write each scaling factor
            }
        }

        Debug.Log($"kBuffer saved to: {filePath}");
    }
    catch (Exception ex)
    {
        Debug.LogError($"Error saving kBuffer: {ex.Message}");
    }
}
void DebugFaceBuffer()
{
    if (faceBuffer == null)
    {
        Debug.LogError("FaceBuffer is null!");
        return;
    }

    int faceCount = faceBuffer.count;
    int3[] faceData = new int3[faceCount];
    faceBuffer.GetData(faceData);

    using (StreamWriter writer = new StreamWriter("Assets/FaceBuffer.txt"))
    {
        for (int i = 0; i < faceData.Length; i++)
        {
            writer.WriteLine($"Face {i}: {faceData[i].x} {faceData[i].y} {faceData[i].z}");
        }
    }

    Debug.Log("FaceBuffer saved to file.");
}
#endif
    void UpdateGaussianRenderer()
    {
        // Updated directly on GPU by MapGaussiansToFaces.
    }

    bool InitializeQuestCpuUpdateData(int gaussianCount)
    {
        if (!hahaImporter.EnsureLoaded() || hahaImporter.data == null)
        {
            Debug.LogError("Quest CPU Gaussian update could not load Haha avatar data.");
            enabled = false;
            return false;
        }

        cpuGaussianToFace = hahaImporter.data.gaussianToFace;
        cpuFaces = hahaImporter.data.facesToVerts;
        cpuOffsets = hahaImporter.data.offsets;
        cpuRotations = hahaImporter.data.rotations;
        cpuPositionData = new uint[gaussianCount * 3];
        cpuSortPositions = new Vector3[gaussianCount];
        gaussianRenderer.SetCpuSortPositions(cpuSortPositions);
        int otherStrideBytes = GaussianSplatAsset.GetOtherSizeNoSHIndex(gaussianRenderer.asset.scaleFormat);
        if (gaussianRenderer.asset.shFormat > GaussianSplatAsset.SHFormat.Norm6)
        {
            otherStrideBytes += 2;
        }
        if (otherStrideBytes % sizeof(uint) != 0)
        {
            Debug.LogError("Quest CPU Gaussian update requires word-aligned Gaussian other data.");
            enabled = false;
            return false;
        }

        cpuOtherStrideWords = otherStrideBytes / sizeof(uint);
        var originalOtherData = gaussianRenderer.asset.otherData.GetData<uint>();
        cpuOtherData = new uint[originalOtherData.Length];
        for (int i = 0; i < originalOtherData.Length; ++i)
        {
            cpuOtherData[i] = originalOtherData[i];
        }

        questCpuPositionBuffer?.Dispose();
        questCpuPositionBuffer = null;

        if (UseDirectQuestPositionBuffer)
        {
            gaussianRenderer.SetUpdatedPositionsBuffer(null);
        }
        else
        {
            questCpuPositionBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource | GraphicsBuffer.Target.CopyDestination,
                gaussianCount * 3,
                sizeof(uint));
            gaussianRenderer.SetUpdatedPositionsBuffer(questCpuPositionBuffer);
        }

        if (cpuGaussianToFace == null || cpuFaces == null || cpuOffsets == null ||
            cpuRotations == null || cpuOtherData == null ||
            cpuGaussianToFace.Length < gaussianCount || cpuOffsets.Length < gaussianCount ||
            cpuRotations.Length < gaussianCount || cpuOtherData.Length < gaussianCount * cpuOtherStrideWords)
        {
            Debug.LogError("Quest CPU Gaussian update data is missing or has an unexpected size.");
            enabled = false;
            return false;
        }

        return true;
    }

    void ExecuteQuestCpuUpdate()
    {
        GraphicsBuffer targetPositionBuffer = UseDirectQuestPositionBuffer
            ? gaussianRenderer.m_GpuPosData
            : questCpuPositionBuffer;
        GraphicsBuffer targetOtherBuffer = gaussianRenderer.m_GpuOtherData;

        if (!isInitialized || cpuPositionData == null || cpuOtherData == null ||
            targetPositionBuffer == null || targetOtherBuffer == null)
            return;

        Vector3[] vertices = poseController.GetCurrentVertices();
        if (vertices == null || vertices.Length == 0)
            return;

        using (GaussianUpdateMarker.Auto())
        {
            bool optimized = false;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            long computeStart = Stopwatch.GetTimestamp();
#endif
            using (GaussianComputeMarker.Auto())
            {
                if (useOptimizedQuestCpuUpdate)
                    optimized = TryComputeOptimized(vertices);

                if (!optimized)
                {
                    // Preserve the reference path's skip/retain behavior across diagnostic toggles.
                    if (lastUpdateUsedOptimizedPath && nativePositionData.IsCreated)
                    {
                        nativePositionData.CopyTo(cpuPositionData);
                        nativeOtherData.CopyTo(cpuOtherData);
                    }
                    ComputeQuestCpuReference(vertices, cpuGaussianToFace, cpuFaces, cpuOffsets,
                        cpuRotations, cpuPositionData, cpuSortPositions, cpuOtherData, cpuOtherStrideWords);
                }
                lastUpdateUsedOptimizedPath = optimized;
            }
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            QuestComputeTicks += Stopwatch.GetTimestamp() - computeStart;
            ++QuestComputeCount;
            long uploadStart = Stopwatch.GetTimestamp();
#endif
            using (GaussianUploadMarker.Auto())
            {
                if (optimized)
                {
                    targetPositionBuffer.SetData(nativePositionData);
                    targetOtherBuffer.SetData(nativeOtherData);
                }
                else
                {
                    targetPositionBuffer.SetData(cpuPositionData);
                    targetOtherBuffer.SetData(cpuOtherData);
                }
            }
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            QuestUploadTicks += Stopwatch.GetTimestamp() - uploadStart;
            ++QuestUploadCount;
#endif
            lastQuestCpuUpdateFrame = Time.frameCount;
        }
    }

    bool TryComputeOptimized(Vector3[] vertices)
    {
        try
        {
            EnsureNativeCpuData(vertices.Length);
            nativeVertices.CopyFrom(vertices);
            if (!lastUpdateUsedOptimizedPath)
            {
                nativePositionData.CopyFrom(cpuPositionData);
                nativeSortPositions.CopyFrom(cpuSortPositions);
                nativeOtherData.CopyFrom(cpuOtherData);
            }

            CreateQuestCpuJob(nativeVertices, nativeGaussianToFace, nativeFaces, nativeOffsets,
                nativeRotations, nativePositionData, nativeSortPositions, nativeOtherData,
                cpuOtherStrideWords).Schedule(cpuSortPositions.Length, 64).Complete();

            // The renderer's existing CPU sort API owns a managed array; keep that contract.
            nativeSortPositions.CopyTo(cpuSortPositions);
            return true;
        }
        catch (Exception exception)
        {
            if (!optimizedFailureLogged)
            {
                Debug.LogWarning("Quest Burst Gaussian update failed; using the existing CPU path. " +
                    exception.GetType().Name + ": " + exception.Message, this);
                optimizedFailureLogged = true;
            }
            useOptimizedQuestCpuUpdate = false;
            return false;
        }
    }

    void EnsureNativeCpuData(int vertexCount)
    {
        if (!nativeGaussianToFace.IsCreated)
        {
            try
            {
                nativeGaussianToFace = new NativeArray<int>(cpuGaussianToFace, Allocator.Persistent);
                nativeFaces = new NativeArray<int3>(cpuFaces, Allocator.Persistent);
                nativeOffsets = new NativeArray<float3>(cpuOffsets, Allocator.Persistent);
                nativeRotations = new NativeArray<float4>(cpuRotations, Allocator.Persistent);
                nativePositionData = new NativeArray<uint>(cpuPositionData, Allocator.Persistent);
                nativeSortPositions = new NativeArray<Vector3>(cpuSortPositions, Allocator.Persistent);
                nativeOtherData = new NativeArray<uint>(cpuOtherData, Allocator.Persistent);
            }
            catch
            {
                DisposeNativeCpuData();
                throw;
            }
        }
        if (!nativeVertices.IsCreated || nativeVertices.Length != vertexCount)
        {
            if (nativeVertices.IsCreated)
                nativeVertices.Dispose();
            nativeVertices = new NativeArray<Vector3>(vertexCount, Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
        }
    }

    void DisposeNativeCpuData()
    {
        if (nativeGaussianToFace.IsCreated) nativeGaussianToFace.Dispose();
        if (nativeFaces.IsCreated) nativeFaces.Dispose();
        if (nativeOffsets.IsCreated) nativeOffsets.Dispose();
        if (nativeRotations.IsCreated) nativeRotations.Dispose();
        if (nativeVertices.IsCreated) nativeVertices.Dispose();
        if (nativePositionData.IsCreated) nativePositionData.Dispose();
        if (nativeSortPositions.IsCreated) nativeSortPositions.Dispose();
        if (nativeOtherData.IsCreated) nativeOtherData.Dispose();
    }

    static void ComputeQuestCpuReference(Vector3[] vertices, int[] gaussianToFace, int3[] faces,
        float3[] offsets, float4[] rotations, uint[] positions, Vector3[] sortPositions,
        uint[] other, int otherStrideWords)
    {
        int gaussianCount = positions.Length / 3;
        for (int i = 0; i < gaussianCount; ++i)
        {
            int faceIndex = gaussianToFace[i];
            if (faceIndex < 0 || faceIndex >= faces.Length)
                continue;

            int3 face = faces[faceIndex];
            if (face.x < 0 || face.x >= vertices.Length ||
                face.y < 0 || face.y >= vertices.Length ||
                face.z < 0 || face.z >= vertices.Length)
                continue;

            float3 v0 = ToFloat3(vertices[face.x]);
            float3 v1 = ToFloat3(vertices[face.y]);
            float3 v2 = ToFloat3(vertices[face.z]);

            float3 t = (v0 + v1 + v2) / 3.0f;
            float3 vec1 = v2 - v1;
            float3 vec2 = v0 - v1;
            float3 vec3 = v0 - v2;
            float3 faceNormal = math.cross(vec1, vec2);
            float3 norm = NormalizeSafe(faceNormal);

            vec1 = NormalizeSafe(vec1);
            float3 prod = NormalizeSafe(math.cross(vec1, norm));
            float3x3 rot = new float3x3(vec1, norm, prod);

            float area = math.length(faceNormal);
            float vec3Length = math.max(math.length(vec3), 1e-6f);
            float h = area / vec3Length;
            float k = (h + vec3Length) * 0.5f / 0.05f;

            float4 faceRotation = MatrixToQuaternionWxyz(rot);
            float4 gaussianRotation = NormalizeQuaternionWxyz(rotations[i]);
            float4 gaussianWorldRotation = MultiplyQuaternionsWxyz(faceRotation, gaussianRotation);
            gaussianWorldRotation.x *= -1.0f;
            gaussianWorldRotation.y *= -1.0f;

            float3 pos = t + QuaternionApplyWxyz(faceRotation, offsets[i]) * k;
            pos.x *= -1.0f;
            sortPositions[i] = new Vector3(pos.x, pos.y, pos.z);

            int outIndex = i * 3;
            positions[outIndex] = math.asuint(pos.x);
            positions[outIndex + 1] = math.asuint(pos.y);
            positions[outIndex + 2] = math.asuint(pos.z);

            float4 normalizedRotXyzw = NormalizeQuaternionXyzw(new float4(
                gaussianWorldRotation.y,
                gaussianWorldRotation.z,
                gaussianWorldRotation.w,
                gaussianWorldRotation.x));
            other[i * otherStrideWords] = EncodeQuatToNorm10(PackSmallest3Rotation(normalizedRotXyzw));
        }
    }

    static QuestCpuUpdateJob CreateQuestCpuJob(NativeArray<Vector3> vertices,
        NativeArray<int> gaussianToFace, NativeArray<int3> faces, NativeArray<float3> offsets,
        NativeArray<float4> rotations, NativeArray<uint> positions, NativeArray<Vector3> sortPositions,
        NativeArray<uint> other, int otherStrideWords)
    {
        return new QuestCpuUpdateJob
        {
            vertices = vertices,
            gaussianToFace = gaussianToFace,
            faces = faces,
            offsets = offsets,
            rotations = rotations,
            positions = positions,
            sortPositions = sortPositions,
            other = other,
            otherStrideWords = otherStrideWords
        };
    }

    [BurstCompile(FloatMode = FloatMode.Strict, CompileSynchronously = true)]
    struct QuestCpuUpdateJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<Vector3> vertices;
        [ReadOnly] public NativeArray<int> gaussianToFace;
        [ReadOnly] public NativeArray<int3> faces;
        [ReadOnly] public NativeArray<float3> offsets;
        [ReadOnly] public NativeArray<float4> rotations;
        // Each iteration exclusively writes its own three position words and first other word.
        [NativeDisableParallelForRestriction] public NativeArray<uint> positions;
        public NativeArray<Vector3> sortPositions;
        [NativeDisableParallelForRestriction] public NativeArray<uint> other;
        public int otherStrideWords;

        public void Execute(int i)
        {
            int faceIndex = gaussianToFace[i];
            if (faceIndex < 0 || faceIndex >= faces.Length)
                return;

            int3 face = faces[faceIndex];
            if (face.x < 0 || face.x >= vertices.Length ||
                face.y < 0 || face.y >= vertices.Length ||
                face.z < 0 || face.z >= vertices.Length)
                return;

            float3 v0 = ToFloat3(vertices[face.x]);
            float3 v1 = ToFloat3(vertices[face.y]);
            float3 v2 = ToFloat3(vertices[face.z]);

            float3 t = (v0 + v1 + v2) / 3.0f;
            float3 vec1 = v2 - v1;
            float3 vec2 = v0 - v1;
            float3 vec3 = v0 - v2;
            float3 faceNormal = math.cross(vec1, vec2);
            float3 norm = NormalizeSafe(faceNormal);

            vec1 = NormalizeSafe(vec1);
            float3 prod = NormalizeSafe(math.cross(vec1, norm));
            float3x3 rot = new float3x3(vec1, norm, prod);

            float area = math.length(faceNormal);
            float vec3Length = math.max(math.length(vec3), 1e-6f);
            float h = area / vec3Length;
            float k = (h + vec3Length) * 0.5f / 0.05f;

            float4 faceRotation = MatrixToQuaternionWxyz(rot);
            float4 gaussianRotation = NormalizeQuaternionWxyz(rotations[i]);
            float4 gaussianWorldRotation = MultiplyQuaternionsWxyz(faceRotation, gaussianRotation);
            gaussianWorldRotation.x *= -1.0f;
            gaussianWorldRotation.y *= -1.0f;

            float3 pos = t + QuaternionApplyWxyz(faceRotation, offsets[i]) * k;
            pos.x *= -1.0f;
            sortPositions[i] = new Vector3(pos.x, pos.y, pos.z);

            int outIndex = i * 3;
            positions[outIndex] = math.asuint(pos.x);
            positions[outIndex + 1] = math.asuint(pos.y);
            positions[outIndex + 2] = math.asuint(pos.z);

            float4 normalizedRotXyzw = NormalizeQuaternionXyzw(new float4(
                gaussianWorldRotation.y,
                gaussianWorldRotation.z,
                gaussianWorldRotation.w,
                gaussianWorldRotation.x));
            other[i * otherStrideWords] = EncodeQuatToNorm10(PackSmallest3Rotation(normalizedRotXyzw));
        }
    }

#if UNITY_EDITOR
    public static void ComputeQuestCpuReferenceForValidation(Vector3[] vertices, int[] gaussianToFace,
        int3[] faces, float3[] offsets, float4[] rotations, uint[] positions,
        Vector3[] sortPositions, uint[] other, int otherStrideWords)
    {
        ComputeQuestCpuReference(vertices, gaussianToFace, faces, offsets, rotations,
            positions, sortPositions, other, otherStrideWords);
    }

    public static void ComputeQuestCpuBurstForValidation(Vector3[] vertices, int[] gaussianToFace,
        int3[] faces, float3[] offsets, float4[] rotations, uint[] positions,
        Vector3[] sortPositions, uint[] other, int otherStrideWords)
    {
        using (var v = new NativeArray<Vector3>(vertices, Allocator.TempJob))
        using (var g = new NativeArray<int>(gaussianToFace, Allocator.TempJob))
        using (var f = new NativeArray<int3>(faces, Allocator.TempJob))
        using (var o = new NativeArray<float3>(offsets, Allocator.TempJob))
        using (var r = new NativeArray<float4>(rotations, Allocator.TempJob))
        using (var p = new NativeArray<uint>(positions, Allocator.TempJob))
        using (var s = new NativeArray<Vector3>(sortPositions, Allocator.TempJob))
        using (var d = new NativeArray<uint>(other, Allocator.TempJob))
        {
            CreateQuestCpuJob(v, g, f, o, r, p, s, d, otherStrideWords)
                .Schedule(sortPositions.Length, 64).Complete();
            p.CopyTo(positions);
            s.CopyTo(sortPositions);
            d.CopyTo(other);
        }
    }
#endif

    static float3 ToFloat3(Vector3 v)
    {
        return new float3(v.x, v.y, v.z);
    }

    static float3 NormalizeSafe(float3 v)
    {
        float length = math.length(v);
        return length > 1e-6f ? v / length : new float3(0, 0, 0);
    }

    static float4 NormalizeQuaternionWxyz(float4 q)
    {
        float length = math.sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
        return length > 1e-6f ? q / length : new float4(1, 0, 0, 0);
    }

    static float4 NormalizeQuaternionXyzw(float4 q)
    {
        float length = math.sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
        return length > 1e-6f ? q / length : new float4(0, 0, 0, 1);
    }

    static float4 StandardizeQuaternionWxyz(float4 q)
    {
        return q.x < 0.0f ? -q : q;
    }

    static float4 MultiplyQuaternionsWxyz(float4 a, float4 b)
    {
        float aw = a.x;
        float ax = a.y;
        float ay = a.z;
        float az = a.w;
        float bw = b.x;
        float bx = b.y;
        float by = b.z;
        float bz = b.w;

        return StandardizeQuaternionWxyz(new float4(
            aw * bw - ax * bx - ay * by - az * bz,
            aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw));
    }

    static float3 QuaternionApplyWxyz(float4 quaternion, float3 point)
    {
        float qw = quaternion.x;
        float3 u = new float3(quaternion.y, quaternion.z, quaternion.w);
        float dotUP = math.dot(u, point);
        float dotUU = math.dot(u, u);
        float3 crossUP = math.cross(u, point);

        return 2.0f * dotUP * u +
               (qw * qw - dotUU) * point +
               2.0f * qw * crossUP;
    }

    static float4 MatrixToQuaternionWxyz(float3x3 m)
    {
        float qAbs0 = math.sqrt(math.max(0.0f, 1.0f + m.c0.x + m.c1.y + m.c2.z));
        float qAbs1 = math.sqrt(math.max(0.0f, 1.0f + m.c0.x - m.c1.y - m.c2.z));
        float qAbs2 = math.sqrt(math.max(0.0f, 1.0f - m.c0.x + m.c1.y - m.c2.z));
        float qAbs3 = math.sqrt(math.max(0.0f, 1.0f - m.c0.x - m.c1.y + m.c2.z));

        float4 candidate0 = new float4(
            qAbs0 * 0.5f,
            SafeDivide(m.c1.z - m.c2.y, 2.0f * qAbs0),
            SafeDivide(m.c2.x - m.c0.z, 2.0f * qAbs0),
            SafeDivide(m.c0.y - m.c1.x, 2.0f * qAbs0));

        float4 candidate1 = new float4(
            SafeDivide(m.c1.z - m.c2.y, 2.0f * qAbs1),
            qAbs1 * 0.5f,
            SafeDivide(m.c1.x + m.c0.y, 2.0f * qAbs1),
            SafeDivide(m.c0.z + m.c2.x, 2.0f * qAbs1));

        float4 candidate2 = new float4(
            SafeDivide(m.c2.x - m.c0.z, 2.0f * qAbs2),
            SafeDivide(m.c1.x + m.c0.y, 2.0f * qAbs2),
            qAbs2 * 0.5f,
            SafeDivide(m.c2.y + m.c1.z, 2.0f * qAbs2));

        float4 candidate3 = new float4(
            SafeDivide(m.c0.y - m.c1.x, 2.0f * qAbs3),
            SafeDivide(m.c0.z + m.c2.x, 2.0f * qAbs3),
            SafeDivide(m.c2.y + m.c1.z, 2.0f * qAbs3),
            qAbs3 * 0.5f);

        int maxIndex = 0;
        float maxValue = qAbs0;
        if (qAbs1 > maxValue)
        {
            maxIndex = 1;
            maxValue = qAbs1;
        }
        if (qAbs2 > maxValue)
        {
            maxIndex = 2;
            maxValue = qAbs2;
        }
        if (qAbs3 > maxValue)
        {
            maxIndex = 3;
        }

        float4 q = candidate0;
        if (maxIndex == 1)
        {
            q = candidate1;
        }
        else if (maxIndex == 2)
        {
            q = candidate2;
        }
        else if (maxIndex == 3)
        {
            q = candidate3;
        }

        return NormalizeQuaternionWxyz(q);
    }

    static float SafeDivide(float numerator, float denominator)
    {
        return math.abs(denominator) > 1e-6f ? numerator / denominator : 0.0f;
    }

    static float4 PackSmallest3Rotation(float4 q)
    {
        float4 absQ = math.abs(q);
        int index = 0;
        float maxV = absQ.x;
        if (absQ.y > maxV)
        {
            index = 1;
            maxV = absQ.y;
        }
        if (absQ.z > maxV)
        {
            index = 2;
            maxV = absQ.z;
        }
        if (absQ.w > maxV)
        {
            index = 3;
        }

        if (index == 0)
        {
            q = new float4(q.y, q.z, q.w, q.x);
        }
        else if (index == 1)
        {
            q = new float4(q.x, q.z, q.w, q.y);
        }
        else if (index == 2)
        {
            q = new float4(q.x, q.y, q.w, q.z);
        }

        float3 three = new float3(q.x, q.y, q.z) * (q.w >= 0.0f ? 1.0f : -1.0f);
        three = three * 1.41421356237f * 0.5f + 0.5f;
        return new float4(three.x, three.y, three.z, index / 3.0f);
    }

    static uint EncodeQuatToNorm10(float4 v)
    {
        return (uint)(math.saturate(v.x) * 1023.5f) |
               ((uint)(math.saturate(v.y) * 1023.5f) << 10) |
               ((uint)(math.saturate(v.z) * 1023.5f) << 20) |
               ((uint)(math.saturate(v.w) * 3.5f) << 30);
    }

    public ComputeBuffer GetTBuffer()
    {
        return TBuffer;
    }



    void OnDestroy()
    {
        DisposeNativeCpuData();
        if (UseQuestCpuSplatUpdate && gaussianRenderer != null)
        {
            gaussianRenderer.SetUpdatedPositionsBuffer(null);
            gaussianRenderer.SetCpuSortPositions(null);
        }
        questCpuPositionBuffer?.Dispose();
        haha_scalingBuffer?.Release();
        TBuffer?.Release();
        RBuffer?.Release();
        kBuffer?.Release();
    }
}
