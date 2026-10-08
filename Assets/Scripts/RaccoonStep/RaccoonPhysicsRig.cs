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
        [Tooltip("Safety cap for recovery proxy speed so a long mouse hold cannot launch the ragdoll.")]
        public float RecoveryMaxLinearSpeed = 2.5f;
        [Tooltip("Additional cap for upward recovery speed. Prevents repeated mouse pulls from launching the pelvis while still allowing the planned stand-up motion.")]
        public float RecoveryMaxUpwardSpeed = 0.75f;
        [Tooltip("Safety cap for recovery proxy angular speed so repeated drives remain stable.")]
        public float RecoveryMaxAngularSpeed = 8f;
        [Tooltip("Number of fixed steps used to hand proxy bodies to dynamic physics during a fall.")]
        public int HybridReleaseFixedSteps = 3;

        [Header("Fall collision envelope")]
        [Tooltip("Temporary multiplier for proxy collider radii during a fall. The imported visual mesh is much wider than the walking proxy, so this must be substantially larger than 1.")]
        public float FallColliderScale = 3.5f;
        [Tooltip("Maximum depenetration speed used only while the fall envelope is active.")]
        public float FallMaxDepenetrationVelocity = 4f;
        [Tooltip("Extra clearance kept between non-tail visible geometry and the ground during the controlled fall.")]
        public float FallVisualGroundMargin = 0.003f;
        [Tooltip("Ground height used by the visible-mesh fall clearance pass.")]
        public float FallGroundHeight = 0f;
        [Tooltip("Ignore tail materials when calculating visible-mesh ground clearance.")]
        public bool ExcludeTailMaterialFromFallClearance = true;
        [Tooltip("Enable the baked visible-mesh clearance pass. Disabled by default because baking a large skinned mesh during physics can stall the editor.")]
        public bool EnableFallVisualGroundClearance = false;
        public string TailMaterialKeyword = "尾巴";

        public int BodyCount { get; private set; }
        public int JointCount { get; private set; }
        public Transform PhysicsRoot { get; private set; }

        /// <summary>World-Y position of a foot proxy whose capsule bottom is
        /// exactly on the supplied ground plane. The foot bone target is not
        /// the same as the collider contact point because the capsule has an
        /// upward local offset.</summary>
        public float RecoveryFootContactHeight(float groundHeight)
        {
            return groundHeight - FootColliderLift + FootColliderRadius;
        }

        readonly Dictionary<Transform, ProxyBinding> _visualToProxy = new Dictionary<Transform, ProxyBinding>();
        readonly RaccoonProxyRegistry _proxyRegistry = new RaccoonProxyRegistry();
        ConfigurableJoint _recoverySpineJoint;
        ConfigurableJoint _recoveryOriginalSpineJoint;
        readonly List<Rigidbody> _bodies = new List<Rigidbody>();
        readonly List<Collider> _colliders = new List<Collider>();
        readonly List<float> _baseColliderRadii = new List<float>();
        readonly List<float> _baseColliderHeights = new List<float>();
        readonly List<float> _baseMaxDepenetrationVelocities = new List<float>();
        readonly List<ConfigurableJoint> _joints = new List<ConfigurableJoint>();
        SkinnedMeshRenderer[] _visualRenderers;
        Mesh _fallBakedMesh;
        bool _fallColliderMode;
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

        void OnDestroy()
        {
            EndRecoverySpineJoint();
            _proxyRegistry.Clear();
            if (_fallBakedMesh != null)
            {
                if (Application.isPlaying)
                    Destroy(_fallBakedMesh);
                else
                    DestroyImmediate(_fallBakedMesh);
                _fallBakedMesh = null;
            }

            if (PhysicsRoot != null)
            {
                GameObject root = PhysicsRoot.gameObject;
                PhysicsRoot = null;
                if (Application.isPlaying)
                    Destroy(root);
                else
                    DestroyImmediate(root);
            }
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
            RaccoonPhysicsLifecycle.EnsureAutomaticSimulation(ConfigurePhysicsSimulation, this);
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

            _visualRenderers = GetComponentsInChildren<SkinnedMeshRenderer>(true);
            _fallBakedMesh = new Mesh { name = "RaccoonFallGroundProbe" };

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
            RaccoonPhysicsLifecycle.SetBodiesActive(_bodies, active);
        }

        void ActivateDynamicBody(Rigidbody body)
        {
            if (body == null || !body.isKinematic)
                return;

            // A kinematic Rigidbody rejects velocity assignments. Switch it
            // to dynamic first, then clear the velocities before simulation.
            body.isKinematic = false;
            body.useGravity = true;
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }

        public void BeginKinematicFallPose()
        {
            SetFallColliderMode(true);
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

            // The proxy capsules approximate the skeleton and can be smaller
            // than the rendered body. Keep the visible non-tail geometry above
            // the ground before the dynamic handoff.
            SyncVisualBonesToProxy();
            if (EnableFallVisualGroundClearance)
                ResolveVisibleGroundPenetration();
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

        void SetFallColliderMode(bool active)
        {
            if (!_built && _colliders.Count == 0)
                return;

            _fallColliderMode = active;
            float scale = Mathf.Max(1f, FallColliderScale);
            for (int i = 0; i < _colliders.Count; i++)
            {
                CapsuleCollider capsule = _colliders[i] as CapsuleCollider;
                if (capsule == null)
                    continue;

                float baseRadius = i < _baseColliderRadii.Count
                    ? _baseColliderRadii[i] : capsule.radius;
                float baseHeight = i < _baseColliderHeights.Count
                    ? _baseColliderHeights[i] : capsule.height;
                float radius = active ? baseRadius * scale : baseRadius;
                capsule.radius = radius;
                capsule.height = baseHeight + 2f * (radius - baseRadius);

                if (i < _bodies.Count && _bodies[i] != null)
                {
                    _bodies[i].maxDepenetrationVelocity = active
                        ? Mathf.Max(0.01f, FallMaxDepenetrationVelocity)
                        : (i < _baseMaxDepenetrationVelocities.Count
                            ? _baseMaxDepenetrationVelocities[i]
                            : Mathf.Max(0.01f, ProxyMaxDepenetrationVelocity));
                }
            }
        }

        void ResolveVisibleGroundPenetration()
        {
            if (!_fallColliderMode || _visualRenderers == null || _fallBakedMesh == null)
                return;

            float minimumY = float.PositiveInfinity;
            for (int r = 0; r < _visualRenderers.Length; r++)
            {
                SkinnedMeshRenderer renderer = _visualRenderers[r];
                if (renderer == null || renderer.sharedMesh == null)
                    continue;

                renderer.BakeMesh(_fallBakedMesh, true);
                Material[] materials = renderer.sharedMaterials;
                int subMeshCount = Mathf.Min(_fallBakedMesh.subMeshCount, materials.Length);
                for (int sub = 0; sub < subMeshCount; sub++)
                {
                    Material material = materials[sub];
                    if (ExcludeTailMaterialFromFallClearance && IsTailMaterial(material))
                        continue;

                    int[] triangles = _fallBakedMesh.GetTriangles(sub);
                    for (int i = 0; i < triangles.Length; i++)
                    {
                        Vector3 world = renderer.transform.TransformPoint(
                            _fallBakedMesh.vertices[triangles[i]]);
                        minimumY = Mathf.Min(minimumY, world.y);
                    }
                }
            }

            if (float.IsPositiveInfinity(minimumY))
                return;

            float requiredY = FallGroundHeight + Mathf.Max(0f, FallVisualGroundMargin);
            float lift = requiredY - minimumY;
            if (lift <= 0f)
                return;

            for (int i = 0; i < _bodies.Count; i++)
            {
                Rigidbody body = _bodies[i];
                if (body != null && body.isKinematic)
                    body.MovePosition(body.position + Vector3.up * lift);
            }
            Physics.SyncTransforms();
        }

        bool IsTailMaterial(Material material)
        {
            if (material == null)
                return false;

            string name = material.name ?? string.Empty;
            return name.IndexOf("tail", System.StringComparison.OrdinalIgnoreCase) >= 0
                || (!string.IsNullOrEmpty(TailMaterialKeyword)
                    && name.IndexOf(TailMaterialKeyword, System.StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public Transform GetProxyFor(Transform visualBone)
        {
            if (visualBone == null)
                return null;

            ProxyBinding binding;
            if (_visualToProxy.TryGetValue(visualBone, out binding) && binding != null)
                return binding.Proxy;

            Transform registeredProxy;
            if (_proxyRegistry.TryGet(visualBone, out registeredProxy))
                return registeredProxy;

            // Domain reloads can preserve the runtime proxy hierarchy while
            // clearing this non-serialized dictionary. Rebuild the requested
            // binding from the stable proxy names instead of silently making
            // recovery a no-op.
            if (PhysicsRoot == null || BoneMap == null)
                return null;
            string proxyName = ProxyNameFor(visualBone);
            if (string.IsNullOrEmpty(proxyName))
                return null;
            Transform proxy = _proxyRegistry.FindOrRegister(visualBone, PhysicsRoot, proxyName);
            if (proxy == null)
                return null;
            _visualToProxy[visualBone] = new ProxyBinding(visualBone, proxy);
            _proxyRegistry.Register(visualBone, proxy);
            return proxy;
        }

        string ProxyNameFor(Transform visualBone)
        {
            if (BoneMap == null || visualBone == null)
                return null;
            if (visualBone == BoneMap.hips) return "HipsBody";
            if (visualBone == BoneMap.spine) return "SpineBody";
            if (visualBone == BoneMap.head) return "HeadBody";
            if (visualBone == BoneMap.leftUpperLeg) return "LeftUpperLegBody";
            if (visualBone == BoneMap.leftLowerLeg) return "LeftLowerLegBody";
            if (visualBone == BoneMap.leftFoot) return "LeftFootBody";
            if (visualBone == BoneMap.rightUpperLeg) return "RightUpperLegBody";
            if (visualBone == BoneMap.rightLowerLeg) return "RightLowerLegBody";
            if (visualBone == BoneMap.rightFoot) return "RightFootBody";
            if (visualBone == BoneMap.leftUpperArm) return "LeftUpperArmBody";
            if (visualBone == BoneMap.leftLowerArm) return "LeftLowerArmBody";
            if (visualBone == BoneMap.leftHand) return "LeftHandBody";
            if (visualBone == BoneMap.rightUpperArm) return "RightUpperArmBody";
            if (visualBone == BoneMap.rightLowerArm) return "RightLowerArmBody";
            if (visualBone == BoneMap.rightHand) return "RightHandBody";
            return null;
        }

        /// <summary>
        /// Physics-driven recovery target. The recovery controller supplies a
        /// desired segment pose; this method applies bounded PD force/torque
        /// instead of teleporting the proxy transform.
        /// </summary>
        public void DriveRecoverySegment(Transform proxy, Vector3 start, Vector3 end, Quaternion twist)
        {
            if (proxy == null)
                return;
            Rigidbody body = proxy.GetComponent<Rigidbody>();
            if (body == null || body.isKinematic)
                return;

            Vector3 axis = end - start;
            if (axis.sqrMagnitude < 0.000001f)
                axis = Vector3.up * 0.01f;
            Vector3 targetPosition = (start + end) * 0.5f;
            Quaternion targetRotation = (Quaternion.FromToRotation(Vector3.up, axis.normalized) * twist).normalized;
            DriveRecoveryBody(body, targetPosition, targetRotation);
        }

        public void DriveRecoveryPoint(Transform proxy, Vector3 targetPosition, Quaternion targetRotation)
        {
            if (proxy == null)
                return;
            Rigidbody body = proxy.GetComponent<Rigidbody>();
            if (body == null)
                return;
            if (body.isKinematic)
            {
                // The recovery pelvis is the single controlled root. Move it
                // kinematically so the articulated dynamic children follow
                // through their ConfigurableJoints without launching the rig.
                body.MovePosition(targetPosition);
                body.MoveRotation(targetRotation);
                return;
            }
            DriveRecoveryBody(body, targetPosition, targetRotation);
        }

        /// <summary>Scaled version used when contact points (hands/feet) are
        /// expected to contribute to a recovery motion. It keeps the target
        /// physical while avoiding a full-strength invisible position pull.</summary>
        public void DriveRecoveryPointScaled(Transform proxy, Vector3 targetPosition,
            Quaternion targetRotation, float strength)
        {
            if (proxy == null)
                return;
            Rigidbody body = proxy.GetComponent<Rigidbody>();
            if (body == null)
                return;
            if (body.isKinematic)
            {
                body.MovePosition(targetPosition);
                body.MoveRotation(targetRotation);
                return;
            }
            DriveRecoveryBody(body, targetPosition, targetRotation,
                Mathf.Clamp01(strength));
        }

        /// <summary>
        /// Drives only a dynamic proxy's orientation. Recovery uses this for
        /// the first torso phase so the locked joint anchors preserve the
        /// spine/head/arm distances and the ragdoll remains physically solved.
        /// </summary>
        public void DriveRecoveryRotation(Transform proxy, Quaternion targetRotation, float strength = 1f)
        {
            if (proxy == null || strength <= 0f)
                return;
            Rigidbody body = proxy.GetComponent<Rigidbody>();
            if (body == null || body.isKinematic)
                return;

            Quaternion delta = targetRotation * Quaternion.Inverse(body.rotation);
            delta.ToAngleAxis(out float angle, out Vector3 axis);
            if (angle > 180f) angle -= 360f;
            if (axis.sqrMagnitude < 0.000001f)
                return;

            Vector3 rotationAxis = axis.normalized;
            float angleRadians = angle * Mathf.Deg2Rad;
            float axisSpeed = Vector3.Dot(body.angularVelocity, rotationAxis);
            float driveStrength = Mathf.Clamp01(strength);
            // The spine has to rotate a connected upper-body mass while it
            // is still touching the floor.  The old gain was too small: it
            // produced a visible twitch, then the contact/joint solver
            // cancelled the remaining angular velocity.  Keep this as a
            // bounded velocity correction, but give it enough authority to
            // overcome the resting-contact torque.
            float requestedDeltaSpeed = angleRadians * 14.0f * driveStrength
                - axisSpeed * 1.6f * driveStrength;
            Vector3 velocityChange = rotationAxis
                * Mathf.Clamp(requestedDeltaSpeed, -25.0f * driveStrength, 25.0f * driveStrength);
            // A bounded angular-velocity correction is more reliable here
            // than acceleration on a dynamically constrained ConfigurableJoint.
            // It remains a Rigidbody drive and never writes Transform.rotation.
            body.AddTorque(velocityChange, ForceMode.VelocityChange);
            body.angularVelocity = Vector3.ClampMagnitude(body.angularVelocity, Mathf.Max(0.1f, RecoveryMaxAngularSpeed));
        }

        public void DriveRecoveryUpright(Transform proxy, float strength = 1f)
        {
            if (proxy == null || strength <= 0f) return;
            Rigidbody body = proxy.GetComponent<Rigidbody>();
            if (body == null || body.isKinematic) return;
            Vector3 axisError = Vector3.Cross(proxy.up, Vector3.up);
            float sin = axisError.magnitude;
            if (sin < 0.0001f) return;
            float angle = Mathf.Asin(Mathf.Clamp(sin, -1f, 1f));
            Vector3 correctionAxis = axisError / sin;
            float s = Mathf.Clamp01(strength);
            // The joint solver consumes ordinary acceleration torque while
            // the torso is in floor contact. Use a bounded velocity change,
            // continuously refreshed each FixedUpdate, so the correction is
            // applied after the solver has resolved the previous step.
            float requestedSpeed = Mathf.Clamp(angle * 10f
                - Vector3.Dot(body.angularVelocity, correctionAxis) * 0.8f,
                -12f, 12f) * s;
            body.AddTorque(correctionAxis * requestedSpeed, ForceMode.VelocityChange);
            body.angularVelocity = Vector3.ClampMagnitude(body.angularVelocity, Mathf.Max(0.1f, RecoveryMaxAngularSpeed));
        }

        /// <summary>Physics-joint target drive used for the torso recovery.
        /// The body remains dynamic; the ConfigurableJoint solver applies the
        /// corrective rotation instead of an external torque being cancelled
        /// by the same solver.</summary>
        public void DriveRecoveryJointTarget(Transform proxy, Quaternion worldTarget, float strength = 1f)
        {
            if (proxy == null) return;
            ConfigurableJoint joint = proxy.GetComponent<ConfigurableJoint>();
            if (joint == null) return;
            joint.angularXMotion = ConfigurableJointMotion.Free;
            joint.angularYMotion = ConfigurableJointMotion.Free;
            joint.angularZMotion = ConfigurableJointMotion.Free;
            joint.rotationDriveMode = RotationDriveMode.Slerp;
            joint.configuredInWorldSpace = false;
            JointDrive drive = joint.slerpDrive;
            float s = Mathf.Clamp01(strength);
            drive.positionSpring = Mathf.Lerp(0f, 1800f, s);
            drive.positionDamper = Mathf.Lerp(0f, 180f, s);
            drive.maximumForce = Mathf.Infinity;
            joint.slerpDrive = drive;
            Rigidbody parent = joint.connectedBody;
            Quaternion parentRotation = parent != null ? parent.rotation : Quaternion.identity;
            Quaternion currentRelative = Quaternion.Inverse(parentRotation) * proxy.rotation;
            Quaternion desiredRelative = Quaternion.Inverse(parentRotation) * worldTarget;
            Quaternion jointSpace = Quaternion.LookRotation(joint.axis, joint.secondaryAxis);
            joint.targetRotation = Quaternion.Inverse(jointSpace)
                * Quaternion.Inverse(desiredRelative) * currentRelative * jointSpace;
        }

        public void BeginRecoverySpineJoint(Transform spine, Transform hips)
        {
            EndRecoverySpineJoint();
            if (spine == null || hips == null) return;
            _recoveryOriginalSpineJoint = spine.GetComponent<ConfigurableJoint>();
            _recoverySpineJoint = RaccoonRecoveryJoint.Create(spine, hips, _recoveryOriginalSpineJoint);
        }

        public void DriveRecoverySpineJoint(Quaternion worldTarget, float strength = 1f)
        {
            if (_recoverySpineJoint == null) return;
            RaccoonRecoveryJoint.Drive(_recoverySpineJoint, worldTarget, strength);
        }

        public void SetRecoverySpineDrive(bool enabled)
        {
            RaccoonRecoveryJoint.SetDriveEnabled(_recoverySpineJoint, enabled);
        }

        public void EndRecoverySpineJoint()
        {
            RaccoonRecoveryJoint.Release(_recoverySpineJoint);
            _recoverySpineJoint = null;
            _recoveryOriginalSpineJoint = null;
        }

        public void SetRecoveryTorsoMotion(Transform spine, bool enabled)
        {
            if (spine == null)
                return;
            ConfigurableJoint joint = spine.GetComponent<ConfigurableJoint>();
            if (joint == null)
                return;

            if (enabled)
            {
                // A fallen torso can be rotated far beyond the small walking
                // posture limits. The joint remains connected; only its
                // angular range is opened for this recovery phase.
                joint.angularXMotion = ConfigurableJointMotion.Free;
                joint.angularYMotion = ConfigurableJointMotion.Free;
                joint.angularZMotion = ConfigurableJointMotion.Free;
                JointDrive recoveryDrive = joint.slerpDrive;
                recoveryDrive.positionSpring = 0f;
                recoveryDrive.positionDamper = 0f;
                recoveryDrive.maximumForce = 0f;
                joint.slerpDrive = recoveryDrive;

                // Unity's default angular-velocity cap (typically 7 rad/s)
                // is too restrictive for the first impulse when the torso is
                // lying against the floor.  Without raising it, the applied
                // velocity correction is clipped before it can overcome the
                // resting contact.  This is restored when recovery ends.
                Rigidbody body = spine.GetComponent<Rigidbody>();
                if (body != null)
                    body.maxAngularVelocity = 30f;
            }
            else
            {
                joint.configuredInWorldSpace = false;
                joint.angularXMotion = ConfigurableJointMotion.Limited;
                joint.angularYMotion = ConfigurableJointMotion.Limited;
                joint.angularZMotion = ConfigurableJointMotion.Limited;
                joint.lowAngularXLimit = new SoftJointLimit { limit = -35f };
                joint.highAngularXLimit = new SoftJointLimit { limit = 35f };
                joint.angularYLimit = new SoftJointLimit { limit = 35f };
                joint.angularZLimit = new SoftJointLimit { limit = 35f };
                JointDrive normalDrive = joint.slerpDrive;
                normalDrive.positionSpring = JointSpring;
                normalDrive.positionDamper = JointDamper;
                normalDrive.maximumForce = Mathf.Infinity;
                joint.slerpDrive = normalDrive;
                Rigidbody body = spine.GetComponent<Rigidbody>();
                if (body != null)
                    body.maxAngularVelocity = 7f;
            }
        }

        /// <summary>
        /// Opens the angular range of a leg chain for IK recovery. Normal
        /// walking uses a narrow +/-35 degree limit on every proxy joint;
        /// that limit prevents the hip and knee rigidbodies from reaching the
        /// folded seated pose even when the IK target is correct.
        /// </summary>
        public void SetRecoveryLegMotion(Transform upperLeg, Transform lowerLeg,
            Transform foot, bool enabled)
        {
            SetRecoveryLimbJointMotion(upperLeg, enabled);
            SetRecoveryLimbJointMotion(lowerLeg, enabled);
            SetRecoveryLimbJointMotion(foot, enabled);
        }

        void SetRecoveryLimbJointMotion(Transform limb, bool enabled)
        {
            if (limb == null)
                return;
            ConfigurableJoint joint = limb.GetComponent<ConfigurableJoint>();
            if (joint == null)
                return;

            if (enabled)
            {
                joint.angularXMotion = ConfigurableJointMotion.Free;
                joint.angularYMotion = ConfigurableJointMotion.Free;
                joint.angularZMotion = ConfigurableJointMotion.Free;
                JointDrive drive = joint.slerpDrive;
                drive.positionSpring = 0f;
                drive.positionDamper = 0f;
                drive.maximumForce = 0f;
                joint.slerpDrive = drive;
            }
            else
            {
                joint.angularXMotion = ConfigurableJointMotion.Limited;
                joint.angularYMotion = ConfigurableJointMotion.Limited;
                joint.angularZMotion = ConfigurableJointMotion.Limited;
                joint.lowAngularXLimit = new SoftJointLimit { limit = -35f };
                joint.highAngularXLimit = new SoftJointLimit { limit = 35f };
                joint.angularYLimit = new SoftJointLimit { limit = 35f };
                joint.angularZLimit = new SoftJointLimit { limit = 35f };
                JointDrive drive = joint.slerpDrive;
                drive.positionSpring = JointSpring;
                drive.positionDamper = JointDamper;
                drive.maximumForce = Mathf.Infinity;
                joint.slerpDrive = drive;
            }
        }

        /// <summary>Controls gravity during recovery. The pelvis is deliberately
        /// not exempt: exempting the root lets the recovery drives accumulate
        /// upward momentum and launch the whole ragdoll on a long mouse hold.</summary>
        public void SetRecoveryGravity(bool enabled)
        {
            for (int i = 0; i < _bodies.Count; i++)
            {
                Rigidbody body = _bodies[i];
                if (body == null) continue;
                body.useGravity = enabled;
            }
        }

        /// <summary>
        /// Keeps a recovery foot collider on the configured floor plane. The
        /// IK target is the foot-bone position, while contact is determined by
        /// the collider bounds; confusing those two heights leaves an obvious
        /// air gap or makes the PD drive push the body upward indefinitely.
        /// </summary>
        public void ClampRecoveryFootToGround(Transform foot, float groundHeight)
        {
            if (foot == null)
                return;

            Rigidbody body = foot.GetComponent<Rigidbody>();
            Collider collider = foot.GetComponent<Collider>();
            if (body == null || collider == null)
                return;

            float correction = groundHeight - collider.bounds.min.y;
            if (Mathf.Abs(correction) > 0.001f && Mathf.Abs(correction) < 0.12f)
                body.position += Vector3.up * correction;

            // A foot that is already on the floor must never carry upward
            // velocity into the next drive tick. Downward velocity is left to
            // the contact solver and gravity.
            if (!body.isKinematic
                && collider.bounds.min.y <= groundHeight + 0.004f
                && body.linearVelocity.y > 0f)
            {
                Vector3 velocity = body.linearVelocity;
                velocity.y = 0f;
                body.linearVelocity = velocity;
            }
        }

        public void SetRecoveryJointDrives(bool enabled)
        {
            for (int i = 0; i < _joints.Count; i++)
            {
                ConfigurableJoint joint = _joints[i];
                if (joint == null)
                    continue;
                JointDrive drive = joint.slerpDrive;
                drive.positionSpring = enabled ? JointSpring : 0f;
                drive.positionDamper = enabled ? JointDamper : 0f;
                drive.maximumForce = enabled ? Mathf.Infinity : 0f;
                joint.slerpDrive = drive;
            }
        }

        void DriveRecoveryBody(Rigidbody body, Vector3 targetPosition, Quaternion targetRotation)
        {
            DriveRecoveryBody(body, targetPosition, targetRotation, 1f);
        }

        void DriveRecoveryBody(Rigidbody body, Vector3 targetPosition, Quaternion targetRotation,
            float strength)
        {
            const float positionSpring = 70f;
            const float positionDamper = 18f;
            const float rotationSpring = 55f;
            const float rotationDamper = 14f;
            Vector3 force = (targetPosition - body.position) * positionSpring
                - body.linearVelocity * positionDamper;
            force *= Mathf.Clamp01(strength);
            if (force.sqrMagnitude > 36f)
                force = force.normalized * 6f;
            body.AddForce(force, ForceMode.Acceleration);

            Quaternion delta = targetRotation * Quaternion.Inverse(body.rotation);
            delta.ToAngleAxis(out float angle, out Vector3 axis);
            if (angle > 180f) angle -= 360f;
            if (axis.sqrMagnitude > 0.000001f)
            {
                Vector3 torque = axis.normalized * (angle * Mathf.Deg2Rad * rotationSpring)
                    - body.angularVelocity * rotationDamper;
                torque *= Mathf.Clamp01(strength);
                if (torque.sqrMagnitude > 36f)
                    torque = torque.normalized * 6f;
                body.AddTorque(torque, ForceMode.Acceleration);
            }
            body.linearVelocity = Vector3.ClampMagnitude(body.linearVelocity, Mathf.Max(0.1f, RecoveryMaxLinearSpeed));
            if (body.linearVelocity.y > Mathf.Max(0.05f, RecoveryMaxUpwardSpeed))
            {
                Vector3 velocity = body.linearVelocity;
                velocity.y = Mathf.Max(0.05f, RecoveryMaxUpwardSpeed);
                body.linearVelocity = velocity;
            }
            body.angularVelocity = Vector3.ClampMagnitude(body.angularVelocity, Mathf.Max(0.1f, RecoveryMaxAngularSpeed));
        }

        /// <summary>
        /// Walking deliberately ignores proxy-vs-proxy contacts. Recovery can
        /// opt a single leg back into contact with the locked pelvis and torso
        /// while it is being folded, so the leg cannot freely pass through the
        /// body envelope.
        /// </summary>
        public void SetRecoveryLegBodyCollision(Transform upperLeg, Transform lowerLeg, Transform foot, bool enabled)
        {
            if (!_built || PhysicsRoot == null)
                return;

            Collider[] leg =
            {
                GetProxyCollider(upperLeg),
                GetProxyCollider(lowerLeg),
                GetProxyCollider(foot)
            };
            Collider[] body =
            {
                GetProxyCollider(BoneMap != null ? BoneMap.hips : null),
                GetProxyCollider(BoneMap != null ? BoneMap.spine : null),
                GetProxyCollider(BoneMap != null ? BoneMap.head : null)
            };

            for (int i = 0; i < leg.Length; i++)
            {
                if (leg[i] == null) continue;
                for (int j = 0; j < body.Length; j++)
                {
                    if (body[j] == null || body[j] == leg[i]) continue;
                    Physics.IgnoreCollision(leg[i], body[j], !enabled);
                }
            }
        }

        Collider GetProxyCollider(Transform visualBone)
        {
            Transform proxy = GetProxyFor(visualBone);
            return proxy != null ? proxy.GetComponent<Collider>() : null;
        }

        public void ResolveRecoveryLegBodyPenetration(Transform upper, Transform lower, Transform foot)
        {
            if (!_built || BoneMap == null)
                return;

            Collider[] leg =
            {
                upper != null ? upper.GetComponent<Collider>() : null,
                lower != null ? lower.GetComponent<Collider>() : null,
                foot != null ? foot.GetComponent<Collider>() : null
            };
            Collider[] body =
            {
                GetProxyCollider(BoneMap.hips),
                GetProxyCollider(BoneMap.spine),
                GetProxyCollider(BoneMap.head)
            };

            for (int i = 0; i < leg.Length; i++)
            {
                Collider legCollider = leg[i];
                if (legCollider == null) continue;
                Vector3 correction = Vector3.zero;
                for (int j = 0; j < body.Length; j++)
                {
                    Collider bodyCollider = body[j];
                    if (bodyCollider == null || bodyCollider == legCollider) continue;
                    Vector3 direction;
                    float distance;
                    if (Physics.ComputePenetration(
                        legCollider, legCollider.transform.position, legCollider.transform.rotation,
                        bodyCollider, bodyCollider.transform.position, bodyCollider.transform.rotation,
                        out direction, out distance))
                    {
                        correction += direction * (distance + 0.001f);
                    }
                }

                if (correction.sqrMagnitude > 0.0000001f)
                {
                    Vector3 delta = correction / Mathf.Max(1, leg.Length);
                    for (int k = 0; k < leg.Length; k++)
                    {
                        if (leg[k] != null)
                            leg[k].transform.position += delta;
                    }
                }
            }
            Physics.SyncTransforms();
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
                    NormalizeRotation(binding.Proxy.rotation * binding.RotationOffsetLocal));
            }
        }

        static Quaternion NormalizeRotation(Quaternion rotation)
        {
            float magnitude = Mathf.Sqrt(rotation.x * rotation.x
                + rotation.y * rotation.y
                + rotation.z * rotation.z
                + rotation.w * rotation.w);
            return magnitude > 0.000001f && !float.IsNaN(magnitude)
                ? new Quaternion(rotation.x / magnitude, rotation.y / magnitude,
                    rotation.z / magnitude, rotation.w / magnitude)
                : Quaternion.identity;
        }

ProxyPart CreatePart(string name, Transform visualBone, Transform visualChild, float mass, float radius)
        {
            if (visualBone == null)
                return null;

            GameObject go = new GameObject(name);
            Transform proxy = go.transform;
            proxy.SetParent(PhysicsRoot, true);

            Vector3 start;
            Vector3 end;
            Vector3 midpoint;
            Quaternion rotation;
            float length;
            RaccoonProxyBuilder.GetSegmentPose(visualBone, visualChild,
                out start, out end, out midpoint, out rotation, out length);

            proxy.position = midpoint;
            proxy.rotation = rotation;
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
            RaccoonProxyBuilder.ConfigureCapsule(capsule, colliderRadius, length, colliderCenter);

            Rigidbody body = go.AddComponent<Rigidbody>();
            RaccoonProxyBuilder.ConfigureBody(body, mass, MinimumProxyMass,
                ProxySolverIterations, ProxySolverVelocityIterations, ProxyMaxDepenetrationVelocity);

            _bodies.Add(body);
            _colliders.Add(capsule);
            _baseColliderRadii.Add(capsule.radius);
            _baseColliderHeights.Add(capsule.height);
            _baseMaxDepenetrationVelocities.Add(body.maxDepenetrationVelocity);
            _visualToProxy[visualBone] = new ProxyBinding(visualBone, proxy);
            return new ProxyPart(proxy, body, start, end);
        }

        void IgnoreInternalCollisions()
        {
            RaccoonProxyRegistry.IgnoreInternalCollisions(_colliders);
        }

        void Connect(ProxyPart child, ProxyPart parent, bool allowFootTranslation)
        {
            if (child == null || parent == null)
                return;

            ConfigurableJoint joint = child.Transform.gameObject.AddComponent<ConfigurableJoint>();
            RaccoonJointConfigurator.Configure(joint, parent.Body, child.Start,
                allowFootTranslation, JointSpring, JointDamper);

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

                // Unity does not allow assigning velocity to a kinematic body.
                // Clear velocities while dynamic, then switch to kinematic.
                if (!body.isKinematic)
                {
                    body.linearVelocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                }

                body.isKinematic = true;
                // Keep gravity active so the floor supplies the reaction
                // force while recovery targets are being followed.
                body.useGravity = true;
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
            SetFallColliderMode(false);
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

        /// <summary>
        /// Freeze the currently settled dynamic ragdoll pose so a recovery
        /// controller can take ownership without teleporting from a stale
        /// kinematic pose or carrying residual velocities into the get-up.
        /// This does not change any body positions or rotations.
        /// </summary>
        public bool FreezeCurrentPoseForRecovery()
        {
            if (!_built || _bodies.Count == 0)
                return false;

            Physics.SyncTransforms();
            _gradualDynamicRelease = false;
            _dynamicReleaseStep = 0;
            // Recovery must remain a real ragdoll.  The old implementation
            // made every proxy kinematic and disabled the rig, which left the
            // ConfigurableJoints present but physically unsolved.  Preserve
            // the settled pose by zeroing velocities, then reactivate the
            // dynamic bodies with gravity temporarily disabled.
            PhysicsActive = true;
            for (int i = 0; i < _joints.Count; i++)
            {
                ConfigurableJoint joint = _joints[i];
                if (joint == null)
                    continue;
                JointDrive drive = joint.slerpDrive;
                drive.positionSpring = JointSpring;
                drive.positionDamper = JointDamper;
                drive.maximumForce = Mathf.Infinity;
                joint.slerpDrive = drive;
            }
            for (int i = 0; i < _bodies.Count; i++)
            {
                Rigidbody body = _bodies[i];
                if (body == null)
                    continue;

                // Keep the pelvis dynamic during recovery. Its position is
                // held by the recovery PD target, while its rotation is part
                // of the torso solve instead of being an invisible kinematic
                // anchor that leaves only the neck visibly rotating.
                body.isKinematic = false;
                body.useGravity = true;
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
            Physics.SyncTransforms();
            return true;
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
                    NormalizeRotation(binding.Proxy.rotation * binding.RotationOffsetLocal));
            }
        }
}
}
