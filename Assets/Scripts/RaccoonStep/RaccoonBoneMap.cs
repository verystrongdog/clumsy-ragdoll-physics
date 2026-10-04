using System;
using UnityEngine;

namespace RaccoonStep
{
    /// <summary>
    /// RaccoonStep 的骨骼绑定表。
    /// 只负责找到并保存实际骨骼引用，不创建物理组件，也不驱动动画。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RaccoonBoneMap : MonoBehaviour
    {
        [Header("Skeleton root")]
        public Transform skeletonRoot;

        [Header("Body")]
        public Transform hips;
        public Transform spine;
        public Transform neck;
        public Transform head;

        [Header("Left leg")]
        public Transform leftUpperLeg;
        public Transform leftLowerLeg;
        public Transform leftFoot;
        public Transform leftToe;

        [Header("Right leg")]
        public Transform rightUpperLeg;
        public Transform rightLowerLeg;
        public Transform rightFoot;
        public Transform rightToe;

        [Header("Arms")]
        public Transform leftShoulder;
        public Transform leftUpperArm;
        public Transform leftLowerArm;
        public Transform leftHand;
        public Transform rightShoulder;
        public Transform rightUpperArm;
        public Transform rightLowerArm;
        public Transform rightHand;

        public bool IsValid
        {
            get
            {
                return skeletonRoot != null &&
                       hips != null &&
                       spine != null &&
                       leftUpperLeg != null &&
                       leftLowerLeg != null &&
                       leftFoot != null &&
                       rightUpperLeg != null &&
                       rightLowerLeg != null &&
                       rightFoot != null;
            }
        }

        public string ValidationReport { get; private set; }

        void Awake()
        {
            BuildFrom(transform);
        }

        [ContextMenu("Rebuild Bone Map")]
        public void RebuildBoneMap()
        {
            BuildFrom(transform);
        }

        public bool BuildFrom(Transform characterRoot)
        {
            ValidationReport = string.Empty;
            if (characterRoot == null)
            {
                ValidationReport = "Character root is null.";
                return false;
            }

            skeletonRoot = FindDescendant(characterRoot, "Armature");
            hips = FindDescendant(characterRoot, "mixamorig:Hips");
            spine = FindDescendant(characterRoot, "mixamorig:Spine");
            neck = FindDescendant(characterRoot, "mixamorig:Neck");
            head = FindDescendant(characterRoot, "mixamorig:Head");

            leftUpperLeg = FindDescendant(characterRoot, "mixamorig:LeftUpLeg");
            leftLowerLeg = FindDescendant(characterRoot, "mixamorig:LeftLeg");
            leftFoot = FindDescendant(characterRoot, "mixamorig:LeftFoot");
            leftToe = FindDescendant(characterRoot, "mixamorig:LeftToeBase");

            rightUpperLeg = FindDescendant(characterRoot, "mixamorig:RightUpLeg");
            rightLowerLeg = FindDescendant(characterRoot, "mixamorig:RightLeg");
            rightFoot = FindDescendant(characterRoot, "mixamorig:RightFoot");
            rightToe = FindDescendant(characterRoot, "mixamorig:RightToeBase");

            leftShoulder = FindDescendant(characterRoot, "mixamorig:LeftShoulder");
            leftUpperArm = FindDescendant(characterRoot, "mixamorig:LeftArm");
            leftLowerArm = FindDescendant(characterRoot, "mixamorig:LeftForeArm");
            leftHand = FindDescendant(characterRoot, "mixamorig:LeftHand");

            rightShoulder = FindDescendant(characterRoot, "mixamorig:RightShoulder");
            rightUpperArm = FindDescendant(characterRoot, "mixamorig:RightArm");
            rightLowerArm = FindDescendant(characterRoot, "mixamorig:RightForeArm");
            rightHand = FindDescendant(characterRoot, "mixamorig:RightHand");

            ValidationReport = BuildReport();
            Debug.Log($"[RaccoonStep] Bone map for '{characterRoot.name}': {ValidationReport}", this);
            return IsValid;
        }

        string BuildReport()
        {
            int required = 0;
            int found = 0;
            Count(skeletonRoot, ref required, ref found);
            Count(hips, ref required, ref found);
            Count(spine, ref required, ref found);
            Count(leftUpperLeg, ref required, ref found);
            Count(leftLowerLeg, ref required, ref found);
            Count(leftFoot, ref required, ref found);
            Count(rightUpperLeg, ref required, ref found);
            Count(rightLowerLeg, ref required, ref found);
            Count(rightFoot, ref required, ref found);

            int optional = 0;
            int optionalFound = 0;
            CountOptional(neck, ref optional, ref optionalFound);
            CountOptional(head, ref optional, ref optionalFound);
            CountOptional(leftToe, ref optional, ref optionalFound);
            CountOptional(rightToe, ref optional, ref optionalFound);
            CountOptional(leftHand, ref optional, ref optionalFound);
            CountOptional(rightHand, ref optional, ref optionalFound);

            return $"required={found}/{required}, optional={optionalFound}/{optional}, valid={IsValid}";
        }

        static void Count(Transform value, ref int total, ref int found)
        {
            total++;
            if (value != null) found++;
        }

        static void CountOptional(Transform value, ref int total, ref int found)
        {
            total++;
            if (value != null) found++;
        }

        static Transform FindDescendant(Transform root, string exactName)
        {
            if (root == null) return null;
            if (root.name == exactName) return root;

            for (int i = 0; i < root.childCount; i++)
            {
                Transform result = FindDescendant(root.GetChild(i), exactName);
                if (result != null) return result;
            }

            return null;
        }
    }
}
