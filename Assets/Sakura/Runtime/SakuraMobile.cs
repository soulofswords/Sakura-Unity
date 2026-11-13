using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Live2D.Cubism.Core;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    public sealed class SakuraMobile : MonoBehaviour
    {
        private VisualElement root, settings;
        private ScrollView messages;
        private Label status;
        private Button mic;
        private SakuraAvatarViewport viewport;
        private JObject preferences = new JObject();
        private JArray history = new JArray();
        private UnityWebRequest request;
        private bool busy, listening, speaking;
        private string selected = "";
        private string DataRoot => Application.persistentDataPath;
        private string Name => (string)preferences["name"] ?? "Sakura";
        private string Language => (string)preferences["language"] ?? "English";
        private string Provider => (string)preferences["provider"] ?? "Gemini";

        private void OnEnable()
        {
            gameObject.name = "Sakura Mobile";
            Application.targetFrameRate = 60;
            LoadPreferences();
            var document = GetComponent<UIDocument>() ?? gameObject.AddComponent<UIDocument>();
            if (document.panelSettings == null) document.panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            document.panelSettings.themeStyleSheet = Resources.Load<ThemeStyleSheet>("Sakura/StudioTheme");
            document.panelSettings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            document.panelSettings.referenceResolution = new Vector2Int(480,900);
            root = document.rootVisualElement; root.Clear(); root.AddToClassList("studio"); root.AddToClassList("mobile-studio");
            root.styleSheets.Add(Resources.Load<StyleSheet>("Sakura/Studio"));
            root.style.unityFontDefinition = FontDefinition.FromFont(Resources.Load<Font>("Sakura/Fonts/DMSans"));
            var header = new VisualElement(); header.AddToClassList("mobile-header"); root.Add(header);
            header.Add(new Label(Name + " · Mobile"));
            header.Add(new Button(ShowSettings) { text = "Settings & characters" });
            var stage = new VisualElement(); stage.style.flexGrow = 1; stage.style.minHeight = 200; root.Add(stage);
            viewport = GetComponent<SakuraAvatarViewport>() ?? gameObject.AddComponent<SakuraAvatarViewport>(); viewport.Attach(stage);
            viewport.StatusChanged += SetStatus;
            var chat = new VisualElement(); chat.AddToClassList("mobile-chat"); root.Add(chat);
            messages = new ScrollView(); messages.style.flexGrow = 1; chat.Add(messages);
            foreach (JObject turn in history.OfType<JObject>()) Message((string)turn["content"], (string)turn["role"] == "user");
            status = new Label("Microphone off"); status.style.whiteSpace = WhiteSpace.Normal; chat.Add(status);
            var row = new VisualElement(); row.style.flexDirection = FlexDirection.Row; chat.Add(row);
            mic = new Button(ToggleMic) { text = "Mic off" }; row.Add(mic);
            var input = new TextField(); input.style.flexGrow = 1; row.Add(input);
            void Send() { string text = input.value; if (busy || string.IsNullOrWhiteSpace(text)) return; input.value = ""; StartCoroutine(Chat(text)); }
            row.Add(new Button(Send) { text = "Send" });
            input.RegisterCallback<KeyDownEvent>(evt => { if (evt.keyCode == KeyCode.Return) { Send(); evt.StopPropagation(); } });
            chat.Add(new Button(StopSpeaking) { text = "Stop speaking" });
            selected = (string)preferences["character"] ?? "";
            if (selected.Length > 0) _ = viewport.LoadModel(DataRoot,selected);
        }

        private void LoadPreferences()
        {
            if (Application.platform != RuntimePlatform.Android) return;
            try
            {
                string json = Platform<string>("load");
                if (json.Length > 0) preferences = JObject.Parse(json);
                history = preferences["history"] as JArray ?? new JArray();
            }
            catch { preferences = new JObject(); SetStatus("Saved settings could not be decrypted. Re-enter the API key in Settings."); }
        }

        private void SavePreferences()
        {
            preferences["history"] = history;
            if (Application.platform == RuntimePlatform.Android) Platform("save",preferences.ToString(Newtonsoft.Json.Formatting.None));
        }

        private static T Platform<T>(string method, params object[] arguments)
        {
            using var unity = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = unity.GetStatic<AndroidJavaObject>("currentActivity");
            using var bridge = new AndroidJavaClass("app.sakura.unity.SakuraMobilePlatform");
            return bridge.CallStatic<T>(method,new object[] { activity }.Concat(arguments).ToArray());
        }

        private static void Platform(string method, params object[] arguments)
        {
            using var unity = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = unity.GetStatic<AndroidJavaObject>("currentActivity");
            using var bridge = new AndroidJavaClass("app.sakura.unity.SakuraMobilePlatform");
            bridge.CallStatic(method,new object[] { activity }.Concat(arguments).ToArray());
        }

        private void ShowSettings()
        {
            if (settings != null) { settings.RemoveFromHierarchy(); settings = null; }
            settings = new ScrollView(); settings.AddToClassList("mobile-settings"); root.Add(settings);
            settings.Add(new Label("Settings & characters · Unity mobile preview"));
            settings.Add(new Button(() => { settings.RemoveFromHierarchy(); settings = null; }) { text = "Back to conversation" });
            var name = new TextField("Companion name") { value = Name }; settings.Add(name);
            var language = new DropdownField("Conversation language",new List<string> { "English","Hindi" },Language); settings.Add(language);
            var provider = new DropdownField("AI provider",new List<string> { "Gemini","OpenAI","Groq" },Provider); settings.Add(provider);
            var model = new TextField("Model") { value = (string)preferences["model"] ?? "" }; settings.Add(model);
            var key = new TextField("API key") { isPasswordField = true, value = (string)preferences["key"] ?? "" }; settings.Add(key);
            var mode = new DropdownField("Conversation mode",new List<string> { "Friend","Teacher" },(string)preferences["mode"] ?? "Friend"); settings.Add(mode);
            var voice = new Toggle("Speak replies") { value = preferences.Value<bool?>("voice") ?? true }; settings.Add(voice);
            settings.Add(new Label("Android speech recognition uses your phone’s speech service. Keys and conversation history are encrypted with Android Keystore. Microphone starts off."));
            settings.Add(new Button(() =>
            {
                preferences["name"] = name.value.Trim().Substring(0,Math.Min(80,name.value.Trim().Length));
                preferences["language"] = language.value; preferences["provider"] = provider.value;
                preferences["model"] = model.value.Trim(); preferences["key"] = key.value.Trim(); preferences["mode"] = mode.value;
                preferences["voice"] = voice.value;
                try { SavePreferences(); SetStatus("Settings saved"); } catch { SetStatus("Could not save encrypted settings. Please retry."); }
            }) { text = "Save settings" });
            settings.Add(new Label("Characters"));
            settings.Add(new Button(() =>
            {
                if (Application.platform == RuntimePlatform.Android) Platform("chooseModel",gameObject.name);
                else SetStatus("Use Android’s file picker on your phone to import a VRM or complete Live2D ZIP.");
            }) { text = "Import model from phone" });
            foreach (var entry in SakuraMobileCore.FindModels(DataRoot))
            {
                var row = new VisualElement(); row.style.flexDirection = FlexDirection.Row; settings.Add(row);
                var choose = new Button(() => { selected = entry.path; preferences["character"] = selected; SavePreferences(); _ = viewport.LoadModel(DataRoot,selected); }) { text = entry.name + " · " + entry.format };
                choose.style.flexGrow = 1; row.Add(choose);
                var remove = new Button { text = "Delete" }; row.Add(remove);
                bool confirmed = false;
                remove.clicked += () =>
                {
                    if (!confirmed) { confirmed = true; remove.text = "Confirm delete"; return; }
                    try
                    {
                        if (selected == entry.path)
                        {
                            viewport.UnloadModel(); selected = ""; preferences.Remove("character"); SavePreferences();
                        }
                        SakuraMobileCore.DeleteModel(entry.path,DataRoot); ShowSettings();
                    }
                    catch (Exception error) { SetStatus(error.Message); }
                };
            }
            settings.Add(new Button(() => { history.Clear(); messages.Clear(); SavePreferences(); SetStatus("Conversation history cleared"); }) { text = "Clear conversation history" });
        }

        public async void OnModelSelected(string path)
        {
            string rootFolder = DataRoot;
            SetStatus("Importing complete model package…");
            try
            {
                string imported = await Task.Run(() => SakuraMobileCore.ImportModel(path,rootFolder));
                preferences["character"] = selected = imported; SavePreferences();
                await viewport.LoadModel(rootFolder,imported);
                if (settings != null) ShowSettings();
            }
            catch (Exception error) { SetStatus("Could not import model. " + error.Message); }
            finally
            {
                if (Application.platform == RuntimePlatform.Android)
                    try { Platform("finishImport",path); } catch { SetStatus("Model processed. Temporary import cleanup needs a retry."); }
            }
        }

        public void OnImportError(string error) => SetStatus(error);
        public void OnTranscript(string text)
        {
            listening = false; UpdateMic();
            if (string.IsNullOrWhiteSpace(text)) return;
            StartCoroutine(Chat(text));
        }
        public void OnRecognitionError(string error) { listening = false; UpdateMic(); SetStatus(error); }
        public void OnSpeechState(string state) { speaking = state == "speaking"; if (!speaking) SetStatus(state == "error" ? "The phone’s speech voice is unavailable. Check Android text-to-speech settings." : "Ready"); }

        private void ToggleMic()
        {
            if (listening) { if (Application.platform == RuntimePlatform.Android) Platform("stopListening"); listening = false; UpdateMic(); return; }
            if (busy) { SetStatus("Preparing reply. Stop speaking or wait before recording another question."); return; }
            if (Application.platform != RuntimePlatform.Android) { SetStatus("Native Android speech is available on the phone build."); return; }
#if UNITY_ANDROID
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
            { UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone); SetStatus("Allow microphone access, then tap Mic."); return; }
#endif
            StopSpeaking(); listening = true; UpdateMic();
            Platform("listen",Language,gameObject.name); SetStatus("Listening…");
        }

        private void UpdateMic() { if (mic != null) { mic.text = listening ? "● Mic on" : "Mic off"; mic.style.backgroundColor = listening ? new Color(.65f,.1f,.25f) : StyleKeyword.Null; } }
        private void StopSpeaking() { speaking = false; if (Application.platform == RuntimePlatform.Android) Platform("stop"); }

        private IEnumerator Chat(string text)
        {
            if (busy || string.IsNullOrWhiteSpace(text)) yield break;
            if (string.IsNullOrWhiteSpace((string)preferences["key"]) || string.IsNullOrWhiteSpace((string)preferences["model"]))
            { SetStatus("Choose your AI provider, model and API key in Settings first."); yield break; }
            busy = true; StopSpeaking(); Message(text,true); SetStatus("Preparing reply…");
            try
            {
                string model = (string)preferences["model"];
                if (model.Length > 120 || !System.Text.RegularExpressions.Regex.IsMatch(model,@"^[A-Za-z0-9_.:/-]+$"))
                { SetStatus("Enter a valid provider model ID in Settings."); yield break; }
                string prompt = "You are " + Name + ", a companion in Sakura Mobile. Current mode is " + ((string)preferences["mode"] ?? "Friend") +
                    ". Reply in " + Language + ". Be concise and conversational. Never claim to execute actions or change clothes without an actual tool result. " +
                    "You have no phone calendar, web search or device tools in this Unity preview. Do not invent access. " +
                    "Current avatar file is " + (selected.Length == 0 ? "none" : Path.GetFileName(selected)) + ". No image is attached. Do not guess its appearance.";
                string endpoint; JObject payload;
                if (Provider == "Gemini")
                {
                    endpoint = "https://generativelanguage.googleapis.com/v1beta/models/" + Uri.EscapeDataString(model) + ":generateContent";
                    var contents = new JArray(history.OfType<JObject>().Select(turn => new JObject { ["role"] = (string)turn["role"] == "assistant" ? "model" : "user", ["parts"] = new JArray(new JObject { ["text"] = turn["content"] }) }));
                    contents.Add(new JObject { ["role"] = "user", ["parts"] = new JArray(new JObject { ["text"] = text }) });
                    payload = new JObject { ["systemInstruction"] = new JObject { ["parts"] = new JArray(new JObject { ["text"] = prompt }) }, ["contents"] = contents };
                }
                else
                {
                    endpoint = Provider == "Groq" ? "https://api.groq.com/openai/v1/chat/completions" : "https://api.openai.com/v1/chat/completions";
                    var turns = new JArray(new JObject { ["role"] = "system", ["content"] = prompt });
                    foreach (JObject turn in history.OfType<JObject>()) turns.Add(turn.DeepClone());
                    turns.Add(new JObject { ["role"] = "user", ["content"] = text });
                    payload = new JObject { ["model"] = model, ["messages"] = turns };
                }
                request = new UnityWebRequest(endpoint,"POST");
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload.ToString(Newtonsoft.Json.Formatting.None)));
                request.downloadHandler = new DownloadHandlerBuffer(); request.timeout = 90; request.redirectLimit = 0;
                request.SetRequestHeader("Content-Type","application/json");
                request.SetRequestHeader(Provider == "Gemini" ? "x-goog-api-key" : "Authorization",Provider == "Gemini" ? (string)preferences["key"] : "Bearer " + (string)preferences["key"]);
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success) { SetStatus(SakuraMobileCore.FriendlyError(request.responseCode,request.downloadHandler.text)); yield break; }
                string reply = null;
                try
                {
                    var result = JObject.Parse(request.downloadHandler.text);
                    reply = Provider == "Gemini" ? string.Concat((result["candidates"]?[0]?["content"]?["parts"] as JArray ?? new JArray()).Select(part => (string)part["text"] ?? "")) : (string)result["choices"]?[0]?["message"]?["content"];
                }
                catch { SetStatus("The AI provider returned an unreadable reply. Please retry."); }
                if (string.IsNullOrWhiteSpace(reply)) { SetStatus("The provider returned no reply. Try another question or check the model’s availability."); yield break; }
                Message(reply,false);
                history.Add(new JObject { ["role"] = "user", ["content"] = text }); history.Add(new JObject { ["role"] = "assistant", ["content"] = reply });
                while (history.Count > 40) { history.RemoveAt(0); history.RemoveAt(0); }
                try { SavePreferences(); } catch { SetStatus("Reply received, but encrypted history could not be saved."); }
                SetStatus("Ready");
                if ((preferences.Value<bool?>("voice") ?? true) && Application.platform == RuntimePlatform.Android)
                    Platform("speak",SakuraMobileCore.SpeechText(reply),Language,gameObject.name);
            }
            finally { request?.Dispose(); request = null; busy = false; }
        }

        private void LateUpdate()
        {
            if (viewport == null || viewport.AvatarRoot == null || viewport.LoadedFormat != "Live2D") return;
            var model = viewport.AvatarRoot.GetComponent<CubismModel>();
            if (model == null) return;
            var mouth = model.Parameters.FirstOrDefault(parameter => parameter.Id == "ParamMouthOpenY");
            if (mouth != null) mouth.Value = speaking ? .12f + .24f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 12)) : 0;
        }

        private void Message(string text, bool user)
        {
            var label = new Label(text ?? ""); label.AddToClassList(user ? "user-message" : "message"); label.style.whiteSpace = WhiteSpace.Normal;
            messages.Add(label); messages.schedule.Execute(() => messages.scrollOffset = new Vector2(0,float.MaxValue));
        }
        private void SetStatus(string text) { if (status != null) status.text = text; }
        private void OnDisable() { request?.Abort(); StopSpeaking(); if (Application.platform == RuntimePlatform.Android) Platform("stopListening"); if (viewport != null) viewport.StatusChanged -= SetStatus; }
    }
}
