using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    [Serializable] internal class AccountRequest { public string email; public string password; }
    [Serializable] internal class DisconnectRequest { public bool disconnect = true; }
    [Serializable] public class CloudCharacter
    { public string id; public string name; public string format; public string asset_kind; public long bytes; public bool installed; public bool preview; }
    [Serializable] internal class CloudCatalog { public CloudCharacter[] models; }
    [Serializable] internal class DownloadProgress { public long downloaded; public long total; public string stage; }

    public sealed class SakuraCloudLibrary
    {
        private readonly SakuraBackend backend;
        private readonly VisualElement root;
        private readonly string format;
        private readonly Action<string> installed;
        private readonly Label status;
        private readonly TextField email, password;
        private readonly Foldout account;
        private readonly VisualElement browse, results, breadcrumb;
        private readonly List<Button> accountButtons = new List<Button>();
        private CloudCharacter[] models = Array.Empty<CloudCharacter>();
        private string folder = "", query = "";
        private bool downloading;
        private bool alive = true;
        private bool signedIn;
        private int catalogGeneration;

        public SakuraCloudLibrary(SakuraBackend backend, VisualElement host, string format, Action<string> installed = null)
        {
            this.backend = backend; this.format = format.ToLowerInvariant(); this.installed = installed;
            root = new VisualElement(); root.AddToClassList("card"); host.Add(root);
            root.RegisterCallback<DetachFromPanelEvent>(args => { alive = false; catalogGeneration++; password.value = ""; });
            AddText("Cloud import " + format, "card-title", root);
            AddText("Downloaded models install into models/" + this.format + "/ and appear in their format selector. Your existing models are kept.", "description", root);
            account = new Foldout { text = "Sign in or create a Sakura account", value = true }; root.Add(account);
            AddText("Enter your email and choose a password to create your Sakura account. You will receive a verification email.", "description", account);
            email = new TextField("Email"); email.name = "library-email"; account.Add(email);
            password = new TextField("Password") { isPasswordField = true }; password.name = "library-password"; account.Add(password);
            var reveal = new Toggle { text = "Show password" }; reveal.RegisterValueChangedCallback(args => password.isPasswordField = !args.newValue); account.Add(reveal);
            var buttons = new VisualElement(); buttons.AddToClassList("row"); account.Add(buttons);
            accountButtons.Add(AddButton("Sign in", () => Submit("signin"), buttons));
            accountButtons.Add(AddButton("Create account", () => Submit("signup"), buttons));
            accountButtons.Add(AddButton("Forgot password?", () => Submit("recover"), account));
            AddButton("Sign out", SignOut, root);
            status = AddText("Checking the saved Sakura account…", "description", root); status.name = "library-status";
            browse = new VisualElement(); root.Add(browse); browse.style.display = DisplayStyle.None;
            var search = new TextField("Find a cloud character"); search.name = "cloud-search"; browse.Add(search);
            search.RegisterValueChangedCallback(args => { query = args.newValue; Render(); });
            AddButton("Refresh library", Refresh, browse);
            breadcrumb = new VisualElement(); breadcrumb.AddToClassList("row"); browse.Add(breadcrumb);
            results = new VisualElement(); results.AddToClassList("cloud-grid"); browse.Add(results);
            int connectionGeneration = catalogGeneration;
            backend.Api("/api/model-library/connection", null, (json, error) =>
            {
                if (!alive || connectionGeneration != catalogGeneration) return;
                if (error != null) { status.text = error; return; }
                var connection = JsonUtility.FromJson<ApiReply>(json);
                if (connection.configured) { signedIn = true; account.value = false; Refresh(); }
                else status.text = "Sign in to the Sakura library to browse characters.";
            });
        }

        public static string ValidateAccount(string email, string password, bool recovery)
        {
            email = (email ?? "").Trim();
            if (email.Length > 254 || !email.Contains("@") || email.StartsWith("@") || email.EndsWith("@"))
                return "Enter a valid email address first.";
            if (!recovery && (password == null || password.Length < 8 || password.Length > 256))
                return "Enter a password of at least 8 characters first.";
            return null;
        }

        private void Submit(string action)
        {
            string error = ValidateAccount(email.value, password.value, action == "recover");
            if (error != null) { status.text = error; return; }
            foreach (var button in accountButtons) button.SetEnabled(false);
            status.text = action == "signup" ? "Creating your Sakura account…" : action == "recover" ? "Sending a reset email…" : "Signing into Sakura…";
            string body = JsonUtility.ToJson(new AccountRequest { email = email.value.Trim(), password = action == "recover" ? "" : password.value });
            backend.Api("/api/model-library/account/" + action, body, (json, failure) =>
            {
                password.value = "";
                if (!alive) return;
                foreach (var button in accountButtons) button.SetEnabled(true);
                if (failure != null) { status.text = failure; return; }
                var reply = JsonUtility.FromJson<ApiReply>(json);
                if (action == "recover" || reply.confirmation_required) { status.text = reply.message ?? "Check your email to continue."; return; }
                signedIn = true; account.value = false; Refresh();
            });
        }

        private void SignOut()
        {
            if (downloading) { status.text = "Wait for the current download to finish before signing out."; return; }
            backend.Api("/api/model-library/connection", JsonUtility.ToJson(new DisconnectRequest()), (json, error) =>
            {
                if (!alive) return;
                if (error != null) { status.text = error; return; }
                signedIn = false; catalogGeneration++; models = Array.Empty<CloudCharacter>(); results.Clear(); password.value = "";
                account.value = true; browse.style.display = DisplayStyle.None;
                status.text = "Signed out of the Sakura model library.";
            });
        }

        private void Refresh()
        {
            if (!alive || !signedIn) return;
            int generation = ++catalogGeneration;
            status.text = "Loading the protected model library…";
            backend.Api("/api/model-library/models", null, (json, error) =>
            {
                if (!alive || !signedIn || generation != catalogGeneration) return;
                if (error != null) { status.text = error; return; }
                models = (JsonUtility.FromJson<CloudCatalog>(json).models ?? Array.Empty<CloudCharacter>())
                    .Where(item => item.format == format && (item.asset_kind == "character" || string.IsNullOrEmpty(item.asset_kind))).ToArray();
                browse.style.display = DisplayStyle.Flex; Render();
                status.text = models.Length + " " + format.ToUpperInvariant() + " cloud characters available.";
            });
        }

        private void Render()
        {
            results.Clear(); breadcrumb.Clear();
            AddButton("All folders", () => { folder = ""; Render(); }, breadcrumb);
            var parts = folder.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            for (int index = 0; index < parts.Length; index++)
            {
                string target = string.Join("/", parts.Take(index + 1));
                AddButton(parts[index], () => { folder = target; Render(); }, breadcrumb);
            }
            string prefix = string.IsNullOrEmpty(folder) ? "" : folder + "/";
            var folders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var model in models)
            {
                string name = (model.name ?? model.id).Replace(" / ", "/");
                if (!string.IsNullOrEmpty(query))
                { if (name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) AddCharacter(model, name); continue; }
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                string remaining = name.Substring(prefix.Length);
                int slash = remaining.IndexOf('/');
                if (slash >= 0) folders.Add(remaining.Substring(0, slash));
                else AddCharacter(model, remaining);
            }
            foreach (string child in folders.Reverse())
            {
                string next = prefix + child;
                var button = new Button(() => { folder = next; Render(); }) { text = "▣\n" + child };
                button.AddToClassList("cloud-folder"); results.Insert(0, button);
            }
        }

        private void AddCharacter(CloudCharacter character, string name)
        {
            var card = new VisualElement(); card.AddToClassList("cloud-character"); results.Add(card);
            if (signedIn && character.preview) AddPreview(character, card);
            AddText(name, "card-title", card);
            AddText((character.bytes / 1048576f).ToString("0.0") + " MB", "description", card);
            var detail = AddText("", "description", card);
            var button = AddButton(character.installed ? "Installed" : "Download", () => Install(character, detail), card);
            button.SetEnabled(!character.installed);
        }

        private void AddPreview(CloudCharacter character, VisualElement card)
        {
            var image = new Image { scaleMode = ScaleMode.ScaleAndCrop, pickingMode = PickingMode.Ignore };
            image.style.width = Length.Percent(100); image.style.height = 140; image.style.alignSelf = Align.Center; card.Add(image);
            image.RegisterCallback<GeometryChangedEvent>(change =>
            {
                if (change.newRect.width > 0 && Mathf.Abs(change.newRect.height - change.newRect.width) > 1) image.style.height = change.newRect.width;
            });
            var message = AddText("", "description", card);
            bool attached = true, attempted = false, hover = false, loading = false;
            bool animated = format != "live2d";
            int frame = 0;
            IDisposable request = null;
            SakuraLibraryPreview preview = null;
            var animation = card.schedule.Execute(() =>
            {
                if (!animated || !hover || preview == null || preview.Frames <= 1) return;
                frame = (frame + 1) % preview.Frames;
                image.uv = SakuraLibraryPreview.FrameUv(frame, preview.Frames, preview.Columns);
            }).Every(100);
            animation.Pause();
            card.RegisterCallback<PointerEnterEvent>(_ => { hover = true; if (animated && preview != null && preview.Frames > 1) animation.Resume(); });
            card.RegisterCallback<PointerLeaveEvent>(_ =>
            {
                hover = false; animation.Pause(); frame = 0;
                if (preview != null) image.uv = SakuraLibraryPreview.FrameUv(0, preview.Frames, preview.Columns);
            });
            var visibility = card.schedule.Execute(() =>
            {
                if (!attached || !alive || !signedIn) return;
                bool visible = PreviewVisible(card);
                if (loading && !visible) { request?.Dispose(); request = null; loading = attempted = false; message.text = ""; return; }
                if (attempted || !visible) return;
                attempted = loading = true; message.text = "Loading preview…";
                request = backend.LoadLibraryPreview(character.id, (loaded, error) =>
                {
                    loading = false; request = null;
                    if (!attached || !alive || !signedIn) { loaded?.Dispose(); return; }
                    if (!PreviewVisible(card)) { loaded?.Dispose(); attempted = false; message.text = ""; return; }
                    if (error != null || loaded == null) { message.text = "Preview unavailable."; return; }
                    preview = loaded; image.image = preview.Texture; image.uv = SakuraLibraryPreview.FrameUv(0, preview.Frames, preview.Columns); message.text = "";
                    if (animated && hover && preview.Frames > 1) animation.Resume();
                });
            }).Every(250);
            card.RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                attached = false; visibility.Pause(); animation.Pause(); request?.Dispose(); request = null;
                image.image = null; preview?.Dispose(); preview = null;
            });
        }

        public static bool PreviewVisible(VisualElement element)
        {
            if (element.panel == null || element.worldBound.width <= 0 || element.worldBound.height <= 0) return false;
            Rect bounds = element.worldBound;
            if (!bounds.Overlaps(element.panel.visualTree.worldBound)) return false;
            for (var parent = element; parent != null; parent = parent.parent)
            {
                if (parent.resolvedStyle.display == DisplayStyle.None || parent.resolvedStyle.visibility == Visibility.Hidden) return false;
                if (parent is ScrollView scroll && !bounds.Overlaps(scroll.contentViewport.worldBound)) return false;
            }
            return true;
        }

        private void Install(CloudCharacter model, Label detail)
        {
            if (downloading) { status.text = "Wait for the current model download to finish."; return; }
            downloading = true;
            string id = Uri.EscapeDataString(model.id);
            long lastBytes = 0; float lastTime = Time.realtimeSinceStartup;
            var progress = detail.schedule.Execute(() =>
            {
                if (!downloading) return;
                backend.Api("/api/model-library/models/" + id + "/install-progress", null, (json, error) =>
                {
                    if (!alive || error != null || !downloading) return;
                    var data = JsonUtility.FromJson<DownloadProgress>(json);
                    float now = Time.realtimeSinceStartup;
                    float speed = Math.Max(0, data.downloaded - lastBytes) / Math.Max(.1f, now - lastTime);
                    detail.text = (data.downloaded / 1048576f).ToString("0.0") + " / " + (data.total / 1048576f).ToString("0.0") + " MB · " + (speed / 1048576f).ToString("0.0") + " MB/s · " + data.stage;
                    lastBytes = data.downloaded; lastTime = now;
                });
            }).Every(1000);
            backend.Api("/api/model-library/models/" + id + "/install", "{}", (json, error) =>
            {
                downloading = false; progress.Pause();
                backend.RefreshCatalog();
                if (!alive) return;
                if (error != null) { status.text = error; return; }
                status.text = "Installed into the existing model library."; Refresh();
                try
                {
                    string path = ((JObject.Parse(json)["models"] as JArray)?.FirstOrDefault())?.Value<string>();
                    if (!string.IsNullOrEmpty(path)) installed?.Invoke(path);
                }
                catch (Exception) { }
            });
        }

        private static Label AddText(string text, string css, VisualElement host)
        { var label = new Label(text); label.AddToClassList(css); host.Add(label); return label; }
        private static Button AddButton(string text, Action action, VisualElement host)
        { var button = new Button(action) { text = text }; button.style.unityFontDefinition = FontDefinition.FromFont(Resources.Load<Font>("Sakura/Fonts/DMSans-Semibold")); button.style.unityFontStyleAndWeight = FontStyle.Normal; host.Add(button); return button; }
    }
}
