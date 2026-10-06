// Disabled by default: stored outside Unity Assets and Packages.
// For an explicit diagnostic build only, copy this C# source into Assets/Scripts
// and its compute shader into Assets/Resources. Normal game builds do not import this tool.
using System;
using System.Collections;
using System.IO;
using Meta.XR.EnvironmentDepth;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;
using OculusUtils = Unity.XR.Oculus.Utils;

// Isolated diagnostic APK only. Explicit marker, one GPU copy, then self-destroy.
public sealed class QuestEnvironmentDepthCapture : MonoBehaviour
{
    bool paused;
    InputDevice head;
    ComputeBuffer buffer;
    string statusPath;
    Capture status;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Create()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        new GameObject("One-shot environment depth capture").AddComponent<QuestEnvironmentDepthCapture>();
#endif
    }

    void OnApplicationPause(bool value) { paused = value; }

    IEnumerator Start()
    {
        statusPath = Path.Combine(Application.persistentDataPath, "environment-capture-status.json");
        string marker = Path.Combine(Application.persistentDataPath, "capture-environment-depth.txt");
        status = new Capture { state = "waiting_for_marker", utc = DateTime.UtcNow.ToString("o") };
        WriteStatus();
        double expires = Time.realtimeSinceStartupAsDouble + 1200;
        var poll = new WaitForSecondsRealtime(1);
        while (!File.Exists(marker) && Time.realtimeSinceStartupAsDouble < expires) yield return poll;
        if (!File.Exists(marker)) { Fail("No explicit marker before timeout."); yield break; }
        File.Delete(marker);
        status.state = "waiting_for_focused_depth";
        WriteStatus();
        expires = Time.realtimeSinceStartupAsDouble + 120;
        double readySince = -1;
        while (Time.realtimeSinceStartupAsDouble < expires)
        {
            bool ready = IsReady(out _, out _);
            if (!ready) readySince = -1;
            else if (readySince < 0) readySince = Time.realtimeSinceStartupAsDouble;
            if (readySince >= 0 && Time.realtimeSinceStartupAsDouble - readySince >= 2)
            {
                yield return new WaitForEndOfFrame();
                if (IsReady(out var manager, out var texture))
                {
                    try { BeginCapture(manager, texture); }
                    catch (Exception exception) { Fail(exception.ToString()); }
                    yield break;
                }
                readySince = -1;
            }
            yield return poll;
        }
        Fail("No focused, worn, tracked headset with a current depth frame within 120 seconds.");
    }

    bool IsReady(out EnvironmentDepthManager manager, out RenderTexture texture)
    {
        manager = FindObjectOfType<EnvironmentDepthManager>();
        texture = Shader.GetGlobalTexture("_EnvironmentDepthTexture") as RenderTexture;
        if (!head.isValid) head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
        bool tracked = head.isValid && head.TryGetFeatureValue(CommonUsages.isTracked, out bool t) && t;
        bool present = head.isValid && head.TryGetFeatureValue(CommonUsages.userPresence, out bool p) && p;
        uint id = 0;
        return !paused && Application.isFocused && present && tracked && manager != null &&
            manager.isActiveAndEnabled && manager.IsDepthAvailable && texture != null && texture.IsCreated() &&
            texture.dimension == TextureDimension.Tex2DArray && texture.volumeDepth >= 2 &&
            OculusUtils.GetEnvironmentDepthTextureId(ref id) && id != 0 &&
            OculusUtils.GetEnvironmentDepthFrameDesc(0).isValid &&
            OculusUtils.GetEnvironmentDepthFrameDesc(1).isValid;
    }

    static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
    static float[] Flatten(Matrix4x4 m)
    {
        var result = new float[16];
        for (int row = 0; row < 4; row++) for (int col = 0; col < 4; col++)
            result[4 * row + col] = m[row, col];
        return result;
    }

    void BeginCapture(EnvironmentDepthManager manager, RenderTexture texture)
    {
        if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)
            throw new InvalidOperationException("Compute shaders and asynchronous GPU readback are required.");
        var matrices = Shader.GetGlobalMatrixArray("_EnvironmentDepthReprojectionMatrices");
        if (matrices == null || matrices.Length < 2) throw new InvalidOperationException("Depth matrices missing.");
        var z = Shader.GetGlobalVector("_EnvironmentDepthZBufferParams");
        foreach (float value in Flatten(matrices[0]))
            if (!Finite(value)) throw new InvalidOperationException("Non-finite depth reprojection matrix.");
        if (!Finite(z.x) || !Finite(z.y) || z.x == 0 || Math.Abs(matrices[0].determinant) < 1e-10f)
            throw new InvalidOperationException("Invalid depth reconstruction parameters.");
        uint textureId = 0;
        if (!OculusUtils.GetEnvironmentDepthTextureId(ref textureId)) throw new InvalidOperationException("Native depth frame lost.");
        var frame = OculusUtils.GetEnvironmentDepthFrameDesc(0);
        if (!frame.isValid) throw new InvalidOperationException("Native depth descriptor invalid.");
        var shader = Resources.Load<ComputeShader>("QuestEnvironmentDepthCapture");
        if (shader == null || !shader.HasKernel("CopyDepth")) throw new InvalidOperationException("Capture compute shader missing.");
        var camera = Camera.main;
        var trackedHands = FindObjectOfType<QuestTrackedHandOcclusion>();
        var rig = FindObjectOfType<OVRCameraRig>();
        string stem = "quest-environment-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
        status = new Capture {
            state = "readback_pending", utc = DateTime.UtcNow.ToString("o"), stem = stem,
            width = texture.width, height = texture.height, layer = 0, unityFrame = Time.frameCount,
            textureFormat = texture.graphicsFormat.ToString(), textureDimension = texture.dimension.ToString(),
            graphicsApi = SystemInfo.graphicsDeviceType.ToString(), rawEncoding = "float32 little-endian; index=y*width+x; raw texture y=0 first",
            reprojection = Flatten(matrices[0]), matrixInverse = Flatten(matrices[0].inverse),
            matrixLayout = "row-major: [row*4+column]", trackingSpaceLocalToWorld = rig != null ? Flatten(rig.trackingSpace.localToWorldMatrix) : null,
            zBufferParams = new[] { z.x, z.y, z.z, z.w }, nativeTextureId = textureId,
            cameraPosition = camera != null ? camera.transform.position : Vector3.zero,
            cameraRotation = camera != null ? camera.transform.rotation : Quaternion.identity,
            cameraPoseAvailable = camera != null, removeHandsRequested = manager.RemoveHands,
            trackedHandReplacementActive = trackedHands != null && trackedHands.IsReplacementActive,
            focused = Application.isFocused, headsetPresent = true, headsetTracked = true,
            nativeCreateTime = frame.createTime, nativePredictedDisplayTime = frame.predictedDisplayTime,
            nativeSwapchainIndex = frame.swapchainIndex, nativeCreatePoseLocation = frame.createPoseLocation,
            nativeCreatePoseRotation = frame.createPoseRotation, nativeNearZ = frame.nearZ,
            nativeFarZ = Finite(frame.farZ) ? frame.farZ : 0, nativeFarInfinite = float.IsInfinity(frame.farZ),
            nativeFovTangents = new[] { frame.fovLeftAngle, frame.fovRightAngle, frame.fovTopAngle, frame.fovDownAngle },
            note = "Actual raw Meta SDK 67 runtime environment depth, left layer. Global matrix is authoritative. Native descriptor is separately sampled diagnostic metadata. No avatar, hand proxy, Scene Mesh, or unseen surfaces are added."
        };
        WriteStatus();
        int count = checked(texture.width * texture.height);
        buffer = new ComputeBuffer(count, sizeof(float), ComputeBufferType.Structured);
        int kernel = shader.FindKernel("CopyDepth");
        shader.SetInt("_CaptureWidth", texture.width);
        shader.SetInt("_CaptureHeight", texture.height);
        shader.SetTexture(kernel, "_CaptureSource", texture);
        shader.SetBuffer(kernel, "_CaptureValues", buffer);
        shader.Dispatch(kernel, (texture.width + 7) / 8, (texture.height + 7) / 8, 1);
        AsyncGPUReadback.Request(buffer, request => {
            try
            {
                if (request.hasError) throw new IOException("GPU readback failed.");
                var data = request.GetData<float>();
                if (data.Length != count) throw new IOException("Unexpected raw depth element count.");
                var values = data.ToArray();
                var bytes = new byte[checked(count * sizeof(float))];
                Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
                if (!BitConverter.IsLittleEndian)
                    for (int index = 0; index < bytes.Length; index += 4) Array.Reverse(bytes, index, 4);
                string raw = Path.Combine(Application.persistentDataPath, stem + ".float32");
                File.WriteAllBytes(raw, bytes);
                status.rawFile = Path.GetFileName(raw);
                status.byteCount = bytes.Length;
                status.state = "complete";
                File.WriteAllText(Path.Combine(Application.persistentDataPath, stem + ".json"), JsonUtility.ToJson(status, true));
                WriteStatus();
                Debug.Log("GSAC_ENVIRONMENT_CAPTURE complete " + raw);
            }
            catch (Exception exception) { status.state = "failed"; status.error = exception.ToString(); WriteStatus(); }
            finally { buffer?.Release(); buffer = null; Destroy(gameObject); }
        });
    }

    void WriteStatus()
    {
        File.WriteAllText(statusPath, JsonUtility.ToJson(status, true));
        Debug.Log("GSAC_ENVIRONMENT_CAPTURE " + status.state);
    }
    void Fail(string message)
    {
        status.state = "failed"; status.error = message; WriteStatus();
        buffer?.Release(); buffer = null; Destroy(gameObject);
    }

    [Serializable] sealed class Capture
    {
        public string state, utc, stem, textureFormat, textureDimension, graphicsApi, rawEncoding, rawFile, note, error, matrixLayout;
        public int width, height, layer, unityFrame, byteCount, nativeSwapchainIndex;
        public uint nativeTextureId;
        public float[] reprojection, matrixInverse, zBufferParams, nativeFovTangents, trackingSpaceLocalToWorld;
        public float nativeNearZ, nativeFarZ;
        public double nativeCreateTime, nativePredictedDisplayTime;
        public Vector3 cameraPosition, nativeCreatePoseLocation;
        public Quaternion cameraRotation;
        public Vector4 nativeCreatePoseRotation;
        public bool cameraPoseAvailable, removeHandsRequested, focused, headsetPresent, headsetTracked, nativeFarInfinite, trackedHandReplacementActive;
    }
}
