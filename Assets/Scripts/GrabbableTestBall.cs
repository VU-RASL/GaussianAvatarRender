using UnityEngine;
using UnityEngine.XR;

[DefaultExecutionOrder(230)]
public sealed class GrabbableTestBall : MonoBehaviour
{
    [SerializeField] Transform ballRoot;
    [SerializeField] Camera xrCamera;
    [SerializeField] float maxGrabDistance = 8.0f;
    [SerializeField] float followSpeed = 40.0f;
    [SerializeField] bool enableInAR = true;
    [SerializeField] bool enableInVR = true;
    [SerializeField] bool showAimRay = true;
    [SerializeField] float aimRayLength = 4.0f;
    [SerializeField] float aimRayWidth = 0.008f;
    [SerializeField] float hitDotSize = 0.035f;
    [SerializeField] bool useTrigger = true;
    [SerializeField] bool reserveLeftTriggerForAvatarPose;
    [SerializeField] bool useGrip = true;
    [SerializeField] bool usePrimaryButton;
    [SerializeField] bool allowMouseInEditor = true;

    Transform target;
    Collider[] targetColliders;
    XRNode holdingNode;
    bool isHolding;
    bool isHoldingMouse;
    bool hasUserPlaced;
    Vector3 localGrabOffset;
    float mouseGrabDistance;
    GameObject pointerRoot;
    LineRenderer pointerLine;
    Transform pointerDot;
    Material pointerMaterial;

    static readonly Color AimColor = new(0.05f, 0.75f, 1.0f, 0.9f);
    static readonly Color HitColor = new(0.2f, 1.0f, 0.45f, 1.0f);
    static readonly Color HoldColor = new(1.0f, 0.78f, 0.16f, 1.0f);

    public bool HasUserPlaced => hasUserPlaced;
    public bool IsHolding => isHolding;

    void OnEnable()
    {
        EnsureReferences();
        BuildPointerVisual();
    }

    void OnDisable()
    {
        isHolding = false;
        SetPointerVisible(false);
    }

    void OnDestroy()
    {
        if (pointerRoot != null)
            Destroy(pointerRoot);

        if (pointerMaterial != null)
            Destroy(pointerMaterial);
    }

    void LateUpdate()
    {
        if (!IsCurrentModeEnabled())
        {
            SetPointerVisible(false);
            return;
        }

        EnsureReferences();
        if (target == null)
        {
            SetPointerVisible(false);
            return;
        }

        if (isHolding)
        {
            UpdateHeldBall();
            return;
        }

        bool hasRay = TryGetBestRay(out Ray ray, out XRNode node, out bool mouseRay);
        bool isPressing = hasRay && IsPressing(node, mouseRay);
        Vector3 hitPoint = hasRay ? ray.origin + ray.direction * aimRayLength : Vector3.zero;
        bool hitBall = hasRay && TryRaycastBall(ray, out hitPoint);
        Vector3 pointerEnd = hitBall ? hitPoint : ray.origin + ray.direction * aimRayLength;

        UpdatePointerVisual(hasRay, ray.origin, pointerEnd, hitBall, false);

        if (isPressing && hitBall)
            BeginGrab(ray, node, mouseRay);
    }

    void EnsureReferences()
    {
        if (ballRoot == null)
            ballRoot = transform;

        target = ballRoot != null ? ballRoot : transform;

        if (xrCamera == null)
            xrCamera = Camera.main;

        if (targetColliders == null || targetColliders.Length == 0)
            targetColliders = target.GetComponentsInChildren<Collider>(true);
    }

    bool IsCurrentModeEnabled()
    {
        GeneralBuildMode mode = GeneralOperator.GetSceneMode();
        return mode == GeneralBuildMode.AR ? enableInAR : enableInVR;
    }

