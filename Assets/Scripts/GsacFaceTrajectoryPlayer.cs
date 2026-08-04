using System;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

[DisallowMultipleComponent]
[DefaultExecutionOrder(70)]
public sealed class GsacFaceTrajectoryPlayer : MonoBehaviour
{
    const int ExpressionCount = GsacFaceBasis.ExpectedExpressionCount;

    [Header("References")]
    [SerializeField] GsacFaceParameterController faceController;

    [Header("Trajectory Source")]
    [SerializeField] TextAsset trajectoryJson;
    [SerializeField] string trajectoryFilePath;
    [SerializeField] string emotionName = "happiness";
    [SerializeField] bool loadOnStart = true;
    [SerializeField] bool playOnStart;

    [Header("Playback")]
    [SerializeField] bool loop = true;
    [SerializeField] float playbackSpeed = 1.0f;
    [SerializeField] bool useUnscaledTime;

    [Header("Runtime Status")]
    [SerializeField, TextArea(2, 5)] string status = "Not loaded.";

    readonly float[] sampledExpression50 = new float[ExpressionCount];
    GsacFaceTrajectoryData trajectory;
    GsacFaceTrajectoryEmotion selectedEmotion;
    int frameCursor;
    float localTimeSeconds;
    bool playing;

    public bool IsLoaded => trajectory != null && selectedEmotion != null;
    public bool IsPlaying => playing;
    public string Status => status;

    void Reset()
    {
        AutoAssignReferences();
    }

    void OnValidate()
    {
        AutoAssignReferences();
    }

    void Start()
    {
        AutoAssignReferences();

        if (loadOnStart)
            LoadTrajectory();

        if (playOnStart)
            Play();
    }

    void Update()
    {
        if (!playing || !IsLoaded)
            return;

        float delta = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
        localTimeSeconds += delta * Mathf.Max(0.0f, playbackSpeed);

        float endTime = Mathf.Max(0.0f, selectedEmotion.LastFrameTime);
        if (!loop && localTimeSeconds >= endTime)
        {
            ApplyExactFrame(selectedEmotion.frames[selectedEmotion.frames.Length - 1]);
            localTimeSeconds = endTime;
            playing = false;
            status = $"Stopped at final frame of '{selectedEmotion.name}'.";
            return;
        }

        float duration = GetLoopDuration();
        float sampleTime = loop && duration > 0.0f
            ? Mathf.Repeat(localTimeSeconds, duration)
            : Mathf.Min(localTimeSeconds, endTime);

        SampleAndApply(sampleTime);
    }

    public bool LoadTrajectory()
    {
        AutoAssignReferences();

        string json = null;
        string source = string.Empty;

        if (trajectoryJson != null)
        {
            json = trajectoryJson.text;
            source = trajectoryJson.name;
        }
        else if (!string.IsNullOrWhiteSpace(trajectoryFilePath))
        {
            source = trajectoryFilePath;
            if (!File.Exists(trajectoryFilePath))
            {
                SetStatus($"Trajectory file was not found: {trajectoryFilePath}", true);
                return false;
            }

            try
            {
                json = File.ReadAllText(trajectoryFilePath);
            }
            catch (Exception exception)
            {
                SetStatus($"Failed to read trajectory file: {exception.Message}", true);
                return false;
            }
        }
        else
        {
            SetStatus("No trajectory TextAsset or file path assigned.", true);
            return false;
        }

        try
        {
            trajectory = JsonConvert.DeserializeObject<GsacFaceTrajectoryData>(json);
        }
        catch (Exception exception)
        {
            trajectory = null;
            selectedEmotion = null;
            SetStatus($"Failed to parse trajectory JSON from {source}: {exception.Message}", true);
            return false;
        }

        if (trajectory == null)
        {
            selectedEmotion = null;
            SetStatus($"Invalid trajectory from {source}: JSON root is null.", true);
            return false;
        }

        if (!trajectory.Validate(out string validationMessage))
        {
            selectedEmotion = null;
            SetStatus($"Invalid trajectory from {source}: {validationMessage}", true);
            return false;
        }

        if (!SelectEmotion(emotionName))
            SelectEmotion(null);

        status = $"Loaded trajectory '{source}' with {trajectory.emotions.Length} emotions.";
        return selectedEmotion != null;
    }

