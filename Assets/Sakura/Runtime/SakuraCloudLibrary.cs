using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
        private readonly Label status;
        private readonly TextField email, password;
        private readonly Foldout account;
        private readonly VisualElement browse, results, breadcrumb;
        private readonly List<Button> accountButtons = new List<Button>();
        private CloudCharacter[] models = Array.Empty<CloudCharacter>();
        private string folder = "", query = "";
        private bool downloading;
        private bool alive = true;

        public SakuraCloudLibrary(SakuraBackend backend, VisualElement host, string format)
        {
            this.backend = backend; this.format = format.ToLowerInvariant();
            root = new VisualElement(); root.AddToClassList("card"); host.Add(root);
            root.RegisterCallback<DetachFromPanelEvent>(args => { alive = false; password.value = ""; });
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
            backend.Api("/api/model-library/connection", null, (json, error) =>
            {
                if (!alive) return;
                if (error != null) { status.text = error; return; }
                var connection = JsonUtility.FromJson<ApiReply>(json);
                if (connection.configured) { account.value = false; Refresh(); }
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
                account.value = false; Refresh();
            });
        }

        private void SignOut()
        {
            if (downloading) { status.text = "Wait for the current download to finish before signing out."; return; }
            backend.Api("/api/model-library/connection", JsonUtility.ToJson(new DisconnectRequest()), (json, error) =>
            {
                if (!alive) return;
                if (error != null) { status.text = error; return; }
                models = Array.Empty<CloudCharacter>(); results.Clear(); password.value = "";
                account.value = true; browse.style.display = DisplayStyle.None;
                status.text = "Signed out of the Sakura model library.";
            });
        }

        private void Refresh()
        {
            status.text = "Loading the protected model library…";
            backend.Api("/api/model-library/models", null, (json, error) =>
            {
                if (!alive) return;
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
            AddText(name, "card-title", card);
            AddText((character.bytes / 1048576f).ToString("0.0") + " MB", "description", card);
            var detail = AddText("", "description", card);
            var button = AddButton(character.installed ? "Installed" : "Download", () => Install(character, detail), card);
            button.SetEnabled(!character.installed);
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
            });
        }

        private static Label AddText(string text, string css, VisualElement host)
        { var label = new Label(text); label.AddToClassList(css); host.Add(label); return label; }
        private static Button AddButton(string text, Action action, VisualElement host)
        { var button = new Button(action) { text = text }; button.style.unityFontDefinition = FontDefinition.FromFont(Resources.Load<Font>("Sakura/Fonts/DMSans-Semibold")); button.style.unityFontStyleAndWeight = FontStyle.Normal; host.Add(button); return button; }
    }
}
