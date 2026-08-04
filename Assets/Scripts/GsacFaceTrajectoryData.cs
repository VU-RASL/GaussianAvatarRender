using System;

[Serializable]
public sealed class GsacFaceTrajectoryData
{
    public string format;
    public float fps;
    public int frameCount;
    public float durationSeconds;
    public GsacFaceTrajectoryEmotion[] emotions;

    public bool Validate(out string message)
    {
        message = string.Empty;

        if (format != "gsac_unity_emotion_trajectory_v1")
        {
            message = $"Unsupported trajectory format '{format}'.";
            return false;
        }

        if (!IsFinite(fps) || fps <= 0.0f)
        {
            message = "Trajectory fps must be finite and positive.";
            return false;
        }

        if (frameCount <= 0)
        {
            message = "Trajectory frameCount must be positive.";
            return false;
        }

        if (emotions == null || emotions.Length == 0)
        {
            message = "Trajectory contains no emotions.";
            return false;
        }

        for (int emotionIndex = 0; emotionIndex < emotions.Length; ++emotionIndex)
        {
            GsacFaceTrajectoryEmotion emotion = emotions[emotionIndex];
            if (emotion == null)
            {
                message = $"Emotion {emotionIndex} is null.";
                return false;
            }

            if (!emotion.Validate(frameCount, out message))
            {
                message = $"Emotion '{emotion.name}' failed validation: {message}";
                return false;
            }
        }

        return true;
    }

    public GsacFaceTrajectoryEmotion FindEmotion(string emotionName)
    {
        if (emotions == null || emotions.Length == 0)
            return null;

        if (string.IsNullOrWhiteSpace(emotionName))
            return emotions[0];

        for (int i = 0; i < emotions.Length; ++i)
        {
            if (string.Equals(emotions[i].name, emotionName, StringComparison.OrdinalIgnoreCase))
                return emotions[i];
        }

        return null;
    }

    static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
}

[Serializable]
public sealed class GsacFaceTrajectoryEmotion
{
    public string name;
    public GsacFaceTrajectoryFrame[] frames;

    public float LastFrameTime
    {
        get
        {
            if (frames == null || frames.Length == 0 || frames[frames.Length - 1] == null)
                return 0.0f;
            return frames[frames.Length - 1].timeSeconds;
        }
    }

    public bool Validate(int expectedFrameCount, out string message)
    {
        message = string.Empty;

        if (string.IsNullOrWhiteSpace(name))
        {
            message = "Emotion name is empty.";
            return false;
        }

        if (frames == null || frames.Length == 0)
        {
            message = "Emotion has no frames.";
            return false;
        }

        if (expectedFrameCount > 0 && frames.Length != expectedFrameCount)
        {
            message = $"Emotion has {frames.Length} frames; expected {expectedFrameCount}.";
            return false;
        }

        float previousTime = -1.0f;
        for (int i = 0; i < frames.Length; ++i)
        {
            GsacFaceTrajectoryFrame frame = frames[i];
            if (frame == null)
            {
                message = $"Frame {i} is null.";
                return false;
            }

            if (!frame.Validate(out message))
            {
                message = $"Frame {i}: {message}";
                return false;
            }

            if (frame.timeSeconds < previousTime)
            {
                message = $"Frame {i} time is earlier than the previous frame.";
                return false;
            }

            previousTime = frame.timeSeconds;
        }

        return true;
    }
}

[Serializable]
public sealed class GsacFaceTrajectoryFrame
{
    public int index;
    public float timeSeconds;
    public string phase;
    public float[] expression50;
    public float[] expression10Fallback;
    public float[] jawPose;
    public float[] eyelid;

    public bool Validate(out string message)
    {
        message = string.Empty;

        if (!IsFinite(timeSeconds))
        {
            message = "timeSeconds is not finite.";
            return false;
        }

        bool hasExpression50 = IsFiniteArray(expression50, GsacFaceBasis.ExpectedExpressionCount);
        bool hasExpression10 = IsFiniteArray(expression10Fallback, 10);
        if (!hasExpression50 && !hasExpression10)
        {
            message = "Frame must contain finite expression50[50] or expression10Fallback[10].";
            return false;
        }

        if (!IsFiniteArray(jawPose, 3))
        {
            message = "Frame jawPose must contain three finite floats.";
            return false;
        }

        if (eyelid != null && !IsFiniteArray(eyelid, 2))
        {
            message = "Frame eyelid must contain two finite floats when present.";
            return false;
        }

        return true;
    }

    public float[] PreferredExpression()
    {
        if (IsFiniteArray(expression50, GsacFaceBasis.ExpectedExpressionCount))
            return expression50;
        return expression10Fallback;
    }

    static bool IsFiniteArray(float[] values, int expectedLength)
    {
        if (values == null || values.Length != expectedLength)
            return false;

        for (int i = 0; i < values.Length; ++i)
        {
            if (!IsFinite(values[i]))
                return false;
        }

        return true;
    }

    static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
