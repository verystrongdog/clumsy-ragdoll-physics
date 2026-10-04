using System.Collections.Generic;
using UnityEngine;

namespace RaccoonStep
{
    /// <summary>
    /// RaccoonStep 的独立物理代理骨架。
    /// 代理刚体与导入的 FBX 骨骼分离，避免破坏视觉模型和旧 ClumsyRagdoll 系统。
    /// 当前默认以运动学方式构建，后续由 StepMotor/B alance 统一接管。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RaccoonPhysicsRig : MonoBehaviour
    {
        public RaccoonBoneMap BoneMap;
        public bool BuildOnAwake = true;
        // The project currently contains another physics-driven demo that can switch
        // Unity into Script simulation mode. This rig is self-contained, so make sure
        // the normal FixedUpdate loop is running before any proxy bodies are built.
        public bool ConfigurePhysicsSimulation = true;
        public bool PhysicsActive;
        public bool DriveVisualBones = true;
        public float ColliderRadius = 0.055f;
        public float FootRadius = 0.075f;
        [Tooltip("Actual capsule radius used by the foot proxy. Kept smaller than FootRadius to avoid standing below the floor.")]
        public float FootColliderRadius = 0.06f;
        [Tooltip("Initial upward offset of the foot collider relative to the foot bone segment.")]
        public float FootColliderLift = 0.075f;
        [Tooltip("Minimum radius used by hand proxies to cover the rendered palm and fingers.")]
        public float HandColliderRadius = 0.055f;
        public float JointSpring = 700f;
        public float JointDamper = 45f;
        [Tooltip("Smallest mass assigned to a proxy body. Avoids unstable tiny hand bodies.")]
        public float MinimumProxyMass = 0.5f;
        [Tooltip("Per-body solver iterations used by the proxy rig.")]
        public int ProxySolverIterations = 12;
        [Tooltip("Per-body velocity solver iterations used by the proxy rig.")]
        public int ProxySolverVelocityIterations = 12;
        [Tooltip("Caps the speed used to resolve accidental collider overlap.")]
        public float ProxyMaxDepenetrationVelocity = 0.5f;
        [Tooltip("Number of fixed steps used to hand proxy bodies to dynamic physics during a fall.")]
        public int HybridReleaseFixedSteps = 3;

        public int BodyCount { get; private set; }
        public int JointCount { get; private set; }
        public Transform PhysicsRoot { get; private set; }

        readonly Dictionary<Transform, ProxyBinding> _visualToProxy = new Dictionary<Transform, ProxyBinding>();
        readonly List<Rigidbody> _bodies = new List<Rigidbody>();
        readonly List<Collider> _colliders = new List<Collider>();
        readonly List<ConfigurableJoint> _joints = new List<ConfigurableJoint>();
        bool _built;

        readonly List<Vector3> _initialBodyLocalPositions = new List<Vector3>();
        readonly List<Quaternion> _initialBodyLocalRotations = new List<Quaternion>();
        readonly List<Vector3> _fallStartWorldPositions = new List<Vector3>();
        readonly List<Quaternion> _fallStartWorldRotations = new List<Quaternion>();
        bool _gradualDynamicRelease;
        int _dynamicReleaseStep;


        void Awake()
        {
            EnsurePhysicsSimulation();
            if (BuildOnAwake)
                Build();
        }

        void FixedUpdate()
        {
            if (!_gradualDynamicRelease || !_built || _bodies.Count == 0)
                return;

            int totalSteps = Mathf.Max(1, HybridReleaseFixedSteps);
            _dynamicReleaseStep++;
            int start = ((_dynamicReleaseStep - 1) * _bodies.Count) / totalSteps;
            int end = (_dynamicReleaseStep * _bodies.Count) / totalSteps;
            for (int i = start; i < end; i++)
                ActivateDynamicBody(_bodies[i]);

            if (_dynamicReleaseStep >= totalSteps)
                _gradualDynamicRelease = false;
        }

        void EnsurePhysicsSimulation()
        {
            if (!ConfigurePhysicsSimulation)
                return;

            if (Physics.simulationMode != SimulationMode.FixedUpdate)
            {
                Physics.simulationMode = SimulationMode.FixedUpdate;
                Debug.Log("[RaccoonStep] Enabled automatic FixedUpdate physics simulation.", this);
            }
        }

        [ContextMenu("Build Physics Proxy Rig")]
        public void Build()
        {
            if (_built)
                return;

            BoneMap = BoneMap != null ? BoneMap : GetComponent<RaccoonBoneMap>();
            if (BoneMap == null)
                BoneMap = gameObject.AddComponent<RaccoonBoneMap>();

            BoneMap.BuildFrom(transform);
            if (!BoneMap.IsValid)
            {
                Debug.LogError($"[RaccoonStep] Cannot build physics rig: {BoneMap.ValidationReport}", this);
                return;
            }

            GameObject root = new GameObject("RaccoonPhysicsRoot");
            PhysicsRoot = root.transform;
            PhysicsRoot.SetParent(transform, false);
            PhysicsRoot.localPosition = Vector3.zero;
            PhysicsRoot.localRotation = Quaternion.identity;
            PhysicsRoot.localScale = Vector3.one;

            ProxyPart hips = CreatePart("HipsBody", BoneMap.hips, BoneMap.spine, 5.0f, ColliderRadius * 1.8f);
            ProxyPart spine = CreatePart("SpineBody", BoneMap.spine, BoneMap.neck != null ? BoneMap.neck : BoneMap.head, 2.5f, ColliderRadius * 1.45f);
            ProxyPart head = CreatePart("HeadBody", BoneMap.head, null, 1.0f, ColliderRadius * 1.2f);
            ProxyPart leftUpper = CreatePart("LeftUpperLegBody", BoneMap.leftUpperLeg, BoneMap.leftLowerLeg, 1.6f, ColliderRadius);
            ProxyPart leftLower = CreatePart("LeftLowerLegBody", BoneMap.leftLowerLeg, BoneMap.leftFoot, 1.2f, ColliderRadius * 0.85f);
            ProxyPart leftFoot = CreatePart("LeftFootBody", BoneMap.leftFoot, BoneMap.leftToe, 0.7f, FootRadius);
            ProxyPart rightUpper = CreatePart("RightUpperLegBody", BoneMap.rightUpperLeg, BoneMap.rightLowerLeg, 1.6f, ColliderRadius);
            ProxyPart rightLower = CreatePart("RightLowerLegBody", BoneMap.rightLowerLeg, BoneMap.rightFoot, 1.2f, ColliderRadius * 0.85f);
            ProxyPart rightFoot = CreatePart("RightFootBody", BoneMap.rightFoot, BoneMap.rightToe, 0.7f, FootRadius);
            ProxyPart leftUpperArm = CreatePart("LeftUpperArmBody", BoneMap.leftUpperArm, BoneMap.leftLowerArm, 0.8f, ColliderRadius * 0.75f);
            ProxyPart leftLowerArm = CreatePart("LeftLowerArmBody", BoneMap.leftLowerArm, BoneMap.leftHand, 0.6f, ColliderRadius * 0.65f);
            ProxyPart leftHand = CreatePart("LeftHandBody", BoneMap.leftHand, null, 0.35f, ColliderRadius * 0.7f);
            ProxyPart rightUpperArm = CreatePart("RightUpperArmBody", BoneMap.rightUpperArm, BoneMap.rightLowerArm, 0.8f, ColliderRadius * 0.75f);
            ProxyPart rightLowerArm = CreatePart("RightLowerArmBody", BoneMap.rightLowerArm, BoneMap.rightHand, 0.6f, ColliderRadius * 0.65f);
            ProxyPart rightHand = CreatePart("RightHandBody", BoneMap.rightHand, null, 0.35f, ColliderRadius * 0.7f);

            Connect(spine, hips, false);
            Connect(head, spine, false);
            Connect(leftUpper, hips, false);
            Connect(leftLower, leftUpper, false);
            Connect(leftFoot, leftLower, false);
            Connect(rightUpper, hips, false);
            Connect(rightLower, rightUpper, false);
            Connect(rightFoot, rightLower, false);
            Connect(leftUpperArm, spine, false);
            Connect(leftLowerArm, leftUpperArm, false);
            Connect(leftHand, leftLowerArm, false);
            Connect(rightUpperArm, spine, false);
            Connect(rightLowerArm, rightUpperArm, false);
            Connect(rightHand, rightLowerArm, false);

            // The proxy rig is an articulated body. Internal capsule collisions
            // otherwise make the limbs push one another apart and immediately
            // topple the character. External collisions (for example the demo
            // platform) remain enabled.
            IgnoreInternalCollisions();

            SetPhysicsActive(PhysicsActive);
            _initialBodyLocalPositions.Clear();
            _initialBodyLocalRotations.Clear();
            for (int i = 0; i < _bodies.Count; i++)
            {
                Rigidbody body = _bodies[i];
                _initialBodyLocalPositions.Add(body != null ? body.transform.localPosition : Vector3.zero);
                _initialBodyLocalRotations.Add(body != null ? body.transform.localRotation : Quaternion.identity);
            }

            _built = true;
            BodyCount = _bodies.Count;
            JointCount = _joints.Count;

            Debug.Log($"[RaccoonStep] Physics proxy rig built: bodies={BodyCount}, joints={JointCount}, active={PhysicsActive}", this);
        }

        public void SetPhysicsActive(bool active)
        {
            PhysicsActive = active;
            _gradualDynamicRelease = false;
            _dynamicReleaseStep = 0;
            for (int i = 0; i < _bodies.Count; i++)
            {
                Rigidbody body = _bodies[i];
                if (body == null) continue;
                body.isKinematic = !active;
                body.useGravity = active;
            }
        }

        void ActivateDynamicBody(Rigidbody body)
        {
            if (body == null || !body.isKinematic)
                return;

            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.isKinematic = false;
            body.useGravity = true;
        }

        public void BeginKinematicFallPose()
        {
            _fallStartWorldPositions.Clear();
            _fallStartWorldRotations.Clear();
            for (int i = 0; i < _bodies.Count; i++)
            {
                Rigidbody body = _bodies[i];
                _fallStartWorldPositions.Add(body != null ? body.position : Vector3.zero);
                _fallStartWorldRotations.Add(body != null ? body.rotation : Quaternion.identity);
            }
        }

        public void MoveKinematicFallPose(Vector3 pivotWorld, Quaternion deltaRotation, Vector3 translation)
        {
            if (_fallStartWorldPositions.Count != _bodies.Count)
                BeginKinematicFallPose();

            for (int i = 0; i < _bodies.Count; i++)
            {
                Rigidbody body = _bodies[i];
                if (body == null)
                    continue;

                Vector3 targetPosition = pivotWorld
                    + deltaRotation * (_fallStartWorldPositions[i] - pivotWorld)
                    + translation;
                Quaternion targetRotation = deltaRotation * _fallStartWorldRotations[i];
                body.MovePosition(targetPosition);
                body.MoveRotation(targetRotation);
            }

            // MovePosition on a kinematic Rigidbody is a commanded pose, not
            // a physical sweep. During the controlled part of a fall it can
            // therefore place a hand/foot capsule below the platform before
            // the rig is released to dynamic physics. Resolve that overlap
            // immediately so the later handoff never starts from penetration.
            ResolveKinematicGroundPenetration();
        }

        void ResolveKinematicGroundPenetration()
        {
            if (_colliders.Count == 0)
                return;

            Physics.SyncTransforms();
            Collider[] sceneColliders = FindObjectsByType<Collider>(FindObjectsSortMode.None);
            for (int pass = 0; pass < 2; pass++)
            {
                bool corrected = false;
                for (int i = 0; i < _colliders.Count; i++)
                {
                    Collider proxyCollider = _colliders[i];
                    Rigidbody proxyBody = i < _bodies.Count ? _bodies[i] : null;
                    if (proxyCollider == null || proxyBody == null)
                        continue;

                    Vector3 correction = Vector3.zero;
                    for (int j = 0; j < sceneColliders.Length; j++)
                    {
                        Collider other = sceneColliders[j];
                        if (other == null || other == proxyCollider || !other.enabled || other.isTrigger)
                            continue;

                        // Ignore the generated ragdoll's own colliders. They
                        // are already excluded from normal contact handling.
                        if (other.transform.IsChildOf(PhysicsRoot))
                            continue;

                        Rigidbody otherBody = other.attachedRigidbody;
                        if (otherBody != null && otherBody.transform.IsChildOf(PhysicsRoot))
                            continue;

                        Vector3 direction;
                        float distance;
                        if (Physics.ComputePenetration(
                            proxyCollider, proxyBody.position, proxyBody.rotation,
                            other, other.transform.position, other.transform.rotation,
                            out direction, out distance))
                        {
                            correction += direction * (distance + 0.002f);
                        }
                    }

                    if (correction.sqrMagnitude > 0.0000001f)
                    {
                        proxyBody.position += correction;
                        corrected = true;
                    }
                }

                if (!corrected)
                    break;

                Physics.SyncTransforms();
            }
        }

        public Transform GetProxyFor(Transform visualBone)
        {
            if (visualBone == null)
                return null;

            ProxyBinding binding;
            return _visualToProxy.TryGetValue(visualBone, out binding) && binding != null
                ? binding.Proxy
                : null;
        }

        void LateUpdate()
        {
            if (!_built || !DriveVisualBones)
                return;

            foreach (KeyValuePair<Transform, ProxyBinding> pair in _visualToProxy)
            {
                ProxyBinding binding = pair.Value;
                if (binding == null || binding.Visual == null || binding.Proxy == null) continue;

                // A proxy capsule is centered on a bone segment, while the
                // visual bone is located at the segment's joint/origin. Keep
                // that original offset instead of writing the capsule center
                // directly onto the visual skeleton.
                binding.Visual.SetPositionAndRotation(
                    binding.Proxy.TransformPoint(binding.PositionOffsetLocal),
                    binding.Proxy.rotation * binding.RotationOffsetLocal);
            }
        }

ProxyPart CreatePart(string name, Transform visualBone, Transform visualChild, float mass, float radius)
        {
            if (visualBone == null)
                return null;

            GameObject go = new GameObject(name);
            Transform proxy = go.transform;
            proxy.SetParent(PhysicsRoot, true);

            Vector3 start = visualBone.position;
            Vector3 end = visualChild != null ? visualChild.position : start + visualBone.up * 0.12f;
            Vector3 axis = end - start;
            float length = Mathf.Max(0.08f, axis.magnitude);
            Vector3 midpoint = Vector3.Lerp(start, end, 0.5f);

            proxy.position = midpoint;
            proxy.rotation = axis.sqrMagnitude > 0.0001f
                ? Quaternion.FromToRotation(Vector3.up, axis.normalized)
                : visualBone.rotation;
            proxy.localScale = Vector3.one;

            float colliderRadius = radius;
            Vector3 colliderCenter = Vector3.zero;
            if (name.Contains("Foot"))
            {
                colliderRadius = Mathf.Max(0.001f, FootColliderRadius);
                colliderCenter = proxy.InverseTransformVector(Vector3.up * FootColliderLift);
            }
            else if (name.Contains("Hand"))
            {
                colliderRadius = Mathf.Max(colliderRadius, HandColliderRadius);
            }

            CapsuleCollider capsule = go.AddComponent<CapsuleCollider>();
            capsule.direction = 1;
            capsule.radius = colliderRadius;
            capsule.height = length + colliderRadius * 2f;
            capsule.center = colliderCenter;

            Rigidbody body = go.AddComponent<Rigidbody>();
            body.mass = Mathf.Max(mass, MinimumProxyMass);
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            body.maxDepenetrationVelocity = Mathf.Max(0.01f, ProxyMaxDepenetrationVelocity);
            body.solverIterations = Mathf.Max(1, ProxySolverIterations);
            body.solverVelocityIterations = Mathf.Max(1, ProxySolverVelocityIterations);
            body.isKinematic = true;
            body.useGravity = false;

            _bodies.Add(body);
            _colliders.Add(capsule);
            _visualToProxy[visualBone] = new ProxyBinding(visualBone, proxy);
            return new ProxyPart(proxy, body, start, end);
        }

        void IgnoreInternalCollisions()
        {
            for (int i = 0; i < _colliders.Count; i++)
            {
                for (int j = i + 1; j < _colliders.Count; j++)
                    Physics.IgnoreCollision(_colliders[i], _colliders[j], true);
            }
        }

        void Connect(ProxyPart child, ProxyPart parent, bool allowFootTranslation)
        {
            if (child == null || parent == null)
                return;

            ConfigurableJoint joint = child.Transform.gameObject.AddComponent<ConfigurableJoint>();
            joint.connectedBody = parent.Body;
            joint.autoConfigureConnectedAnchor = false;
            Vector3 jointWorldPosition = child.Start;
            joint.anchor = child.Transform.InverseTransformPoint(jointWorldPosition);
            joint.connectedAnchor = parent.Transform.InverseTransformPoint(jointWorldPosition);
            joint.xMotion = allowFootTranslation ? ConfigurableJointMotion.Free : ConfigurableJointMotion.Locked;
            joint.yMotion = allowFootTranslation ? ConfigurableJointMotion.Free : ConfigurableJointMotion.Locked;
            joint.zMotion = allowFootTranslation ? ConfigurableJointMotion.Free : ConfigurableJointMotion.Locked;
            joint.angularXMotion = ConfigurableJointMotion.Limited;
            joint.angularYMotion = ConfigurableJointMotion.Limited;
            joint.angularZMotion = ConfigurableJointMotion.Limited;
            joint.lowAngularXLimit = new SoftJointLimit { limit = -35f };
            joint.highAngularXLimit = new SoftJointLimit { limit = 35f };
            joint.angularYLimit = new SoftJointLimit { limit = 35f };
            joint.angularZLimit = new SoftJointLimit { limit = 35f };
            joint.rotationDriveMode = RotationDriveMode.Slerp;// Projection disabled to avoid contact bounce.


            joint.slerpDrive = new JointDrive
            {
                positionSpring = JointSpring,
                positionDamper = JointDamper,
                maximumForce = Mathf.Infinity
            };

            _joints.Add(joint);

        }

        sealed class ProxyBinding
        {
            public readonly Transform Visual;
            public readonly Transform Proxy;
            public readonly Vector3 PositionOffsetLocal;
            public readonly Quaternion RotationOffsetLocal;

            public ProxyBinding(Transform visual, Transform proxy)
            {
                Visual = visual;
                Proxy = proxy;
                PositionOffsetLocal = proxy.InverseTransformPoint(visual.position);
                RotationOffsetLocal = Quaternion.Inverse(proxy.rotation) * visual.rotation;
            }
        }

        sealed class ProxyPart
        {
            public readonly Transform Transform;
            public readonly Rigidbody Body;
            public readonly Vector3 Start;
            public readonly Vector3 End;

            public ProxyPart(Transform transform, Rigidbody body, Vector3 start, Vector3 end)
            {
                Transform = transform;
                Body = body;
                Start = start;
                End = end;
            }
        }
    

public void BeginHybridFall(float jointStrength, float jointDamperScale)
        {
            float springScale = Mathf.Clamp01(jointStrength);
            float damperScale = Mathf.Clamp01(jointDamperScale);
            for (int i = 0; i < _joints.Count; i++)
            {
                ConfigurableJoint joint = _joints[i];
                if (joint == null)
                    continue;

                JointDrive drive = joint.slerpDrive;
                drive.positionSpring = JointSpring * springScale;
                drive.positionDamper = JointDamper * damperScale;
                drive.maximumForce = Mathf.Infinity;
                joint.slerpDrive = drive;
            }
            // Do not switch all proxy bodies in one physics frame. Releasing
            // them in small batches avoids a visible solver spike while the
            // current pose and zero initial velocities are preserved.
            PhysicsActive = true;
            _gradualDynamicRelease = true;
            _dynamicReleaseStep = 0;
            for (int i = 0; i < _bodies.Count; i++)
            {
                Rigidbody body = _bodies[i];
                if (body == null)
                    continue;
                body.isKinematic = true;
                body.useGravity = false;
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
        }


public void ResetToInitialPose()
        {
            if (!_built)
                return;

            // Clear any dynamic velocities while bodies are dynamic, then
            // restore the complete cached kinematic pose.
            SetPhysicsActive(true);
            for (int i = 0; i < _bodies.Count; i++)
            {
                Rigidbody body = _bodies[i];
                if (body == null)
                    continue;
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }

            SetPhysicsActive(false);
            for (int i = 0; i < _bodies.Count; i++)
            {
                Rigidbody body = _bodies[i];
                if (body == null)
                    continue;

                body.transform.localPosition = _initialBodyLocalPositions[i];
                body.transform.localRotation = _initialBodyLocalRotations[i];
            }

            SyncVisualBonesToProxy();
        }

        void SyncVisualBonesToProxy()
        {
            foreach (KeyValuePair<Transform, ProxyBinding> pair in _visualToProxy)
            {
                ProxyBinding binding = pair.Value;
                if (binding == null || binding.Visual == null || binding.Proxy == null)
                    continue;

                binding.Visual.SetPositionAndRotation(
                    binding.Proxy.TransformPoint(binding.PositionOffsetLocal),
                    binding.Proxy.rotation * binding.RotationOffsetLocal);
            }
        }
}
}
