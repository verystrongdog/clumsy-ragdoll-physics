using UnityEngine;

namespace RaccoonStep
{
    public enum RaccoonStepState
    {
        Stable,
        PreparingStep,
        SwingingFoot,
        PlacingFoot,
        TransferringWeight,
        Unstable,
        Falling,
        Recovering
    }

    /// <summary>
    /// RaccoonStep 独立角色根组件。
    /// 当前阶段只负责角色状态和骨骼绑定验证，不创建物理部件。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RaccoonStepCharacter : MonoBehaviour
    {
        public RaccoonBoneMap BoneMap;
        public RaccoonStepState State = RaccoonStepState.Stable;
        public bool IsReady { get; private set; }
        public float ModelHeight { get; private set; }
        public string Status { get; private set; }

        void Awake()
        {
            EnsureBoneMap();
            ValidateCharacter();
        }

public void ValidateCharacter()
        {
            EnsureBoneMap();
            if (BoneMap == null)
            {
                IsReady = false;
                Status = "Missing RaccoonBoneMap.";
                Debug.LogError($"[RaccoonStep] {Status}", this);
                return;
            }

            // 先主动建立映射，避免依赖多个 MonoBehaviour 的 Awake 调用顺序。
            BoneMap.BuildFrom(transform);

            IsReady = BoneMap.IsValid;
            ModelHeight = EstimateHeight();
            Status = IsReady
                ? $"Ready. height={ModelHeight:F3}m"
                : "Bone map is incomplete.";

            if (IsReady)
                Debug.Log($"[RaccoonStep] {name}: {Status}", this);
            else
                Debug.LogError($"[RaccoonStep] {name}: {Status} {BoneMap.ValidationReport}", this);
        }

        void EnsureBoneMap()
        {
            if (BoneMap == null)
                BoneMap = GetComponent<RaccoonBoneMap>();

            if (BoneMap == null)
                BoneMap = gameObject.AddComponent<RaccoonBoneMap>();
        }

        float EstimateHeight()
        {
            if (BoneMap == null || BoneMap.hips == null || BoneMap.head == null)
                return 0f;

            float hipsY = BoneMap.hips.position.y;
            float headY = BoneMap.head.position.y;
            float footY = Mathf.Min(
                BoneMap.leftFoot != null ? BoneMap.leftFoot.position.y : hipsY,
                BoneMap.rightFoot != null ? BoneMap.rightFoot.position.y : hipsY);

            return Mathf.Max(0f, headY - footY);
        }
    }
}
