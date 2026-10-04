using UnityEngine;

namespace ClumsyRagdoll
{
    /// <summary>
    /// 手动迈步活动层：玩家先选一只脚和落脚点，再确认一次迈步。
    /// 迈步期间只向现有脚种植步态提供这一小段方向输入；停下后必须再次确认。
    /// </summary>
    [DefaultExecutionOrder(-250)]
    public sealed class ClumsyStepActivity : MonoBehaviour
    {
        public ClumsyRagdoll Ragdoll;
        public ClumsyController Controller;
        public Transform Facing;
        public Camera ViewCamera;
        public float MaxStepDistance = 0.65f;
        public float MinStepDistance = 0.08f;
        public float StepSpeed = 1.25f;
        public LayerMask GroundMask = ~0;
        public bool Enabled = true;

        public string SelectedFoot { get; private set; } = "footl";
        public Vector3 LandingPoint { get; private set; }
        public bool HasLandingPoint { get; private set; }
        public bool StepInProgress { get; private set; }
        public float StepRemaining { get; private set; }
        public float CenterOfMassSupportError { get; private set; }
        public Vector3 CenterOfMass { get; private set; }
        public Vector3 SupportCenter { get; private set; }
        public Vector2 BalanceFeedback { get; private set; }

        readonly StepInput _input = new StepInput();
        Vector3 _stepDirection;
        float _stepDuration;
        Vector3 _swingStart;
        Vector3 _bodyStart;
        Vector3 _bodyTarget;

        public void Setup(ClumsyRagdoll ragdoll, ClumsyController controller, Transform facing)
        {
            Ragdoll = ragdoll;
            Controller = controller;
            Facing = facing;
            ViewCamera = Camera.main;
            _input.Owner = this;
            if (Controller != null)
            {
                Controller.MovementMode = ClumsyMovementMode.ManualFootStep;
                // 保留 Controller 的骨盆托举、接地和稳定层；只由 MovementMode 屏蔽旧移动。
                Controller.enabled = true;
                Controller.Source = _input;
                Controller.ManualStepActive = false;
            }
        }

        public void TakeInput()
        {
            if (Controller != null)
                Controller.Source = _input;
        }

        void Update()
        {
            if (!Enabled || Ragdoll == null || Controller == null || Ragdoll.Hips == null)
                return;
            if (ViewCamera == null) ViewCamera = Camera.main;

            UpdateLandingPoint();
            bool leftHeld = Input.GetMouseButton(0);
            bool rightHeld = Input.GetMouseButton(1);
            if (!StepInProgress && leftHeld != rightHeld)
            {
                SelectedFoot = leftHeld ? "footl" : "footr";
                _input.MoveValue = WorldToFacing((LandingPoint - Ragdoll.Hips.Body.position).normalized);
            }
            if (!StepInProgress && HasLandingPoint &&
                (Input.GetMouseButtonUp(0) || Input.GetMouseButtonUp(1)))
                CommitStep();
        }

        void FixedUpdate()
        {
            if (!Enabled || Ragdoll == null || Ragdoll.Hips == null)
                return;
            if (StepInProgress)
            {
                StepRemaining -= Time.fixedDeltaTime;
                DriveManualStep();
                if (StepRemaining <= 0f)
                {
                    StepRemaining = 0f;
                    StepInProgress = false;
                    if (Controller != null)
                        Controller.ManualStepActive = false;
                    _input.MoveValue = Vector2.zero;
                }
            }
            UpdateBalanceFeedback();
        }

        void CommitStep()
        {
            Vector3 origin = Ragdoll.Hips.Body.position;
            Vector3 delta = LandingPoint - origin;
            delta.y = 0f;
            float distance = delta.magnitude;
            if (distance < MinStepDistance)
                return;

            distance = Mathf.Min(distance, MaxStepDistance);
            _stepDirection = delta.normalized;
            _stepDuration = Mathf.Clamp(distance / Mathf.Max(0.1f, StepSpeed), 0.28f, 1.1f);
            StepRemaining = _stepDuration;
            StepInProgress = true;
            ClumsyPart swing = Ragdoll.Find(SelectedFoot);
            if (swing == null || swing.Body == null)
            {
                StepInProgress = false;
                return;
            }
            _swingStart = swing.Body.position;
            _bodyStart = Ragdoll.Hips.Body.position;
            _bodyTarget = _bodyStart + Vector3.ProjectOnPlane(delta, Vector3.up) * 0.58f;
            // 不把方向再传给旧姿势/移动输入；真正的位移完全由 DriveManualStep 完成。
            _input.MoveValue = Vector2.zero;
            HasLandingPoint = false;
        }

        void DriveManualStep()
        {
            if (Ragdoll == null || Ragdoll.Hips == null || Ragdoll.Hips.Body == null)
                return;
            ClumsyPart swing = Ragdoll.Find(SelectedFoot);
            if (swing == null || swing.Body == null)
                return;

            float t = 1f - Mathf.Clamp01(StepRemaining / Mathf.Max(_stepDuration, 0.01f));
            Vector3 footTarget = Vector3.Lerp(_swingStart, LandingPoint, Mathf.SmoothStep(0f, 1f, t));
            footTarget += Vector3.up * (Mathf.Sin(t * Mathf.PI) * 0.10f);
            ApplyPositionServo(swing.Body, footTarget, 160f, 18f, 90f);

            Vector3 bodyTarget = Vector3.Lerp(_bodyStart, _bodyTarget, Mathf.SmoothStep(0f, 1f, t));
            bodyTarget.y = _bodyStart.y;
            ApplyPositionServo(Ragdoll.Hips.Body, bodyTarget, 42f, 12f, 35f);
        }

