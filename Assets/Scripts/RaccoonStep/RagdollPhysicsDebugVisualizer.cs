using System.Collections.Generic;
using UnityEngine;

namespace RaccoonStep
{
    /// <summary>
    /// Displays the actual physics proxy bodies and joint anchors used by
    /// RaccoonPhysicsRig. This is intentionally separate from the skeleton
    /// visualizer so it never changes the ragdoll solve.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RagdollPhysicsDebugVisualizer : MonoBehaviour
    {
        public RaccoonPhysicsRig PhysicsRig;
        public bool Visible = true;
        [Tooltip("Local-space offset of the debug ragdoll from the rendered model.")]
        public Vector3 DisplayOffset = new Vector3(1.25f, 0f, 0f);
        [Range(0.05f, 1f)] public float BodyAlpha = 0.35f;
        public Color BodyColor = new Color(0.1f, 0.75f, 1f, 0.35f);
        public Color JointColor = new Color(1f, 0.65f, 0.1f, 1f);
        public Color RecoveryJointColor = new Color(1f, 0.15f, 0.1f, 1f);
        public float JointMarkerRadius = 0.018f;
        public float JointLineWidth = 0.008f;

        Transform _visualRoot;
        Material _bodyMaterial;
        Material _jointMaterial;
        readonly List<GameObject> _owned = new List<GameObject>();
        readonly List<BodyVisual> _bodyVisuals = new List<BodyVisual>();
        readonly List<JointLink> _links = new List<JointLink>();
        int _jointCount = -1;
        bool _built;

        void Awake()
        {
            if (PhysicsRig == null) PhysicsRig = GetComponent<RaccoonPhysicsRig>();
        }

        void OnDestroy()
        {
            if (_bodyMaterial != null) DestroyOwned(_bodyMaterial);
            if (_jointMaterial != null) DestroyOwned(_jointMaterial);
        }

        void LateUpdate()
        {
            if (PhysicsRig == null) PhysicsRig = GetComponent<RaccoonPhysicsRig>();
            if (PhysicsRig == null || PhysicsRig.PhysicsRoot == null)
                return;

            if (!_built || _visualRoot == null || _jointCount != PhysicsRig.PhysicsRoot.GetComponentsInChildren<ConfigurableJoint>(true).Length)
                Rebuild();

            if (_visualRoot != null)
                _visualRoot.gameObject.SetActive(Visible);

            Vector3 displayOffset = transform.TransformVector(DisplayOffset);
            for (int i = 0; i < _bodyVisuals.Count; i++)
            {
                BodyVisual body = _bodyVisuals[i];
                if (body.Body == null || body.Visual == null) continue;
                body.Visual.transform.SetPositionAndRotation(
                    body.Body.transform.TransformPoint(body.Center) + displayOffset,
                    body.Body.transform.rotation * body.Rotation);
            }

            for (int i = 0; i < _links.Count; i++)
            {
                JointLink link = _links[i];
                if (link.Joint == null || link.Line == null) continue;
                Vector3 a = link.Joint.transform.TransformPoint(link.Joint.anchor);
                Vector3 b = link.Joint.connectedBody != null
                    ? link.Joint.connectedBody.transform.TransformPoint(link.Joint.connectedAnchor)
                    : a;
                a += displayOffset;
                b += displayOffset;
                link.Line.SetPosition(0, a);
                link.Line.SetPosition(1, b);
                if (link.Anchor != null) link.Anchor.transform.position = a;
                if (link.ConnectedAnchor != null) link.ConnectedAnchor.transform.position = b;
                Color color = link.Joint.gameObject.name.Contains("Recovery")
                    ? RecoveryJointColor : JointColor;
                link.Line.startColor = color;
                link.Line.endColor = color;
                if (link.AnchorRenderer != null) link.AnchorRenderer.material.color = color;
                if (link.ConnectedAnchorRenderer != null) link.ConnectedAnchorRenderer.material.color = color;
            }
        }

        void Rebuild()
        {
            ClearOwned();
            _visualRoot = new GameObject("RagdollPhysicsDebugVisualization").transform;
            _visualRoot.SetParent(transform, false);
            EnsureMaterials();

            Rigidbody[] bodies = PhysicsRig.PhysicsRoot.GetComponentsInChildren<Rigidbody>(true);
            for (int i = 0; i < bodies.Length; i++)
                AddBodyVisual(bodies[i]);

            ConfigurableJoint[] joints = PhysicsRig.PhysicsRoot.GetComponentsInChildren<ConfigurableJoint>(true);
            for (int i = 0; i < joints.Length; i++)
                AddJointVisual(joints[i]);

            _jointCount = joints.Length;
            _built = true;
        }

        void AddBodyVisual(Rigidbody body)
        {
            Collider source = body.GetComponent<Collider>();
            CapsuleCollider capsule = source as CapsuleCollider;
            if (capsule == null) return;

            GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            visual.name = body.name + "_PhysicsBodyVisual";
            DestroyOwned(visual.GetComponent<Collider>());
            visual.transform.SetParent(_visualRoot, true);
            visual.transform.localScale = new Vector3(capsule.radius * 2f, capsule.height * 0.5f, capsule.radius * 2f);
            visual.transform.SetPositionAndRotation(
                body.transform.TransformPoint(capsule.center) + transform.TransformVector(DisplayOffset),
                body.transform.rotation * CapsuleDirectionRotation(capsule.direction));
            Renderer renderer = visual.GetComponent<Renderer>();
            renderer.sharedMaterial = _bodyMaterial;
            _owned.Add(visual);
            _bodyVisuals.Add(new BodyVisual(body, visual, capsule.center, CapsuleDirectionRotation(capsule.direction)));
        }

        void AddJointVisual(ConfigurableJoint joint)
        {
            if (joint.connectedBody == null) return;
            GameObject a = CreateMarker(joint.gameObject.name + "_Anchor");
            GameObject b = CreateMarker(joint.gameObject.name + "_ConnectedAnchor");
            LineRenderer line = new GameObject(joint.gameObject.name + "_JointLink").AddComponent<LineRenderer>();
            line.transform.SetParent(_visualRoot, true);
            line.positionCount = 2;
            line.startWidth = JointLineWidth;
            line.endWidth = JointLineWidth;
            line.material = _jointMaterial;
            _owned.Add(line.gameObject);
            _links.Add(new JointLink(joint, line, a, b));
        }

        GameObject CreateMarker(string name)
        {
            GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = name;
            DestroyOwned(marker.GetComponent<Collider>());
            marker.transform.SetParent(_visualRoot, true);
            marker.transform.localScale = Vector3.one * JointMarkerRadius * 2f;
            marker.GetComponent<Renderer>().sharedMaterial = _jointMaterial;
            _owned.Add(marker);
            return marker;
        }

        void EnsureMaterials()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Standard");
            _bodyMaterial = new Material(shader) { name = "RagdollPhysicsBodyDebugMaterial" };
            _jointMaterial = new Material(shader) { name = "RagdollPhysicsJointDebugMaterial" };
            Color body = BodyColor;
            body.a = BodyAlpha;
            ConfigureTransparent(_bodyMaterial, body);
            ConfigureTransparent(_jointMaterial, JointColor);
        }