    public bool SelectEmotion(string requestedEmotionName)
    {
        if (trajectory == null)
            return false;

        GsacFaceTrajectoryEmotion emotion = trajectory.FindEmotion(requestedEmotionName);
        if (emotion == null)
        {
            SetStatus($"Emotion '{requestedEmotionName}' was not found in the loaded trajectory.", true);
            return false;
        }

        selectedEmotion = emotion;
        emotionName = emotion.name;
        localTimeSeconds = 0.0f;
        frameCursor = 0;
        return true;
    }

    public void Play()
    {
        if (!IsLoaded && !LoadTrajectory())
            return;

        AutoAssignReferences();
        if (faceController == null)
        {
            SetStatus("Cannot play trajectory because GsacFaceParameterController is missing.", true);
            return;
        }

        playing = true;
        if (localTimeSeconds <= 0.0f)
            ApplyFirstFrame();
    }

    public void Pause()
    {
        playing = false;
    }

    public void StopAndReset()
    {
        playing = false;
        localTimeSeconds = 0.0f;
        frameCursor = 0;
        ApplyFirstFrame();
    }

    public bool SampleAndApply(float timeSeconds)
    {
        if (!IsLoaded)
            return false;

        if (faceController == null)
            AutoAssignReferences();

        if (faceController == null)
            return false;

        GsacFaceTrajectoryFrame[] frames = selectedEmotion.frames;
        if (frames == null || frames.Length == 0)
            return false;

        if (timeSeconds <= frames[0].timeSeconds)
            return ApplyExactFrame(frames[0]);

        int lastIndex = frames.Length - 1;
        if (timeSeconds >= frames[lastIndex].timeSeconds)
            return ApplyExactFrame(frames[lastIndex]);

        while (frameCursor > 0 && frames[frameCursor].timeSeconds > timeSeconds)
            --frameCursor;

        while (frameCursor < lastIndex - 1 && frames[frameCursor + 1].timeSeconds < timeSeconds)
            ++frameCursor;

        GsacFaceTrajectoryFrame a = frames[frameCursor];
        GsacFaceTrajectoryFrame b = frames[frameCursor + 1];
        float span = Mathf.Max(1e-6f, b.timeSeconds - a.timeSeconds);
        float t = Mathf.Clamp01((timeSeconds - a.timeSeconds) / span);

        SampleExpression(a, b, t);
        Vector3 jaw = SampleJaw(a, b, t);
        Vector2 eyelid = SampleEyelid(a, b, t);
        faceController.ApplyAbsoluteFaceParameters(sampledExpression50, jaw, eyelid);
        return true;
    }

    [ContextMenu("GSAC Trajectory/Load")]
    void LoadTrajectoryContextMenu()
    {
        LoadTrajectory();
    }

    [ContextMenu("GSAC Trajectory/Play")]
    void PlayContextMenu()
    {
        Play();
    }

    [ContextMenu("GSAC Trajectory/Pause")]
    void PauseContextMenu()
    {
        Pause();
    }

    [ContextMenu("GSAC Trajectory/Stop And Reset")]
    void StopAndResetContextMenu()
    {
        StopAndReset();
    }

    [ContextMenu("GSAC Trajectory/Apply First Frame")]
    public void ApplyFirstFrame()
    {
        if (!IsLoaded && !LoadTrajectory())
            return;

        ApplyExactFrame(selectedEmotion.frames[0]);
    }

    [ContextMenu("GSAC Trajectory/Apply Final Frame")]
    public void ApplyFinalFrame()
    {
        if (!IsLoaded && !LoadTrajectory())
            return;

        GsacFaceTrajectoryFrame[] frames = selectedEmotion.frames;
        ApplyExactFrame(frames[frames.Length - 1]);
    }

    bool ApplyExactFrame(GsacFaceTrajectoryFrame frame)
    {
        if (frame == null)
            return false;

        CopyExpression(frame.PreferredExpression(), sampledExpression50);
        Vector3 jaw = ArrayToVector3(frame.jawPose);
        Vector2 eyelid = frame.eyelid != null ? ArrayToVector2(frame.eyelid) : Vector2.zero;
        faceController.ApplyAbsoluteFaceParameters(sampledExpression50, jaw, eyelid);
        return true;
    }

