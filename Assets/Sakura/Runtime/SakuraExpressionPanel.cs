using System;
using System.Linq;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    public sealed class SakuraExpressionPanel
    {
        public SakuraExpressionPanel(SakuraAvatarViewport viewport, VisualElement parent, string initialSearch = "", bool mouthOnly = false)
        {
            var owner = new VisualElement(); parent.Add(owner);
            var controller = viewport.Expressions;
            if (controller == null) { owner.Add(new Label("Load a model first.")); return; }
            viewport.AvatarRoot?.GetComponent<SakuraMaterialExpressions>()?.BuildPanel(owner);
            var search = new TextField { value = initialSearch }; search.textEdition.placeholder = "Search expressions…"; owner.Add(search);
            var status = new Label("Shortcuts work while Sakura has focus. Manual expressions take priority over idle motion."); status.AddToClassList("description"); owner.Add(status);
            var rows = new VisualElement(); owner.Add(rows);
            bool alive = true, previousSuspend = false;
            string recording = null;
            bool recordingAction = false;
            var studio = viewport.GetComponent<SakuraStudio>();
            IVisualElementScheduledItem recorder = null;
            void StopRecording()
            {
                if (recording == null) return;
                recorder?.Pause(); recorder = null; recording = null;
                if (controller != null) controller.SuspendHotkeys = previousSuspend;
            }
            void Run(Action action)
            {
                if (!alive || controller == null || viewport.Expressions != controller) return;
                try { action(); } catch (Exception error) { status.text = error.Message; }
            }
            void Record(string id, bool action, Label shortcut)
            {
                StopRecording(); previousSuspend = controller.SuspendHotkeys;
                controller.SuspendHotkeys = true; recording = id; recordingAction = action;
                owner.Focus(); status.text = "Press your shortcut. Escape cancels.";
                recorder = owner.schedule.Execute(() =>
                {
                    string key = SakuraAvatarExpressions.PressedHotkey(); if (key == null || recording == null) return;
                    if (key == "Escape") { StopRecording(); status.text = "Shortcut unchanged."; return; }
                    Run(() =>
                    {
                        if (recordingAction) controller.BindAction(recording, key); else controller.Bind(recording, key);
                        shortcut.text = key; StopRecording(); status.text = "Shortcut saved.";
                    });
                }).Every(16);
            }
            void Render()
            {
                StopRecording(); rows.Clear();
                var matches = controller.Entries.Where(entry => (mouthOnly || !entry.Id.StartsWith("parameter:", StringComparison.Ordinal)) && (!mouthOnly || IsMouth(entry.Name)) && (entry.Label + " " + entry.Name).IndexOf(search.value ?? "", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
                if (matches.Length == 0) rows.Add(new Label("No matching expressions."));
                foreach (var entry in matches)
                {
                    var fold = new Foldout { text = entry.Label, value = false }; fold.AddToClassList("expression-card"); rows.Add(fold);
                    var header = fold.Q<Toggle>();
                    var shortcut = new Label(entry.Hotkey ?? ""); shortcut.AddToClassList("expression-shortcut"); header.Add(shortcut);
                    var expand = new Label("+"); expand.AddToClassList("expression-expand"); header.Add(expand);
                    bool built = false;
                    fold.RegisterValueChangedCallback(evt =>
                    {
                        expand.text = fold.value ? "−" : "+";
                        if (!evt.newValue || built) return; built = true;
                        var slider = new SakuraSlider("Strength", 0, 100) { value = entry.Strength * 100, showInputField = true };
                        slider.RegisterValueChangedCallback(change => Run(() => controller.SetStrength(entry.Id, change.newValue / 100))); fold.Add(slider);
                        var actions = new VisualElement(); actions.AddToClassList("row"); fold.Add(actions);
                        actions.Add(new Button(() => Run(() => controller.Preview(entry.Id))) { text = "Preview" });
                        actions.Add(new Button(() => Run(() => Record(entry.Id, false, shortcut))) { text = "Set hotkey" });
                        actions.Add(new Button(() => Run(() => { StopRecording(); controller.Bind(entry.Id, ""); shortcut.text = ""; })) { text = "Clear hotkey" });
                        var active = new Button(() => Run(() => controller.SetActive(entry.Id, !entry.Active))) { text = entry.Active ? "Turn off" : "Turn on" }; fold.Add(active);
                        var renameRow = new VisualElement(); renameRow.AddToClassList("row"); fold.Add(renameRow);
                        var rename = new TextField { value = entry.Label }; rename.style.flexGrow = 1; rename.style.minWidth = 70; renameRow.Add(rename);
                        renameRow.Add(new Button(() => Run(() => { controller.Rename(entry.Id, rename.value); fold.text = entry.Label; })) { text = "Rename" });
                        void Refresh() { if (alive) active.text = entry.Active ? "Turn off" : "Turn on"; }
                        controller.Changed += Refresh;
                        active.RegisterCallback<DetachFromPanelEvent>(_ => { if (controller != null) controller.Changed -= Refresh; });
                    });
                }
                if (!mouthOnly)
                {
                    var actionMatches = controller.Actions.Where(entry => entry.Name.IndexOf(search.value ?? "", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
                    if (actionMatches.Length > 0) rows.Add(new Label("Action hotkeys"));
                    foreach (var action in actionMatches)
                    {
                        var fold = new Foldout { text = action.Name, value = false }; fold.AddToClassList("expression-card"); rows.Add(fold);
                        var shortcut = new Label(action.Hotkey ?? "No shortcut"); shortcut.AddToClassList("expression-shortcut"); fold.Q<Toggle>().Add(shortcut);
                        var buttons = new VisualElement(); buttons.AddToClassList("row"); fold.Add(buttons);
                        buttons.Add(new Button(() => Run(() => { controller.RunAction(action.Id); status.text = action.Name + "."; })) { text = "Run action" });
                        buttons.Add(new Button(() => Run(() => Record(action.Id, true, shortcut))) { text = "Set shortcut" });
                        buttons.Add(new Button(() => Run(() => { StopRecording(); controller.BindAction(action.Id, ""); shortcut.text = "No shortcut"; })) { text = "Clear" });
                    }
                    if (controller.ImportedVtubeHotkeys) status.text = "Shortcuts imported from .vtube.json. Unassigned expressions stay unassigned. Shortcuts work while Sakura has focus.";
                }
            }
            owner.focusable = true;
            if (studio != null) studio.PanelsHidden += StopRecording;
            owner.RegisterCallback<DetachFromPanelEvent>(_ => { alive = false; StopRecording(); if (studio != null) studio.PanelsHidden -= StopRecording; });
            search.RegisterValueChangedCallback(_ => Render()); Render();
        }

        private static bool IsMouth(string name)
        {
            string normalized = (name ?? "").Trim().ToLowerInvariant();
            return new[] { "あ","い","う","え","お","a","i","u","e","o","aa","ih","ou","ee","oh" }.Contains(normalized)
                || normalized.Contains("mouth") || normalized.Contains("jaw") || normalized.Contains("lip") || normalized.Contains("口");
        }
    }
}
