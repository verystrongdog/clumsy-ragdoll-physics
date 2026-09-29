using UnityEngine;

namespace ClumsyRagdoll
{
    /// <summary>
    /// 玩家意图。电机只读这个结构，不读键盘。
    /// 本地同屏时由本机输入填；以后联机时由主机收到的输入包填，字段保持不变。
    /// 瞄准点是世界坐标：发令的那一侧用自己的镜头算好再送出。
    /// </summary>
    public struct PuppetCommand
    {
        public Vector2 Move;
        public float CameraYaw;
        public Vector3 AimWorldPoint;
        public float Reach;
        public bool Grab;
        public bool JumpPressed;
        public bool RespawnPressed;
    }

    public struct PuppetSense
    {
        public Vector3 ChestPosition;
        public Vector3 ChestForward;
        public float CameraYaw;
        public Camera View;
    }

    public interface IPuppetCommandSource
    {
        PuppetCommand Read(PuppetSense sense);
    }

    public static class GameLayers
    {
        public const int Puppet = 8;
        public const int WorldMask = ~(1 << Puppet);
    }

    /// <summary>
    /// 挂在地上、边界这类不该被手粘住的碰撞体上。
    /// </summary>
    public sealed class NotGrabbable : MonoBehaviour
    {
    }
}