        static void ConfigureTransparent(Material material, Color color)
        {
            material.color = color;
            if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);
            if (material.HasProperty("_Mode")) material.SetFloat("_Mode", 3f);
            if (material.HasProperty("_SrcBlend")) material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            if (material.HasProperty("_DstBlend")) material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", 0f);
            material.renderQueue = 3000;
        }

        static Quaternion CapsuleDirectionRotation(int direction)
        {
            if (direction == 0) return Quaternion.Euler(0f, 0f, 90f);
            if (direction == 2) return Quaternion.Euler(90f, 0f, 0f);
            return Quaternion.identity;
        }

        void ClearOwned()
        {
            for (int i = 0; i < _owned.Count; i++)
                if (_owned[i] != null) DestroyOwned(_owned[i]);
            _owned.Clear();
            _bodyVisuals.Clear();
            _links.Clear();
            if (_visualRoot != null) DestroyOwned(_visualRoot.gameObject);
            _visualRoot = null;
            _built = false;
        }

        static void DestroyOwned(Object value)
        {
            if (value == null) return;
            if (Application.isPlaying) Destroy(value);
            else DestroyImmediate(value);
        }

        sealed class JointLink
        {
            public readonly ConfigurableJoint Joint;
            public readonly LineRenderer Line;
            public readonly GameObject Anchor;
            public readonly GameObject ConnectedAnchor;
            public readonly Renderer AnchorRenderer;
            public readonly Renderer ConnectedAnchorRenderer;

            public JointLink(ConfigurableJoint joint, LineRenderer line, GameObject anchor, GameObject connectedAnchor)
            {
                Joint = joint; Line = line; Anchor = anchor; ConnectedAnchor = connectedAnchor;
                AnchorRenderer = anchor.GetComponent<Renderer>();
                ConnectedAnchorRenderer = connectedAnchor.GetComponent<Renderer>();
            }
        }

        sealed class BodyVisual
        {
            public readonly Rigidbody Body;
            public readonly GameObject Visual;
            public readonly Vector3 Center;
            public readonly Quaternion Rotation;

            public BodyVisual(Rigidbody body, GameObject visual, Vector3 center, Quaternion rotation)
            {
                Body = body; Visual = visual; Center = center; Rotation = rotation;
            }
        }
    }
}
