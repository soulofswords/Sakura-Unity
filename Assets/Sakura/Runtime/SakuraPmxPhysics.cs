using System;
using Mmd.Physics;
using Mmd.UnityIntegration;
using UnityEngine;

namespace Sakura.Unity
{
    [DefaultExecutionOrder(30000)]
    public sealed class SakuraPmxPhysics : MonoBehaviour
    {
        MmdUnityPlaybackBinding binding;
        SakuraPmxModel model;
        int sequence;
        bool reset = true;
        public bool Simulating { get; private set; }
        public string Warning { get; private set; }
        public MmdLivePhysicsFrameDiagnostics Diagnostics => binding?.LastLivePhysicsDiagnostics;

        public void Initialize(SakuraPmxModel source)
        {
            model = source;
            if (source.Definition.bones.Count == 0 || source.Definition.physics.rigidbodies.Count == 0) return;
            try
            {
                binding = MmdUnityPlaybackBinding.CreateRuntimePosePhysics(source.Instance, source.Definition);
                binding.LivePhysicsBodyDiagnosticsSampleInterval = 6;
                binding.SetPhysicsMode(MmdPhysicsMode.Live);
                Simulating = true;
            }
            catch (Exception error) { StopWithError(error); }
        }

        public void SetSimulation(bool enabled)
        {
            if (binding == null) return;
            if (Simulating == enabled) return;
            Simulating = enabled; reset = true; sequence = 0;
            binding.SetPhysicsMode(enabled ? MmdPhysicsMode.Live : MmdPhysicsMode.Off);
        }

        void LateUpdate()
        {
            if (!Simulating || binding == null || model == null) return;
            try
            {
                binding.StepRuntimePosePhysics(sequence++, Mathf.Min(Time.unscaledDeltaTime, 1f / 15), reset);
                reset = false;
                model.ApplyFrozenBones();
                // MMD applies material morphs during its step; user appearance overrides run last.
                model.Apply();
            }
            catch (Exception error) { StopWithError(error); }
        }

        void StopWithError(Exception error)
        {
            Simulating = false; Warning = "PMX physics stopped: " + error.Message;
            try { binding?.Dispose(); } catch { }
            binding = null;
            Debug.LogWarning(Warning);
        }

        void OnEnable() { reset = true; sequence = 0; }
        void OnDestroy() { binding?.Dispose(); binding = null; }
    }
}
