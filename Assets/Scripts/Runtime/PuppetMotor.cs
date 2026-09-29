using UnityEngine;

namespace ClumsyRagdoll
{
    /// <summary>
    /// 主动布娃娃：腿和脊柱跟着步态与平衡，手臂跟着瞄准点。
    /// 肌肉是有上限的 PD 扭矩，所以会慢半拍，撞上东西就倒。
    /// </summary>
    public sealed class PuppetMotor : MonoBehaviour
    {
        public SharedScreenCamera View;
        public MatchTuning Tuning;
        public IPuppetCommandSource Source { get; set; }

        PuppetRig _rig;
        PuppetCommand _cmd;
        bool _jumpLatch;
        bool _respawn;
        Vector3 _facing = Vector3.forward;
        Vector3 _aim;
        bool _aimReady;
        float _downTimer;
        Leg _left;
        Leg _right;

        sealed class Leg
        {
            public bool Left;
            public bool Swinging;
            public float SwingT;
            public float SwingDuration = 0.28f;
            public float Cooldown;
            public Vector3 Plant;
            public Vector3 SwingFrom;
            public Vector3 SwingTo;
            public float AnkleHeight = 0.1f;
        }

        void Awake()
        {
            _rig = GetComponent<PuppetRig>();
            _facing = _rig.SpawnRotation * Vector3.forward;
            _aim = _rig.Chest.transform.position + _facing * 1.2f;
            _aimReady = true;
            _left = CreateLeg(true);
            _right = CreateLeg(false);
        }

        Leg CreateLeg(bool left)
        {
            BodyPart foot = left ? _rig.FootL : _rig.FootR;
            Vector3 ankle = foot.Joint != null
                ? foot.transform.TransformPoint(foot.Joint.anchor)
                : foot.transform.position;
            float ground = SampleGround(ankle);
            return new Leg
            {
                Left = left,
                Plant = ankle,
                AnkleHeight = Mathf.Max(0.04f, ankle.y - ground)
            };
        }

        void Update()
        {
            if (Source == null || _rig == null)
                return;

            PuppetSense sense = new PuppetSense
            {
                ChestPosition = _rig.Chest.transform.position,
                ChestForward = _facing,
                CameraYaw = View != null ? View.Yaw : 0f,
                View = View != null ? View.GetComponent<Camera>() : Camera.main
            };
            PuppetCommand cmd = Source.Read(sense);
            if (cmd.JumpPressed)
                _jumpLatch = true;
            if (cmd.RespawnPressed)
                _respawn = true;
            _cmd = cmd;
        }

        void FixedUpdate()
        {
            if (_rig == null)
                return;
            if (_respawn)
            {
                _respawn = false;
                Respawn();
                return;
            }

            PuppetCommand cmd = _cmd;
            cmd.JumpPressed = _jumpLatch;
            _jumpLatch = false;
            Step(cmd);
        }

        void Step(PuppetCommand cmd)
        {
            if (OutOfBounds())
            {
                Respawn();
                return;
            }

            float flop = Tuning != null ? Mathf.Clamp(Tuning.Floppiness, 0.05f, 0.95f) : 0.5f;
            float flopScale = Mathf.Lerp(1.28f, 0.4f, flop);
            float dt = Time.fixedDeltaTime;

            if (cmd.Move.sqrMagnitude > 0.01f || cmd.Grab || cmd.JumpPressed)
                _rig.Pelvis.Body.WakeUp();

            UpdateFacing(cmd, dt);
            UpdateAim(cmd, dt);

            float uprightDot = Vector3.Dot(_rig.Pelvis.transform.up, Vector3.up);
            if (uprightDot < 0.42f)
                _downTimer += dt;
            else
                _downTimer = Mathf.Max(0f, _downTimer - dt * 2.5f);
            bool fallen = _downTimer > 0.22f;
            bool effort = cmd.JumpPressed || cmd.Move.sqrMagnitude > 0.08f || cmd.Grab;

            PoseLegs(cmd, fallen, dt);
            PoseArms(cmd, fallen, effort);
            PoseTorso(cmd, fallen);
            ApplyMuscles(flopScale);
            ApplyBalance(cmd, fallen, effort, flopScale, uprightDot);
            _rig.GrabL.SetGrab(cmd.Grab);
            _rig.GrabR.SetGrab(cmd.Grab);
            ApplyClimbPull(flopScale);
            ClampMotion();
        }

