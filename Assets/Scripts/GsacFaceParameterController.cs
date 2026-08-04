using System;
using System.IO;
using UnityEngine;

[DisallowMultipleComponent]
[DefaultExecutionOrder(75)]
public sealed class GsacFaceParameterController : MonoBehaviour
{
    const int ExpressionCount = GsacFaceBasis.ExpectedExpressionCount;
    const int EyelidCount = GsacFaceBasis.ExpectedEyelidCount;

    [Header("Avatar References")]
    [SerializeField] PoseController poseController;
    [SerializeField] SMPLX smplx;
    [SerializeField] SkinnedMeshRenderer skinnedMeshRenderer;

    [Header("Optional Exact 50D Basis")]
    [SerializeField] TextAsset faceBasisBytes;
    [SerializeField] string faceBasisFilePath;
    [SerializeField] bool loadBasisOnStart = true;

    [Header("Inspector Debug Sample")]
    [SerializeField] bool applyInspectorValuesEveryFrame;
    [SerializeField] float[] inspectorExpression50 = new float[ExpressionCount];
    [SerializeField] Vector3 inspectorJawAxisAngleRadians;
    [SerializeField] Vector2 inspectorEyelid;
    [SerializeField] Vector3 openMouthSmokeJaw = new Vector3(0.15f, 0.0f, 0.0f);

    [Header("Runtime Status")]
    [SerializeField, TextArea(2, 5)] string status = "Not initialized.";

    readonly float[] currentExpression50 = new float[ExpressionCount];
    readonly float[] neutralExpression50 = new float[ExpressionCount];
    readonly float[] expressionScratch50 = new float[ExpressionCount];
    readonly int[] expressionIndices = new int[ExpressionCount];
    readonly int[] eyelidIndices = new int[EyelidCount];

    PoseController subscribedPoseController;
    Mesh cachedBlendShapeMesh;
    Transform jawJoint;
    Quaternion jawBindLocalRotation = Quaternion.identity;
    Vector3 currentJawAxisAngleRadians;
    Vector3 neutralJawAxisAngleRadians;
    Vector2 currentEyelid;
    Vector2 neutralEyelid;
    int availableExpressionControls;
    int availableEyelidControls;
    bool jawBindCached;
    bool warnedMissingBakeHook;

    public bool HasExactExpression50 => availableExpressionControls == ExpressionCount;
    public bool HasExpression10Fallback => HasFirstNExpressionControls(10);
    public bool HasEyelidControls => availableEyelidControls == EyelidCount;
    public string Status => status;

    void Reset()
    {
        AutoAssignReferences();
        EnsureInspectorArrayLength();
    }

    void OnValidate()
    {
        EnsureInspectorArrayLength();
        AutoAssignReferences();
    }

    void OnEnable()
    {
        EnsureArrays();
        AutoAssignReferences();
        SubscribeToPoseController();
    }

    void Start()
    {
        EnsureRuntimeReady(true);
        SubscribeToPoseController();

        if (loadBasisOnStart)
            TryLoadAndInstallBasis();
        else
            RefreshBlendShapeCache();

        ApplyCurrentParametersToRig();
        RequestImmediateBake();
    }

    void OnDisable()
    {
        if (subscribedPoseController != null)
            subscribedPoseController.BeforeVertexBake -= ApplyBeforeVertexBake;

        subscribedPoseController = null;
    }

    void Update()
    {
        if (applyInspectorValuesEveryFrame)
            ApplyInspectorParameters();
    }

    public FaceApplyResult ApplyAbsoluteFaceParameters(float[] expression50, Vector3 jawAxisAngleRadians, Vector2 eyelid)
    {
        if (!EnsureRuntimeReady(true))
            return FaceApplyResult.Failed("Face controller is missing SMPL-X, SkinnedMeshRenderer, or jaw references.");

        bool expressionValid = TryCopyExpression(expression50, currentExpression50, out string expressionMessage);
        bool jawValid = IsFinite(jawAxisAngleRadians);
        bool eyelidValid = IsFinite(eyelid);

        if (jawValid)
            currentJawAxisAngleRadians = jawAxisAngleRadians;

        if (eyelidValid)
            currentEyelid = eyelid;

        ApplyCurrentParametersToRig();
        RequestImmediateBake();

        string message = BuildResultMessage(expressionMessage, jawValid, eyelidValid);
        return new FaceApplyResult(
            expressionValid || jawValid || eyelidValid,
            jawValid && jawJoint != null,
            expressionValid && availableExpressionControls > 0,
            eyelidValid && availableEyelidControls > 0,
            HasExactExpression50,
            message);
    }

