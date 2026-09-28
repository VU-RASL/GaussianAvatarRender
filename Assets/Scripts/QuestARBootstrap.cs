using System;
using System.Collections;
using System.Collections.Generic;
using Meta.XR.EnvironmentDepth;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.Management;

[DefaultExecutionOrder(-9000)]
public sealed class QuestARBootstrap : MonoBehaviour
{
    static readonly string[] s_DefaultObjectsToHide = { "Sphere", "Plane", "Plane (1)", "Cube" };
    static readonly HashSet<string> s_WarnedMissingTypes = new();
    static readonly int s_DepthBiasId = Shader.PropertyToID("_GsacEnvironmentDepthBias");
    static bool s_XRSubsystemsStarted;
    static bool s_ScenePermissionRequested;

    readonly List<MaterialReplacement> materialReplacements = new();
    readonly HashSet<Renderer> replacedRenderers = new();
    Coroutine setupRoutine;
    Coroutine depthStartRoutine;
    bool sceneSetupInProgress;
    bool sceneSetupComplete;
    GameObject depthRuntimeRoot;
    OVRCameraRig depthCameraRig;
    OVRManager ovrManager;
    EnvironmentDepthManager depthManager;
    Transform trackingSpaceSource;
    bool useOculusProfile;
    bool depthRequested;
    bool previousDepthAvailable;
    bool reportedDepthTimeout;
    bool isARMode;
    bool floorOriginApplied;
    bool applicationPaused;
    bool applicationFocused = true;
    bool ownsEyeAlphaMode;
    bool previousEyeAlphaMode;
    float depthStartedAt;
    float nextCameraConfigureTime;

    sealed class MaterialReplacement
    {
        public Renderer renderer;
        public Material[] originals;
        public Material[] instances;
    }

    public bool EnvironmentDepthEnabled => depthRequested;
    public bool EnvironmentDepthAvailable => CanUseDepthNow && depthManager != null && depthManager.IsDepthAvailable;
    bool CanUseDepthNow => isARMode && useOculusProfile && depthRequested &&
                           !applicationPaused && applicationFocused;

    // Inspect the selected loader before XR startup as well, so scene Awake cannot
    // briefly start ARFoundation managers in an Oculus depth build.
    public static bool IsOculusLoaderConfigured()
    {
        XRManagerSettings manager = XRGeneralSettings.Instance?.Manager;
        if (manager == null)
            return false;
        if (manager.activeLoader != null)
            return manager.activeLoader.GetType().FullName == "Unity.XR.Oculus.OculusLoader";
        var loaders = manager.activeLoaders;
        return loaders.Count > 0 && loaders[0] != null &&
               loaders[0].GetType().FullName == "Unity.XR.Oculus.OculusLoader";
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (GeneralOperator.GetSceneMode() != GeneralBuildMode.AR ||
            FindObjectOfType<QuestARBootstrap>() != null)
            return;
        var root = new GameObject("Quest AR Bootstrap");
        DontDestroyOnLoad(root);
        root.AddComponent<QuestARBootstrap>();
#endif
    }

    void Start()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        QueueSceneSetup();
#endif
    }

    void OnEnable()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        SceneManager.sceneLoaded += OnSceneLoaded;
        ConfigureEyeAlphaMode();
#endif
    }

    void OnDisable()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        SceneManager.sceneLoaded -= OnSceneLoaded;
