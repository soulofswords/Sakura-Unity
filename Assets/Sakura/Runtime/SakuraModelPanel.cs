using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    public sealed class SakuraModelPanel
    {
        private readonly SakuraAvatarViewport viewport;
        private readonly SakuraBackend backend;
        private readonly VisualElement tabs, body;
        private string selected;

        public SakuraModelPanel(SakuraAvatarViewport viewport, SakuraBackend backend, VisualElement parent, string initialTab = null)
        {
            this.viewport = viewport; this.backend = backend;
            var owner = new VisualElement(); owner.AddToClassList("model-settings-panel"); parent.Add(owner);
            var zoom = new VisualElement(); zoom.AddToClassList("model-zoom"); owner.Add(zoom);
            zoom.Add(new Label("Model zoom"));
            var value = new IntegerField { value = Mathf.RoundToInt(viewport.ZoomPercent) }; value.name = "model-zoom-percent";
            void SetZoom(float percent) { viewport.SetZoomPercent(percent); value.SetValueWithoutNotify(Mathf.RoundToInt(viewport.ZoomPercent)); }
            zoom.Add(new Button(() => SetZoom(viewport.ZoomPercent - 10)) { text = "−" }); zoom.Add(value); zoom.Add(new Label("%"));
            zoom.Add(new Button(() => SetZoom(viewport.ZoomPercent + 10)) { text = "+" });
            zoom.Add(new Button(() => SetZoom(100)) { text = "100%" });
            value.RegisterValueChangedCallback(change => SetZoom(change.newValue));
            var strip = new ScrollView(ScrollViewMode.Horizontal); strip.AddToClassList("model-tabs"); owner.Add(strip);
            tabs = strip.contentContainer; tabs.style.flexDirection = FlexDirection.Row;
            string[] pages = viewport.LoadedFormat == "Live2D"
                ? new[] { "Texture remapping","Texture coloring","Expressions & hotkeys","Bones","Mouth","Parameters","Effects","Motion" }
                : new[] { "Texture remapping","Texture coloring","Expressions & hotkeys","Bones","Mouth" };
            strip.EnableInClassList("model-tabs-three",viewport.LoadedFormat != "Live2D");
            foreach (string page in pages)
            {
                string tab = page;
                var button = new Button(() => Select(tab)) { text = tab }; button.AddToClassList("model-tab"); tabs.Add(button);
            }
            body = new VisualElement(); body.AddToClassList("model-tab-body"); owner.Add(body);
            selected = initialTab ?? PlayerPrefs.GetString("sakura-model-settings-tab:" + (viewport.LoadedFormat ?? "none"),"Texture remapping");
            if (!pages.Contains(selected)) selected = pages[0];
            Select(selected);
        }

        private void Select(string tab)
        {
            selected = tab; body.Clear();
            foreach (var button in tabs.Children().OfType<Button>()) button.EnableInClassList("tab-selected",button.text == selected);
            PlayerPrefs.SetString("sakura-model-settings-tab:" + (viewport.LoadedFormat ?? "none"),selected);
            if (selected == "Expressions & hotkeys") { new SakuraExpressionPanel(viewport,body); return; }
            if (selected == "Motion") { Motion(); return; }
            if (viewport.Pmx != null && (selected == "Texture remapping" || selected == "Texture coloring" || selected == "Bones"))
            { new SakuraPmxPanel(viewport,backend,body,selected,false); return; }
            if (viewport.HasAvatar && (selected == "Texture remapping" || selected == "Texture coloring" || selected == "Bones" || selected == "Parameters" || selected == "Effects"))
            { new SakuraMaterialPanel(viewport,backend,body,selected); return; }
            if (selected == "Mouth")
            {
                body.Add(new Label("Mouth & lip sync"));
                body.Add(new Label("Select a mouth expression to preview or bind it. Camera mouth tracking is controlled separately in Camera & tracking."));
                new SakuraExpressionPanel(viewport,body,"",true);
                return;
            }
            body.Add(new Label(viewport.HasAvatar ? "This native renderer control is still being migrated for " + viewport.LoadedFormat + "." : "Choose a character from Models first."));
        }

        private void Motion()
        {
            var motion = viewport.Idle;
            if (motion == null) { body.Add(new Label("Choose a model first.")); return; }
            foreach (string title in new[] { "Auto breathing","Auto blinking","Idle motions" })
            {
                string option = title;
                var toggle = new Toggle(option) { value = option == "Auto breathing" ? motion.Breathing : option == "Auto blinking" ? motion.Blinking : motion.IdleMotions };
                toggle.RegisterValueChangedCallback(change =>
                {
                    if (viewport.Idle != motion) return;
                    if (option == "Auto breathing") motion.Breathing = change.newValue;
                    else if (option == "Auto blinking") motion.Blinking = change.newValue;
                    else motion.IdleMotions = change.newValue;
                    motion.Save();
                }); body.Add(toggle);
            }
            var strength = new Slider("Breathing strength",0,2) { value = motion.BreathStrength, showInputField = true }; body.Add(strength);
            strength.RegisterValueChangedCallback(change => { if (viewport.Idle == motion) { motion.BreathStrength = change.newValue; motion.Save(); } });
            var speed = new Slider("Breathing speed",.25f,3) { value = motion.BreathSpeed, showInputField = true }; body.Add(speed);
            speed.RegisterValueChangedCallback(change => { if (viewport.Idle == motion) { motion.BreathSpeed = change.newValue; motion.Save(); } });
        }
    }
}