    public FaceApplyResult ApplyAbsoluteExpressionAndJaw(float[] expression, float[] jawPose, float[] eyelid)
    {
        Vector3 jaw = currentJawAxisAngleRadians;
        Vector2 eyelidVector = currentEyelid;

        bool jawValid = TryReadVector3(jawPose, out jaw);
        bool eyelidValid = TryReadVector2(eyelid, out eyelidVector);
        bool expressionValid = TryPrepareExpressionInput(expression, expressionScratch50, out string expressionMessage);

        if (!expressionValid)
            expressionMessage = $"Invalid expression array. {expressionMessage}";

        if (!expressionValid && !jawValid && !eyelidValid)
            return FaceApplyResult.Failed(AppendMessage(expressionMessage, "Invalid jawPose and eyelid arrays."));

        FaceApplyResult result = ApplyAbsoluteFaceParameters(
            expressionValid ? expressionScratch50 : currentExpression50,
            jawValid ? jaw : currentJawAxisAngleRadians,
            eyelidValid ? eyelidVector : currentEyelid);

        if (expressionValid && jawValid && eyelidValid)
            return result;

        string message = result.message;
        if (!expressionValid)
            message = AppendMessage(message, expressionMessage);
        if (!jawValid)
            message = AppendMessage(message, "Invalid jawPose; expected three finite floats.");
        if (!eyelidValid)
            message = AppendMessage(message, "Invalid eyelid; expected two finite floats.");

        result.message = message;
        result.expressionApplied = expressionValid && result.expressionApplied;
        result.jawApplied = jawValid && result.jawApplied;
        result.eyelidApplied = eyelidValid && result.eyelidApplied;
        result.success = result.jawApplied || result.expressionApplied || result.eyelidApplied;
        return result;
    }

    public void SetJawAxisAngleRadians(Vector3 jawAxisAngleRadians)
    {
        if (!IsFinite(jawAxisAngleRadians))
        {
            Debug.LogError("[GSAC Face] Jaw axis-angle must contain finite radians.", this);
            return;
        }

        if (!EnsureRuntimeReady(true))
            return;

        currentJawAxisAngleRadians = jawAxisAngleRadians;
        ApplyCurrentParametersToRig();
        RequestImmediateBake();
    }

    public void SetNeutralFace(float[] expression50, Vector3 jaw, Vector2 eyelid)
    {
        if (!TryCopyExpression(expression50, neutralExpression50, out string expressionMessage))
        {
            Debug.LogError($"[GSAC Face] Neutral expression was not accepted. {expressionMessage}", this);
            return;
        }

        if (!IsFinite(jaw) || !IsFinite(eyelid))
        {
            Debug.LogError("[GSAC Face] Neutral jaw and eyelid values must be finite.", this);
            return;
        }

        neutralJawAxisAngleRadians = jaw;
        neutralEyelid = eyelid;
    }

    public void ResetToNeutral()
    {
        Array.Copy(neutralExpression50, currentExpression50, ExpressionCount);
        currentJawAxisAngleRadians = neutralJawAxisAngleRadians;
        currentEyelid = neutralEyelid;
        ApplyCurrentParametersToRig();
        RequestImmediateBake();
    }

    public void RequestImmediateBake()
    {
        AutoAssignReferences();
        if (poseController != null)
        {
            poseController.RequestImmediateBake();
            return;
        }

        if (!warnedMissingBakeHook)
        {
            warnedMissingBakeHook = true;
            Debug.LogWarning("[GSAC Face] PoseController is not assigned; face parameters can move the SMPL-X rig, but Gaussian splats will not update until the avatar bake path is wired.", this);
        }
    }

