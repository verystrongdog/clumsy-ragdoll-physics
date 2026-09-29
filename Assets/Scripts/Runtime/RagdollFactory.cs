using System.Collections.Generic;
using UnityEngine;

namespace ClumsyRagdoll
{
    public sealed class BodyPart : MonoBehaviour
    {
        public Rigidbody Body;
        public Collider Shape;
        public ConfigurableJoint Joint;
        public BodyPart ParentPart;
        public float Stiffness = 24f;
        public float Reaction = 0.3f;
        public float MaxTorque = 24f;
        public Quaternion TargetWorld;
        public Vector3 BindPosition;
        public Quaternion BindRotation;
    }

    public sealed class PuppetRig : MonoBehaviour
    {
        public string DisplayName;
        public int PlayerIndex;
        public Color LabelColor;
        public BodyPart Pelvis;
        public BodyPart Spine;
        public BodyPart Chest;
        public BodyPart Head;
        public BodyPart UpperArmL;
        public BodyPart LowerArmL;
        public BodyPart HandL;
        public BodyPart UpperArmR;
        public BodyPart LowerArmR;
        public BodyPart HandR;
        public BodyPart ThighL;
        public BodyPart ShinL;
        public BodyPart FootL;
        public BodyPart ThighR;
        public BodyPart ShinR;
        public BodyPart FootR;
        public BodyPart[] All;
        public HandGrabber GrabL;
        public HandGrabber GrabR;
        public Vector3 LeftHipLocal;
        public Vector3 RightHipLocal;
        public Vector3 LeftShoulderLocal;
        public Vector3 RightShoulderLocal;
        public float ThighLength;
        public float ShinLength;
        public float UpperArmLength;
        public float LowerArmLength;
        public Vector3 SpawnPosition;
        public Quaternion SpawnRotation;
    }