#endif
        ReleaseDepthManager();
        RestoreEyeAlphaMode();
    }

    void OnDestroy()
    {
        RestoreEyeAlphaMode();
        RestoreOrdinaryMaterials();
    }

    void OnApplicationPause(bool paused)
    {
        applicationPaused = paused;
        if (!paused && applicationFocused)
            ConfigureEyeAlphaMode();
        if (!TryResumeSceneSetup())
            RefreshDepthLifecycle();
    }

    void OnApplicationFocus(bool focused)
    {
        applicationFocused = focused;
        if (focused && !applicationPaused)
            ConfigureEyeAlphaMode();
        if (!TryResumeSceneSetup())
            RefreshDepthLifecycle();
    }

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        QueueSceneSetup();
#endif
    }

    bool TryResumeSceneSetup()
    {
        if (!isActiveAndEnabled || applicationPaused || !applicationFocused ||
            !isARMode || !useOculusProfile || sceneSetupInProgress)
            return false;
        if (sceneSetupComplete && depthRuntimeRoot != null && depthCameraRig != null && trackingSpaceSource != null)
            return false;
        // A delayed XR startup may have timed out before the passthrough rig
        // existed. Retry the whole setup on resume, keeping the A/B selection.
        QueueSceneSetup(true);
        return true;
    }

    void QueueSceneSetup(bool preserveDepthRequest = false)
    {
        if (setupRoutine != null)
            StopCoroutine(setupRoutine);
        sceneSetupInProgress = true;
        sceneSetupComplete = false;
        var routine = StartCoroutine(ConfigureScene(preserveDepthRequest));
        // An immediate early exit can run before StartCoroutine returns.
        setupRoutine = sceneSetupInProgress ? routine : null;
    }

    void FinishSceneSetup(bool completed)
    {
        sceneSetupComplete = completed;
        sceneSetupInProgress = false;
        setupRoutine = null;
    }

    IEnumerator ConfigureScene(bool preserveDepthRequest)
    {
        ReleaseDepthManager();
        RestoreOrdinaryMaterials();
        isARMode = GeneralOperator.GetSceneMode() == GeneralBuildMode.AR;
        if (!isARMode)
        {
            RestoreEyeAlphaMode();
            SetEnvironmentDepthEnabled(false);
            if (depthRuntimeRoot != null)
                depthRuntimeRoot.SetActive(false);
            FinishSceneSetup(false);
            yield break;
        }

        useOculusProfile = IsOculusLoaderConfigured();
        if (!useOculusProfile)
            RestoreEyeAlphaMode();
        depthRequested = useOculusProfile && (preserveDepthRequest
            ? depthRequested : GeneralOperator.GetSceneEnvironmentDepthEnabled());
        Shader.SetGlobalFloat(s_DepthBiasId, 0.0f);
        if (useOculusProfile)
            DisableARFoundationManagers();
        else
            EnsureComponent("UnityEngine.XR.ARFoundation.ARSession, Unity.XR.ARFoundation", null);
        ConfigureCameras();
        HideTestEnvironment();

        yield return EnsureXRLoaderStarted();
        XRLoader loader = XRGeneralSettings.Instance?.Manager?.activeLoader;
        if (loader == null)
        {
            FinishSceneSetup(false);
            yield break;
        }

        if (useOculusProfile)
        {
            if (!IsOculusLoaderConfigured())
            {
                Debug.LogError("GSAC depth profile requires Oculus XR 4.2. No environment depth was started.");
                FinishSceneSetup(false);
                yield break;
            }
            var display = loader.GetLoadedSubsystem<XRDisplaySubsystem>();
            for (int frame = 0; frame < 180 && (display == null || !display.running); ++frame)
            {
                yield return null;
                display = loader.GetLoadedSubsystem<XRDisplaySubsystem>();
            }
            if (display == null || !display.running)
            {
                Debug.LogError("GSAC depth could not start: the XR display is not running.");
                FinishSceneSetup(false);
                yield break;
            }

            ConfigureOculusPassthroughAndRig();
            yield return null;
            ConfigureEyeAlphaMode();
            if (depthCameraRig == null || trackingSpaceSource == null)
            {
                FinishSceneSetup(false);
                yield break;
            }
            SyncTrackingSpace();

            ApplyDepthOcclusionShaderToOrdinaryObjects();
            RefreshDepthLifecycle();
            Debug.Log("GSAC Oculus passthrough profile configured. Environment depth requested=" + depthRequested +
          (depthRequested ? "; waiting for SDK depth frames." : "; depth acquisition is disabled.") +
          " Soft occlusion and hand inclusion are configured when depth is requested. " +
          "Ground placement uses XR floor height rather than detected AR planes.");
        }
        else
        {
            DisableEnvironmentOcclusionManagers();
            RestartEnabledBehaviours(typeof(ARSession));
            RestartEnabledBehaviours(typeof(ARCameraManager));
            RestartEnabledBehaviours(typeof(ARCameraBackground));
            RestartEnabledBehaviours(typeof(ARPlaneManager));
            RestartEnabledBehaviours(typeof(ARRaycastManager));
            Debug.Log("Quest AR OpenXR passthrough baseline configured; environment depth is disabled.");
        }

        RestartEnabledBehaviours(typeof(ARGroundPlacement));
        FinishSceneSetup(true);
    }

    IEnumerator EnsureXRLoaderStarted()
    {
        XRManagerSettings manager = XRGeneralSettings.Instance?.Manager;
        if (manager == null)
        {
            Debug.LogError("Quest AR could not find XR Manager Settings.");
            yield break;
        }
        if (manager.activeLoader == null)
            yield return manager.InitializeLoader();
        if (manager.activeLoader == null)
        {
            Debug.LogError("Quest AR could not initialize the selected Android XR loader.");
            yield break;
        }
        if (!s_XRSubsystemsStarted)
        {
            manager.StartSubsystems();
            s_XRSubsystemsStarted = true;
        }
    }

    void ConfigureOculusPassthroughAndRig()
    {
        var origin = FindObjectOfType<XROrigin>(true);
        trackingSpaceSource = origin != null && origin.CameraFloorOffsetObject != null
            ? origin.CameraFloorOffsetObject.transform
            : origin != null ? origin.transform : null;
        if (trackingSpaceSource == null)
        {
            Debug.LogError("GSAC depth could not find the XR Origin tracking space. Depth was not enabled.");
            return;
        }

        if (depthRuntimeRoot == null)
        {
            depthRuntimeRoot = new GameObject("Quest Meta Depth Runtime");
            depthRuntimeRoot.SetActive(false);
            depthRuntimeRoot.transform.SetParent(transform, false);
        }

        ovrManager = FindObjectOfType<OVRManager>(true);
        if (ovrManager == null)
            ovrManager = depthRuntimeRoot.AddComponent<OVRManager>();
        ovrManager.isInsightPassthroughEnabled = true;
        // Keep the existing single-sample Gaussian target and original quality.
        // The SDK default otherwise raises MSAA to 4 and breaks depth attachment compatibility.
        ovrManager.useRecommendedMSAALevel = false;
        floorOriginApplied = false;

        var passthrough = FindObjectOfType<OVRPassthroughLayer>(true);
        if (passthrough == null)
            passthrough = depthRuntimeRoot.AddComponent<OVRPassthroughLayer>();
        passthrough.overlayType = OVROverlay.OverlayType.Underlay;
        passthrough.enabled = true;

        depthCameraRig = FindObjectOfType<OVRCameraRig>(true);
        if (depthCameraRig == null)
            depthCameraRig = depthRuntimeRoot.AddComponent<OVRCameraRig>();
        depthCameraRig.disableEyeAnchorCameras = true;
        depthRuntimeRoot.SetActive(true);
        depthCameraRig.EnsureGameObjectIntegrity();
        foreach (var rigCamera in depthCameraRig.GetComponentsInChildren<Camera>(true))
        {
            rigCamera.enabled = false;
            rigCamera.tag = "Untagged";
        }
        SyncTrackingSpace();
    }

    void ConfigureEyeAlphaMode()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!isActiveAndEnabled || !isARMode || !useOculusProfile ||
            depthRuntimeRoot == null || !depthRuntimeRoot.activeInHierarchy ||
            !OVRManager.OVRManagerinitialized)
            return;

        // Our eye buffer contains premultiplied transparency from both ordinary
        // soft-occluded objects and the Gaussian composite. The compositor must
        // not multiply it by alpha a second time when blending over passthrough.
        // This is an eye-layer setting, independent of OVROverlay texture flags.
        bool before = OVRManager.eyeFovPremultipliedAlphaModeEnabled;
        if (ownsEyeAlphaMode && before)
            return;
        if (!ownsEyeAlphaMode)
        {
            previousEyeAlphaMode = before;
            ownsEyeAlphaMode = true;
        }
        OVRManager.eyeFovPremultipliedAlphaModeEnabled = true;
        bool after = OVRManager.eyeFovPremultipliedAlphaModeEnabled;
        Debug.Log("GSAC Oculus eye alpha mode: reported before=" + before +
            ", requested premultiplied=true, reported after=" + after +
            ", OVRPlugin=" + OVRPlugin.version + ".");
        if (!after)
            Debug.LogWarning("GSAC Oculus compositor did not report premultiplied eye alpha; soft occlusion edges need verification.");