        void UpdateFacing(PuppetCommand cmd, float dt)
        {
            Vector2 move = cmd.Move;
            if (move.sqrMagnitude > 1f)
                move.Normalize();
            if (move.sqrMagnitude < 0.04f)
                return;
            Vector3 wish = Quaternion.Euler(0f, cmd.CameraYaw, 0f) * new Vector3(move.x, 0f, move.y);
            wish.y = 0f;
            if (wish.sqrMagnitude < 1e-6f)
                return;
            _facing = Vector3.Slerp(_facing, wish.normalized, 1f - Mathf.Exp(-7f * dt));
            _facing.y = 0f;
            if (_facing.sqrMagnitude > 1e-6f)
                _facing.Normalize();
        }

        void UpdateAim(PuppetCommand cmd, float dt)
        {
            Vector3 target = cmd.AimWorldPoint;
            if (!_aimReady || target.sqrMagnitude < 1e-6f)
            {
                _aim = _rig.Chest.transform.position + _facing * 1.1f;
                _aimReady = true;
                return;
            }
            _aim = Vector3.Lerp(_aim, target, 1f - Mathf.Exp(-12f * dt));
        }

        void PoseLegs(PuppetCommand cmd, bool fallen, float dt)
        {
            Vector2 move = cmd.Move;
            if (move.sqrMagnitude > 1f)
                move.Normalize();
            Vector3 wish = Quaternion.Euler(0f, cmd.CameraYaw, 0f) * new Vector3(move.x, 0f, move.y);
            float speed01 = Mathf.Clamp01(wish.magnitude);
            ConsiderStep(_left, _right, wish, speed01, fallen, dt);
            ConsiderStep(_right, _left, wish, speed01, fallen, dt);
            PoseLeg(_left, fallen);
            PoseLeg(_right, fallen);
        }

        void ConsiderStep(Leg leg, Leg other, Vector3 wish, float speed01, bool fallen, float dt)
        {
            if (leg.Cooldown > 0f)
                leg.Cooldown -= dt;

            Vector3 hip = Hip(leg.Left);
            Vector3 lateral = Vector3.Cross(Vector3.up, _facing) * (leg.Left ? -1f : 1f) * 0.1f;
            Vector3 rest = hip + lateral + _facing * 0.02f;
            rest.y = SampleGround(rest) + leg.AnkleHeight;

            if (fallen)
            {
                leg.Swinging = false;
                Vector3 tuck = hip + _facing * 0.18f + Vector3.up * -0.05f;
                tuck.y = SampleGround(tuck) + leg.AnkleHeight * 0.4f;
                leg.Plant = tuck;
                return;
            }

            if (leg.Swinging)
            {
                leg.SwingT += dt / Mathf.Max(0.12f, leg.SwingDuration);
                float t = Mathf.Clamp01(leg.SwingT);
                float smooth = t * t * (3f - 2f * t);
                Vector3 flat = Vector3.Lerp(leg.SwingFrom, leg.SwingTo, smooth);
                float y0 = SampleGround(leg.SwingFrom) + leg.AnkleHeight;
                float y1 = SampleGround(leg.SwingTo) + leg.AnkleHeight;
                flat.y = Mathf.Lerp(y0, y1, smooth) + Mathf.Sin(smooth * Mathf.PI) * Mathf.Lerp(0.08f, 0.16f, speed01);
                leg.Plant = flat;
                if (t >= 1f)
                {
                    leg.Swinging = false;
                    leg.Plant = leg.SwingTo;
                    leg.Plant.y = SampleGround(leg.Plant) + leg.AnkleHeight;
                    leg.Cooldown = 0.07f;
                }
                return;
            }

            leg.Plant.y = SampleGround(leg.Plant) + leg.AnkleHeight;
            if (other.Swinging || leg.Cooldown > 0f)
                return;

            float err = Vector2.Distance(new Vector2(leg.Plant.x, leg.Plant.z), new Vector2(rest.x, rest.z));
            float threshold = speed01 > 0.2f ? 0.2f : 0.36f;
            if (err < threshold)
                return;

            leg.Swinging = true;
            leg.SwingT = 0f;
            leg.SwingDuration = Mathf.Lerp(0.34f, 0.2f, speed01);
            leg.SwingFrom = leg.Plant;
            Vector3 vel = Vector3.ProjectOnPlane(_rig.Pelvis.Body.linearVelocity, Vector3.up);
            leg.SwingTo = rest + wish.normalized * (wish.sqrMagnitude > 0.01f ? Mathf.Lerp(0.12f, 0.32f, speed01) : 0f) + vel * 0.06f;
            leg.SwingTo.y = SampleGround(leg.SwingTo) + leg.AnkleHeight;
        }