    public static class RagdollFactory
    {
        public static PuppetRig Create(string displayName, int playerIndex, Color bodyColor, Color accent, Vector3 spawn, float yawDegrees)
        {
            Quaternion spawnRot = Quaternion.Euler(0f, yawDegrees, 0f);
            GameObject root = new GameObject(displayName);
            root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            PhysicsMaterial flesh = SurfaceKit.Friction(0.2f, 0.02f, PhysicsMaterialCombine.Average, PhysicsMaterialCombine.Average);
            PhysicsMaterial feet = SurfaceKit.Friction(1.45f, 0f, PhysicsMaterialCombine.Maximum, PhysicsMaterialCombine.Minimum);
            PhysicsMaterial hands = SurfaceKit.Friction(0.85f, 0.02f, PhysicsMaterialCombine.Average, PhysicsMaterialCombine.Average);

            Material bodyMat = SurfaceKit.Lit(bodyColor, 0.18f);
            Material accentMat = SurfaceKit.Lit(accent, 0.22f);
            Material footMat = SurfaceKit.Lit(bodyColor * 0.55f, 0.08f);
            Material skinMat = SurfaceKit.Lit(Color.Lerp(bodyColor, new Color(0.96f, 0.86f, 0.74f), 0.35f), 0.2f);

            var parts = new List<BodyPart>(16);
            Vector3 W(Vector3 local) => spawn + spawnRot * local;

            Vector3 pelvisC = W(new Vector3(0f, 0.84f, 0f));
            Vector3 spineJ = W(new Vector3(0f, 0.96f, 0f));
            Vector3 chestJ = W(new Vector3(0f, 1.10f, 0f));
            Vector3 neckJ = W(new Vector3(0f, 1.26f, 0f));
            Vector3 headC = W(new Vector3(0f, 1.40f, 0f));

            Vector3 shoulderL = W(new Vector3(-0.20f, 1.20f, 0.02f));
            Vector3 elbowL = W(new Vector3(-0.40f, 0.96f, 0.06f));
            Vector3 wristL = W(new Vector3(-0.52f, 0.70f, 0.12f));
            Vector3 shoulderR = W(new Vector3(0.20f, 1.20f, 0.02f));
            Vector3 elbowR = W(new Vector3(0.40f, 0.96f, 0.06f));
            Vector3 wristR = W(new Vector3(0.52f, 0.70f, 0.12f));

            Vector3 hipL = W(new Vector3(-0.09f, 0.74f, 0f));
            Vector3 kneeL = W(new Vector3(-0.10f, 0.42f, 0.04f));
            Vector3 ankleL = W(new Vector3(-0.10f, 0.11f, 0.02f));
            Vector3 hipR = W(new Vector3(0.09f, 0.74f, 0f));
            Vector3 kneeR = W(new Vector3(0.10f, 0.42f, 0.04f));
            Vector3 ankleR = W(new Vector3(0.10f, 0.11f, 0.02f));

            BodyPart pelvis = Capsule(root.transform, "骨盆", pelvisC, spawnRot * Vector3.up, 0.32f, 0.15f, 8f, bodyMat, flesh, parts);
            BodyPart spine = CapsuleBetween(root.transform, "腹", spineJ, chestJ, 0.065f, 3.2f, bodyMat, flesh, parts);
            BodyPart chest = Box(root.transform, "胸", W(new Vector3(0f, 1.18f, 0.02f)), spawnRot, new Vector3(0.42f, 0.28f, 0.22f), 7f, bodyMat, flesh, parts);
            BodyPart head = Sphere(root.transform, "头", headC, 0.15f, 3.1f, skinMat, flesh, parts);

            BodyPart uArmL = CapsuleBetween(root.transform, "上臂.左", shoulderL, elbowL, 0.055f, 1.3f, bodyMat, flesh, parts);
            BodyPart lArmL = CapsuleBetween(root.transform, "前臂.左", elbowL, wristL, 0.048f, 1.05f, bodyMat, flesh, parts);
            BodyPart handL = Sphere(root.transform, "手.左", wristL + (wristL - elbowL).normalized * 0.02f, 0.09f, 0.55f, accentMat, hands, parts);
            BodyPart uArmR = CapsuleBetween(root.transform, "上臂.右", shoulderR, elbowR, 0.055f, 1.3f, bodyMat, flesh, parts);
            BodyPart lArmR = CapsuleBetween(root.transform, "前臂.右", elbowR, wristR, 0.048f, 1.05f, bodyMat, flesh, parts);
            BodyPart handR = Sphere(root.transform, "手.右", wristR + (wristR - elbowR).normalized * 0.02f, 0.09f, 0.55f, accentMat, hands, parts);

            BodyPart thighL = CapsuleBetween(root.transform, "大腿.左", hipL, kneeL, 0.075f, 2.4f, bodyMat, flesh, parts);
            BodyPart shinL = CapsuleBetween(root.transform, "小腿.左", kneeL, ankleL, 0.055f, 1.5f, bodyMat, flesh, parts);
            BodyPart footL = Box(root.transform, "脚.左", ankleL + spawnRot * new Vector3(0f, -0.015f, 0.05f), spawnRot, new Vector3(0.12f, 0.07f, 0.24f), 0.55f, footMat, feet, parts, true);
            BodyPart thighR = CapsuleBetween(root.transform, "大腿.右", hipR, kneeR, 0.075f, 2.4f, bodyMat, flesh, parts);
            BodyPart shinR = CapsuleBetween(root.transform, "小腿.右", kneeR, ankleR, 0.055f, 1.5f, bodyMat, flesh, parts);
            BodyPart footR = Box(root.transform, "脚.右", ankleR + spawnRot * new Vector3(0f, -0.015f, 0.05f), spawnRot, new Vector3(0.12f, 0.07f, 0.24f), 0.55f, footMat, feet, parts, true);

            Link(spine, pelvis, spineJ, 0.22f);
            Link(chest, spine, chestJ, 0.2f);
            Link(head, chest, neckJ, 0.12f);
            Link(uArmL, chest, shoulderL, 0.55f);
            Link(lArmL, uArmL, elbowL, 0.4f);
            Link(handL, lArmL, wristL, 0.2f);
            Link(uArmR, chest, shoulderR, 0.55f);
            Link(lArmR, uArmR, elbowR, 0.4f);
            Link(handR, lArmR, wristR, 0.2f);
            Link(thighL, pelvis, hipL, 0.35f);
            Link(shinL, thighL, kneeL, 0.3f);
            Link(footL, shinL, ankleL, 0.15f);
            Link(thighR, pelvis, hipR, 0.35f);
            Link(shinR, thighR, kneeR, 0.3f);
            Link(footR, shinR, ankleR, 0.15f);

            pelvis.Body.mass = 8f;
            pelvis.Body.linearDamping = 0.02f;
            pelvis.Body.angularDamping = 0.45f;
            pelvis.Body.maxAngularVelocity = 8f;
            pelvis.Body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            Physics.SyncTransforms();
            IgnoreInitialOverlaps(parts);
            AddFace(head, accentMat);

            PuppetRig rig = root.AddComponent<PuppetRig>();
            rig.DisplayName = displayName;
            rig.PlayerIndex = playerIndex;
            rig.LabelColor = accent;
            rig.Pelvis = pelvis;
            rig.Spine = spine;
            rig.Chest = chest;
            rig.Head = head;
            rig.UpperArmL = uArmL;
            rig.LowerArmL = lArmL;
            rig.HandL = handL;
            rig.UpperArmR = uArmR;
            rig.LowerArmR = lArmR;
            rig.HandR = handR;
            rig.ThighL = thighL;
            rig.ShinL = shinL;
            rig.FootL = footL;
            rig.ThighR = thighR;
            rig.ShinR = shinR;
            rig.FootR = footR;
            rig.All = parts.ToArray();
            rig.LeftHipLocal = pelvis.transform.InverseTransformPoint(hipL);
            rig.RightHipLocal = pelvis.transform.InverseTransformPoint(hipR);
            rig.LeftShoulderLocal = chest.transform.InverseTransformPoint(shoulderL);
            rig.RightShoulderLocal = chest.transform.InverseTransformPoint(shoulderR);
            rig.ThighLength = Vector3.Distance(hipL, kneeL);
            rig.ShinLength = Vector3.Distance(kneeL, ankleL);
            rig.UpperArmLength = Vector3.Distance(shoulderL, elbowL);
            rig.LowerArmLength = Vector3.Distance(elbowL, wristL);
            rig.SpawnPosition = spawn;
            rig.SpawnRotation = spawnRot;
            rig.GrabL = handL.gameObject.AddComponent<HandGrabber>();
            rig.GrabR = handR.gameObject.AddComponent<HandGrabber>();
            rig.GrabL.Rig = rig;
            rig.GrabR.Rig = rig;

            foreach (BodyPart part in parts)
            {
                part.BindPosition = part.transform.position;
                part.BindRotation = part.transform.rotation;
                part.TargetWorld = part.transform.rotation;
                part.Body.solverIterations = 16;
                part.Body.solverVelocityIterations = 6;
            }

            SurfaceKit.CreateLabel(head.transform, displayName, accent, 0.42f);
            return rig;
        }