        static void ApplyPositionServo(Rigidbody body, Vector3 target, float stiffness, float damping, float maxAcceleration)
        {
            Vector3 error = target - body.position;
            Vector3 acceleration = error * stiffness - body.linearVelocity * damping;
            acceleration = Vector3.ClampMagnitude(acceleration, maxAcceleration);
            body.AddForce(acceleration, ForceMode.Acceleration);
        }

        Vector2 WorldToFacing(Vector3 direction)
        {
            Vector3 f = Facing != null ? Facing.forward : Vector3.forward;
            Vector3 r = Facing != null ? Facing.right : Vector3.right;
            f.y = 0f; r.y = 0f;
            f.Normalize(); r.Normalize();
            return new Vector2(Vector3.Dot(direction, r), Vector3.Dot(direction, f));
        }

        void UpdateLandingPoint()
        {
            if (ViewCamera == null) return;
            Ray ray = ViewCamera.ScreenPointToRay(Input.mousePosition);
            RaycastHit hit;
            if (Physics.Raycast(ray, out hit, 200f, GroundMask, QueryTriggerInteraction.Ignore))
            {
                LandingPoint = hit.point;
                HasLandingPoint = true;
                return;
            }
            Plane plane = new Plane(Vector3.up, Vector3.zero);
            float enter;
            if (plane.Raycast(ray, out enter))
            {
                LandingPoint = ray.GetPoint(enter);
                HasLandingPoint = true;
            }
        }

        void UpdateBalanceFeedback()
        {
            Vector3 sum = Vector3.zero;
            float mass = 0f;
            for (int i = 0; i < Ragdoll.Parts.Count; i++)
            {
                Rigidbody body = Ragdoll.Parts[i].Body;
                if (body == null) continue;
                sum += body.worldCenterOfMass * body.mass;
                mass += body.mass;
            }
            if (mass <= 0f) return;
            CenterOfMass = sum / mass;

            ClumsyPart left = Ragdoll.Find("footl");
            ClumsyPart right = Ragdoll.Find("footr");
            if (left == null || right == null || left.Body == null || right.Body == null)
                return;
            Vector3 a = left.Body.worldCenterOfMass;
            Vector3 b = right.Body.worldCenterOfMass;
            SupportCenter = Vector3.Lerp(a, b, 0.5f);
            Vector3 axis = b - a;
            axis.y = 0f;
            if (axis.sqrMagnitude < 0.0001f) return;
            axis.Normalize();
            Vector3 forward = Facing != null ? Facing.forward : Vector3.forward;
            forward.y = 0f; forward.Normalize();
            Vector3 side = Vector3.Cross(Vector3.up, forward).normalized;
            Vector3 offset = CenterOfMass - SupportCenter;
            offset.y = 0f;
            BalanceFeedback = new Vector2(Vector3.Dot(offset, side), Vector3.Dot(offset, forward));
            CenterOfMassSupportError = offset.magnitude;
        }

        void OnGUI()
        {
            if (!Enabled || Ragdoll == null) return;
            GUILayout.BeginArea(new Rect(12f, 355f, 520f, 130f), GUI.skin.box);
            GUILayout.Label("单脚移动：按住左键拖左脚 / 右键拖右脚，松开落脚；中键旋转视角");
            GUILayout.Label("当前脚：" + (SelectedFoot == "footl" ? "左脚" : "右脚") +
                "   状态：" + (StepInProgress ? "迈步中" : "等待确认"));
            GUILayout.Label("重心偏移 侧向 " + BalanceFeedback.x.ToString("F2") +
                " m / 前后 " + BalanceFeedback.y.ToString("F2") + " m" +
                (CenterOfMassSupportError > 0.18f ? "  ⚠ 重心偏离" : "  ✓ 稳定"));
            GUILayout.EndArea();
        }

        void OnDrawGizmos()
        {
            if (!Enabled) return;
            if (HasLandingPoint)
            {
                Gizmos.color = SelectedFoot == "footl" ? Color.cyan : Color.yellow;
                Gizmos.DrawSphere(LandingPoint + Vector3.up * 0.025f, 0.055f);
                if (Ragdoll != null && Ragdoll.Hips != null)
                    Gizmos.DrawLine(Ragdoll.Hips.Body.position, LandingPoint);
            }
            if (Ragdoll != null && Ragdoll.Hips != null)
            {
                Gizmos.color = CenterOfMassSupportError > 0.18f ? Color.red : Color.green;
                Gizmos.DrawSphere(CenterOfMass, 0.045f);
                Gizmos.DrawLine(SupportCenter, CenterOfMass);
            }
        }

        sealed class StepInput : IClumsyInput
        {
            public ClumsyStepActivity Owner;
            public Vector2 MoveValue;
            public Vector2 Move { get { return Owner != null && Owner.StepInProgress ? MoveValue : Vector2.zero; } }
            public bool Sprint { get { return false; } }
            public bool JumpHeld { get { return false; } }
        }
    }
}
