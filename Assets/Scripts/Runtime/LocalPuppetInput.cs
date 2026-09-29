using UnityEngine;

namespace ClumsyRagdoll
{
    /// <summary>
    /// 玩家一：WASD、鼠标、左键、空格、R。
    /// </summary>
    public sealed class KeyboardMouseInput : IPuppetCommandSource
    {
        public PuppetCommand Read(PuppetSense sense)
        {
            Vector2 move = Vector2.zero;
            if (Input.GetKey(KeyCode.W)) move.y += 1f;
            if (Input.GetKey(KeyCode.S)) move.y -= 1f;
            if (Input.GetKey(KeyCode.A)) move.x -= 1f;
            if (Input.GetKey(KeyCode.D)) move.x += 1f;
            if (move.sqrMagnitude > 1f) move.Normalize();

            bool grab = Input.GetMouseButton(0);
            float reach = grab || Input.GetKey(KeyCode.LeftShift) ? 1f : 0.4f;

            return new PuppetCommand
            {
                Move = move,
                CameraYaw = sense.CameraYaw,
                AimWorldPoint = AimFromPointer(sense),
                Reach = reach,
                Grab = grab,
                JumpPressed = Input.GetKeyDown(KeyCode.Space),
                RespawnPressed = Input.GetKeyDown(KeyCode.R)
            };
        }

        static Vector3 AimFromPointer(PuppetSense sense)
        {
            Camera cam = sense.View;
            if (cam == null)
                return sense.ChestPosition + sense.ChestForward * 1.2f;

            Ray ray = cam.ScreenPointToRay(Input.mousePosition);
            Vector3 fallback = ray.GetPoint(3.5f);
            if (Physics.Raycast(ray, out RaycastHit hit, 24f, GameLayers.WorldMask, QueryTriggerInteraction.Ignore))
            {
                if (hit.distance < 8f)
                    return hit.point;
                fallback = hit.point;
            }

            Plane chestPlane = new Plane(Vector3.up, new Vector3(0f, sense.ChestPosition.y, 0f));
            if (chestPlane.Raycast(ray, out float enter) && enter > 0.15f && enter < 9f)
                return ray.GetPoint(enter);

            return fallback;
        }
    }

    /// <summary>
    /// 玩家二：方向键移动，IJKL 伸手，U/O 抬高或放低手，右 Ctrl 抓，右 Shift 跳，M 重生。
    /// </summary>
    public sealed class ArrowKeysInput : IPuppetCommandSource
    {
        Vector2 _aim = new Vector2(0f, 1f);
        float _height = 0.15f;

        public PuppetCommand Read(PuppetSense sense)
        {
            Vector2 move = Vector2.zero;
            if (Input.GetKey(KeyCode.UpArrow)) move.y += 1f;
            if (Input.GetKey(KeyCode.DownArrow)) move.y -= 1f;
            if (Input.GetKey(KeyCode.LeftArrow)) move.x -= 1f;
            if (Input.GetKey(KeyCode.RightArrow)) move.x += 1f;
            if (move.sqrMagnitude > 1f) move.Normalize();

            Vector2 aim = Vector2.zero;
            if (Input.GetKey(KeyCode.I)) aim.y += 1f;
            if (Input.GetKey(KeyCode.K)) aim.y -= 1f;
            if (Input.GetKey(KeyCode.J)) aim.x -= 1f;
            if (Input.GetKey(KeyCode.L)) aim.x += 1f;
            bool aiming = aim.sqrMagnitude > 0.01f;
            if (aiming) _aim = aim.normalized;

            if (Input.GetKey(KeyCode.U)) _height += 1.4f * Time.deltaTime;
            if (Input.GetKey(KeyCode.O)) _height -= 1.4f * Time.deltaTime;
            _height = Mathf.Clamp(_height, -0.45f, 1.7f);

            Quaternion yaw = Quaternion.Euler(0f, sense.CameraYaw, 0f);
            Vector3 dir = yaw * new Vector3(_aim.x, 0f, _aim.y);
            Vector3 point = sense.ChestPosition + dir * 1.45f + Vector3.up * _height;

            bool grab = Input.GetKey(KeyCode.RightControl) || Input.GetKey(KeyCode.RightAlt);
            float reach = grab || aiming ? 1f : 0.35f;

            return new PuppetCommand
            {
                Move = move,
                CameraYaw = sense.CameraYaw,
                AimWorldPoint = point,
                Reach = reach,
                Grab = grab,
                JumpPressed = Input.GetKeyDown(KeyCode.RightShift),
                RespawnPressed = Input.GetKeyDown(KeyCode.M)
            };
        }
    }
}