        void PoseLeg(Leg leg, bool fallen)
        {
            BodyPart thigh = leg.Left ? _rig.ThighL : _rig.ThighR;
            BodyPart shin = leg.Left ? _rig.ShinL : _rig.ShinR;
            BodyPart foot = leg.Left ? _rig.FootL : _rig.FootR;
            Vector3 hip = Hip(leg.Left);
            Vector3 pole = hip + _facing * 0.7f + Vector3.up * 0.12f;
            IkMath.SolveTwoBone(hip, leg.Plant, pole, _rig.ThighLength, _rig.ShinLength, out Vector3 dirA, out Vector3 dirB);
            thigh.TargetWorld = IkMath.RotationFromUp(dirA, pole - hip);
            shin.TargetWorld = IkMath.RotationFromUp(dirB, pole - hip);
            foot.TargetWorld = IkMath.SafeLook(_facing, Vector3.up);

            float stiff = fallen ? 16f : leg.Swinging ? 30f : 52f;
            thigh.Stiffness = stiff;
            shin.Stiffness = stiff * 0.85f;
            foot.Stiffness = fallen ? 8f : 18f;
            thigh.MaxTorque = 32f;
            shin.MaxTorque = 26f;
            foot.MaxTorque = 12f;

            if (!leg.Swinging && !fallen)
            {
                Vector3 slip = Vector3.ProjectOnPlane(foot.Body.linearVelocity, Vector3.up);
                foot.Body.AddForce(-slip * 9f, ForceMode.Acceleration);
            }
        }

        void PoseArms(PuppetCommand cmd, bool fallen, bool effort)
        {
            PoseArm(true, cmd, fallen, effort);
            PoseArm(false, cmd, fallen, effort);
        }

