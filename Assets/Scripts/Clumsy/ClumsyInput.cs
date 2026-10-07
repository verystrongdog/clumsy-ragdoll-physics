using UnityEngine;

namespace ClumsyRagdoll
{
    public interface IClumsyInput
    {
        Vector2 Move { get; }
        bool Sprint { get; }
        bool JumpHeld { get; }
    }

    /// <summary>
    /// Default keyboard input provider for the playable ragdoll.
    /// </summary>
    public sealed class KeyboardInput : IClumsyInput
    {
        public Vector2 Move
        {
            get
            {
                float x = 0f;
                float y = 0f;
                if (Input.GetKey(KeyCode.A)) x -= 1f;
                if (Input.GetKey(KeyCode.D)) x += 1f;
                if (Input.GetKey(KeyCode.S)) y -= 1f;
                if (Input.GetKey(KeyCode.W)) y += 1f;

                Vector2 value = new Vector2(x, y);
                return value.sqrMagnitude > 1f ? value.normalized : value;
            }
        }

        public bool Sprint
        {
            get { return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift); }
        }

        public bool JumpHeld
        {
            get { return Input.GetKey(KeyCode.Space) || Input.GetAxisRaw("Jump") > 0.5f; }
        }
    }
}