    void BeginGrab(Ray ray, XRNode node, bool mouseRay)
    {
        isHolding = true;
        isHoldingMouse = mouseRay;
        holdingNode = node;
        hasUserPlaced = true;

        if (mouseRay)
        {
            mouseGrabDistance = Vector3.Distance(ray.origin, target.position);
            return;
        }

        if (TryGetControllerPose(node, out Vector3 position, out Quaternion rotation))
            localGrabOffset = Quaternion.Inverse(rotation) * (target.position - position);
        else
            localGrabOffset = target.position - ray.origin;
    }

    void UpdateHeldBall()
    {
        if (isHoldingMouse)
        {
            if (!Input.GetMouseButton(0) || !TryGetMouseRay(out Ray mouseRay))
            {
                EndGrab();
                return;
            }

            MoveTarget(mouseRay.origin + mouseRay.direction * mouseGrabDistance);
            UpdatePointerVisual(true, mouseRay.origin, target.position, true, true);
            return;
        }

        if (!IsDevicePressing(holdingNode))
        {
            EndGrab();
            return;
        }

        if (!TryGetControllerPose(holdingNode, out Vector3 position, out Quaternion rotation))
        {
            EndGrab();
            return;
        }

        Vector3 targetPosition = position + rotation * localGrabOffset;
        MoveTarget(targetPosition);
        UpdatePointerVisual(true, position, target.position, true, true);
    }

    void EndGrab()
    {
        isHolding = false;
        isHoldingMouse = false;
    }

    void MoveTarget(Vector3 targetPosition)
    {
        if (followSpeed <= 0.0f)
        {
            target.position = targetPosition;
            return;
        }

        float t = 1.0f - Mathf.Exp(-followSpeed * Time.deltaTime);
        target.position = Vector3.Lerp(target.position, targetPosition, t);
    }

    bool TryRaycastBall(Ray ray, out Vector3 hitPoint)
    {
        hitPoint = ray.origin + ray.direction * aimRayLength;

        RaycastHit[] hits = Physics.RaycastAll(ray, maxGrabDistance, ~0, QueryTriggerInteraction.Collide);
        if (hits == null || hits.Length == 0)
            return false;

        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        foreach (RaycastHit hit in hits)
        {
            if (hit.collider == null || IsPointerCollider(hit.collider))
                continue;

            if (IsTargetCollider(hit.collider))
            {
                hitPoint = hit.point;
                return true;
            }

            // Avatar/proxy/world colliders should not make the test ball ungrabbable.
            // Keep searching the ray hits until the ball itself is found.
            continue;
        }

        return false;
    }

    bool IsTargetCollider(Collider collider)
    {
        if (collider == null || target == null)
            return false;

        if (collider.transform == target || collider.transform.IsChildOf(target))
            return true;

        if (targetColliders == null)
            return false;

        foreach (Collider targetCollider in targetColliders)
        {
            if (collider == targetCollider)
                return true;
        }

        return false;
    }

    bool IsPointerCollider(Collider collider)
    {
        return pointerRoot != null && collider != null && collider.transform.IsChildOf(pointerRoot.transform);
    }

    bool TryGetBestRay(out Ray ray, out XRNode node, out bool mouseRay)
    {
        if (IsDevicePressing(XRNode.RightHand) && TryGetControllerRay(XRNode.RightHand, out ray))
        {
            node = XRNode.RightHand;
            mouseRay = false;
            return true;
        }

        if (IsDevicePressing(XRNode.LeftHand) && TryGetControllerRay(XRNode.LeftHand, out ray))
        {
            node = XRNode.LeftHand;
            mouseRay = false;
            return true;
        }

        if (TryGetControllerRay(XRNode.RightHand, out ray))
        {
            node = XRNode.RightHand;
            mouseRay = false;
            return true;
        }

        if (TryGetControllerRay(XRNode.LeftHand, out ray))
        {
            node = XRNode.LeftHand;
            mouseRay = false;
            return true;
        }

#if UNITY_EDITOR
        if (allowMouseInEditor && TryGetMouseRay(out ray))
        {
            node = XRNode.RightHand;
            mouseRay = true;
            return true;
        }
#endif

        ray = default;
        node = XRNode.RightHand;
        mouseRay = false;
        return false;
    }

