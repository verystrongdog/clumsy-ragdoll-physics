using UnityEngine;

namespace ClumsyRagdoll
{
    public struct GaitPoseFrame
    {
        public bool FootGaitActive;
        public float AirBlend;

        public float UpperLegLeftRight;
        public float UpperLegLeftForward;
        public float UpperLegRightRight;
        public float UpperLegRightForward;
        public float LowerLegLeftRight;
        public float LowerLegRightRight;

        public float ShoulderLeftForward;
        public float ShoulderRightForward;
        public float ArmLeftRight;
        public float ArmRightRight;
        public float ForearmLeftRight;
        public float ForearmRightRight;

        public float SpineRight;
        public float SpineUp;
        public float SpineForward;
        public float NeckRight;
        public float NeckForward;
        public float HeadRight;
        public float HeadForward;
    }

    /// <summary>
    /// Pure gait-state calculator. It produces angles only; it does not find
    /// bones, read colliders or write joints.
    /// </summary>
    public sealed class GaitPoseCalculator
    {
        float _airBlend;

        public GaitPoseFrame Calculate(
            RagdollRecipe recipe,
            ClumsyController controller,
            float phase,
            float turnPhase,
            float blend,
            float turn,
            float moveForward,
            float pitchDegrees,
            float deltaTime,
            bool isGrounded)
        {
            GaitPoseFrame frame = new GaitPoseFrame();

            bool backward = moveForward < -0.05f;
            float stride = backward ? recipe.BackwardStrideScale : 1f;
            float gaitSign = backward ? -1f : 1f;
            float gStride = controller != null && controller.GaitActive
                ? controller.GaitStrideScale
                : 1f;

            float twoPi = Mathf.PI * 2f;
            float s = Mathf.Sin(phase * twoPi);
            float c = Mathf.Cos(phase * twoPi);
            float ts = Mathf.Sin(turnPhase * twoPi);
            float tAbs = Mathf.Abs(turn);
            float amp = blend;
            float tAmp = recipe.TurnStepping ? tAbs : 0f;
            float turnSign = turn >= 0f ? 1f : -1f;

            float legL;
            float legR;
            float kneeL;
            float kneeR;

            bool gaitLegs = recipe.UseFootGait
                && controller != null
                && controller.GaitActive;
            frame.FootGaitActive = gaitLegs;

            float gaitAbduct = 0f;
            float gaitStanceAbduct = 0f;
            bool abductIsRightSwing = false;

            if (gaitLegs)
            {
                float u = controller.SwingProgress;
                bool rightSwings = controller.SwingFoot == RagdollPartId.FootRight;

                float uReach = Mathf.Clamp(recipe.SwingReachAt, 0.3f, 0.95f);
                float reach = 1f + recipe.SwingOvershoot;
                float fSwing;

                if (u < uReach)
                {
                    fSwing = Mathf.Lerp(
                        -1f,
                        reach,
                        Mathf.SmoothStep(0f, 1f, u / uReach));
                }
                else
                {
                    fSwing = Mathf.Lerp(
                        reach,
                        1f,
                        Mathf.SmoothStep(
                            0f,
                            1f,
                            (u - uReach) / Mathf.Max(0.05f, 1f - uReach)));
                }

                float fSupport = 1f - 2f * u;
                float fL = rightSwings ? fSupport : fSwing;
                float fR = rightSwings ? fSwing : fSupport;

                legL = -recipe.HipSwingDegrees * amp * stride
                    * gStride * gaitSign * fL;
                legR = -recipe.HipSwingDegrees * amp * stride
                    * gStride * gaitSign * fR;

                float kp = Mathf.Clamp(recipe.SwingKneePeakAt, 0.15f, 0.85f);
                float kneeShape = u < kp
                    ? Mathf.Sin((u / kp) * (Mathf.PI * 0.5f))
                    : Mathf.Cos(
                        ((u - kp) / Mathf.Max(0.05f, 1f - kp))
                        * (Mathf.PI * 0.5f));

                float kneeSw = recipe.KneeBendDegrees * kneeShape * amp;
                gaitAbduct = recipe.GaitSwingAbductDegrees
                    * Mathf.Sin(u * Mathf.PI)
                    * recipe.GaitAbductSign;
                gaitStanceAbduct = recipe.GaitStanceAbductDegrees
                    * amp
                    * recipe.GaitAbductSign;
                abductIsRightSwing = rightSwings;

                float kneeSup = recipe.GaitSupportKneeScale
                    * recipe.KneeBendDegrees
                    * amp
                    * stride
                    * gStride;

                kneeL = rightSwings ? kneeSup : kneeSw;
                kneeR = rightSwings ? kneeSw : kneeSup;
            }
            else
            {
                legL = -recipe.HipSwingDegrees * s * amp * stride * gaitSign;
                legR = +recipe.HipSwingDegrees * s * amp * stride * gaitSign;
                kneeL = recipe.KneeBendDegrees
                    * Mathf.Max(0f, s * gaitSign)
                    * amp
                    * stride;
                kneeR = recipe.KneeBendDegrees
                    * Mathf.Max(0f, -s * gaitSign)
                    * amp
                    * stride;
            }

            float turnLiftAmp = gaitLegs ? 0f : tAmp;
            float liftL = -recipe.TurnLiftDegrees
                * Mathf.Max(0f, ts)
                * turnLiftAmp;
            float liftR = -recipe.TurnLiftDegrees
                * Mathf.Max(0f, -ts)
                * turnLiftAmp;
            float abdL = -recipe.TurnAbductDegrees * ts * tAmp * turnSign;
            float abdR = +recipe.TurnAbductDegrees * ts * tAmp * turnSign;

            if (gaitLegs)
            {
                if (abductIsRightSwing)
                    abdR += gaitAbduct;
                else
                    abdL -= gaitAbduct;

                abdL -= gaitStanceAbduct;
                abdR += gaitStanceAbduct;
            }

            bool air = !isGrounded;
            float airWant = air ? 1f : 0f;
            _airBlend = Mathf.MoveTowards(
                _airBlend,
                airWant,
                deltaTime * (air ? 9f : 11f));

            if (_airBlend > 0.001f)
            {
                legL = Mathf.Lerp(legL, -recipe.AirHipDegrees, _airBlend);
                legR = Mathf.Lerp(legR, -recipe.AirHipDegrees, _airBlend);
                kneeL = Mathf.Lerp(kneeL, recipe.AirKneeDegrees, _airBlend);
                kneeR = Mathf.Lerp(kneeR, recipe.AirKneeDegrees, _airBlend);
            }

            frame.AirBlend = _airBlend;
            frame.UpperLegLeftRight = legL + liftL;
            frame.UpperLegLeftForward = abdL;
            frame.UpperLegRightRight = legR + liftR;
            frame.UpperLegRightForward = abdR;
            frame.LowerLegLeftRight = kneeL + Mathf.Max(0f, ts) * tAmp * 10f;
            frame.LowerLegRightRight = kneeR + Mathf.Max(0f, -ts) * tAmp * 10f;

            float armAmp = recipe.ArmSwingDegrees * amp * stride;
            frame.ShoulderLeftForward = -6f * tAmp * turnSign;
            frame.ShoulderRightForward = -6f * tAmp * turnSign;

            if (gaitLegs)
            {
                float gl = Mathf.Cos(controller.SwingProgress * Mathf.PI);
                bool rightSwings = controller.SwingFoot == RagdollPartId.FootRight;
                float ffL = (rightSwings ? +gl : -gl) * gaitSign;
                float ffR = (rightSwings ? -gl : +gl) * gaitSign;

                if (_airBlend > 0.001f)
                {
                    frame.ArmLeftRight = Mathf.Lerp(
                        +armAmp * ffL,
                        -recipe.AirArmDegrees,
                        _airBlend);
                    frame.ArmRightRight = Mathf.Lerp(
                        -armAmp * ffR,
                        -recipe.AirArmDegrees,
                        _airBlend);
                }
                else
                {
                    frame.ArmLeftRight = +armAmp * ffL;
                    frame.ArmRightRight = -armAmp * ffR;
                }

                frame.ForearmLeftRight =
                    -armAmp * 0.4f * Mathf.Max(0f, -ffL);
                frame.ForearmRightRight =
                    +armAmp * 0.4f * Mathf.Max(0f, -ffR);
            }
            else
            {
                frame.ArmLeftRight = +armAmp * s * gaitSign;
                frame.ArmRightRight = -armAmp * s * gaitSign;
                frame.ForearmLeftRight =
                    -armAmp * 0.4f * Mathf.Max(0f, s * gaitSign);
                frame.ForearmRightRight =
                    +armAmp * 0.4f * Mathf.Max(0f, -s * gaitSign);
            }

            float lean = (backward
                    ? recipe.BackwardLeanDegrees
                    : recipe.SpineLeanDegrees)
                * amp
                + pitchDegrees;
            float sway = (1f - amp) * 1.5f * c;
            float roll = -5f * tAmp * turnSign;

            frame.SpineRight = lean;
            frame.SpineUp = sway;
            frame.SpineForward = roll;
            frame.NeckRight = -lean * 0.4f;
            frame.NeckForward = roll * 0.3f;
            frame.HeadRight = -lean * 0.3f + (1f - amp) * 1.2f * s;
            frame.HeadForward = roll * 0.5f;

            return frame;
        }
    }
}
