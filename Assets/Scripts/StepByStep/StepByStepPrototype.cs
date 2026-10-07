using UnityEngine;
using RaccoonStep;

namespace StepByStep
{
    /// <summary>平台观察测试入口：只创建摄像机，不创建角色或骨骼。</summary>
    [DefaultExecutionOrder(-200)]
    public sealed class StepByStepPrototype : MonoBehaviour
    {
        // 由 ClumsyRagdollGame 的独立原型入口注入；该观察入口暂时不生成角色，
        // 但保留字段可避免切换入口时破坏 Unity 编译。
        public GameObject ModelPrefab;
        public Vector3 Spawn;
        public string CharacterObjectName = "RaccoonHandsDownTest";
        public bool AutoBind = true;
        public RaccoonStepCharacter Character { get; private set; }
        public RaccoonBoneMap BoneMap { get; private set; }
        public RaccoonPhysicsRig PhysicsRig { get; private set; }
        public RaccoonAlternatingLegController Gait { get; private set; }
        public RaccoonCenterOfMassBalance CenterOfMassBalance { get; private set; }
        public RaccoonSitRecoveryController Recovery { get; private set; }
        public bool IsBound { get; private set; }

        void Awake()
        {
            Camera camera = Camera.main;
            if (camera == null)
            {
                GameObject go = new GameObject("StepByStep_MainCamera");
                go.tag = "MainCamera";
                camera = go.AddComponent<Camera>();
            }
            camera.targetDisplay = 0;
            camera.enabled = true;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.12f, 0.18f, 0.25f, 1f);
            camera.fieldOfView = 55f;
            camera.transform.position = new Vector3(0f, 1.8f, -4.5f);
            camera.transform.rotation = Quaternion.LookRotation(new Vector3(0f, 0.5f, 0f) - camera.transform.position, Vector3.up);
            if (camera.GetComponent<StepByStepFreeCamera>() == null)
                camera.gameObject.AddComponent<StepByStepFreeCamera>();

            if (AutoBind)
                BindCharacter();
        }

        public bool BindCharacter()
        {
            GameObject characterObject = GameObject.Find(CharacterObjectName);
            if (characterObject == null)
            {
                Debug.LogWarning("[RaccoonStep] StepByStepPrototype could not find '" + CharacterObjectName + "'.", this);
                IsBound = false;
                return false;
            }

            Character = characterObject.GetComponent<RaccoonStepCharacter>();
            BoneMap = characterObject.GetComponent<RaccoonBoneMap>();
            PhysicsRig = characterObject.GetComponent<RaccoonPhysicsRig>();
            RaccoonFootController foot = characterObject.GetComponent<RaccoonFootController>();
            RaccoonBalance balance = characterObject.GetComponent<RaccoonBalance>();
            RaccoonMouseInput mouse = characterObject.GetComponent<RaccoonMouseInput>();
            RaccoonLegMechanism legMechanism = characterObject.GetComponent<RaccoonLegMechanism>();
            RaccoonLegStepController legStep = characterObject.GetComponent<RaccoonLegStepController>();
            Gait = characterObject.GetComponent<RaccoonAlternatingLegController>();
            CenterOfMassBalance = characterObject.GetComponent<RaccoonCenterOfMassBalance>();
            Recovery = characterObject.GetComponent<RaccoonSitRecoveryController>();

            if (Character != null) Character.BoneMap = BoneMap;
            if (PhysicsRig != null) PhysicsRig.BoneMap = BoneMap;
            if (foot != null) { foot.BoneMap = BoneMap; foot.PhysicsRig = PhysicsRig; }
            if (balance != null)
            {
                balance.BoneMap = BoneMap;
                balance.PhysicsRig = PhysicsRig;
                balance.Character = Character;
                balance.FootController = foot;
                balance.StepController = Gait;
                balance.SingleLegController = legStep;
                balance.CenterOfMassBalance = CenterOfMassBalance;
            }
            if (mouse != null) mouse.FootController = foot;
            if (legMechanism != null) { legMechanism.BoneMap = BoneMap; legMechanism.PhysicsRig = PhysicsRig; }
            if (legStep != null)
            {
                legStep.BoneMap = BoneMap;
                legStep.PhysicsRig = PhysicsRig;
                legStep.InputCamera = Camera.main;
            }
            if (Gait != null)
            {
                Gait.BoneMap = BoneMap;
                Gait.PhysicsRig = PhysicsRig;
                Gait.InputCamera = Camera.main;
            }
            if (CenterOfMassBalance != null)
            {
                CenterOfMassBalance.PhysicsRig = PhysicsRig;
                CenterOfMassBalance.StepController = Gait;
                CenterOfMassBalance.BoneMap = BoneMap;
                CenterOfMassBalance.Character = Character;
            }
            if (Recovery != null)
            {
                Recovery.BoneMap = BoneMap;
                Recovery.PhysicsRig = PhysicsRig;
                Recovery.Balance = CenterOfMassBalance;
                Recovery.Character = Character;
            }

            IsBound = Character != null && BoneMap != null && PhysicsRig != null;
            if (IsBound)
                Debug.Log("[RaccoonStep] StepByStepPrototype bound character dependencies: " + characterObject.name, this);
            return IsBound;
        }

        [ContextMenu("Reset StepByStep Demo")]
        public void ResetDemo()
        {
            if (!IsBound && !BindCharacter())
                return;
            if (Recovery != null) Recovery.ResetRecovery();
            if (Gait != null) Gait.ResetToStandingPose();
            if (PhysicsRig != null) PhysicsRig.ResetToInitialPose();
            if (Character != null) Character.State = RaccoonStepState.Stable;
        }
    }
}