    [ContextMenu("GSAC Face/Load Basis Now")]
    public bool TryLoadAndInstallBasis()
    {
        if (!EnsureRuntimeReady(true))
            return false;

        GsacFaceBasis basis = null;
        string error;
        string source = string.Empty;
        string textAssetError = null;

        if (faceBasisBytes != null)
        {
            source = faceBasisBytes.name;
            if (!GsacFaceBasis.TryLoad(faceBasisBytes.bytes, source, out basis, out error))
                textAssetError = error;
        }

        if (basis == null && !string.IsNullOrWhiteSpace(faceBasisFilePath))
        {
            source = faceBasisFilePath;
            if (!GsacFaceBasis.TryLoadFromFile(faceBasisFilePath, out basis, out error))
            {
                string prefix = string.IsNullOrEmpty(textAssetError)
                    ? string.Empty
                    : $"TextAsset also failed ({textAssetError}). ";
                SetStatus($"Exact basis unavailable: {prefix}{error}", true);
                RefreshBlendShapeCache();
                return false;
            }
        }

        if (basis == null)
        {
            RefreshBlendShapeCache();
            if (string.IsNullOrEmpty(textAssetError))
                SetStatus("No optional face basis assigned. Using any existing Exp000..Exp009 controls plus jaw.", false);
            else
                SetStatus($"Exact basis unavailable: {textAssetError}", true);
            return false;
        }

        if (!basis.TryInstallOn(skinnedMeshRenderer, out string message))
        {
            RefreshBlendShapeCache();
            SetStatus($"Exact basis not installed from {source}: {message}", true);
            return false;
        }

        RefreshBlendShapeCache();
        SetStatus($"{message} Active expressions={availableExpressionControls}/50, eyelids={availableEyelidControls}/2.", false);
        return HasExactExpression50;
    }

    [ContextMenu("GSAC Face/Apply Inspector Parameters")]
    public void ApplyInspectorParameters()
    {
        ApplyAbsoluteFaceParameters(inspectorExpression50, inspectorJawAxisAngleRadians, inspectorEyelid);
    }

    [ContextMenu("GSAC Face/Open Mouth Smoke Test")]
    public void OpenMouthSmokeTest()
    {
        SetJawAxisAngleRadians(openMouthSmokeJaw);
    }

    [ContextMenu("GSAC Face/Close Mouth")]
    public void CloseMouth()
    {
        SetJawAxisAngleRadians(Vector3.zero);
    }

    [ContextMenu("GSAC Face/Reset To Neutral")]
    public void ResetToNeutralContextMenu()
    {
        ResetToNeutral();
    }

    void ApplyBeforeVertexBake(PoseController controller)
    {
        ApplyCurrentParametersToRig();
    }

    void ApplyCurrentParametersToRig()
    {
        if (!EnsureRuntimeReady(false))
            return;

        RefreshBlendShapeCacheIfNeeded();
        ApplyExpressionWeights();
        ApplyEyelidWeights();
        ApplyJawRotation();

        if (smplx != null)
        {
            smplx.UpdatePoseCorrectives();
            smplx.UpdateJointPositions(false);
        }
    }

    void ApplyExpressionWeights()
    {
        if (skinnedMeshRenderer == null)
            return;

        for (int i = 0; i < ExpressionCount; ++i)
        {
            int index = expressionIndices[i];
            if (index >= 0)
                skinnedMeshRenderer.SetBlendShapeWeight(index, currentExpression50[i] * 100.0f);
        }
    }

    void ApplyEyelidWeights()
    {
        if (skinnedMeshRenderer == null)
            return;

        if (eyelidIndices[0] >= 0)
            skinnedMeshRenderer.SetBlendShapeWeight(eyelidIndices[0], currentEyelid.x * 100.0f);
        if (eyelidIndices[1] >= 0)
            skinnedMeshRenderer.SetBlendShapeWeight(eyelidIndices[1], currentEyelid.y * 100.0f);
    }

