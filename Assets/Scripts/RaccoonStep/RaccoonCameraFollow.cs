using UnityEngine;

namespace RaccoonStep
{
    /// <summary>
    /// Fixed test camera with an overhead-rear view and a first-person
    /// foot-placement view. V toggles modes; mouse buttons remain reserved
    /// for left/right foot commands.
    /// </summary>
    [DefaultExecutionOrder(20000)]
    [DisallowMultipleComponent]
    public sealed class RaccoonCameraFollow : MonoBehaviour
    {
        public RaccoonPhysicsRig PhysicsRig;
        public Transform Target;

        [Header("Overhead rear view")]
        public Vector3 Offset = new Vector3(0f, 2.8f, -3.8f);
        public Vector3 LookOffset = new Vector3(0f, 0.35f, 0.25f);
        public float FollowSharpness = 10f;
        public float LookSharpness = 12f;
        [Tooltip("How quickly third-person orbit follows the character's stepped heading.")]
        public float ThirdPersonYawFollowSharpness = 12f;

        [Header("First-person foot view")]
        // This is an overhead leg-observation view rather than a level
        // eye-height first-person camera. The offset is relative to the
        // character root, so a large head cannot block the legs.
        public Vector3 FirstPersonOffset = new Vector3(0f, 2.2f, -1.0f);
        public Vector3 FirstPersonLookOffset = new Vector3(0f, 0.05f, 0.5f);
        public float FirstPersonFollowSharpness = 18f;
        public float FirstPersonLookSharpness = 20f;

        [Header("Test camera")]
        public KeyCode ToggleViewKey = KeyCode.V;
        public bool StartInFirstPerson = false;
        public float OrbitSensitivity = 4f;
        public float ZoomSensitivity = 2f;
        public float MinOrbitDistance = 1.2f;
        public float MaxOrbitDistance = 8f;

        Transform _hipsTarget;
        Transform _headTarget;
        bool _firstPerson;
        int _viewMode;
        float _orbitYaw;
        float _orbitPitch;
        float _orbitDistance;
        bool _orbitInitialized;
        float _stableThirdPersonHeight;
        float _stableThirdPersonYaw;
        bool _stableThirdPersonInitialized;

        public bool IsFirstPerson { get { return _firstPerson; } }

        void Awake()
        {
            if (PhysicsRig == null && Target != null)
                PhysicsRig = Target.GetComponent<RaccoonPhysicsRig>();
            _firstPerson = StartInFirstPerson;
            _viewMode = _firstPerson ? 1 : 0;
            if (Target != null)
            {
                _stableThirdPersonHeight = Target.position.y;
                _stableThirdPersonYaw = Target.eulerAngles.y;
                _stableThirdPersonInitialized = true;
            }
            DisableLegacyFreeCamera();
        }

        void LateUpdate()
        {
            DisableLegacyFreeCamera();
            if (_hipsTarget == null && PhysicsRig != null && PhysicsRig.PhysicsRoot != null)
                _hipsTarget = PhysicsRig.PhysicsRoot.Find("HipsBody");
            if (_hipsTarget == null && Target != null)
                _hipsTarget = Target;
            if (_hipsTarget == null)
                return;

            if (_headTarget == null && PhysicsRig != null && PhysicsRig.PhysicsRoot != null)
                _headTarget = PhysicsRig.PhysicsRoot.Find("HeadBody");
            if (Input.GetKeyDown(ToggleViewKey))
            {
                _viewMode = (_viewMode + 1) % 3;
                _firstPerson = _viewMode == 1;
                _orbitInitialized = false;
            }

            if (_viewMode == 2)
            {
                UpdateFreeObserve(_hipsTarget.position + TransformOffset(LookOffset));
                return;
            }

            Transform focus = _hipsTarget;
            Vector3 desiredPosition;
            Vector3 lookPoint;
            float positionSharpness;
            float lookSharpness;
            if (_firstPerson)
            {
                desiredPosition = focus.position + TransformOffset(FirstPersonOffset);
                lookPoint = focus.position + TransformOffset(FirstPersonLookOffset);
                positionSharpness = FirstPersonFollowSharpness;
                lookSharpness = FirstPersonLookSharpness;
            }
            else
            {
                desiredPosition = GetStableThirdPersonFocus() + StableThirdPersonOffset(Offset);
                lookPoint = GetStableThirdPersonFocus() + StableThirdPersonOffset(LookOffset);
                positionSharpness = FollowSharpness;
                lookSharpness = LookSharpness;
            }

            float positionBlend = 1f - Mathf.Exp(-positionSharpness * Time.unscaledDeltaTime);
            transform.position = Vector3.Lerp(transform.position, desiredPosition, positionBlend);
            Vector3 lookDirection = lookPoint - transform.position;
            if (lookDirection.sqrMagnitude < 0.0001f)
                return;
            Quaternion desiredRotation = Quaternion.LookRotation(lookDirection, Vector3.up);
            float rotationBlend = 1f - Mathf.Exp(-lookSharpness * Time.unscaledDeltaTime);
            transform.rotation = Quaternion.Slerp(transform.rotation, desiredRotation, rotationBlend);
        }

