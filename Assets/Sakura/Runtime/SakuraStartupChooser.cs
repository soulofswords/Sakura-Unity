using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    /// <summary>
    /// Port of the native launcher's "Choose your experience" dialog. It is shown
    /// before the Studio's splash so the first choice is Cloud API, Local LLM or
    /// VTuber, exactly like the existing desktop app.
    /// </summary>
    public sealed class SakuraStartupChooser
    {
        private static readonly string[] ProviderIds = { "auto", "gemini", "openai", "anthropic", "deepseek", "openrouter", "groq", "xai" };
        private static readonly string[] ProviderLabels = { "Auto-detect key", "Google Gemini", "OpenAI", "Anthropic Claude", "DeepSeek", "OpenRouter", "Groq", "xAI" };

        private readonly SakuraStudio studio;
        private readonly SakuraBackend backend;
        private readonly VisualElement overlay;
        private readonly Action completed;
        private readonly TextField keyField, modelField;
        private readonly DropdownField providerField;
        private readonly Label keyHint, status;
        private readonly List<Button> cards = new List<Button>();
        private bool busy, closed;
        private bool? savedCloudKey;
        private string savedProvider = "gemini";

        public static void Attach(SakuraStudio studio, SakuraBackend backend, Action completed = null)
        {
            if (studio == null || backend == null) return;
            var root = studio.GetComponent<UIDocument>()?.rootVisualElement;
            if (root == null || root.Q("startup-chooser") != null) return;
            new SakuraStartupChooser(studio, backend, root, completed);
        }

        private SakuraStartupChooser(SakuraStudio studio, SakuraBackend backend, VisualElement root, Action completed)
        {
            this.studio = studio; this.backend = backend; this.completed = completed;
            overlay = new VisualElement { name = "startup-chooser" };
            overlay.AddToClassList("startup-chooser");
            root.Add(overlay);

            var card = new VisualElement { name = "startup-card" };
            card.AddToClassList("startup-card");
            overlay.Add(card);

            var scroll = new ScrollView();
            scroll.AddToClassList("startup-scroll");
            card.Add(scroll);
            var body = scroll.contentContainer;

            var top = new VisualElement(); top.AddToClassList("startup-top"); body.Add(top);
            var logo = new Image { image = Resources.Load<Texture2D>("Sakura/CompanionLogo"), pickingMode = PickingMode.Ignore };
            logo.AddToClassList("startup-logo"); top.Add(logo);
            var brand = new Label("SAKURA  /  COMPANION"); brand.AddToClassList("startup-brand"); top.Add(brand);
            var close = new Button(() => Quit()) { text = "×", name = "startup-close" };
            close.AddToClassList("startup-close"); close.tooltip = "Close startup chooser";
            top.Add(close);

            var line = new VisualElement(); line.AddToClassList("startup-line"); body.Add(line);
            line.Add(new VisualElement { name = "startup-line-left" });
            line.Add(new VisualElement { name = "startup-line-right" });

            var eyebrow = new Label("CHOOSE YOUR EXPERIENCE"); eyebrow.AddToClassList("startup-eyebrow"); body.Add(eyebrow);
            var title = new Label("Welcome to Sakura"); title.AddToClassList("startup-title"); body.Add(title);
            var subtitle = new Label("One companion. Three ways to connect."); subtitle.AddToClassList("startup-subtitle"); body.Add(subtitle);

            var keyRow = new VisualElement(); keyRow.AddToClassList("startup-key-row"); body.Add(keyRow);
            var keyLabel = new Label("CLOUD API KEY"); keyLabel.AddToClassList("startup-key-label"); keyRow.Add(keyLabel);
            providerField = new DropdownField(ProviderLabels.ToList(), 0) { name = "startup-provider" };
            providerField.AddToClassList("startup-provider"); keyRow.Add(providerField);

            keyField = new TextField { name = "startup-api-key", isPasswordField = true };
            keyField.AddToClassList("startup-input");
            keyField.textEdition.placeholder = "Paste your API key, or leave blank to use the saved key";
            body.Add(keyField);

            var hintRow = new VisualElement(); hintRow.AddToClassList("startup-hint-row"); body.Add(hintRow);
            keyHint = new Label("Blank keeps saved key. Model ID (optional):"); keyHint.AddToClassList("startup-hint"); hintRow.Add(keyHint);
            modelField = new TextField { name = "startup-model" };
            modelField.AddToClassList("startup-model");
            modelField.textEdition.placeholder = "Model ID (optional)";
            hintRow.Add(modelField);

            status = new Label(); status.AddToClassList("startup-status"); body.Add(status);

            AddMode(body, 0, "Cloud API", "Cloud AI companion · Use your API key", "CLOUD", "tab-ai", "#61e1eb", () => Choose("cloud"));
            AddMode(body, 1, "Local LLM", "On-device AI companion · Run on this PC", "LOCAL", "tab-models", "#b499ff", () => Choose("local"));
            AddMode(body, 2, "VTuber", "Face + hand tracking · No AI models loaded", "TRACKING", "camera", "#f590c4", () => Choose("vtuber"));

            var footer = new Label("This app is currently under development.\nYour feedback is most important."); footer.AddToClassList("startup-footer"); body.Add(footer);
            var discord = new Button(() => Application.OpenURL("https://discord.gg/kgxeMRFWDw")) { text = "Join my Discord and DM me" };
            discord.AddToClassList("startup-link"); body.Add(discord);
            var note = new Label("You can change your AI provider later in Settings."); note.AddToClassList("startup-note"); body.Add(note);

            overlay.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Escape) Quit();
            });
            overlay.schedule.Execute(() => keyField.Focus()).ExecuteLater(80);
            backend.Api("/api/settings", null, (json, error) =>
            {
                if (closed || error != null) return;
                try
                {
                    var settings = JObject.Parse(json);
                    savedCloudKey = settings.Value<bool?>("gemini_key_set") == true || settings.Value<bool?>("cloud_key_set") == true;
                    string provider = settings.Value<string>("llm_provider") ?? "gemini";
                    savedProvider = provider == "ollama" ? settings.Value<string>("last_cloud_provider") ?? "gemini" : provider;
                }
                catch (Newtonsoft.Json.JsonException) { }
            });
        }

        private void AddMode(VisualElement parent, int index, string title, string description, string tag, string iconName, string accent, Action action)
        {
            var card = new Button(action) { name = "startup-mode-" + index, text = "" };
            card.AddToClassList("startup-mode");
            card.AddToClassList(index == 0 ? "startup-mode-cloud" : index == 1 ? "startup-mode-local" : "startup-mode-vtuber");
            parent.Add(card);
            cards.Add(card);

            var iconWrap = new VisualElement(); iconWrap.AddToClassList("startup-mode-icon-wrap"); card.Add(iconWrap);
            var icon = new Image { image = Resources.Load<Texture2D>("Sakura/Icons/" + iconName), pickingMode = PickingMode.Ignore };
            icon.AddToClassList("startup-mode-icon");
            icon.tintColor = ColorUtility.TryParseHtmlString(accent, out var parsed) ? parsed : Color.cyan;
            iconWrap.Add(icon);

            var copy = new VisualElement(); copy.AddToClassList("startup-mode-copy"); card.Add(copy);
            var heading = new Label(title); heading.AddToClassList("startup-mode-title"); copy.Add(heading);
            var detail = new Label(description); detail.AddToClassList("startup-mode-description"); copy.Add(detail);

            var tagLabel = new Label(tag); tagLabel.AddToClassList("startup-mode-tag"); card.Add(tagLabel);
            var arrow = new Label("›"); arrow.AddToClassList("startup-mode-arrow"); card.Add(arrow);
        }

        private void Choose(string mode)
        {
            if (busy || closed) return;
            string key = (keyField.value ?? "").Trim();
            string provider = ProviderIds[Mathf.Clamp(providerField.index, 0, ProviderIds.Length - 1)];
            string model = (modelField.value ?? "").Trim();
            if (mode == "cloud")
            {
                if (provider == "auto" && key.Length > 0)
                {
                    provider = DetectProvider(key);
                    if (provider == null)
                    {
                        Fail("Ambiguous key. Select its provider above.");
                        return;
                    }
                }
                else if (provider == "auto")
                {
                    if (savedCloudKey == false) { Fail("Enter an API key to continue."); return; }
                    provider = savedProvider;
                }
                if ((provider == "groq" || provider == "xai" || provider == "openrouter") && model.Length == 0)
                {
                    Fail("Enter this provider's model ID on the right.");
                    return;
                }
            }

            busy = true; SetEnabled(false); status.text = "Starting Sakura…";
            var payload = new JObject();
            if (mode == "vtuber")
            {
                payload["vtuber_tracking_only"] = true;
            }
            else if (mode == "local")
            {
                payload["llm_provider"] = "ollama";
                payload["vtuber_tracking_only"] = false;
                payload["gemini_fallback_enabled"] = false;
            }
            else
            {
                payload["llm_provider"] = provider;
                payload["vtuber_tracking_only"] = false;
                if (key.Length > 0) payload["api_key"] = key;
                if (provider == "gemini" && model.Length > 0) payload["gemini_model"] = model;
                else if (provider != "gemini" && model.Length > 0) payload["cloud_model"] = model;
            }

            backend.Api("/api/settings", payload.ToString(Newtonsoft.Json.Formatting.None), (_, error) =>
            {
                if (closed) return;
                if (error != null)
                {
                    busy = false; SetEnabled(true); Fail(error); return;
                }
                Close();
                studio.SwitchMode(mode == "vtuber" ? "VTuber" : "Companion");
                completed?.Invoke();
            });
        }

        private static string DetectProvider(string key)
        {
            if (key.StartsWith("AIza", StringComparison.Ordinal)) return "gemini";
            if (key.StartsWith("sk-ant-", StringComparison.Ordinal)) return "anthropic";
            if (key.StartsWith("sk-or-", StringComparison.Ordinal)) return "openrouter";
            if (key.StartsWith("sk-proj-", StringComparison.Ordinal) || key.StartsWith("sk-svcacct-", StringComparison.Ordinal)) return "openai";
            if (key.StartsWith("gsk_", StringComparison.Ordinal)) return "groq";
            if (key.StartsWith("xai-", StringComparison.Ordinal)) return "xai";
            return null;
        }

        private void Fail(string message)
        {
            status.text = message;
            keyHint.text = message;
            keyHint.AddToClassList("startup-error");
        }

        private void SetEnabled(bool value)
        {
            foreach (var card in cards) card.SetEnabled(value);
            keyField.SetEnabled(value); modelField.SetEnabled(value); providerField.SetEnabled(value);
        }

        private void Close()
        {
            if (closed) return;
            closed = true;
            overlay.RemoveFromHierarchy();
        }

        private void Quit()
        {
            if (closed) return;
            Close();
            if (Application.isEditor) return;
            Application.Quit();
        }
    }
}