    bool TryGetMouseRay(out Ray ray)
    {
        if (xrCamera == null)
        {
            ray = default;
            return false;
        }

        ray = xrCamera.ScreenPointToRay(Input.mousePosition);
        return true;
    }

    bool IsPressing(XRNode node, bool mouseRay)
    {
        return mouseRay ? Input.GetMouseButton(0) : IsDevicePressing(node);
    }

    bool IsDevicePressing(XRNode node)
    {
        InputDevice device = InputDevices.GetDeviceAtXRNode(node);
        if (!device.isValid)
            return false;

        if (useTrigger && !(reserveLeftTriggerForAvatarPose && node == XRNode.LeftHand))
        {
            if (device.TryGetFeatureValue(CommonUsages.triggerButton, out bool triggerPressed) && triggerPressed)
                return true;

            if (device.TryGetFeatureValue(CommonUsages.trigger, out float triggerValue) && triggerValue > 0.65f)
                return true;
        }

        if (useGrip)
        {
            if (device.TryGetFeatureValue(CommonUsages.gripButton, out bool gripPressed) && gripPressed)
                return true;

            if (device.TryGetFeatureValue(CommonUsages.grip, out float gripValue) && gripValue > 0.65f)
                return true;
        }

        return usePrimaryButton &&
               device.TryGetFeatureValue(CommonUsages.primaryButton, out bool primaryPressed) &&
               primaryPressed;
    }

    static bool TryGetControllerRay(XRNode node, out Ray ray)
    {
        if (TryGetControllerPose(node, out Vector3 position, out Quaternion rotation))
        {
            ray = new Ray(position, rotation * Vector3.forward);
            return true;
        }

        ray = default;
        return false;
    }

    static bool TryGetControllerPose(XRNode node, out Vector3 position, out Quaternion rotation)
    {
        InputDevice device = InputDevices.GetDeviceAtXRNode(node);
        if (device.isValid &&
            device.TryGetFeatureValue(CommonUsages.devicePosition, out position) &&
            device.TryGetFeatureValue(CommonUsages.deviceRotation, out rotation))
        {
            return true;
        }

        position = default;
        rotation = default;
        return false;
    }

    void BuildPointerVisual()
    {
        if (pointerRoot != null)
            return;

        pointerMaterial = CreatePointerMaterial();
        pointerMaterial.color = AimColor;

        pointerRoot = new GameObject("Ball Grab Pointer");

        GameObject lineObject = new("Ray");
        lineObject.transform.SetParent(pointerRoot.transform, false);
        pointerLine = lineObject.AddComponent<LineRenderer>();
        pointerLine.material = pointerMaterial;
        pointerLine.positionCount = 2;
        pointerLine.useWorldSpace = true;
        pointerLine.startWidth = aimRayWidth;
        pointerLine.endWidth = aimRayWidth * 0.4f;
        pointerLine.numCapVertices = 6;
        pointerLine.numCornerVertices = 2;
        pointerLine.textureMode = LineTextureMode.Stretch;

        GameObject dotObject = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        dotObject.name = "Hit Dot";
        dotObject.transform.SetParent(pointerRoot.transform, false);
        dotObject.transform.localScale = Vector3.one * hitDotSize;

        Collider collider = dotObject.GetComponent<Collider>();
        if (collider != null)
            Destroy(collider);

        Renderer renderer = dotObject.GetComponent<Renderer>();
        if (renderer != null)
            renderer.sharedMaterial = pointerMaterial;

        pointerDot = dotObject.transform;
        SetPointerVisible(false);
    }

    void UpdatePointerVisual(bool hasRay, Vector3 origin, Vector3 end, bool hitBall, bool holding)
    {
        if (!showAimRay)
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
        pointerDot.localScale = Vector3.one * (hitBall ? hitDotSize * 1.35f : hitDotSize);
        pointerMaterial.color = holding ? HoldColor : hitBall ? HitColor : AimColor;
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
            shader = Shader.Find("Standard");

        return new Material(shader);
    }
}
