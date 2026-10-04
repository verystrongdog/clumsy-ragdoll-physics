using UnityEngine;

namespace StepByStep
{
    public sealed class StepByStepFreeCamera : MonoBehaviour
    {
        public float MoveSpeed = 4f;
        public float LookSpeed = 3f;
        public float ZoomSpeed = 3f;

        void Update()
        {
            Vector3 input = new Vector3(Input.GetAxisRaw("Horizontal"), 0f, Input.GetAxisRaw("Vertical"));
            if (Input.GetKey(KeyCode.Q)) input.y -= 1f;
            if (Input.GetKey(KeyCode.E)) input.y += 1f;
            transform.position += transform.TransformDirection(input.normalized) * MoveSpeed * Time.unscaledDeltaTime;
            transform.position += transform.forward * Input.mouseScrollDelta.y * ZoomSpeed * Time.unscaledDeltaTime * 10f;
            if (Input.GetMouseButton(1))
            {
                float yaw = Input.GetAxis("Mouse X") * LookSpeed;
                float pitch = -Input.GetAxis("Mouse Y") * LookSpeed;
                transform.Rotate(Vector3.up, yaw, Space.World);
                transform.Rotate(Vector3.right, pitch, Space.Self);
            }
        }
    }
}