    void SampleExpression(GsacFaceTrajectoryFrame a, GsacFaceTrajectoryFrame b, float t)
    {
        float[] expressionA = a.PreferredExpression();
        float[] expressionB = b.PreferredExpression();
        int count = Mathf.Min(
            expressionA != null ? expressionA.Length : 0,
            expressionB != null ? expressionB.Length : 0);

        Array.Clear(sampledExpression50, 0, sampledExpression50.Length);
        count = Mathf.Min(count, sampledExpression50.Length);
        for (int i = 0; i < count; ++i)
            sampledExpression50[i] = Mathf.LerpUnclamped(expressionA[i], expressionB[i], t);
    }

    Vector3 SampleJaw(GsacFaceTrajectoryFrame a, GsacFaceTrajectoryFrame b, float t)
    {
        Vector3 jawA = ArrayToVector3(a.jawPose);
        Vector3 jawB = ArrayToVector3(b.jawPose);
        Quaternion qa = SMPLX.QuatFromRodrigues(jawA.x, jawA.y, jawA.z);
        Quaternion qb = SMPLX.QuatFromRodrigues(jawB.x, jawB.y, jawB.z);
        Quaternion q = Quaternion.Slerp(qa, qb, t);
        return RodriguesFromUnitySmplxQuaternion(q);
    }

    static Vector2 SampleEyelid(GsacFaceTrajectoryFrame a, GsacFaceTrajectoryFrame b, float t)
    {
        Vector2 eyelidA = a.eyelid != null ? ArrayToVector2(a.eyelid) : Vector2.zero;
        Vector2 eyelidB = b.eyelid != null ? ArrayToVector2(b.eyelid) : Vector2.zero;
        return Vector2.LerpUnclamped(eyelidA, eyelidB, t);
    }

    static void CopyExpression(float[] source, float[] destination)
    {
        Array.Clear(destination, 0, destination.Length);
        if (source == null)
            return;

        int count = Mathf.Min(source.Length, destination.Length);
        for (int i = 0; i < count; ++i)
            destination[i] = source[i];
    }

    float GetLoopDuration()
    {
        if (trajectory != null && trajectory.durationSeconds > 0.0f)
            return trajectory.durationSeconds;

        return selectedEmotion != null ? selectedEmotion.LastFrameTime : 0.0f;
    }

    void AutoAssignReferences()
    {
        if (faceController == null)
            faceController = GetComponent<GsacFaceParameterController>();

        if (faceController == null)
            faceController = GetComponentInParent<GsacFaceParameterController>();

        if (faceController == null)
            faceController = GetComponentInChildren<GsacFaceParameterController>(true);
    }

    void SetStatus(string newStatus, bool warning)
    {
        status = newStatus;
        if (warning)
            Debug.LogWarning($"[GSAC Trajectory] {newStatus}", this);
        else
            Debug.Log($"[GSAC Trajectory] {newStatus}", this);
    }

    static Vector3 ArrayToVector3(float[] values)
    {
        if (values == null || values.Length < 3)
            return Vector3.zero;

        return new Vector3(values[0], values[1], values[2]);
    }

    static Vector2 ArrayToVector2(float[] values)
    {
        if (values == null || values.Length < 2)
            return Vector2.zero;

        return new Vector2(values[0], values[1]);
    }

    static Vector3 RodriguesFromUnitySmplxQuaternion(Quaternion quaternion)
    {
        quaternion = NormalizeQuaternion(quaternion);
        quaternion.ToAngleAxis(out float angleDegrees, out Vector3 unityAxis);

        if (angleDegrees > 180.0f)
            angleDegrees -= 360.0f;

        if (float.IsNaN(unityAxis.x) || unityAxis.sqrMagnitude < 1e-12f)
            return Vector3.zero;

        float angleRadians = angleDegrees * Mathf.Deg2Rad;
        return new Vector3(unityAxis.x, -unityAxis.y, -unityAxis.z) * angleRadians;
    }

    static Quaternion NormalizeQuaternion(Quaternion quaternion)
    {
        float length = Mathf.Sqrt(
            quaternion.x * quaternion.x +
            quaternion.y * quaternion.y +
            quaternion.z * quaternion.z +
            quaternion.w * quaternion.w);
        if (length <= 1e-8f)
            return Quaternion.identity;

        float invLength = 1.0f / length;
        return new Quaternion(
            quaternion.x * invLength,
            quaternion.y * invLength,
            quaternion.z * invLength,
            quaternion.w * invLength);
    }
}
