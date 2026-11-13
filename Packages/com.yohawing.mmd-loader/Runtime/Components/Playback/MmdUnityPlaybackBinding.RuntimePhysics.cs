using Mmd.Parser;

namespace Mmd.UnityIntegration
{
    public sealed partial class MmdUnityPlaybackBinding
    {
        /// <summary>Borrow an already imported character for pose-driven physics without replacing its renderer.</summary>
        public static MmdUnityPlaybackBinding CreateRuntimePosePhysics(MmdUnityModelInstance instance, MmdModelDefinition model)
        {
            if (instance == null || model == null) throw new System.ArgumentNullException();
            MmdModelValidator.ThrowIfInvalid(model);
            var session = new MmdRuntimeSession(model, CreateModelOnlyRestMotion(model), "runtime-character", "pose-physics");
            try
            {
                // Physics-only use deliberately keeps the host's material array and renderer ownership.
                return new MmdUnityPlaybackBinding(instance, session, model, "runtime-character", "pose-physics", MmdUnityModelInstanceOwnership.Borrowed);
            }
            catch { session.Dispose(); throw; }
        }

        /// <summary>Advance physics using the current host skeleton, preserving tracking and authored poses.</summary>
        public void StepRuntimePosePhysics(int sequenceFrame, float deltaTime, bool reset)
            => StepLivePhysicsFromCurrentPose(sequenceFrame, deltaTime, reset);
    }
}