#endif
    }

    void RestoreEyeAlphaMode()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!ownsEyeAlphaMode)
            return;
        OVRManager.eyeFovPremultipliedAlphaModeEnabled = previousEyeAlphaMode;
        Debug.Log("GSAC Oculus eye alpha mode restored: requested=" + previousEyeAlphaMode +
            ", reported=" + OVRManager.eyeFovPremultipliedAlphaModeEnabled + ".");
        ownsEyeAlphaMode = false;
#endif
    }

    // Called before SDK67's default-order Update. Cached references avoid scene
    // searches or allocations during the per-frame tracking-space correction.
    void Update()
    {
        if (useOculusProfile)
        {
            // SDK67 ignores trackingOriginType assignments before an HMD is
            // present. Apply it after initialization, then leave tracking alone.
            if (!floorOriginApplied && ovrManager != null && OVRManager.isHmdPresent)
            {
                ovrManager.trackingOriginType = OVRManager.TrackingOrigin.FloorLevel;
                floorOriginApplied = ovrManager.trackingOriginType == OVRManager.TrackingOrigin.FloorLevel;
            }
            SyncTrackingSpace();
        }
    }

    void SyncTrackingSpace()
    {
        if (trackingSpaceSource == null || depthCameraRig == null || depthCameraRig.trackingSpace == null)
            return;
        Transform target = depthCameraRig.trackingSpace;
        target.SetPositionAndRotation(trackingSpaceSource.position, trackingSpaceSource.rotation);
        target.localScale = trackingSpaceSource.lossyScale;
    }

    void LateUpdate()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!isARMode)
            return;
        if (!useOculusProfile)
        {
            if (Time.unscaledTime >= nextCameraConfigureTime)
            {
                nextCameraConfigureTime = Time.unscaledTime + 1.0f;
                ConfigureCameras();
            }
            return;
        }
        if (depthManager == null || !depthRequested)
            return;
        bool available = depthManager.IsDepthAvailable;
        if (available != previousDepthAvailable)
        {
            previousDepthAvailable = available;
            Debug.Log(available
                ? "GSAC environment depth is receiving frames. Real-world occlusion is active."
                : "GSAC environment depth frames are temporarily unavailable.");
        }
        else if (!available && !reportedDepthTimeout && Time.unscaledTime - depthStartedAt > 15.0f)
        {
            reportedDepthTimeout = true;
            Debug.LogWarning("GSAC depth has not received a frame. Check spatial-data permission, headset support and runtime logs; real-world occlusion is not verified.");
        }
