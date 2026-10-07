using UnityEngine;
using System.Collections.Generic;

namespace RaccoonStep
{
    [ExecuteAlways]
    public sealed class SkeletonDebugVisualizer : MonoBehaviour
    {
        public RaccoonBoneMap BoneMap;
        public Color BoneColor = Color.yellow;
        public Color JointColor = Color.red;
        public float BoneWidth = 0.018f;
        public float JointSize = 0.055f;
        [Tooltip("World-space offset used to display the debug skeleton beside the character.")]
        public Vector3 WorldOffset = new Vector3(1.6f, 0f, 0f);

        readonly List<Transform[]> _pairs = new List<Transform[]>();
        readonly List<LineRenderer> _lines = new List<LineRenderer>();
        readonly List<Transform> _joints = new List<Transform>();
        readonly List<Transform> _bones = new List<Transform>();
        Transform _root;
        Material _boneMaterial;
        Material _jointMaterial;

        void OnEnable() { Rebuild(); }
        void Update() { if (_root == null || _lines.Count == 0) Rebuild(); else Sync(); }
        void LateUpdate() { if (_root == null || _lines.Count == 0) Rebuild(); else Sync(); }
        void OnDisable()
        {
            CleanupGeneratedRoots();
            _root = null;
            _pairs.Clear(); _lines.Clear(); _joints.Clear(); _bones.Clear();
        }

        public void Rebuild()
        {
            if (BoneMap == null) BoneMap = GetComponent<RaccoonBoneMap>();
            if (BoneMap == null) return;
            BoneMap.BuildFrom(transform);
            if (!BoneMap.IsValid) return;
            CleanupGeneratedRoots();
            _pairs.Clear(); _lines.Clear(); _joints.Clear(); _bones.Clear();
            _root = new GameObject("SkeletonDebugVisualization").transform;
            _root.SetParent(transform, false);
            EnsureMaterials();
            AddPair(BoneMap.hips, BoneMap.spine); AddPair(BoneMap.spine, BoneMap.neck); AddPair(BoneMap.neck, BoneMap.head);
            AddPair(BoneMap.spine, BoneMap.leftShoulder); AddPair(BoneMap.leftShoulder, BoneMap.leftUpperArm); AddPair(BoneMap.leftUpperArm, BoneMap.leftLowerArm); AddPair(BoneMap.leftLowerArm, BoneMap.leftHand);
            AddPair(BoneMap.spine, BoneMap.rightShoulder); AddPair(BoneMap.rightShoulder, BoneMap.rightUpperArm); AddPair(BoneMap.rightUpperArm, BoneMap.rightLowerArm); AddPair(BoneMap.rightLowerArm, BoneMap.rightHand);
            AddPair(BoneMap.hips, BoneMap.leftUpperLeg); AddPair(BoneMap.leftUpperLeg, BoneMap.leftLowerLeg); AddPair(BoneMap.leftLowerLeg, BoneMap.leftFoot); AddPair(BoneMap.leftFoot, BoneMap.leftToe);
            AddPair(BoneMap.hips, BoneMap.rightUpperLeg); AddPair(BoneMap.rightUpperLeg, BoneMap.rightLowerLeg); AddPair(BoneMap.rightLowerLeg, BoneMap.rightFoot); AddPair(BoneMap.rightFoot, BoneMap.rightToe);
            Transform[] bones = { BoneMap.hips, BoneMap.spine, BoneMap.neck, BoneMap.head, BoneMap.leftShoulder, BoneMap.leftUpperArm, BoneMap.leftLowerArm, BoneMap.leftHand, BoneMap.rightShoulder, BoneMap.rightUpperArm, BoneMap.rightLowerArm, BoneMap.rightHand, BoneMap.leftUpperLeg, BoneMap.leftLowerLeg, BoneMap.leftFoot, BoneMap.leftToe, BoneMap.rightUpperLeg, BoneMap.rightLowerLeg, BoneMap.rightFoot, BoneMap.rightToe };
            _bones.AddRange(bones);
            foreach (Transform t in bones)
                AddJoint(t);
            Sync();
        }

        void EnsureMaterials()
        {
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null)
                return;

            if (_boneMaterial == null)
                _boneMaterial = new Material(shader) { name = "SkeletonDebugBoneMaterial" };
            if (_jointMaterial == null)
                _jointMaterial = new Material(shader) { name = "SkeletonDebugJointMaterial" };

            _boneMaterial.color = BoneColor;
            _jointMaterial.color = JointColor;
        }

        void AddPair(Transform a, Transform b)
        {
            if (a == null || b == null) return;
            var go = new GameObject("Bone"); go.transform.SetParent(_root, false);
            var lr = go.AddComponent<LineRenderer>(); lr.positionCount = 2; lr.useWorldSpace = true; lr.startWidth = BoneWidth; lr.endWidth = BoneWidth; lr.sharedMaterial = _boneMaterial; lr.startColor = BoneColor; lr.endColor = Color.cyan;
            _pairs.Add(new Transform[] { a, b }); _lines.Add(lr);
        }

        void AddJoint(Transform t)
        {
            if (t == null) return;
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere); go.name = "Joint"; go.transform.SetParent(_root, false); go.transform.localScale = Vector3.one * JointSize;
            var col = go.GetComponent<Collider>(); if (col != null) DestroyOwnedObject(col);
            var mr = go.GetComponent<Renderer>(); mr.sharedMaterial = _jointMaterial; _joints.Add(go.transform);
        }

        void Sync()
        {
            for (int i = 0; i < _pairs.Count && i < _lines.Count; i++) { _lines[i].SetPosition(0, _pairs[i][0].position + WorldOffset); _lines[i].SetPosition(1, _pairs[i][1].position + WorldOffset); }
            for (int i = 0; i < _joints.Count; i++) { Transform t = JointBone(i); if (t != null) _joints[i].position = t.position + WorldOffset; }
        }

        void CleanupGeneratedRoots()
        {
            // Generated debug objects are not serialized. After a domain
            // reload or an enable/disable cycle, _root may be null while the
            // previous generated root is still in the scene. Remove every
            // stale root before creating a replacement, otherwise the old
            // standing red joints remain visible when the model moves/falls.
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                Transform child = transform.GetChild(i);
                if (child.name == "SkeletonDebugVisualization")
                {
                    child.gameObject.SetActive(false);
                    DestroyOwnedObject(child.gameObject);
                }
            }
        }

        Transform JointBone(int i)
        {
            return i >= 0 && i < _bones.Count ? _bones[i] : null;
        }

        void OnDestroy()
        {
            DestroyOwnedObject(_boneMaterial);
            DestroyOwnedObject(_jointMaterial);
            _boneMaterial = null;
            _jointMaterial = null;
        }

        static void DestroyOwnedObject(Object o) { if (o == null) return; if (Application.isPlaying) Destroy(o); else DestroyImmediate(o); }
    }
}