    void ApplyJawRotation()
    {
        if (jawJoint == null)
            return;

        if (!jawBindCached)
        {
            jawBindLocalRotation = jawJoint.localRotation;
            jawBindCached = true;
        }

        Quaternion absoluteJaw = SMPLX.QuatFromRodrigues(
            currentJawAxisAngleRadians.x,
            currentJawAxisAngleRadians.y,
            currentJawAxisAngleRadians.z);
        jawJoint.localRotation = jawBindLocalRotation * absoluteJaw;
    }

    void RefreshBlendShapeCacheIfNeeded()
    {
        Mesh mesh = skinnedMeshRenderer != null ? skinnedMeshRenderer.sharedMesh : null;
        if (mesh == cachedBlendShapeMesh)
            return;

        RefreshBlendShapeCache();
    }

    void RefreshBlendShapeCache()
    {
        EnsureArrays();

        for (int i = 0; i < ExpressionCount; ++i)
            expressionIndices[i] = -1;
        for (int i = 0; i < EyelidCount; ++i)
            eyelidIndices[i] = -1;

        availableExpressionControls = 0;
        availableEyelidControls = 0;
        cachedBlendShapeMesh = skinnedMeshRenderer != null ? skinnedMeshRenderer.sharedMesh : null;
        if (cachedBlendShapeMesh == null)
            return;

        for (int i = 0; i < ExpressionCount; ++i)
        {
            expressionIndices[i] = GsacFaceBasis.FindBlendShapeIndex(cachedBlendShapeMesh, GsacFaceBasis.ExpressionName(i));
            if (expressionIndices[i] >= 0)
                ++availableExpressionControls;
        }

        eyelidIndices[0] = GsacFaceBasis.FindBlendShapeIndex(cachedBlendShapeMesh, GsacFaceBasis.EyelidLeftName);
        eyelidIndices[1] = GsacFaceBasis.FindBlendShapeIndex(cachedBlendShapeMesh, GsacFaceBasis.EyelidRightName);
        availableEyelidControls = (eyelidIndices[0] >= 0 ? 1 : 0) + (eyelidIndices[1] >= 0 ? 1 : 0);
    }

    bool EnsureRuntimeReady(bool logErrors)
    {
        EnsureArrays();
        AutoAssignReferences();

        if (smplx == null)
        {
            if (logErrors)
                Debug.LogError("[GSAC Face] SMPLX reference is missing.", this);
            return false;
        }

        smplx.Awake();

        if (skinnedMeshRenderer == null)
            skinnedMeshRenderer = smplx.GetComponentInChildren<SkinnedMeshRenderer>(true);

        if (skinnedMeshRenderer == null)
        {
            if (logErrors)
                Debug.LogError("[GSAC Face] SkinnedMeshRenderer was not found under the SMPLX object.", this);
            return false;
        }

        if (jawJoint == null)
            jawJoint = FindChildByName(smplx.transform, "jaw");

        if (jawJoint == null)
        {
            if (logErrors)
                Debug.LogError("[GSAC Face] Jaw joint named 'jaw' was not found under the SMPLX object.", this);
            return false;
        }

        return true;
    }

    void AutoAssignReferences()
    {
        if (poseController == null)
            poseController = GetComponent<PoseController>();

        if (poseController == null)
            poseController = GetComponentInParent<PoseController>();

        if (poseController == null)
            poseController = GetComponentInChildren<PoseController>(true);

        if (smplx == null && poseController != null)
            smplx = poseController.smplx;

        if (smplx == null)
            smplx = GetComponent<SMPLX>();

        if (smplx == null)
            smplx = GetComponentInChildren<SMPLX>(true);

        if (skinnedMeshRenderer == null && smplx != null)
            skinnedMeshRenderer = smplx.GetComponentInChildren<SkinnedMeshRenderer>(true);
    }

    void SubscribeToPoseController()
    {
        AutoAssignReferences();

        if (subscribedPoseController == poseController)
            return;

        if (subscribedPoseController != null)
            subscribedPoseController.BeforeVertexBake -= ApplyBeforeVertexBake;

        subscribedPoseController = poseController;
        if (subscribedPoseController != null)
            subscribedPoseController.BeforeVertexBake += ApplyBeforeVertexBake;
    }

