using System;
using System.Linq;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    public sealed class SakuraExpressionPanel
    {
        public SakuraExpressionPanel(SakuraAvatarViewport viewport, VisualElement parent)
        {
            var owner = new VisualElement(); parent.Add(owner);
            var controller = viewport.Expressions;
            if (controller == null) { owner.Add(new Label("Load a model first.")); return; }
            var search = new TextField("Search expressions"); owner.Add(search);
            var status = new Label("Shortcuts work while Sakura has focus. Manual expressions take priority over idle motion."); status.style.whiteSpace = WhiteSpace.Normal; owner.Add(status);
            var rows = new VisualElement(); owner.Add(rows);
            bool alive = true;
            void Run(Action action)
            {
                if (!alive || controller == null || viewport.Expressions != controller) return;
                try { action(); } catch (Exception error) { status.text = error.Message; }
            }
            void Render()
            {
                rows.Clear();
                var matches = controller.Entries.Where(entry => (entry.Label + " " + entry.Name).IndexOf(search.value ?? "", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
                if (matches.Length == 0) rows.Add(new Label("No matching expressions."));
                foreach (var entry in matches)
                {
                    var fold = new Foldout { text = entry.Label, value = false }; rows.Add(fold);
                    bool built = false;
                    fold.RegisterValueChangedCallback(evt =>
                    {
                        if (!evt.newValue || built) return; built = true;
                        var slider = new Slider("Strength", 0, 1) { value = entry.Strength, showInputField = true };
                        slider.RegisterValueChangedCallback(change => Run(() => controller.SetStrength(entry.Id, change.newValue))); fold.Add(slider);
                        fold.Add(new Button(() => Run(() => controller.Preview(entry.Id))) { text = "Preview" });
                        var active = new Toggle("On") { value = entry.Active };
                        active.RegisterValueChangedCallback(change => Run(() => controller.SetActive(entry.Id, change.newValue))); fold.Add(active);
                        var rename = new TextField("Name") { value = entry.Label }; fold.Add(rename);
                        fold.Add(new Button(() => Run(() => { controller.Rename(entry.Id, rename.value); fold.text = entry.Label; })) { text = "Rename" });
                        var key = new TextField("Hotkey") { value = entry.Hotkey ?? "" }; fold.Add(key);
                        fold.Add(new Button(() => Run(() => { controller.Bind(entry.Id, key.value); status.text = "Shortcut saved."; })) { text = "Set hotkey" });
                        fold.Add(new Button(() => Run(() => { controller.Bind(entry.Id, ""); key.SetValueWithoutNotify(""); })) { text = "Clear hotkey" });
                        void Refresh() { if (alive) active.SetValueWithoutNotify(entry.Active); }
                        controller.Changed += Refresh;
                        active.RegisterCallback<DetachFromPanelEvent>(_ => { if (controller != null) controller.Changed -= Refresh; });
                    });
                }
            }
            owner.RegisterCallback<DetachFromPanelEvent>(_ => alive = false);
            search.RegisterValueChangedCallback(_ => Render()); Render();
        }
    }
}
