using UnityEngine;

namespace ClumsyRagdoll
{
    /// <summary>
    /// Reads movement input and owns short-lived control state.
    /// It does not access Unity Input, scene objects, or physics bodies.
    /// </summary>
    public sealed class ClumsyMovementState
    {
        public struct InputFrame
        {
            public Vector2 Move;
            public bool Sprint;
            public bool JumpPressed;
        }

        bool _jumpHeldLatch;
        float _turnRate;
        float _lastRootYaw;
        bool _hasRootYaw;

        public InputFrame ReadInput(IClumsyInput source)
        {
            if (source == null)
                return default(InputFrame);

            bool jumpHeld = source.JumpHeld;
            InputFrame frame = new InputFrame
            {
                Move = source.Move,
                Sprint = source.Sprint,
                JumpPressed = jumpHeld && !_jumpHeldLatch,
            };

            _jumpHeldLatch = jumpHeld;
            return frame;
        }

        public float UpdateTurnRate(float rootYaw, float deltaTime)
        {
            if (_hasRootYaw)
            {
                float raw = Mathf.DeltaAngle(_lastRootYaw, rootYaw)
                    / Mathf.Max(deltaTime, 1e-4f);
                _turnRate = Mathf.Lerp(_turnRate, raw, 0.4f);
            }

            _lastRootYaw = rootYaw;
            _hasRootYaw = true;
            return _turnRate;
        }

        public void Reset()
        {
            _jumpHeldLatch = false;
            _turnRate = 0f;
            _lastRootYaw = 0f;
            _hasRootYaw = false;
        }
    }
}