    bool HasFirstNExpressionControls(int count)
    {
        if (count <= 0 || count > ExpressionCount)
            return false;

        for (int i = 0; i < count; ++i)
        {
            if (expressionIndices[i] < 0)
                return false;
        }

        return true;
    }

    bool TryCopyExpression(float[] source, float[] destination, out string message)
    {
        message = string.Empty;

        if (source == null)
        {
            message = "expression50 is null; expected 50 finite floats.";
            return false;
        }

        if (source.Length != ExpressionCount)
        {
            message = $"expression50 length is {source.Length}; expected {ExpressionCount}.";
            return false;
        }

        for (int i = 0; i < ExpressionCount; ++i)
        {
            float value = source[i];
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                message = $"expression50[{i}] is not finite.";
                return false;
            }
        }

        Array.Copy(source, destination, ExpressionCount);
        return true;
    }

    bool TryPrepareExpressionInput(float[] source, float[] destination, out string message)
    {
        message = string.Empty;

        if (source == null)
        {
            message = "Expression array is null.";
            return false;
        }

        if (source.Length != ExpressionCount && source.Length != 10)
        {
            message = $"Expression length is {source.Length}; expected 50 or 10.";
            return false;
        }

        for (int i = 0; i < source.Length; ++i)
        {
            float value = source[i];
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                message = $"Expression value {i} is not finite.";
                return false;
            }
        }

        Array.Clear(destination, 0, destination.Length);
        Array.Copy(source, destination, source.Length);
        return true;
    }

    string BuildResultMessage(string expressionMessage, bool jawValid, bool eyelidValid)
    {
        string message = HasExactExpression50
            ? "Applied face sample with exact 50-expression support."
            : $"Applied face sample with {availableExpressionControls}/50 expression controls and {availableEyelidControls}/2 eyelid controls.";

        if (!string.IsNullOrEmpty(expressionMessage))
            message = AppendMessage(message, expressionMessage);
        if (!jawValid)
            message = AppendMessage(message, "Jaw value was rejected because it is not finite.");
        if (!eyelidValid)
            message = AppendMessage(message, "Eyelid value was rejected because it is not finite.");

        return message;
    }

    void EnsureArrays()
    {
        for (int i = 0; i < expressionIndices.Length; ++i)
        {
            if (cachedBlendShapeMesh == null)
                expressionIndices[i] = -1;
        }
    }

    void EnsureInspectorArrayLength()
    {
        if (inspectorExpression50 == null || inspectorExpression50.Length != ExpressionCount)
            inspectorExpression50 = new float[ExpressionCount];
    }

    void SetStatus(string newStatus, bool warning)
    {
        status = newStatus;
        if (warning)
            Debug.LogWarning($"[GSAC Face] {newStatus}", this);
        else
            Debug.Log($"[GSAC Face] {newStatus}", this);
    }

    static Transform FindChildByName(Transform root, string childName)
    {
        if (root == null)
            return null;

        Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < transforms.Length; ++i)
        {
            if (transforms[i].name == childName)
                return transforms[i];
        }

        return null;
    }

    static bool TryReadVector3(float[] source, out Vector3 value)
    {
        value = Vector3.zero;
        if (source == null || source.Length != 3)
            return false;

        value = new Vector3(source[0], source[1], source[2]);
        return IsFinite(value);
    }

    static bool TryReadVector2(float[] source, out Vector2 value)
    {
        value = Vector2.zero;
        if (source == null || source.Length != 2)
            return false;

        value = new Vector2(source[0], source[1]);
        return IsFinite(value);
    }

    static bool IsFinite(Vector3 value)
    {
        return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
    }

    static bool IsFinite(Vector2 value)
    {
        return IsFinite(value.x) && IsFinite(value.y);
    }

    static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    static string AppendMessage(string message, string addition)
    {
        if (string.IsNullOrEmpty(message))
            return addition;
        if (string.IsNullOrEmpty(addition))
            return message;
        return $"{message} {addition}";
    }
}
