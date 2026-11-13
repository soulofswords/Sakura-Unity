using System;
using System.Collections.Generic;
using Live2D.Cubism.Core;
using Live2D.Cubism.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    [DefaultExecutionOrder(-8)]
    public sealed class SakuraLipSync : MonoBehaviour
    {
        [Serializable] private sealed class Preferences { public bool enabled = true; public float gain = 1, cutoff = .01f, smoothing = .08f; public string mapping = ""; }
        private Preferences settings = new Preferences();
        private readonly float[] samples = new float[1024];
        private SakuraAvatarViewport viewport;
        private SakuraVoice voice;
        private SakuraTracking tracking;
        private GameObject avatar;
        private SakuraLipSyncRig rig;
        private string preference;
        private float level;
        public event Action Changed;
        public float Level => level;
        public bool Enabled => settings.enabled;
        private void Awake() { viewport = GetComponent<SakuraAvatarViewport>(); voice = GetComponent<SakuraVoice>(); tracking = GetComponent<SakuraTracking>(); }
        public static float Envelope(float previous, float rms, float gain, float cutoff, float smoothing, float delta)
        {
            if (!float.IsFinite(previous)) previous = 0;
            if (float.IsNaN(rms) || float.IsInfinity(rms)) rms = 0;
            if (!float.IsFinite(gain)) gain = 1;
            if (!float.IsFinite(cutoff)) cutoff = .01f;
            if (!float.IsFinite(smoothing)) smoothing = .08f;
            if (!float.IsFinite(delta)) delta = 0;
            float target = Mathf.Clamp01(Mathf.Max(0, rms - Mathf.Clamp(cutoff, 0, .2f)) * Mathf.Clamp(gain, 0, 5) * 8);
            return Mathf.Clamp01(previous + (target - previous) * (1 - Mathf.Exp(-Mathf.Clamp(delta, 0, .1f) / Mathf.Clamp(smoothing, .01f, .5f))));
        }
        public static bool AutomaticAllowed(bool enabled, bool cameraMouth, bool manualMouth) => enabled && !cameraMouth && !manualMouth;
        public static bool AutomaticMouthName(string name)
        {
            string value = (name ?? "").Replace("Parameter · ", "").Trim().ToLowerInvariant();
            return value == "aa" || value == "あ" || value == "a" || value == "parammouthopeny" || value == "jawopen" || value == "mouthopen";
        }
        public static bool MouthName(string name)
        {
            string value = (name ?? "").ToLowerInvariant();
            return AutomaticMouthName(value) || value.Contains("mouth") || value.Contains("jaw") || value.Contains("lip") || value.Contains("口") || value == "い" || value == "う" || value == "え" || value == "お";
        }
        private void Update()
        {
            if (viewport == null) viewport = GetComponent<SakuraAvatarViewport>();
            if (voice == null) voice = GetComponent<SakuraVoice>();
            if (tracking == null) tracking = GetComponent<SakuraTracking>();
            if (viewport == null) return;
            if (avatar != viewport.AvatarRoot)
            {
                if (rig != null) rig.Release(false);
                avatar = viewport.AvatarRoot; level = 0;
                preference = string.IsNullOrEmpty(viewport.LoadedModelPath) ? null : SakuraAvatarExpressions.PreferenceKey(viewport.LoadedModelPath) + ":mouth";
                settings = new Preferences();
                if (preference != null && PlayerPrefs.HasKey(preference))
                { try { settings = JsonUtility.FromJson<Preferences>(PlayerPrefs.GetString(preference)) ?? new Preferences(); } catch (ArgumentException) { settings = new Preferences(); } }
                settings.gain = float.IsFinite(settings.gain) ? Mathf.Clamp(settings.gain, 0, 5) : 1;
                settings.cutoff = float.IsFinite(settings.cutoff) ? Mathf.Clamp(settings.cutoff, 0, .2f) : .01f;
                settings.smoothing = float.IsFinite(settings.smoothing) ? Mathf.Clamp(settings.smoothing, .01f, .5f) : .08f;
                rig = avatar == null ? null : avatar.GetComponent<SakuraLipSyncRig>() ?? avatar.AddComponent<SakuraLipSyncRig>();
                if (rig != null) rig.Initialize(this, viewport.Expressions);
                Changed?.Invoke();
            }
            if (rig == null) return;
            var expressions = viewport.Expressions;
            bool manual = false;
            if (expressions != null)
                foreach (var entry in expressions.Entries) if ((entry.Active || entry.PreviewUntil > Time.unscaledTime) && MouthName(entry.Name)) { manual = true; break; }
            bool camera = tracking != null && tracking.Mouth && tracking.Source != SakuraTracking.InputSource.Off && tracking.Source != SakuraTracking.InputSource.Cursor;
            if (!AutomaticAllowed(settings.enabled, camera, manual))
            { level = 0; rig.Release(camera); return; }
            var changer = GetComponent<SakuraVoiceChanger>();
            AudioSource source = changer?.PlaybackSource != null && changer.PlaybackSource.isPlaying ? changer.PlaybackSource : voice?.SpeechSource;
            float rms = 0;
            if (source != null && source.isPlaying)
            { source.GetOutputData(samples, 0); rms = SakuraVoice.Rms(samples); }
            level = Envelope(level, rms, settings.gain, settings.cutoff, settings.smoothing, Time.unscaledDeltaTime);
            rig.Select(settings.mapping);
        }
        private void Save() { if (preference != null) { PlayerPrefs.SetString(preference, JsonUtility.ToJson(settings)); PlayerPrefs.Save(); } Changed?.Invoke(); }
        public void BuildPanel(VisualElement parent)
        {
            var card = new VisualElement(); card.AddToClassList("card"); parent.Add(card);
            var title = new Label("Speech lip sync"); title.AddToClassList("card-title"); card.Add(title);
            var enabled = new Toggle("Animate mouth from TTS, Chat Reader and converted voice playback") { value = settings.enabled }; card.Add(enabled);
            var gain = new Slider("Mouth strength", 0, 5) { value = settings.gain, showInputField = true }; card.Add(gain);
            var cutoff = new Slider("Silence cutoff", 0, .2f) { value = settings.cutoff, showInputField = true }; card.Add(cutoff);
            var smoothing = new Slider("Mouth smoothing", .01f, .5f) { value = settings.smoothing, showInputField = true }; card.Add(smoothing);
            var mapping = new DropdownField("A / mouth-open shape", new List<string> { "Automatic", "Disabled" }, 0); card.Add(mapping);
            var note = new Label("Uses the actual playback envelope. Manual mouth expressions and enabled camera mouth tracking take priority. Mapping and strength are saved for the loaded model."); note.AddToClassList("description"); card.Add(note);
            var ids = new List<string>();
            Action refresh = () =>
            {
                enabled.SetValueWithoutNotify(settings.enabled); gain.SetValueWithoutNotify(settings.gain); cutoff.SetValueWithoutNotify(settings.cutoff); smoothing.SetValueWithoutNotify(settings.smoothing);
                ids.Clear(); ids.Add(""); ids.Add("none"); var titles = new List<string> { "Automatic", "Disabled" };
                if (viewport?.Expressions != null) foreach (var entry in viewport.Expressions.Entries) { ids.Add(entry.Id); titles.Add(entry.Name); }
                mapping.choices = titles; mapping.SetValueWithoutNotify(titles[Math.Max(0, ids.IndexOf(settings.mapping ?? ""))]);
            };
            enabled.RegisterValueChangedCallback(evt => { settings.enabled = evt.newValue; if (!evt.newValue) { level = 0; rig?.Release(false); } Save(); });
            gain.RegisterValueChangedCallback(evt => { settings.gain = evt.newValue; Save(); });
            cutoff.RegisterValueChangedCallback(evt => { settings.cutoff = evt.newValue; Save(); });
            smoothing.RegisterValueChangedCallback(evt => { settings.smoothing = evt.newValue; Save(); });
            mapping.RegisterValueChangedCallback(_ => { if (mapping.index >= 0 && mapping.index < ids.Count) { rig?.Release(false); settings.mapping = ids[mapping.index]; Save(); } });
            Changed += refresh; card.RegisterCallback<DetachFromPanelEvent>(_ => Changed -= refresh); refresh();
        }
        private void OnDisable() { level = 0; rig?.Release(false); }
    }

    [DefaultExecutionOrder(-7)]
    public sealed class SakuraLipSyncRig : MonoBehaviour, ICubismUpdatable
    {
        private SakuraLipSync owner;
        private SakuraAvatarExpressions expressions;
        private SakuraAvatarExpressions.Entry target;
        private bool applied;
        private bool live2D, vrm;
        public bool HasUpdateController { get; set; }
        public int ExecutionOrder => 880;
        public bool NeedsUpdateOnEditing => false;
        public void Initialize(SakuraLipSync source, SakuraAvatarExpressions controller)
        {
            Release(false); owner = source; expressions = controller;
            live2D = GetComponent<CubismModel>() != null; vrm = GetComponent<UniVRM10.Vrm10Instance>() != null;
            var updates = GetComponent<CubismUpdateController>(); HasUpdateController = updates != null; updates?.Refresh();
        }
        public void Select(string mapping)
        {
            SakuraAvatarExpressions.Entry selected = null;
            if (expressions != null && mapping != "none")
                foreach (var entry in expressions.Entries)
                    if (!string.IsNullOrEmpty(mapping) ? entry.Id == mapping : SakuraLipSync.AutomaticMouthName(entry.Name)) { selected = entry; break; }
            if (selected != target) { Release(false); target = selected; }
        }
        public void Release(bool cameraOwnsMouth)
        {
            if (applied && target != null && !cameraOwnsMouth && !target.Active && target.PreviewUntil <= Time.unscaledTime) target.Apply(0);
            applied = false;
        }
        private void Apply()
        {
            if (owner == null || !owner.isActiveAndEnabled || !owner.Enabled || target == null || target.Active || target.PreviewUntil > Time.unscaledTime) return;
            if (owner.Level <= .0001f) { Release(false); return; }
            target.Apply(owner.Level); applied = true;
        }
        private void Update() { if (vrm) Apply(); }
        public void OnLateUpdate() { if (isActiveAndEnabled && live2D) Apply(); }
        private void LateUpdate() { if (!live2D && !vrm) Apply(); else if (live2D && !HasUpdateController) OnLateUpdate(); }
        private void OnDisable() { Release(false); }
    }
}