        void PoseArm(bool left, PuppetCommand cmd, bool fallen, bool effort)
        {
            BodyPart upper = left ? _rig.UpperArmL : _rig.UpperArmR;
            BodyPart lower = left ? _rig.LowerArmL : _rig.LowerArmR;
            BodyPart hand = left ? _rig.HandL : _rig.HandR;
            float side = left ? -1f : 1f;
            Vector3 shoulder = _rig.Chest.transform.TransformPoint(left ? _rig.LeftShoulderLocal : _rig.RightShoulderLocal);
            float reachLen = _rig.UpperArmLength + _rig.LowerArmLength;
            Vector3 chestRight = _rig.Chest.transform.right;
            Vector3 dangle = shoulder + Vector3.down * reachLen * 0.92f + chestRight * side * 0.04f;
            float reach = Mathf.Clamp01(Mathf.Max(cmd.Reach, cmd.Grab ? 1f : 0f));
            Vector3 goal = Vector3.Lerp(dangle, _aim + chestRight * side * 0.1f, reach);

            if (fallen && effort)
            {
                goal = shoulder;
                goal.y = SampleGround(shoulder) + 0.1f;
                goal += chestRight * side * 0.16f + _facing * 0.22f;
                reach = 1f;
            }

            Vector3 pole = shoulder + chestRight * side * 0.55f - _facing * 0.35f + Vector3.down * 0.12f;
            IkMath.SolveTwoBone(shoulder, goal, pole, _rig.UpperArmLength, _rig.LowerArmLength, out Vector3 dirA, out Vector3 dirB);
            upper.TargetWorld = IkMath.RotationFromUp(dirA, pole - shoulder);
            lower.TargetWorld = IkMath.RotationFromUp(dirB, pole - shoulder);
            hand.TargetWorld = lower.TargetWorld;

            float stiff = Mathf.Lerp(15f, 46f, reach);
            if (cmd.Grab)
                stiff *= 1.65f;
            if (fallen && !effort)
                stiff *= 0.45f;
            upper.Stiffness = stiff;
            lower.Stiffness = stiff * 0.82f;
            hand.Stiffness = 11f;
            upper.MaxTorque = 24f;
            lower.MaxTorque = 20f;
            hand.MaxTorque = 9f;
        }

        void PoseTorso(PuppetCommand cmd, bool fallen)
        {
            float lean = fallen ? 22f : Mathf.Clamp(cmd.Move.magnitude, 0f, 1f) * 14f;
            float roll = fallen ? 0f : -cmd.Move.x * 8f;
            Quaternion spine = _rig.Pelvis.transform.rotation * Quaternion.Euler(lean, 0f, roll);
            _rig.Spine.TargetWorld = spine;
            _rig.Chest.TargetWorld = spine * Quaternion.Euler(lean * 0.35f, 0f, 0f);

            Vector3 toAim = _aim - _rig.Head.transform.position;
            Vector3 look = toAim.sqrMagnitude > 0.0001f ? toAim.normalized : _rig.Chest.transform.forward;
            Vector3 fwd = Vector3.Slerp(_rig.Chest.transform.forward, look, 0.7f);
            _rig.Head.TargetWorld = IkMath.SafeLook(fwd, Vector3.up);

            _rig.Spine.Stiffness = fallen ? 10f : 24f;
            _rig.Chest.Stiffness = fallen ? 10f : 20f;
            _rig.Head.Stiffness = 9f;
            _rig.Spine.MaxTorque = 18f;
            _rig.Chest.MaxTorque = 16f;
            _rig.Head.MaxTorque = 8f;
        }

        void ApplyMuscles(float flopScale)
        {
            BodyPart[] parts = _rig.All;
            for (int i = 0; i < parts.Length; i++)
            {
                BodyPart part = parts[i];
                if (part == _rig.Pelvis)
                    continue;
                float stiff = part.Stiffness * flopScale;
                float damp = 2.05f * Mathf.Sqrt(Mathf.Max(stiff, 0.01f));
                Vector3 torque = IkMath.PdTorque(
                    part.Body.rotation,
                    part.TargetWorld,
                    part.Body.angularVelocity,
                    stiff,
                    damp,
                    part.MaxTorque * flopScale);
                part.Body.AddTorque(torque, ForceMode.Acceleration);
                if (part.ParentPart != null)
                    part.ParentPart.Body.AddTorque(-torque * part.Reaction, ForceMode.Acceleration);
            }
        }