        Vector3 TransformOffset(Vector3 offset)
        {
            return Target != null ? Target.TransformDirection(offset) : offset;
        }

        void DisableLegacyFreeCamera()
        {
            MonoBehaviour[] behaviours = GetComponents<MonoBehaviour>();
            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour != null && behaviour != this
                    && behaviour.GetType().Name == "StepByStepFreeCamera")
                    behaviour.enabled = false;
            }
        }

        void UpdateFreeObserve(Vector3 focusPoint)
        {
            if (!_orbitInitialized)
            {
                Vector3 fromFocus = transform.position - focusPoint;
                _orbitDistance = Mathf.Clamp(fromFocus.magnitude, MinOrbitDistance, MaxOrbitDistance);
                Vector3 angles = Quaternion.LookRotation(fromFocus.normalized, Vector3.up).eulerAngles;
                _orbitYaw = angles.y;
                _orbitPitch = angles.x > 180f ? angles.x - 360f : angles.x;
                _orbitInitialized = true;
            }

            // Middle mouse is reserved for camera orbit. Right mouse remains
            // the right-foot command in both stepping modes.
            if (Input.GetMouseButton(2))
            {
                _orbitYaw += Input.GetAxis("Mouse X") * OrbitSensitivity;
                _orbitPitch -= Input.GetAxis("Mouse Y") * OrbitSensitivity;
                _orbitPitch = Mathf.Clamp(_orbitPitch, -10f, 85f);
            }

            _orbitDistance = Mathf.Clamp(
                _orbitDistance - Input.mouseScrollDelta.y * ZoomSensitivity,
                MinOrbitDistance,
                MaxOrbitDistance);

            Quaternion orbitRotation = Quaternion.Euler(_orbitPitch, _orbitYaw, 0f);
            // Use the backward ray from the focus. With a positive pitch this
            // places the camera above the character instead of below it.
            Vector3 desiredPosition = focusPoint + orbitRotation * Vector3.back * _orbitDistance;
            float blend = 1f - Mathf.Exp(-FollowSharpness * Time.unscaledDeltaTime);
            transform.position = Vector3.Lerp(transform.position, desiredPosition, blend);
            Vector3 lookDirection = focusPoint - transform.position;
            if (lookDirection.sqrMagnitude > 0.0001f)
            {
                Quaternion desiredRotation = Quaternion.LookRotation(lookDirection, Vector3.up);
                transform.rotation = Quaternion.Slerp(transform.rotation, desiredRotation, blend);
            }
        }
    

        Vector3 GetStableThirdPersonFocus()
        {
            if (Target == null)
                return _hipsTarget != null ? _hipsTarget.position : transform.position;

            if (!_stableThirdPersonInitialized)
            {
                _stableThirdPersonHeight = Target.position.y;
                _stableThirdPersonYaw = Target.eulerAngles.y;
                _stableThirdPersonInitialized = true;
            }

            // The stepping controller turns the character a few degrees per
            // landing. Keep the rear offset in the character's current yaw;
            // otherwise the body turns while the third-person camera remains
            // locked to the yaw captured in Awake().
            float yawBlend = 1f - Mathf.Exp(-Mathf.Max(0.01f, ThirdPersonYawFollowSharpness)
                * Time.unscaledDeltaTime);
            _stableThirdPersonYaw = Mathf.LerpAngle(
                _stableThirdPersonYaw, Target.eulerAngles.y, yawBlend);

            Vector3 focus = Target.position;
            focus.y = _stableThirdPersonHeight;
            return focus;
        }

        Vector3 StableThirdPersonOffset(Vector3 offset)
        {
            Quaternion yawOnly = Quaternion.Euler(0f, _stableThirdPersonYaw, 0f);
            return yawOnly * offset;
        }
}
}
