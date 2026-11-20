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
        private readonly VisualElement overlay, body;
        private readonly Action completed;
        private readonly TextField keyField, modelField;
        private readonly DropdownField providerField;
        private readonly Label keyHint, status;
        private readonly List<Button> cards = new List<Button>();
        private VisualElement localSetup;
        private DropdownField localCatalog;
        private Label localStatus;
        private Button localDownload;
        private IVisualElementScheduledItem localPoll;
        private readonly List<JObject> localModels = new List<JObject>();
        private bool busy, closed, localReady;
        private bool? savedCloudKey;
        private string savedProvider = "gemini";
        private string savedLocalModel = "";
        private string localModel = "";

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
            studio.GetComponent<SakuraWindow>()?.SetStartupChooser(true);

            var card = new VisualElement { name = "startup-card" };
            card.AddToClassList("startup-card");
            overlay.Add(card);

            // Keep the same layout and allow shorter displays to scroll to the footer.
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("startup-scroll");
            scroll.style.position = Position.Absolute;
            scroll.style.left = 0; scroll.style.top = 0; scroll.style.right = 0; scroll.style.bottom = 0;
            scroll.verticalScrollerVisibility = ScrollerVisibility.Auto;
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            card.Add(scroll);
            body = scroll.contentContainer;
            body.AddToClassList("startup-body");
            body.style.width = Length.Percent(100); body.style.height = 692; body.style.flexShrink = 0;

            var logo = new Image { image = Resources.Load<Texture2D>("Sakura/CompanionLogo"), pickingMode = PickingMode.Ignore };
            logo.AddToClassList("startup-logo"); body.Add(logo); Place(logo, 32, 14, 22, 22);
            var brand = new Label("SAKURA  /  COMPANION"); brand.AddToClassList("startup-brand"); body.Add(brand); Place(brand, 65, 14, 300, 22);
            var close = new Button(() => Quit()) { text = "×", name = "startup-close" };
            close.AddToClassList("startup-close"); close.tooltip = "Close startup chooser";
            body.Add(close); Place(close, 566, 6, 42, 34);

            // Smooth cyan-to-purple rule, drawn as thin strips so it stays sharp.
            var line = new VisualElement(); line.AddToClassList("startup-line"); body.Add(line); Place(line, 32, 46, 556, 2);
            for (int index = 0; index < 64; index++)
            {
                var strip = new VisualElement(); strip.style.flexGrow = 1;
                strip.style.backgroundColor = Color.Lerp(new Color(.38f, .88f, .92f), new Color(.66f, .44f, .96f), index / 63f);
                line.Add(strip);
            }

            var eyebrow = new Label("CHOOSE YOUR EXPERIENCE"); eyebrow.AddToClassList("startup-eyebrow"); body.Add(eyebrow); Place(eyebrow, 32, 59, 500, 23);
            var title = new Label("Welcome to Sakura"); title.AddToClassList("startup-title"); body.Add(title); Place(title, 30, 86, 555, 48);
            var subtitle = new Label("One companion. Three ways to connect."); subtitle.AddToClassList("startup-subtitle"); body.Add(subtitle); Place(subtitle, 33, 137, 550, 26);

            var keyLabel = new Label("CLOUD API KEY"); keyLabel.AddToClassList("startup-key-label"); body.Add(keyLabel); Place(keyLabel, 32, 183, 230, 22);
            providerField = new DropdownField(ProviderLabels.ToList(), 0) { name = "startup-provider" };
            providerField.AddToClassList("startup-provider"); body.Add(providerField); Place(providerField, 320, 174, 268, 34);

            keyField = new TextField { name = "startup-api-key", isPasswordField = true };
            keyField.AddToClassList("startup-input");
            keyField.textEdition.placeholder = "Paste your API key, or leave blank to use the saved key";
            body.Add(keyField); Place(keyField, 32, 210, 556, 42);

            keyHint = new Label("Blank keeps saved key. Model ID (optional):"); keyHint.AddToClassList("startup-hint"); body.Add(keyHint); Place(keyHint, 33, 258, 350, 28);
            modelField = new TextField { name = "startup-model" };
            modelField.AddToClassList("startup-model");
            modelField.textEdition.placeholder = "Model ID (optional)";
            body.Add(modelField); Place(modelField, 393, 255, 195, 30);

            status = new Label(); status.AddToClassList("startup-status"); body.Add(status); Place(status, 32, 282, 556, 12);

            AddMode(body, 0, "Cloud API", "Cloud AI companion · Use your API key", "CLOUD", "startup-cloud", "#61e1eb", () => Choose("cloud"));
            AddMode(body, 1, "Local LLM", "On-device AI companion · Run on this PC", "LOCAL", "startup-local", "#b499ff", () => Choose("local"));
            AddMode(body, 2, "VTuber", "Face + hand tracking · No AI models loaded", "TRACKING", "startup-vtuber", "#f590c4", () => Choose("vtuber"));

            var footer = new Label("This app is currently under development.\nYour feedback is most important."); footer.AddToClassList("startup-footer"); body.Add(footer); Place(footer, 33, 552, 550, 44);
            var discord = new Button(() => Application.OpenURL("https://discord.gg/kgxeMRFWDw")) { text = "Join my Discord and DM me" };
            discord.AddToClassList("startup-link"); body.Add(discord); Place(discord, 33, 605, 550, 24);
            var note = new Label("You can change your AI provider later in Settings."); note.AddToClassList("startup-note"); body.Add(note); Place(note, 33, 648, 550, 22);

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
                    savedLocalModel = settings.Value<string>("ollama_model") ?? "";
                    studio.ApplyTheme(settings.Value<string>("ui_theme") ?? "system");
                }
                catch (Newtonsoft.Json.JsonException) { }
            });
        }

        private void AddMode(VisualElement parent, int index, string title, string description, string tag, string iconName, string accent, Action action)
        {
            var card = new Button(action) { name = "startup-mode-" + index, text = "" };
            card.AddToClassList("startup-mode");
            card.AddToClassList(index == 0 ? "startup-mode-cloud" : index == 1 ? "startup-mode-local" : "startup-mode-vtuber");
            parent.Add(card); Place(card, 32, 295 + index * 80, 556, 76);
            cards.Add(card);

            var iconWrap = new VisualElement(); iconWrap.AddToClassList("startup-mode-icon-wrap"); card.Add(iconWrap);
            var icon = new Image { image = Resources.Load<Texture2D>("Sakura/Icons/" + iconName), pickingMode = PickingMode.Ignore };
            icon.AddToClassList("startup-mode-icon");
            // These assets have transparent padding above and to the left of the glyph.
            const float atlasSize = 208;
            icon.uv = index == 0 ? new Rect(108 / atlasSize, 0, 100 / atlasSize, 96 / atlasSize) :
                      index == 1 ? new Rect(96 / atlasSize, 0, 112 / atlasSize, 108 / atlasSize) :
                                   new Rect(112 / atlasSize, 0, 96 / atlasSize, 96 / atlasSize);
            icon.tintColor = ColorUtility.TryParseHtmlString(accent, out var parsed) ? parsed : Color.cyan;
            iconWrap.Add(icon);

            var copy = new VisualElement(); copy.AddToClassList("startup-mode-copy"); card.Add(copy);
            var heading = new Label(title); heading.AddToClassList("startup-mode-title"); copy.Add(heading);
            var detail = new Label(description); detail.AddToClassList("startup-mode-description"); copy.Add(detail);

            var tagLabel = new Label(tag); tagLabel.AddToClassList("startup-mode-tag"); card.Add(tagLabel);
            var arrow = new Label("›"); arrow.AddToClassList("startup-mode-arrow"); card.Add(arrow);
        }

        private static void Place(VisualElement element, float left, float top, float width, float height)
        {
            element.style.position = Position.Absolute;
            element.style.left = Length.Percent(left / 618 * 100); element.style.top = top;
            element.style.width = Length.Percent(width / 618 * 100); element.style.height = height;
            element.style.marginLeft = 0; element.style.marginRight = 0;
            element.style.marginTop = 0; element.style.marginBottom = 0;
        }

        private void Choose(string mode)
        {
            if (busy || closed) return;
            if (mode == "local" && !localReady) { CheckLocalModels(); return; }
            StartMode(mode);
        }

        private void StartMode(string mode)
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
                if (localModel.Length > 0) payload["ollama_model"] = localModel;
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
                if (mode != "vtuber") backend.BeginStartupGreeting();
                completed?.Invoke();
            });
        }

        private void CheckLocalModels()
        {
            status.text = "Checking installed local models…";
            backend.Api("/api/ollama/models", new JObject { ["folder"] = "" }.ToString(Newtonsoft.Json.Formatting.None), (json, error) =>
            {
                if (closed || busy) return;
                if (error == null)
                {
                    try
                    {
                        var response = JObject.Parse(json);
                        var rows = response["models"] as JArray ?? new JArray();
                        var native = response["native_models"] as JArray ?? new JArray();
                        if (rows.Count > 0 || native.Count > 0)
                        {
                            localReady = true;
                            localModel = savedLocalModel;
                            if (localModel.Length == 0 || (!rows.Any(row => row.Value<string>("name") == localModel) &&
                                !native.Any(row => row.Value<string>("name") == localModel)))
                                localModel = rows.OfType<JObject>().Select(row => row.Value<string>("name"))
                                    .Concat(native.OfType<JObject>().Select(row => row.Value<string>("name")))
                                    .FirstOrDefault(name => !string.IsNullOrEmpty(name)) ?? "";
                            status.text = ""; StartMode("local"); return;
                        }
                    }
                    catch (Newtonsoft.Json.JsonException) { }
                }
                ShowLocalSetup();
            });
        }

        private void ShowLocalSetup()
        {
            status.text = "";
            if (localSetup != null) return;
            localSetup = new VisualElement(); localSetup.AddToClassList("startup-local-setup");
            body.Add(localSetup); Place(localSetup, 18, 174, 582, 370);
            var heading = new Label("Set up local AI"); heading.AddToClassList("startup-local-title"); localSetup.Add(heading);
            var copy = new Label("Local AI downloads an optional runtime and a vision model into your Sakura folder. Nothing is downloaded until you choose Download. Existing models and settings are kept."); copy.AddToClassList("startup-local-copy"); localSetup.Add(copy);
            localCatalog = new DropdownField(new List<string> { "Reading local model catalog…" }, 0); localSetup.Add(localCatalog);
            localStatus = new Label("Nothing is downloaded until you select Download."); localStatus.AddToClassList("startup-local-status"); localSetup.Add(localStatus);
            var actions = new VisualElement(); actions.AddToClassList("startup-local-actions"); localSetup.Add(actions);
            localDownload = new Button(DownloadLocal) { text = "Download and use local AI" }; localDownload.SetEnabled(false); actions.Add(localDownload);
            actions.Add(new Button(CancelLocalSetup) { text = "Cancel" });
            backend.Api("/api/ollama/manager", null, (json, error) =>
            {
                if (localSetup == null) return;
                if (error != null) { localStatus.text = "The local model catalog is unavailable: " + error; return; }
                try
                {
                    var rows = JObject.Parse(json)["catalog"] as JArray ?? new JArray();
                    localModels.Clear(); var labels = new List<string>();
                    foreach (var row in rows.OfType<JObject>())
                    {
                        string model = row.Value<string>("model"); if (string.IsNullOrEmpty(model)) continue;
                        localModels.Add(row); labels.Add((row.Value<string>("name") ?? model) + " · " + (row.Value<string>("download") ?? "download"));
                    }
                    localCatalog.choices = labels.Count == 0 ? new List<string> { "No local catalog models" } : labels;
                    localCatalog.SetValueWithoutNotify(localCatalog.choices[0]); localDownload.SetEnabled(localModels.Count > 0);
                    localStatus.text = localModels.Count == 0 ? "No downloadable models were returned. You can add one later in Settings." : "Recommended: choose a vision model that fits your GPU memory.";
                }
                catch (Newtonsoft.Json.JsonException) { localStatus.text = "The local model catalog returned an unreadable response."; }
            });
        }

        private void DownloadLocal()
        {
            if (localSetup == null || localModels.Count == 0) return;
            int index = Mathf.Clamp(localCatalog.index, 0, localModels.Count - 1);
            string model = localModels[index].Value<string>("model");
            if (string.IsNullOrEmpty(model)) return;
            localDownload.SetEnabled(false); localCatalog.SetEnabled(false); localStatus.text = "Starting download…";
            backend.Api("/api/ollama/manager/download", new JObject { ["model"] = model, ["folder"] = "" }.ToString(Newtonsoft.Json.Formatting.None), (_, error) =>
            {
                if (localSetup == null) return;
                if (error != null) { localStatus.text = "Download could not start: " + error; localDownload.SetEnabled(true); localCatalog.SetEnabled(true); return; }
                localPoll?.Pause(); localPoll = localSetup.schedule.Execute(() => PollLocal(model)).Every(1000); localPoll.ExecuteLater(500);
            });
        }

        private void PollLocal(string model)
        {
            backend.Api("/api/ollama/manager", null, (json, error) =>
            {
                if (localSetup == null) return;
                if (error != null) { localStatus.text = "Download status is unavailable: " + error; return; }
                try
                {
                    var job = JObject.Parse(json)["job"] as JObject;
                    if (job == null) return;
                    string message = job.Value<string>("message") ?? "Downloading…";
                    localStatus.text = message;
                    if (job.Value<bool?>("running") == false)
                    {
                        if (message.StartsWith("Download failed", StringComparison.OrdinalIgnoreCase)) { localDownload.SetEnabled(true); localCatalog.SetEnabled(true); localPoll?.Pause(); return; }
                        if (message.StartsWith("Downloaded", StringComparison.OrdinalIgnoreCase) || job.Value<double?>("percent") == 100)
                        {
                            localPoll?.Pause();
                            backend.Api("/api/settings", new JObject { ["ollama_model"] = model }.ToString(Newtonsoft.Json.Formatting.None), (_, saveError) =>
                            {
                                if (localSetup == null) return;
                                if (saveError != null) { localStatus.text = "The model downloaded, but could not be selected: " + saveError; localDownload.SetEnabled(true); localCatalog.SetEnabled(true); return; }
                                localReady = true; localSetup.RemoveFromHierarchy(); localSetup = null; StartMode("local");
                            });
                        }
                    }
                }
                catch (Newtonsoft.Json.JsonException) { localStatus.text = "Download status returned an unreadable response."; }
            });
        }

        private void CancelLocalSetup()
        {
            localPoll?.Pause(); localPoll = null; localSetup?.RemoveFromHierarchy(); localSetup = null;
            status.text = "";
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
            closed = true; localPoll?.Pause();
            overlay.RemoveFromHierarchy();
            studio.GetComponent<SakuraWindow>()?.SetStartupChooser(false);
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