        void ApplyBalance(PuppetCommand cmd, bool fallen, bool effort, float flopScale, float uprightDot)
        {
            Rigidbody pelvis = _rig.Pelvis.Body;
            Vector3 up = _rig.Pelvis.transform.up;
            Vector3 axis = Vector3.zero;
            float angle = 0f;
            if (uprightDot < -0.15f)
                axis = Vector3.Cross(up, Vector3.up + _facing * 0.35f);
            else
            {
                Quaternion fix = Quaternion.FromToRotation(up, Vector3.up);
                fix.ToAngleAxis(out angle, out axis);
                if (axis.sqrMagnitude < 1e-6f)
                    axis = Vector3.Cross(up, _facing);
            }

            if (axis.sqrMagnitude > 1e-6f)
            {
                axis.Normalize();
                Quaternion fixAll = Quaternion.FromToRotation(up, Vector3.up);
                fixAll.ToAngleAxis(out angle, out _);
                if (angle > 180f)
                    angle -= 360f;
                float gain = (fallen ? (effort ? 11f : 1.4f) : 8.5f) * flopScale;
                Vector3 av = pelvis.angularVelocity;
                Vector3 torque = axis * (angle * Mathf.Deg2Rad * gain) - new Vector3(av.x, 0f, av.z) * 3.2f;
                if (!fallen)
                {
                    Vector3 fwd = Vector3.ProjectOnPlane(_rig.Pelvis.transform.forward, Vector3.up);
                    if (fwd.sqrMagnitude > 1e-5f)
                    {
                        float yaw = Vector3.SignedAngle(fwd.normalized, _facing, Vector3.up);
                        torque += Vector3.up * (yaw * Mathf.Deg2Rad * 7f - av.y * 1.6f);
                    }
                }
                pelvis.AddTorque(Vector3.ClampMagnitude(torque, fallen && effort ? 22f : 14f), ForceMode.Acceleration);
            }

            Vector2 move = cmd.Move;
            if (move.sqrMagnitude > 1f)
                move.Normalize();
            Vector3 wish = Quaternion.Euler(0f, cmd.CameraYaw, 0f) * new Vector3(move.x, 0f, move.y);
            float speed = (Tuning != null ? Tuning.MoveSpeed : 2.25f) * Mathf.Lerp(1.05f, 0.75f, Tuning != null ? Tuning.Floppiness : 0.5f);
            bool grounded = FootGrounded(_left) || FootGrounded(_right);
            if (!fallen)
            {
                Vector3 vel = Vector3.ProjectOnPlane(pelvis.linearVelocity, Vector3.up);
                Vector3 delta = wish * speed - vel;
                pelvis.AddForce(delta * (grounded ? 9.5f : 3.5f), ForceMode.Acceleration);
                if (grounded && pelvis.linearVelocity.y < -0.35f)
                    pelvis.AddForce(Vector3.up * (-pelvis.linearVelocity.y) * 3.5f, ForceMode.Acceleration);
                if (cmd.JumpPressed && grounded)
                    pelvis.AddForce(Vector3.up * 5.1f, ForceMode.VelocityChange);
            }
            else if (effort)
            {
                pelvis.AddForce(wish * 3.2f, ForceMode.Acceleration);
            }
        }

        void ApplyClimbPull(float flopScale)
        {
            Vector3 pull = Vector3.zero;
            int anchors = 0;
            if (_rig.GrabL.ClimbAnchor)
            {
                pull += _rig.HandL.transform.position;
                anchors++;
            }
            if (_rig.GrabR.ClimbAnchor)
            {
                pull += _rig.HandR.transform.position;
                anchors++;
            }
            if (anchors == 0)
                return;

            pull /= anchors;
            Vector3 to = pull - _rig.Pelvis.transform.position;
            if (to.sqrMagnitude < 0.04f)
                return;
            Vector3 force = to.normalized * 7.5f * flopScale;
            if (to.y > 0.15f)
                force += Vector3.up * Mathf.Clamp(to.y, 0f, 1.2f) * 5.5f * flopScale;
            _rig.Pelvis.Body.AddForce(force, ForceMode.Acceleration);
        }

        bool FootGrounded(Leg leg)
        {
            if (leg.Swinging)
                return false;
            BodyPart foot = leg.Left ? _rig.FootL : _rig.FootR;
            return Physics.Raycast(foot.transform.position + Vector3.up * 0.05f, Vector3.down, 0.22f, GameLayers.WorldMask, QueryTriggerInteraction.Ignore);
        }

