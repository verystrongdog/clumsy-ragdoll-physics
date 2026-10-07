namespace ClumsyRagdoll
{
    /// <summary>
    /// Canonical identifiers for the logical ragdoll parts.
    /// The legacy string-based lookup remains compatible, but callers no longer
    /// need to repeat literal keys throughout the control code.
    /// </summary>
    public static class RagdollPartId
    {
        public const string Head = "head";
        public const string Neck = "neck";
        public const string Spine = "spine";

        public const string UpperLegLeft = "uplegl";
        public const string UpperLegRight = "uplegr";
        public const string LowerLegLeft = "legl";
        public const string LowerLegRight = "legr";
        public const string FootLeft = "footl";
        public const string FootRight = "footr";

        public const string ShoulderLeft = "shoulderl";
        public const string ShoulderRight = "shoulderr";
        public const string ArmLeft = "arml";
        public const string ArmRight = "armr";
        public const string ForearmLeft = "forearml";
        public const string ForearmRight = "forearmr";
        public const string HandLeft = "handl";
        public const string HandRight = "handr";

        public static string UpperLeg(bool right)
        {
            return right ? UpperLegRight : UpperLegLeft;
        }

        public static string LowerLeg(bool right)
        {
            return right ? LowerLegRight : LowerLegLeft;
        }

        public static string Foot(bool right)
        {
            return right ? FootRight : FootLeft;
        }

        public static bool IsLegOrFoot(string key)
        {
            return key == UpperLegLeft || key == UpperLegRight
                || key == LowerLegLeft || key == LowerLegRight
                || key == FootLeft || key == FootRight;
        }

        public static bool IsArmOrHand(string key)
        {
            return key == ShoulderLeft || key == ShoulderRight
                || key == ArmLeft || key == ArmRight
                || key == ForearmLeft || key == ForearmRight
                || key == HandLeft || key == HandRight;
        }

        public static bool IsForearmOrHand(string key)
        {
            return key == ForearmLeft || key == ForearmRight
                || key == HandLeft || key == HandRight;
        }
    }
}
