using UnityEngine;

namespace ClumsyRagdoll
{
    /// <summary>
    /// 《一步一脚印》的独立 locomotion：身体不会被“传送”到目标速度，
    /// 每一步都从支撑脚的接触点开始，先抬起摆动脚，再把重心送到新脚上。
    /// 这套脚本不依赖 ClumsyController、ClumsyBalance 或 ClumsyCarry。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StepByStepMotor : MonoBehaviour
    {
        public ClumsyRagdoll Ragdoll;
        public RagdollRecipe Recipe;
        public Transform Facing;
        public IClumsyInput Source;
        public ClumsyController InputController;

        [Header("一步一脚印")]
        public float StepLength = 0.34f;
        public float StepWidth = 0.18f;
        public float StepDuration = 0.42f;
        public float SwingClearance = 0.16f;
        public float WeightShift = 0.28f;
        public float FootServo = 85f;
        public float FootDamping = 12f;
        public float BalanceTorque = 18f;
        public float TurnTorque = 7f;
        public float MaxSpeed = 1.55f;

        public bool StepActive { get; private set; }
        public string SupportFoot { get; private set; }
        public float StepProgress { get; private set; }
        public Vector3 SupportAnchor { get; private set; }

        ClumsyPart _left, _right;
        ClumsyPart _support, _swing;
        Vector3 _swingStart;
        Vector3 _swingTarget;
        Vector3 _lastHips;
        float _stepClock;
        bool _initialized;

        void Start()
        {
            if (Ragdoll == null) Ragdoll = GetComponent<ClumsyRagdoll>();
            if (Recipe == null && Ragdoll != null) Recipe = Ragdoll.Recipe;
            if (Facing == null && Ragdoll != null) Facing = Ragdoll.Root;
            if (Ragdoll == null) return;
            _left = Ragdoll.Find("footl");
            _right = Ragdoll.Find("footr");
            _support = _left != null ? _left : _right;
            _swing = _support == _left ? _right : _left;
            SupportFoot = _support != null ? _support.Spec.Key : "footl";
            if (_support != null) SupportAnchor = _support.Body.position;
            if (Ragdoll.Hips != null && Ragdoll.Hips.Body != null) _lastHips = Ragdoll.Hips.Body.position;
            _initialized = _support != null && _swing != null;
        }

        void FixedUpdate()
        {
            if (!_initialized || Ragdoll.Hips == null || Ragdoll.Hips.Body == null) return;
            IClumsyInput inputSource = InputController != null ? InputController.Source : Source;
            Vector2 input = inputSource != null ? Vector2.ClampMagnitude(inputSource.Move, 1f) : Vector2.zero;
            Rigidbody hips = Ragdoll.Hips.Body;
            Vector3 forward = Facing != null ? Vector3.ProjectOnPlane(Facing.forward, Vector3.up).normalized : Vector3.forward;
            Vector3 right = Facing != null ? Vector3.ProjectOnPlane(Facing.right, Vector3.up).normalized : Vector3.right;
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
            if (right.sqrMagnitude < 0.01f) right = Vector3.right;
            Vector3 wish = Vector3.ClampMagnitude(forward * input.y + right * input.x, 1f);

            if (!StepActive && wish.sqrMagnitude > 0.04f)
                BeginStep(wish, forward, right);
            if (StepActive)
                DriveStep(wish, forward, right);
            else
                HoldSupport(hips, _support, SupportAnchor);

            ApplyBalance(hips, wish, forward);
            _lastHips = hips.position;
        }

        void BeginStep(Vector3 wish, Vector3 forward, Vector3 right)
        {
            StepActive = true;
            StepProgress = 0f;
            _stepClock = 0f;
            _swingStart = _swing.Body.position;
            Vector3 lateral = right * (_swing == _right ? StepWidth : -StepWidth);
            Vector3 stride = wish.normalized * StepLength;
            _swingTarget = _support.Body.position + lateral + stride;
            _swingTarget.y = Ragdoll.GroundY + 0.055f;
        }

        void DriveStep(Vector3 wish, Vector3 forward, Vector3 right)
        {
            float dt = Time.fixedDeltaTime;
            _stepClock += dt;
            StepProgress = Mathf.Clamp01(_stepClock / Mathf.Max(0.08f, StepDuration));
            float u = Mathf.SmoothStep(0f, 1f, StepProgress);
            Vector3 target = Vector3.Lerp(_swingStart, _swingTarget, u);
            target.y += Mathf.Sin(StepProgress * Mathf.PI) * SwingClearance;
            HoldSupport(Ragdoll.Hips.Body, _support, SupportAnchor);
            Servo(_swing.Body, target, FootServo, FootDamping);

            // 只有重心越过支撑脚之后才换脚，避免两脚同时滑动。
            Vector3 hipsDelta = Vector3.ProjectOnPlane(Ragdoll.Hips.Body.position - SupportAnchor, Vector3.up);
            float passed = Vector3.Dot(hipsDelta, wish.sqrMagnitude > 0.01f ? wish.normalized : forward);
            if (StepProgress >= 1f && passed > -WeightShift)
            {
                SupportAnchor = _swingTarget;
                ClumsyPart old = _support;
                _support = _swing;
                _swing = old;
                SupportFoot = _support.Spec.Key;
                StepActive = false;
                StepProgress = 0f;
                if (wish.sqrMagnitude > 0.04f) BeginStep(wish, forward, right);
            }
        }

        void HoldSupport(Rigidbody hips, ClumsyPart foot, Vector3 anchor)
        {
            if (foot == null || foot.Body == null) return;
            Vector3 p = foot.Body.position;
            Vector3 error = Vector3.ProjectOnPlane(anchor - p, Vector3.up);
            Vector3 velocity = Vector3.ProjectOnPlane(foot.Body.linearVelocity, Vector3.up);
            foot.Body.AddForce(error * FootServo - velocity * FootDamping, ForceMode.Acceleration);
            Vector3 pull = Vector3.ProjectOnPlane(anchor - hips.position, Vector3.up);
            hips.AddForce(pull * (FootServo * 0.22f), ForceMode.Acceleration);
        }

        static void Servo(Rigidbody body, Vector3 target, float stiffness, float damping)
        {
            if (body == null) return;
            Vector3 error = target - body.position;
            body.AddForce(error * stiffness - body.linearVelocity * damping, ForceMode.Acceleration);
        }

        void ApplyBalance(Rigidbody hips, Vector3 wish, Vector3 forward)
        {
            Vector3 tiltAxis = Vector3.Cross(hips.transform.up, Vector3.up);
            hips.AddTorque(tiltAxis * BalanceTorque, ForceMode.Acceleration);
            if (wish.sqrMagnitude > 0.01f)
            {
                Vector3 flatForward = Vector3.ProjectOnPlane(hips.transform.forward, Vector3.up).normalized;
                float yaw = Vector3.SignedAngle(flatForward, wish.normalized, Vector3.up);
                hips.AddTorque(Vector3.up * (yaw * Mathf.Deg2Rad * TurnTorque), ForceMode.Acceleration);
            }
        }
    }
}