        Vector3 Hip(bool left)
        {
            return _rig.Pelvis.transform.TransformPoint(left ? _rig.LeftHipLocal : _rig.RightHipLocal);
        }

        float SampleGround(Vector3 point)
        {
            Vector3 origin = new Vector3(point.x, Mathf.Max(point.y, 0.2f) + 2.2f, point.z);
            if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, 8f, GameLayers.WorldMask, QueryTriggerInteraction.Ignore))
                return hit.point.y;
            return 0f;
        }

        void ClampMotion()
        {
            BodyPart[] parts = _rig.All;
            for (int i = 0; i < parts.Length; i++)
            {
                Rigidbody body = parts[i].Body;
                Vector3 v = body.linearVelocity;
                if (float.IsNaN(v.x) || float.IsInfinity(v.x))
                {
                    _respawn = true;
                    return;
                }
                float maxSpeed = parts[i] == _rig.Pelvis ? 8f : 11f;
                float maxSpin = parts[i] == _rig.Pelvis ? 8f : 14f;
                float speed = v.magnitude;
                if (speed > maxSpeed)
                    body.linearVelocity = v * (maxSpeed / speed);
                Vector3 w = body.angularVelocity;
                float spin = w.magnitude;
                if (spin > maxSpin)
                    body.angularVelocity = w * (maxSpin / spin);
            }
        }

        bool OutOfBounds()
        {
            Vector3 p = _rig.Pelvis.transform.position;
            return p.y < -2f || p.y > 14f || Mathf.Abs(p.x) > 26f || Mathf.Abs(p.z) > 26f;
        }

        public void Respawn()
        {
            _jumpLatch = false;
            _downTimer = 0f;
            _facing = _rig.SpawnRotation * Vector3.forward;
            _aim = _rig.SpawnPosition + _facing * 1.2f + Vector3.up * 1.2f;
            _rig.GrabL.Release();
            _rig.GrabR.Release();

            BodyPart[] parts = _rig.All;
            for (int i = 0; i < parts.Length; i++)
            {
                BodyPart part = parts[i];
                Rigidbody body = part.Body;
                body.interpolation = RigidbodyInterpolation.None;
                body.position = part.BindPosition;
                body.rotation = part.BindRotation;
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                body.interpolation = RigidbodyInterpolation.Interpolate;
                part.TargetWorld = part.BindRotation;
                body.WakeUp();
            }

            _left.Swinging = false;
            _right.Swinging = false;
            _left.Cooldown = 0.1f;
            _right.Cooldown = 0.1f;
            // 刚体传送要等下一步物理才会写回 Transform，落点用绑定姿态算。
            _left.Plant = BindAnkle(_rig.FootL);
            _right.Plant = BindAnkle(_rig.FootR);
        }

        static Vector3 BindAnkle(BodyPart foot)
        {
            if (foot.Joint == null)
                return foot.BindPosition;
            return foot.BindPosition + foot.BindRotation * foot.Joint.anchor;
        }

        static Vector3 AnkleWorld(BodyPart foot)
        {
            return foot.Joint != null ? foot.transform.TransformPoint(foot.Joint.anchor) : foot.transform.position;
        }

        void OnDrawGizmos()
        {
            if (!Application.isPlaying || _rig == null || _left == null)
                return;
            Gizmos.color = new Color(0.3f, 0.9f, 0.4f, 0.9f);
            Gizmos.DrawSphere(_left.Plant, 0.045f);
            Gizmos.DrawSphere(_right.Plant, 0.045f);
            Gizmos.color = new Color(1f, 0.85f, 0.2f, 0.9f);
            Gizmos.DrawLine(_rig.Chest.transform.position, _aim);
            Gizmos.DrawSphere(_aim, 0.05f);
        }
    }
}
