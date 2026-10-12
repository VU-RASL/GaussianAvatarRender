using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.XR;

[DefaultExecutionOrder(200)]
public sealed class ARPoseControlPanel : MonoBehaviour
{
    enum PoseAction
    {
        Clean,
        TAPose,
        Pose1,
        ToggleMeshes,
    }

    [SerializeField] PoseController poseController;
    [SerializeField] Camera arCamera;
    [SerializeField] float followDistance = 1.0f;
    [SerializeField] float verticalOffset = 0.05f;
    [SerializeField] float followSpeed = 10.0f;
    [SerializeField] float interactionDistance = 8.0f;
    [SerializeField] bool showPointerRay = true;
    [SerializeField] float pointerRayLength = 2.5f;
    [SerializeField] float pointerDotSize = 0.025f;

    readonly Dictionary<Button, Image> buttonImages = new();
    readonly List<Texture2D> labelTextures = new();
    GameObject panelRoot;
    GameObject pointerRoot;
    LineRenderer pointerLine;
    Transform pointerDot;
    Material pointerMaterial;
    Button hoveredButton;
    Button pressedButton;
    QuestSavedRoomMesh savedRoomMesh;

    static readonly Color PanelColor = new(0.05f, 0.06f, 0.07f, 0.55f);
    static readonly Color ButtonColor = new(0.16f, 0.23f, 0.29f, 0.88f);
    static readonly Color HoverColor = new(0.05f, 0.5f, 0.8f, 0.95f);
    static readonly Color TextColor = new(0.94f, 0.96f, 0.98f, 1.0f);
    static readonly Color PointerColor = new(0.05f, 0.75f, 1.0f, 0.9f);
    static readonly Color PointerHitColor = new(0.2f, 1.0f, 0.45f, 1.0f);

    void OnEnable()
    {
        EnsureReferences();
        EnsureEventSystem();
        BuildPanel();
        BuildPointerVisual();
        SetPanelVisible(GeneralOperator.GetSceneMode() == GeneralBuildMode.AR);
    }

    void OnDisable()
    {
        SetPanelVisible(false);
        SetPointerVisible(false);
        hoveredButton = null;
        pressedButton = null;
    }

    void OnDestroy()
    {
        foreach (Texture2D texture in labelTextures)
        {
            if (texture != null)
                Destroy(texture);
        }

        labelTextures.Clear();

        if (pointerMaterial != null)
        {
            Destroy(pointerMaterial);
            pointerMaterial = null;
        }
    }

    void LateUpdate()
    {
        bool isAR = GeneralOperator.GetSceneMode() == GeneralBuildMode.AR;
        SetPanelVisible(isAR);
        if (!isAR)
            return;

        EnsureReferences();
        if (arCamera == null || panelRoot == null)
            return;

        FollowCamera();
        HandlePointerInput();
    }

    void EnsureReferences()
    {
        if (arCamera == null)
            arCamera = Camera.main;

        if (panelRoot != null && arCamera != null)
        {
            Canvas canvas = panelRoot.GetComponent<Canvas>();
            if (canvas != null && canvas.worldCamera == null)
                canvas.worldCamera = arCamera;
        }

        if (poseController == null)
            poseController = FindObjectOfType<PoseController>(true);
    }

    void BuildPanel()
    {
        if (panelRoot != null)
            return;

        panelRoot = new GameObject("AR Pose Control Panel", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        panelRoot.transform.SetParent(transform, false);
        panelRoot.transform.localScale = Vector3.one * 0.001f;

        Canvas canvas = panelRoot.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = arCamera;
        canvas.sortingOrder = 100;

        CanvasScaler scaler = panelRoot.GetComponent<CanvasScaler>();
        scaler.dynamicPixelsPerUnit = 900.0f;

        RectTransform panelRect = panelRoot.GetComponent<RectTransform>();
        panelRect.sizeDelta = new Vector2(690.0f, 190.0f);

        Image panelImage = panelRoot.AddComponent<Image>();
        panelImage.color = PanelColor;

        CreateBitmapLabel(panelRect, "SCENE CONTROL", new Vector2(0.0f, 58.0f), new Vector2(380.0f, 34.0f), 4);
        CreateButton(panelRect, "CLEAN", new Vector2(-255.0f, -30.0f), PoseAction.Clean);
        CreateButton(panelRect, "T-A", new Vector2(-85.0f, -30.0f), PoseAction.TAPose);
        CreateButton(panelRect, "POSE 1", new Vector2(85.0f, -30.0f), PoseAction.Pose1);
        CreateButton(panelRect, "MESH", new Vector2(255.0f, -30.0f), PoseAction.ToggleMeshes);
    }

    void CreateButton(RectTransform parent, string label, Vector2 anchoredPosition, PoseAction action)
    {
        GameObject buttonObject = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button), typeof(BoxCollider));
        buttonObject.transform.SetParent(parent, false);

