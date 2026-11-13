using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    public sealed class SakuraSetupGuide
    {
        public sealed class GuideStep
        {
            public string Title { get; }
            public string Description { get; }
            public string Panel { get; }
            public string Target { get; }

            public GuideStep(string title, string description, string panel = null, string target = null)
            {
                Title = title; Description = description; Panel = panel; Target = target;
            }
        }

        [Serializable]
        private sealed class Progress
        {
            public int step;
            public bool done, seen;
        }

        private readonly SakuraStudio studio;
        private readonly VisualElement root, card, ring, stepButtons;
        private readonly Label progressLabel, title, description, hint;
        private readonly Button back, next;
        private readonly IVisualElementScheduledItem monitor;
        private IReadOnlyList<GuideStep> steps;
        private Progress progress;
        private VisualElement target;
        private string mode;
        private bool disposed;

        public bool IsOpen => card.style.display == DisplayStyle.Flex;
        public int CurrentStep => progress?.step ?? 0;
        public string Mode => mode;

        public static string PreferenceKey(string mode) => "sakura-setup-guide-v1-" + mode.ToLowerInvariant();

        public static IReadOnlyList<GuideStep> StepsFor(string mode)
        {
            if (mode != "Companion" && mode != "VTuber") return Array.Empty<GuideStep>();
            var result = new List<GuideStep>
            {
                new GuideStep("Welcome to " + (mode == "VTuber" ? "Sakura VTuber" : "Sakura Companion"), mode == "VTuber"
                    ? "Bring your character to life with camera tracking, expressions and streaming tools."
                    : "Chat with your character, switch to VTuber tracking, or use Agent mode for tasks."),
                new GuideStep("Choose your character", "Open Models on the left. Choose a saved PMX, VRM or Live2D model, or import a complete model package. Cloud import asks you to sign in only when you use it.", "Character", "Models")
            };
            if (mode == "Companion") result.Add(new GuideStep("Choose chat and voice", "Open the speaker icon for Voice & speech. Friend is for conversation; Teacher supports practical work. Choose your AI provider or local model in Settings.", "Voice & speech", "Voice & speech"));
            result.Add(new GuideStep("Camera is optional", "Open Camera & tracking to choose a webcam or Sakura Mobile. Start tracking when ready, then look straight ahead and calibrate. You can toggle hand tracking separately.", "Camera", "Camera & tracking"));
            result.Add(new GuideStep("Make it yours", "Use Model Settings to adjust your character and Background for your stage. Streaming & output contains Virtual Camera and Spout. Reopen this guide from Settings → General.", "Settings", "Settings"));
            return result;
        }

        public static SakuraSetupGuide Attach(SakuraStudio studio, VisualElement root)
        {
            if (studio == null || root == null) throw new ArgumentNullException(studio == null ? nameof(studio) : nameof(root));
            if (root.Q("sakura-setup-guide")?.userData is SakuraSetupGuide existing) return existing;
            return new SakuraSetupGuide(studio, root);
        }

        private SakuraSetupGuide(SakuraStudio studio, VisualElement root)
        {
            this.studio = studio; this.root = root;
            ring = new VisualElement { name = "sakura-setup-focus", pickingMode = PickingMode.Ignore };
            ring.style.position = Position.Absolute; ring.style.display = DisplayStyle.None;
            Border(ring, new Color(1, .62f, .784f), 3, 12); root.Add(ring);
            card = new VisualElement { name = "sakura-setup-guide", userData = this, focusable = true };
            card.style.position = Position.Absolute; card.style.width = 420; card.style.maxWidth = Length.Percent(95);
            card.style.paddingTop = card.style.paddingBottom = card.style.paddingLeft = card.style.paddingRight = 22;
            card.style.display = DisplayStyle.None; card.style.flexShrink = 0;
            Border(card, new Color(.255f, .294f, .349f), 1, 20); root.Add(card);
            var scroll = new ScrollView(); scroll.style.minHeight = 0; scroll.style.flexShrink = 1; card.Add(scroll);
            var body = scroll.contentContainer;
            progressLabel = Text("", body, 12); progressLabel.name = "setup-progress";
            stepButtons = Row(body); stepButtons.style.marginTop = 13; stepButtons.style.marginBottom = 8;
            title = Text("", body, 22); title.name = "sakura-setup-title"; title.style.marginTop = 12; title.style.marginBottom = 12;
            description = Text("", body, 15); description.style.marginBottom = 12;
            hint = Text("", body, 12); hint.style.marginTop = 12; hint.style.marginBottom = 12;
            var actions = Row(body); actions.style.justifyContent = Justify.SpaceBetween;
            Button("Skip setup", Finish, actions, "setup-skip");
            back = Button("Back", () => Go(CurrentStep - 1), actions, "setup-back");
            next = Button("Next", () => { if (CurrentStep == steps.Count - 1) Finish(); else Go(CurrentStep + 1); }, actions, "setup-next");
            card.RegisterCallback<KeyDownEvent>(evt => { if (evt.keyCode == KeyCode.Escape) { Finish(); evt.StopPropagation(); } });
            studio.PanelsHidden += Close;
            card.RegisterCallback<DetachFromPanelEvent>(_ => Dispose());
            monitor = card.schedule.Execute(Tick).Every(100);
            Tick();
        }

        public void Open()
        {
            if (disposed || studio.CurrentMode == "Agent") return;
            SetMode(studio.CurrentMode);
            progress = new Progress { seen = true };
            Save(); Present();
        }

        public void Close()
        {
            card.style.display = DisplayStyle.None; ring.style.display = DisplayStyle.None; target = null;
        }

        public void Finish()
        {
            if (progress != null && steps.Count > 0) { progress.done = true; progress.seen = true; Save(); }
            Close();
        }

        private void SetMode(string value)
        {
            mode = value; steps = StepsFor(value); progress = new Progress();
            if (steps.Count == 0) return;
            try { progress = JsonUtility.FromJson<Progress>(PlayerPrefs.GetString(PreferenceKey(mode), "{}")) ?? new Progress(); }
            catch (ArgumentException) { progress = new Progress(); }
            progress.step = Mathf.Clamp(progress.step, 0, steps.Count - 1);
        }

        private void Save()
        {
            PlayerPrefs.SetString(PreferenceKey(mode), JsonUtility.ToJson(progress)); PlayerPrefs.Save();
        }

        private void Tick()
        {
            if (disposed) return;
            if (mode != studio.CurrentMode)
            {
                Close(); SetMode(studio.CurrentMode);
                if (steps.Count > 0 && !progress.seen && !progress.done) { progress.seen = true; Save(); Present(); }
            }
            if (!IsOpen) return;
            ApplyTheme(); Layout();
        }

        private void Go(int index)
        {
            if (index < 0 || index >= steps.Count) return;
            progress.step = index; Save(); Present(); ShowStep();
        }

        private void Present()
        {
            target = null; ring.style.display = DisplayStyle.None;
            var step = steps[CurrentStep];
            title.text = step.Title; description.text = step.Description;
            progressLabel.text = "Quick setup · " + (CurrentStep + 1) + " of " + steps.Count;
            back.SetEnabled(CurrentStep > 0); next.text = CurrentStep == steps.Count - 1 ? "Finish" : "Next";
            hint.text = "Next opens the matching panel automatically. The guide stays open while you try the controls.";
            stepButtons.Clear();
            for (int index = 0; index < steps.Count; index++)
            {
                int destination = index;
                var button = Button((index + 1).ToString(), () => Go(destination), stepButtons, "setup-step-" + index);
                button.style.flexGrow = 1; button.style.minHeight = 32; button.style.paddingLeft = button.style.paddingRight = 0;
                button.tooltip = steps[index].Title;
                button.EnableInClassList("setup-current", index == CurrentStep);
            }
            card.style.display = DisplayStyle.Flex;
            card.BringToFront(); next.Focus(); ApplyTheme(); Layout();
        }

        private void ShowStep()
        {
            if (!IsOpen || studio.CurrentMode != mode) return;
            var step = steps[CurrentStep];
            if (step.Panel == null) return;
            if (studio.ControlsHidden) studio.ToggleChrome();
            studio.ShowPanel(step.Panel);
            target = root.Q("companion-dock")?.Query<Button>(className: "dock-button").ToList().FirstOrDefault(button => button.tooltip == step.Target);
            if (target != null) target.GetFirstAncestorOfType<ScrollView>()?.ScrollTo(target);
            hint.text = "Try the highlighted controls, then choose Next.";
            card.BringToFront(); Layout();
        }

        private void Layout()
        {
            float width = root.layout.width, height = root.layout.height;
            if (width < 1 || height < 1) return;
            card.style.maxHeight = Mathf.Max(140, height - 28);
            float cardWidth = Mathf.Min(420, width * .95f), cardHeight = card.layout.height;
            if (float.IsNaN(cardHeight) || cardHeight < 1) cardHeight = 380;
            float left = (width - cardWidth) / 2, top = (height - cardHeight) / 2;
            if (target != null && target.panel != null && target.resolvedStyle.display != DisplayStyle.None)
            {
                Rect bounds = target.worldBound;
                Vector2 position = root.WorldToLocal(bounds.position);
                ring.style.left = position.x - 5; ring.style.top = position.y - 5;
                ring.style.width = bounds.width + 10; ring.style.height = bounds.height + 10;
                ring.style.display = DisplayStyle.Flex;
                if (position.x + bounds.width + 20 + cardWidth <= width - 14) { left = position.x + bounds.width + 20; top = position.y; }
                else if (position.x - 20 - cardWidth >= 14) { left = position.x - 20 - cardWidth; top = position.y; }
                else top = position.y < height / 2 ? height - cardHeight - 14 : 14;
            }
            else ring.style.display = DisplayStyle.None;
            card.style.left = Mathf.Clamp(left, 14, Mathf.Max(14, width - cardWidth - 14));
            card.style.top = Mathf.Clamp(top, 14, Mathf.Max(14, height - cardHeight - 14));
        }

        private void ApplyTheme()
        {
            bool light = root.ClassListContains("light");
            Color accent = light ? new Color(.631f, .204f, .392f) : new Color(1, .62f, .784f);
            Color ink = light ? new Color(.286f, .204f, .263f) : new Color(1, .941f, .973f);
            card.style.backgroundColor = light ? new Color(1, .98f, .988f) : new Color(.145f, .106f, .176f);
            card.style.color = ink;
            ring.style.borderTopColor = ring.style.borderBottomColor = ring.style.borderLeftColor = ring.style.borderRightColor = accent;
            next.style.backgroundColor = accent; next.style.color = light ? Color.white : new Color(.145f, .106f, .176f);
            foreach (var button in stepButtons.Query<Button>().ToList())
                if (button.ClassListContains("setup-current")) { button.style.backgroundColor = accent; button.style.color = next.style.color; }
        }

        private void Dispose()
        {
            if (disposed) return;
            disposed = true; studio.PanelsHidden -= Close; monitor?.Pause(); ring.RemoveFromHierarchy();
        }

        private static Label Text(string value, VisualElement parent, int size)
        {
            var label = new Label(value); label.style.fontSize = size; label.style.whiteSpace = WhiteSpace.Normal; parent.Add(label); return label;
        }

        private static VisualElement Row(VisualElement parent)
        {
            var row = new VisualElement(); row.style.flexDirection = FlexDirection.Row; row.style.flexWrap = Wrap.Wrap; parent.Add(row); return row;
        }

        private static Button Button(string title, Action action, VisualElement parent, string name)
        {
            var button = new Button(action) { text = title, name = name };
            button.style.minHeight = 44; button.style.marginRight = 6; button.style.marginBottom = 4;
            button.style.paddingLeft = button.style.paddingRight = 12; parent.Add(button); return button;
        }

        private static void Border(VisualElement element, Color color, float width, float radius)
        {
            element.style.borderTopWidth = element.style.borderBottomWidth = element.style.borderLeftWidth = element.style.borderRightWidth = width;
            element.style.borderTopColor = element.style.borderBottomColor = element.style.borderLeftColor = element.style.borderRightColor = color;
            element.style.borderTopLeftRadius = element.style.borderTopRightRadius = element.style.borderBottomLeftRadius = element.style.borderBottomRightRadius = radius;
        }
    }
}
