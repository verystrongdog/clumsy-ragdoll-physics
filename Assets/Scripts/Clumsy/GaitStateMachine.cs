using UnityEngine;

namespace ClumsyRagdoll
{
    /// <summary>
    /// 步态的时间状态。只推进相位并平滑输入，不读取或修改任何场景对象。
    /// </summary>
    public sealed class GaitStateMachine
    {
        public struct Frame
        {
            public float Phase;
            public float TurnPhase;
            public float Drive;
            public float TurnRate;
            public float TurnDrive;
            public float MoveForward;
        }

        float _phase;
        float _turnPhase;
        float _drive;
        float _turnRate;
        float _lastYaw;
        bool _hasYaw;

        public Frame Step(
            RagdollRecipe recipe,
            Vector2 move,
            bool sprint,
            float rootYaw,
            float deltaTime)
        {
            float dt = Mathf.Max(0f, deltaTime);

            if (_hasYaw)
            {
                float rawTurnRate = Mathf.DeltaAngle(_lastYaw, rootYaw)
                    / Mathf.Max(dt, 1e-4f);
                _turnRate = Mathf.Lerp(_turnRate, rawTurnRate, 0.35f);
            }

            _lastYaw = rootYaw;
            _hasYaw = true;

            float turnDrive = Mathf.Clamp(
                _turnRate / Mathf.Max(recipe.TurnReferenceRate, 1f),
                -1f,
                1f);

            float rawDrive = Mathf.Clamp01(
                Mathf.Abs(move.y) + Mathf.Abs(move.x) * 0.5f);
            float targetDrive = rawDrive > 0.05f ? rawDrive : 0f;
            _drive = Mathf.MoveTowards(_drive, targetDrive, dt * 3.5f);

            // 后退仍然让时间正向推进；反转相位等于倒放走路动画。
            float frequency = recipe.StrideFrequency
                * (sprint ? recipe.SprintScale : 1f);
            _phase = Wrap01(_phase + dt * frequency);

            if (recipe.TurnStepping && Mathf.Abs(turnDrive) > 0.02f)
            {
                _turnPhase = Wrap01(
                    _turnPhase
                    + dt * recipe.TurnStepRate * Mathf.Abs(turnDrive));
            }

            return new Frame
            {
                Phase = _phase,
                TurnPhase = _turnPhase,
                Drive = _drive,
                TurnRate = _turnRate,
                TurnDrive = turnDrive,
                MoveForward = move.y,
            };
        }

        public void Reset()
        {
            _phase = 0f;
            _turnPhase = 0f;
            _drive = 0f;
            _turnRate = 0f;
            _lastYaw = 0f;
            _hasYaw = false;
        }

        static float Wrap01(float value)
        {
            return value - Mathf.Floor(value);
        }
    }
}