        RectTransform rect = buttonObject.GetComponent<RectTransform>();
        rect.sizeDelta = new Vector2(150.0f, 72.0f);
        rect.anchoredPosition = anchoredPosition;

        Image image = buttonObject.GetComponent<Image>();
        image.color = ButtonColor;

        Button button = buttonObject.GetComponent<Button>();
        button.targetGraphic = image;
        button.onClick.AddListener(() => ApplyPoseAction(action));
        buttonImages[button] = image;

        BoxCollider collider = buttonObject.GetComponent<BoxCollider>();
        collider.size = new Vector3(rect.sizeDelta.x, rect.sizeDelta.y, 20.0f);
        collider.center = Vector3.zero;

        CreateBitmapLabel(rect, label, Vector2.zero, new Vector2(132.0f, 30.0f), 5);
    }

    void CreateBitmapLabel(RectTransform parent, string text, Vector2 anchoredPosition, Vector2 size, int pixelScale)
    {
        Texture2D texture = BuildLabelTexture(text, pixelScale);
        labelTextures.Add(texture);

        GameObject textObject = new GameObject(text, typeof(RectTransform), typeof(Image));
        textObject.transform.SetParent(parent, false);

        RectTransform rect = textObject.GetComponent<RectTransform>();
        rect.sizeDelta = size;
        rect.anchoredPosition = anchoredPosition;

        Image label = textObject.GetComponent<Image>();
        label.sprite = Sprite.Create(texture, new Rect(0.0f, 0.0f, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100.0f);
        label.preserveAspect = true;
        label.color = TextColor;
        label.raycastTarget = false;
    }

    void FollowCamera()
    {
        Vector3 forward = Vector3.ProjectOnPlane(arCamera.transform.forward, Vector3.up);
        if (forward.sqrMagnitude < 0.001f)
            forward = Vector3.ProjectOnPlane(arCamera.transform.up, Vector3.up);
        if (forward.sqrMagnitude < 0.001f)
            forward = Vector3.forward;
        forward.Normalize();

        Vector3 targetPosition = arCamera.transform.position + forward * followDistance + Vector3.up * verticalOffset;
        Quaternion targetRotation = Quaternion.LookRotation(forward, Vector3.up);
        float t = 1.0f - Mathf.Exp(-followSpeed * Time.deltaTime);

        panelRoot.transform.position = Vector3.Lerp(panelRoot.transform.position, targetPosition, t);
        panelRoot.transform.rotation = Quaternion.Slerp(panelRoot.transform.rotation, targetRotation, t);
    }

    void HandlePointerInput()
    {
        Button button = null;
        bool hasPointerRay = TryGetBestPointerRay(out Ray pointerRay);
        Vector3 pointerEnd = hasPointerRay ? pointerRay.origin + pointerRay.direction * pointerRayLength : Vector3.zero;
        if (hasPointerRay)
            button = RaycastButton(pointerRay, out pointerEnd);

        UpdatePointerVisual(hasPointerRay, pointerRay.origin, pointerEnd, button != null);
        SetHoveredButton(button);

        bool isPressed = IsPressing();
        if (isPressed && hoveredButton != null && pressedButton != hoveredButton)
        {
            hoveredButton.onClick.Invoke();
            pressedButton = hoveredButton;
        }

        if (!isPressed)
            pressedButton = null;
    }

    bool TryGetBestPointerRay(out Ray ray)
    {
        if (IsDevicePressing(XRNode.RightHand) && TryGetControllerRay(XRNode.RightHand, out ray))
            return true;

        if (IsDevicePressing(XRNode.LeftHand) && TryGetControllerRay(XRNode.LeftHand, out ray))
            return true;

        if (TryGetControllerRay(XRNode.RightHand, out ray))
            return true;

        if (TryGetControllerRay(XRNode.LeftHand, out ray))
            return true;

        if (arCamera != null)
        {
            ray = new Ray(arCamera.transform.position, arCamera.transform.forward);
            return true;
        }

        ray = default;
        return false;
    }

    Button RaycastButton(Ray ray, out Vector3 hitPoint)
    {
        RaycastHit[] hits = Physics.RaycastAll(ray, interactionDistance, ~0, QueryTriggerInteraction.Collide);
        float bestDistance = float.MaxValue;
        Button bestButton = null;
        hitPoint = ray.origin + ray.direction * pointerRayLength;

        foreach (RaycastHit hit in hits)
        {
            Button button = hit.collider != null ? hit.collider.GetComponentInParent<Button>() : null;
            if (button == null || hit.distance >= bestDistance)
                continue;

            bestDistance = hit.distance;
            bestButton = button;
            hitPoint = hit.point;
        }

        return bestButton;
    }

    static bool TryGetControllerRay(XRNode node, out Ray ray)
    {
        InputDevice device = InputDevices.GetDeviceAtXRNode(node);
        if (device.isValid &&
            device.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 position) &&
            device.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion rotation))
        {
            ray = new Ray(position, rotation * Vector3.forward);
            return true;
        }

        ray = default;
        return false;
    }

    static bool IsPressing()
    {
        return Input.GetMouseButton(0) ||
               IsDevicePressing(XRNode.RightHand) ||
               IsDevicePressing(XRNode.LeftHand);
    }

    static bool IsDevicePressing(XRNode node)
    {
        InputDevice device = InputDevices.GetDeviceAtXRNode(node);
        if (!device.isValid)
            return false;

        if (device.TryGetFeatureValue(CommonUsages.triggerButton, out bool triggerPressed) && triggerPressed)
            return true;

        if (device.TryGetFeatureValue(CommonUsages.trigger, out float triggerValue) && triggerValue > 0.65f)
            return true;

        if (device.TryGetFeatureValue(CommonUsages.gripButton, out bool gripPressed) && gripPressed)
            return true;

        if (device.TryGetFeatureValue(CommonUsages.primaryButton, out bool primaryPressed) && primaryPressed)
            return true;

        return false;
    }

    void SetHoveredButton(Button button)
    {
        if (hoveredButton == button)
            return;

        SetButtonColor(hoveredButton, ButtonColor);
        hoveredButton = button;
        SetButtonColor(hoveredButton, HoverColor);
    }

    void SetButtonColor(Button button, Color color)
    {
        if (button == null || !buttonImages.TryGetValue(button, out Image image))
            return;

        image.color = color;
    }

    void ApplyPoseAction(PoseAction action)
    {
        if (action == PoseAction.ToggleMeshes)
        {
            if (savedRoomMesh == null) savedRoomMesh = FindObjectOfType<QuestSavedRoomMesh>(true);
            if (savedRoomMesh != null) savedRoomMesh.ToggleMeshes();
            return;
        }
        EnsureReferences();
        if (poseController == null)
            return;

        switch (action)
        {
            case PoseAction.Clean:
                poseController.ApplyCleanPose();
                break;
            case PoseAction.TAPose:
                poseController.ApplyTAPose();
                break;
            case PoseAction.Pose1:
                poseController.ApplyPose1();
                break;
        }
    }

    void SetPanelVisible(bool visible)
    {
        if (panelRoot != null && panelRoot.activeSelf != visible)
            panelRoot.SetActive(visible);
    }

    void BuildPointerVisual()
    {
        if (pointerRoot != null)
            return;

        pointerMaterial = CreatePointerMaterial();
        pointerMaterial.color = PointerColor;

        pointerRoot = new GameObject("AR Pose Pointer");
        pointerRoot.transform.SetParent(transform, false);

        GameObject lineObject = new("Ray");
        lineObject.transform.SetParent(pointerRoot.transform, false);
        pointerLine = lineObject.AddComponent<LineRenderer>();
        pointerLine.material = pointerMaterial;
        pointerLine.positionCount = 2;
        pointerLine.useWorldSpace = true;
        pointerLine.startWidth = 0.008f;
        pointerLine.endWidth = 0.003f;
        pointerLine.numCapVertices = 6;
        pointerLine.numCornerVertices = 2;
        pointerLine.textureMode = LineTextureMode.Stretch;
        pointerLine.sortingOrder = 200;

        GameObject dotObject = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        dotObject.name = "Hit Dot";
        dotObject.transform.SetParent(pointerRoot.transform, false);
        dotObject.transform.localScale = Vector3.one * pointerDotSize;
        Collider collider = dotObject.GetComponent<Collider>();
        if (collider != null)
            Destroy(collider);

        Renderer renderer = dotObject.GetComponent<Renderer>();
        if (renderer != null)
            renderer.sharedMaterial = pointerMaterial;

        pointerDot = dotObject.transform;
        SetPointerVisible(false);
    }

    void UpdatePointerVisual(bool hasRay, Vector3 origin, Vector3 end, bool hitButton)
    {
        if (!showPointerRay)
        {
            SetPointerVisible(false);
            return;
        }

        BuildPointerVisual();
        SetPointerVisible(hasRay);
        if (!hasRay || pointerLine == null || pointerDot == null || pointerMaterial == null)
            return;

        pointerLine.SetPosition(0, origin);
        pointerLine.SetPosition(1, end);
        pointerDot.position = end;
        pointerDot.localScale = Vector3.one * (hitButton ? pointerDotSize * 1.35f : pointerDotSize);
        pointerMaterial.color = hitButton ? PointerHitColor : PointerColor;
    }

    void SetPointerVisible(bool visible)
    {
        if (pointerRoot != null && pointerRoot.activeSelf != visible)
            pointerRoot.SetActive(visible);
    }

    static Material CreatePointerMaterial()
    {
        Shader shader = Shader.Find("Sprites/Default");
        if (shader == null)
            shader = Shader.Find("Unlit/Color");
        if (shader == null)
            shader = Shader.Find("UI/Default");
        if (shader == null)
            shader = Shader.Find("Standard");

        return new Material(shader);
    }

    static void EnsureEventSystem()
    {
        if (FindObjectOfType<EventSystem>(true) != null)
            return;

        GameObject eventSystem = new("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        DontDestroyOnLoad(eventSystem);
    }

    static Texture2D BuildLabelTexture(string text, int scale)
    {
        const int glyphWidth = 5;
        const int glyphHeight = 7;
        const int spacing = 1;
        const int padding = 2;

        text = text.ToUpperInvariant();
        int cellWidth = padding * 2;
        foreach (char character in text)
            cellWidth += character == ' ' ? 4 : glyphWidth + spacing;

        int width = Mathf.Max(1, cellWidth * scale);
        int height = (glyphHeight + padding * 2) * scale;
        Texture2D texture = new(width, height, TextureFormat.RGBA32, false);
        texture.filterMode = FilterMode.Point;
        texture.wrapMode = TextureWrapMode.Clamp;

        Color32 clear = new(255, 255, 255, 0);
        Color32 solid = new(255, 255, 255, 255);
        Color32[] pixels = new Color32[width * height];
        for (int i = 0; i < pixels.Length; ++i)
            pixels[i] = clear;

        int x = padding;
        foreach (char character in text)
        {
            if (character == ' ')
            {
                x += 4;
                continue;
            }

            string[] glyph = GetGlyph(character);
            if (glyph == null)
            {
                x += glyphWidth + spacing;
                continue;
            }

            DrawGlyph(pixels, width, x, padding, scale, glyph, solid);
            x += glyphWidth + spacing;
        }

        texture.SetPixels32(pixels);
        texture.Apply(false, true);
        return texture;
    }

    static void DrawGlyph(Color32[] pixels, int textureWidth, int cellX, int cellY, int scale, string[] glyph, Color32 color)
    {
        for (int row = 0; row < glyph.Length; ++row)
        {
            for (int column = 0; column < glyph[row].Length; ++column)
            {
                if (glyph[row][column] != '1')
                    continue;

                int pixelX = (cellX + column) * scale;
                int pixelY = (cellY + glyph.Length - 1 - row) * scale;
                for (int y = 0; y < scale; ++y)
                {
                    for (int x = 0; x < scale; ++x)
                    {
                        int target = (pixelY + y) * textureWidth + pixelX + x;
                        if (target >= 0 && target < pixels.Length)
                            pixels[target] = color;
                    }
                }
            }
        }
    }

    static string[] GetGlyph(char character)
    {
        switch (character)
        {
            case 'A':
                return new[] { "01110", "10001", "10001", "11111", "10001", "10001", "10001" };
            case 'C':
                return new[] { "01111", "10000", "10000", "10000", "10000", "10000", "01111" };
            case 'E':
                return new[] { "11111", "10000", "10000", "11110", "10000", "10000", "11111" };
            case 'H':
                return new[] { "10001", "10001", "10001", "11111", "10001", "10001", "10001" };
            case 'M':
                return new[] { "10001", "11011", "10101", "10101", "10001", "10001", "10001" };
            case 'L':
                return new[] { "10000", "10000", "10000", "10000", "10000", "10000", "11111" };
            case 'N':
                return new[] { "10001", "11001", "10101", "10011", "10001", "10001", "10001" };
            case 'O':
                return new[] { "01110", "10001", "10001", "10001", "10001", "10001", "01110" };
            case 'P':
                return new[] { "11110", "10001", "10001", "11110", "10000", "10000", "10000" };
            case 'R':
                return new[] { "11110", "10001", "10001", "11110", "10100", "10010", "10001" };
            case 'S':
                return new[] { "01111", "10000", "10000", "01110", "00001", "00001", "11110" };
            case 'T':
                return new[] { "11111", "00100", "00100", "00100", "00100", "00100", "00100" };
            case '1':
                return new[] { "00100", "01100", "00100", "00100", "00100", "00100", "01110" };
            case '-':
                return new[] { "00000", "00000", "00000", "11111", "00000", "00000", "00000" };
            default:
                return null;
        }
    }
}