        static void AddFace(BodyPart head, Material accent)
        {
            Material white = SurfaceKit.Lit(new Color(0.95f, 0.95f, 0.93f), 0.35f);
            Material dark = SurfaceKit.Lit(new Color(0.12f, 0.1f, 0.1f), 0.4f);
            Transform t = head.transform;
            SurfaceKit.Decor(t, PrimitiveType.Sphere, new Vector3(-0.05f, 0.035f, 0.145f), 0.07f, white);
            SurfaceKit.Decor(t, PrimitiveType.Sphere, new Vector3(0.05f, 0.035f, 0.145f), 0.07f, white);
            SurfaceKit.Decor(t, PrimitiveType.Sphere, new Vector3(-0.05f, 0.035f, 0.178f), 0.034f, dark);
            SurfaceKit.Decor(t, PrimitiveType.Sphere, new Vector3(0.05f, 0.035f, 0.178f), 0.034f, dark);
            SurfaceKit.Decor(t, PrimitiveType.Cube, new Vector3(0f, -0.01f, 0.165f), 0.05f, accent);
            GameObject hat = SurfaceKit.Decor(t, PrimitiveType.Sphere, new Vector3(0f, 0.16f, 0.01f), 0.16f, accent);
            hat.transform.localScale = new Vector3(hat.transform.localScale.x * 1.15f, hat.transform.localScale.y * 0.55f, hat.transform.localScale.z * 1.15f);
        }

        static void Link(BodyPart child, BodyPart parent, Vector3 jointWorld, float reaction)
        {
            ConfigurableJoint joint = child.gameObject.AddComponent<ConfigurableJoint>();
            joint.connectedBody = parent.Body;
            joint.anchor = child.transform.InverseTransformPoint(jointWorld);
            joint.autoConfigureConnectedAnchor = false;
            joint.connectedAnchor = parent.transform.InverseTransformPoint(jointWorld);
            joint.xMotion = ConfigurableJointMotion.Locked;
            joint.yMotion = ConfigurableJointMotion.Locked;
            joint.zMotion = ConfigurableJointMotion.Locked;
            joint.angularXMotion = ConfigurableJointMotion.Free;
            joint.angularYMotion = ConfigurableJointMotion.Free;
            joint.angularZMotion = ConfigurableJointMotion.Free;
            joint.projectionMode = JointProjectionMode.PositionAndRotation;
            joint.projectionDistance = 0.08f;
            joint.projectionAngle = 20f;
            joint.enablePreprocessing = false;
            joint.enableCollision = false;
            joint.breakForce = Mathf.Infinity;
            joint.breakTorque = Mathf.Infinity;
            child.Joint = joint;
            child.ParentPart = parent;
            child.Reaction = reaction;
        }