#endif
    }

    // A/B comparison keeps the same XR loader, passthrough, avatar and materials.
    // Disabling the SDK manager also stops requesting depth textures.
    public void SetEnvironmentDepthEnabled(bool enabled)
    {
        depthRequested = useOculusProfile && enabled;
        if (!TryResumeSceneSetup())
            RefreshDepthLifecycle();
        Debug.Log(depthRequested ? "GSAC environment depth enabled." : "GSAC environment depth disabled for A/B comparison.");
    }

    void RefreshDepthLifecycle()
    {
        if (!CanUseDepthNow || !isActiveAndEnabled)
        {
            ReleaseDepthManager();
            return;
        }
        if (depthManager == null && depthStartRoutine == null && depthRuntimeRoot != null &&
            depthRuntimeRoot.activeInHierarchy && depthCameraRig != null && trackingSpaceSource != null)
            depthStartRoutine = StartCoroutine(CreateDepthManagerWhenReady());
    }

    IEnumerator CreateDepthManagerWhenReady()
    {
        // SDK67 retains _prevTextureId after disable. Recreate our owned manager
        // after destruction has finished so resumed native texture IDs cannot
        // leave IsDepthAvailable false, and its singleton assertion stays valid.
        yield return null;
        bool ready = false;
        for (int frame = 0; frame < 180 && CanUseDepthNow; ++frame)
        {
            var loader = XRGeneralSettings.Instance?.Manager?.activeLoader;
            var display = loader != null ? loader.GetLoadedSubsystem<XRDisplaySubsystem>() : null;
            ready = display != null && display.running && OVRManager.OVRManagerinitialized &&
                    EnvironmentDepthManager.IsSupported;
            if (ready)
                break;
            yield return null;
        }
        if (!CanUseDepthNow)
        {
            depthStartRoutine = null;
            yield break;
        }
        if (!ready)
        {
            depthStartRoutine = null;
            Debug.LogWarning("GSAC environment depth is unavailable on this headset/runtime. Passthrough and virtual-object rendering remain active.");
            yield break;
        }
        if (FindObjectOfType<EnvironmentDepthManager>(true) != null)
        {
            depthStartRoutine = null;
            Debug.LogError("GSAC depth requires a single environment-depth owner. Another EnvironmentDepthManager already exists; no duplicate was created.");
            yield break;
        }

        // Oculus XR 4.2 rejects depth texture creation in MultiPass. Stop before
        // allocating the native provider so a mismatched build cannot spam a
        // failed depth request on every render frame.
        if (XRSettings.stereoRenderingMode == XRSettings.StereoRenderingMode.MultiPass)
        {
            depthStartRoutine = null;
            Debug.LogError("GSAC environment depth requires Oculus Multiview. Rebuild with the depth profile; no depth provider was started.");
            yield break;
        }

        SyncTrackingSpace();
        var depthObject = new GameObject("Environment Depth");
        depthObject.SetActive(false);
        depthObject.transform.SetParent(depthRuntimeRoot.transform, false);
        depthManager = depthObject.AddComponent<EnvironmentDepthManager>();
        depthManager.OcclusionShadersMode = OcclusionShadersMode.SoftOcclusion;
        depthManager.RemoveHands = false;
        depthObject.SetActive(true);
        depthStartedAt = Time.unscaledTime;
        previousDepthAvailable = false;
        reportedDepthTimeout = false;
        depthStartRoutine = null;
        RequestScenePermissionIfNeeded();
    }

    void ReleaseDepthManager()
    {
        if (depthStartRoutine != null)
        {
            StopCoroutine(depthStartRoutine);
            depthStartRoutine = null;
        }
        if (depthManager != null)
        {
            depthManager.enabled = false;
            Destroy(depthManager.gameObject);
            depthManager = null;
        }
        previousDepthAvailable = false;
    }

    void ConfigureCameras()
    {
        foreach (var camera in Camera.allCameras)
        {
            if (camera == null || camera.targetTexture != null)
                continue;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0, 0, 0, 0);
            if (!useOculusProfile)
            {
                EnsureComponent("UnityEngine.XR.ARFoundation.ARCameraManager, Unity.XR.ARFoundation", camera.gameObject);
                EnsureComponent("UnityEngine.XR.ARFoundation.ARCameraBackground, Unity.XR.ARFoundation", camera.gameObject);
            }
        }
        DisableEnvironmentOcclusionManagers();
    }

    static void DisableARFoundationManagers()
    {
        SetBehavioursEnabled<ARSession>(false);
        SetBehavioursEnabled<ARCameraManager>(false);
        SetBehavioursEnabled<ARCameraBackground>(false);
        SetBehavioursEnabled<ARPlaneManager>(false);
        SetBehavioursEnabled<ARRaycastManager>(false);
        DisableEnvironmentOcclusionManagers();
    }

    static void SetBehavioursEnabled<T>(bool enabled) where T : Behaviour
    {
        foreach (var behaviour in FindObjectsOfType<T>(true))
            behaviour.enabled = enabled;
    }

    static void DisableEnvironmentOcclusionManagers()
    {
        foreach (var manager in FindObjectsOfType<AROcclusionManager>(true))
        {
            manager.requestedEnvironmentDepthMode = EnvironmentDepthMode.Disabled;
            manager.requestedOcclusionPreferenceMode = OcclusionPreferenceMode.NoOcclusion;
            manager.enabled = false;
        }
    }

    void ApplyDepthOcclusionShaderToOrdinaryObjects()
    {
        Shader shader = Shader.Find("GSAC/Quest Depth Occluded Color");
        if (shader == null)
        {
            Debug.LogError("GSAC ordinary-object depth shader is missing from the build.");
            return;
        }
        ApplyDepthOcclusionShader(GameObject.Find("Sphere (1)"), shader);
        foreach (var ball in FindObjectsOfType<GrabbableTestBall>(true))
            ApplyDepthOcclusionShader(ball.gameObject, shader);
    }

    void ApplyDepthOcclusionShader(GameObject root, Shader shader)
    {
        if (root == null)
            return;
        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            // Keep controller rays, UI and skinned proxy materials intact.
            if (!(renderer is MeshRenderer))
                continue;
            if (!replacedRenderers.Add(renderer))
                continue;
            Material[] originals = renderer.sharedMaterials;
            var instances = new Material[originals.Length];
            for (int i = 0; i < originals.Length; ++i)
            {
                if (originals[i] == null)
                    continue;
                instances[i] = new Material(originals[i]) { shader = shader, name = originals[i].name + " (Quest depth runtime)" };
                instances[i].SetFloat("_EnvironmentDepthBias", 0.0f);
            }
            renderer.sharedMaterials = instances;
            materialReplacements.Add(new MaterialReplacement { renderer = renderer, originals = originals, instances = instances });
        }
    }

    void RestoreOrdinaryMaterials()
    {
        foreach (var replacement in materialReplacements)
        {
            if (replacement.renderer != null)
                replacement.renderer.sharedMaterials = replacement.originals;
            foreach (var instance in replacement.instances)
                if (instance != null)
                    Destroy(instance);
        }
        materialReplacements.Clear();
        replacedRenderers.Clear();
    }

    static void RequestScenePermissionIfNeeded()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        const string permission = "com.oculus.permission.USE_SCENE";
        if (!s_ScenePermissionRequested && !UnityEngine.Android.Permission.HasUserAuthorizedPermission(permission))
        {
            s_ScenePermissionRequested = true;
            UnityEngine.Android.Permission.RequestUserPermission(permission);
        }
#endif
    }

    void HideTestEnvironment()
    {
        foreach (string objectName in s_DefaultObjectsToHide)
        {
            var root = GameObject.Find(objectName);
            if (root != null)
                root.SetActive(false);
        }
    }

    static Component EnsureComponent(string typeName, GameObject target)
    {
        Type type = Type.GetType(typeName);
        if (type == null || !typeof(Component).IsAssignableFrom(type))
        {
            if (s_WarnedMissingTypes.Add(typeName))
                Debug.LogWarning($"Quest AR setup could not find component type '{typeName}'.");
            return null;
        }
        if (target == null)
        {
            var existing = FindObjectOfType(type) as Component;
            if (existing != null)
                return existing;
            target = new GameObject(type.Name);
        }
        return target.GetComponent(type) ?? target.AddComponent(type);
    }

    static void RestartEnabledBehaviours(Type type)
    {
        foreach (var obj in FindObjectsOfType(type, true))
        {
            var behaviour = (Behaviour)obj;
            behaviour.enabled = false;
            behaviour.enabled = true;
        }
    }
}
