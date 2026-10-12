// Visibility only: Scene anchors, chair poses, exports and GameObjects remain active.
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.XR;
#if ENABLE_INPUT_SYSTEM
using Keyboard = UnityEngine.InputSystem.Keyboard;
#endif

public sealed partial class QuestSavedRoomMesh
{
    const string MeshVisibilityPreference = "Aim2.RoomAndChairMeshesVisible";
    const string MeshVisibilityRevisionPreference = "Aim2.MeshVisibilityCommandRevision";
    [SerializeField] bool showMeshes = true;
    bool meshVisibilityInitialized, rightVisibilityButtonSeeded, rightVisibilityButtonPrevious, meshVisibilityInspectorDirty;
    InputDevice meshVisibilityRightHand;
    double nextMeshVisibilityPoll;
    string meshVisibilityControlPath, meshVisibilityStatusPath, lastMeshVisibilityRevision = "";
    string meshVisibilitySource = "saved_preference", meshVisibilityControlError = "";
    public bool MeshesVisible => showMeshes;

    void InitializeMeshVisibility()
    {
        if (meshVisibilityInitialized) return;
        meshVisibilityInitialized = true;
        showMeshes = PlayerPrefs.GetInt(MeshVisibilityPreference, showMeshes ? 1 : 0) != 0;
        lastMeshVisibilityRevision = PlayerPrefs.GetString(MeshVisibilityRevisionPreference, "");
        meshVisibilityControlPath = Path.Combine(Application.persistentDataPath, "mesh-visibility-control.json");
        meshVisibilityStatusPath = Path.Combine(Application.persistentDataPath, "mesh-visibility-status.json");
        ApplyMeshVisibility();
    }
    public void SetMeshesVisible(bool visible) => SetMeshVisibility(visible, "api_or_ui");
    public void ToggleMeshes() => SetMeshesVisible(!showMeshes);
    void SetMeshVisibility(bool visible, string source)
    {
        if (!meshVisibilityInitialized) InitializeMeshVisibility();
        showMeshes = visible; meshVisibilitySource = source;
        PlayerPrefs.SetInt(MeshVisibilityPreference, visible ? 1 : 0);
        PlayerPrefs.SetString(MeshVisibilityRevisionPreference, lastMeshVisibilityRevision);
        PlayerPrefs.Save();
        ApplyMeshVisibility();
    }
    void ApplyMeshVisibility()
    {
        foreach (var preview in previewObjects)
        {
            if (preview == null) continue;
            foreach (var renderer in preview.GetComponentsInChildren<MeshRenderer>(true)) renderer.enabled = showMeshes;
        }
        if (statusLabel != null)
        {
            var labelRenderer = statusLabel.GetComponent<MeshRenderer>();
            if (labelRenderer != null) labelRenderer.enabled = showMeshes;
        }
        WriteMeshVisibilityStatus();
    }
#if UNITY_EDITOR
    void OnValidate()
    {
        meshVisibilityInspectorDirty = true; // Apply Unity renderer changes on the next main-thread update.
    }
#endif
    void PollMeshVisibilityInput()
    {
        if (!meshVisibilityInitialized) InitializeMeshVisibility();
        if (meshVisibilityInspectorDirty) { meshVisibilityInspectorDirty = false; SetMeshVisibility(showMeshes, "runtime_inspector"); }
        if (Now >= nextMeshVisibilityPoll)
        {
            nextMeshVisibilityPoll = Now + 0.5;
            ReadMeshVisibilityCommand();
            WriteMeshVisibilityStatus();
        }
        bool focused = Application.isFocused && !paused;
#if UNITY_ANDROID && !UNITY_EDITOR
        focused = focused && OVRPlugin.hasVrFocus;
#endif
        if (!focused) { rightVisibilityButtonSeeded = false; return; }
        if (!meshVisibilityRightHand.isValid)
        {
            rightVisibilityButtonSeeded = false;
            meshVisibilityRightHand = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        }
        if (meshVisibilityRightHand.isValid && meshVisibilityRightHand.TryGetFeatureValue(CommonUsages.secondaryButton, out bool pressed))
        {
            if (rightVisibilityButtonSeeded && pressed && !rightVisibilityButtonPrevious) SetMeshVisibility(!showMeshes, "right_controller_b");
            rightVisibilityButtonPrevious = pressed; rightVisibilityButtonSeeded = true;
        }
        else rightVisibilityButtonSeeded = false;
#if UNITY_EDITOR || UNITY_STANDALONE
#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current != null && Keyboard.current.mKey.wasPressedThisFrame) SetMeshVisibility(!showMeshes, "keyboard_m");
#elif ENABLE_LEGACY_INPUT_MANAGER
        if (Input.GetKeyDown(KeyCode.M)) SetMeshVisibility(!showMeshes, "keyboard_m");
#endif
#endif
    }
    void ReadMeshVisibilityCommand()
    {
        try
        {
            if (!File.Exists(meshVisibilityControlPath)) return;
            if (new FileInfo(meshVisibilityControlPath).Length > 4096) throw new InvalidDataException("Visibility control must be at most 4096 bytes.");
            string json = File.ReadAllText(meshVisibilityControlPath).TrimStart('\uFEFF');
            // Exact two-field JSON schema, either order. Missing/duplicate bools,
            // nested values, strings in place of bools and trailing junk are rejected.
            const string stringValue = @"""(?:[^""\\\x00-\x1F]|\\(?:[""\\/bfnrt]|u[0-9a-fA-F]{4}))*""";
            const string visibleField = @"""visible""\s*:\s*(?:true|false)";
            const string revisionField = @"""revision""\s*:\s*" + stringValue;
            string schema = @"\A\s*\{\s*(?:" + visibleField + @"\s*,\s*" + revisionField + "|" + revisionField + @"\s*,\s*" + visibleField + @")\s*\}\s*\z";
            if (!Regex.IsMatch(json, schema)) throw new InvalidDataException("Control requires exactly visible:bool and revision:string.");
            var command = JsonUtility.FromJson<MeshVisibilityCommand>(json);
            if (command == null || string.IsNullOrWhiteSpace(command.revision) || command.revision.Length > 128)
                throw new InvalidDataException("Visibility command revision must be nonempty and at most 128 characters.");
            meshVisibilityControlError = "";
            if (command.revision == lastMeshVisibilityRevision) return;
            lastMeshVisibilityRevision = command.revision;
            SetMeshVisibility(command.visible, "control_file");
        }
        catch (Exception error) { meshVisibilityControlError = error.Message; }
    }
    void WriteMeshVisibilityStatus()
    {
        if (string.IsNullOrEmpty(meshVisibilityStatusPath)) return;
        try
        {
            int count = 0, enabled = 0;
            foreach (var preview in previewObjects)
            {
                if (preview == null) continue;
                foreach (var renderer in preview.GetComponentsInChildren<MeshRenderer>(true)) { count++; if (renderer.enabled) enabled++; }
            }
            var state = new MeshVisibilityStatus {
                visible = showMeshes, rendererCount = count, enabledRendererCount = enabled,
                lastCommandRevision = lastMeshVisibilityRevision, source = meshVisibilitySource,
                controlError = meshVisibilityControlError, updatedUtc = DateTime.UtcNow.ToString("o")
            };
            File.WriteAllText(meshVisibilityStatusPath + ".tmp", JsonUtility.ToJson(state, true), new UTF8Encoding(false));
            if (File.Exists(meshVisibilityStatusPath)) File.Delete(meshVisibilityStatusPath);
            File.Move(meshVisibilityStatusPath + ".tmp", meshVisibilityStatusPath);
        }
        catch (Exception error) { Debug.LogWarning("MESH_VISIBILITY status write failed: " + error.Message); }
    }
    [Serializable] sealed class MeshVisibilityCommand { public bool visible; public string revision; }
    [Serializable] sealed class MeshVisibilityStatus
    {
        public bool visible;
        public int rendererCount, enabledRendererCount;
        public string lastCommandRevision, source, controlError, updatedUtc;
    }
}