        static void IgnoreInitialOverlaps(List<BodyPart> parts)
        {
            for (int i = 0; i < parts.Count; i++)
            {
                for (int j = i + 1; j < parts.Count; j++)
                {
                    Collider a = parts[i].Shape;
                    Collider b = parts[j].Shape;
                    bool hit = Physics.ComputePenetration(
                        a, a.transform.position, a.transform.rotation,
                        b, b.transform.position, b.transform.rotation,
                        out _, out _);
                    if (hit)
                        Physics.IgnoreCollision(a, b, true);
                }
            }
        }

        static BodyPart CapsuleBetween(Transform root, string name, Vector3 from, Vector3 to, float radius, float mass, Material mat, PhysicsMaterial phys, List<BodyPart> parts)
        {
            Vector3 dir = to - from;
            float length = Mathf.Max(dir.magnitude, radius * 2.02f);
            Vector3 center = (from + to) * 0.5f;
            Quaternion rot = IkMath.RotationFromUp(dir, Vector3.forward);
            return Capsule(root, name, center, rot * Vector3.up, length, radius, mass, mat, phys, parts);
        }

        static BodyPart Capsule(Transform root, string name, Vector3 center, Vector3 up, float height, float radius, float mass, Material mat, PhysicsMaterial phys, List<BodyPart> parts)
        {
            float h = Mathf.Max(height, radius * 2.02f);
            GameObject go = NewBody(root, name, center, IkMath.RotationFromUp(up, Vector3.forward));
            CapsuleCollider collider = go.AddComponent<CapsuleCollider>();
            collider.direction = 1;
            collider.radius = radius;
            collider.height = h;
            collider.center = Vector3.zero;
            AddVisual(go.transform, PrimitiveType.Capsule, new Vector3(radius * 2f, h * 0.5f, radius * 2f), mat);
            return Finish(go, collider, mass, phys, false, parts);
        }

        static BodyPart Sphere(Transform root, string name, Vector3 center, float radius, float mass, Material mat, PhysicsMaterial phys, List<BodyPart> parts)
        {
            GameObject go = NewBody(root, name, center, Quaternion.identity);
            SphereCollider collider = go.AddComponent<SphereCollider>();
            collider.radius = radius;
            AddVisual(go.transform, PrimitiveType.Sphere, Vector3.one * (radius * 2f), mat);
            return Finish(go, collider, mass, phys, true, parts);
        }

        static BodyPart Box(Transform root, string name, Vector3 center, Quaternion rotation, Vector3 size, float mass, Material mat, PhysicsMaterial phys, List<BodyPart> parts, bool continuous = false)
        {
            GameObject go = NewBody(root, name, center, rotation);
            BoxCollider collider = go.AddComponent<BoxCollider>();
            collider.size = size;
            AddVisual(go.transform, PrimitiveType.Cube, size, mat);
            return Finish(go, collider, mass, phys, continuous, parts);
        }

        // 刚体保持缩放 1。非均匀缩放和关节叠在一起时，PhysX 很容易把人弹飞。
        static GameObject NewBody(Transform root, string name, Vector3 center, Quaternion rotation)
        {
            GameObject go = new GameObject(name);
            go.layer = GameLayers.Puppet;
            go.transform.SetParent(root, false);
            go.transform.SetPositionAndRotation(center, rotation);
            return go;
        }

        static void AddVisual(Transform body, PrimitiveType type, Vector3 scale, Material mat)
        {
            GameObject visual = GameObject.CreatePrimitive(type);
            visual.name = "模型";
            visual.layer = GameLayers.Puppet;
            Collider extra = visual.GetComponent<Collider>();
            if (extra != null)
            {
                extra.enabled = false;
                Object.Destroy(extra);
            }
            visual.transform.SetParent(body, false);
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            visual.transform.localScale = scale;
            visual.GetComponent<Renderer>().sharedMaterial = mat;
        }

        static BodyPart Finish(GameObject go, Collider collider, float mass, PhysicsMaterial phys, bool continuous, List<BodyPart> parts)
        {
            collider.sharedMaterial = phys;
            Rigidbody body = go.AddComponent<Rigidbody>();
            body.mass = mass;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = continuous ? CollisionDetectionMode.Continuous : CollisionDetectionMode.ContinuousSpeculative;
            body.linearDamping = 0.08f;
            body.angularDamping = 1.15f;
            body.maxAngularVelocity = 12f;
            body.maxDepenetrationVelocity = 1.8f;
            BodyPart part = go.AddComponent<BodyPart>();
            part.Body = body;
            part.Shape = collider;
            parts.Add(part);
            return part;
        }
    }
}
