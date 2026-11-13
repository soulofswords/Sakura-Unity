using System;
using System.Collections;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.Video;

namespace Sakura.Unity
{
    public sealed class SakuraBackdrop : MonoBehaviour
    {
        public Color Colour { get; private set; } = Color.green;
        public bool SolidOutput { get; private set; }
        public Texture OutputMedia => video != null ? video : picture;
        public string Status { get; private set; } = "Background media appears behind your character in the app window.";
        Image image;
        Texture2D picture;
        VideoPlayer player;
        RenderTexture video;
        int generation;
        const string Key = "sakura.backdrop.";
        string StorageFolder => SakuraInstances.IsSecondary ? SakuraInstances.SessionProfile : Application.persistentDataPath;
        public event Action Changed;

        public void Attach(VisualElement stage)
        {
            image?.RemoveFromHierarchy();
            image = new Image { name = "stage-backdrop", pickingMode = PickingMode.Ignore, scaleMode = ScaleMode.ScaleAndCrop };
            image.style.position = Position.Absolute;
            image.style.left = image.style.right = image.style.top = image.style.bottom = 0;
            stage.Insert(0, image);
            if (SakuraInstances.IsSecondary) { Paint(); return; }
            if (ColorUtility.TryParseHtmlString(PlayerPrefs.GetString(Key + "colour", "#00ff00"), out var saved)) Colour = saved;
            SolidOutput = PlayerPrefs.GetInt(Key + "solid", 0) == 1;
            string kind = PlayerPrefs.GetString(Key + "kind", "");
            if (kind == "video") StartCoroutine(PrepareVideo(PlayerPrefs.GetString(Key + "video", ""), ++generation));
            else if (kind == "image")
            {
                try
                {
                    string file = Path.Combine(Application.persistentDataPath, "stage-background.png");
                    if (File.Exists(file) && new FileInfo(file).Length <= 64L * 1024 * 1024)
                    { picture = SakuraPmxModel.Decode(File.ReadAllBytes(file)); image.image = picture; }
                }
                catch (Exception error) { Status = "Saved background could not load. " + error.Message; }
            }
            Paint();
        }

        public void SetColour(Color value)
        {
            if (!float.IsFinite(value.r) || !float.IsFinite(value.g) || !float.IsFinite(value.b)) return;
            Colour = new Color(Mathf.Clamp01(value.r), Mathf.Clamp01(value.g), Mathf.Clamp01(value.b), 1);
            if (!SakuraInstances.IsSecondary) PlayerPrefs.SetString(Key + "colour", "#" + ColorUtility.ToHtmlStringRGB(Colour));
            Paint();
        }
        public void SetSolid(bool value) { SolidOutput = value; if (!SakuraInstances.IsSecondary) PlayerPrefs.SetInt(Key + "solid", value ? 1 : 0); Paint(); }
        void Paint()
        {
            if (image != null) image.style.backgroundColor = SolidOutput ? Colour : Color.clear;
            Changed?.Invoke();
        }
        public void Clear()
        {
            generation++;
            if (image != null) image.image = null;
            ReleaseMedia();
            if (!SakuraInstances.IsSecondary) { PlayerPrefs.DeleteKey(Key + "kind"); PlayerPrefs.DeleteKey(Key + "video"); }
            Status = "Background media removed."; Changed?.Invoke();
        }
        void ReleaseMedia()
        {
            if (picture != null) { Destroy(picture); picture = null; }
            if (player != null) { player.Stop(); Destroy(player); player = null; }
            if (video != null) { video.Release(); Destroy(video); video = null; }
        }
        public void Choose(SakuraBackend backend, bool movie)
        {
            int request = ++generation;
            Status = "Choose a " + (movie ? "video" : "background image") + " on this PC…"; Changed?.Invoke();
            backend.RunPmxTool(movie ? "backdrop-video" : "backdrop-image", "", (json, error) =>
            {
                if (this == null || request != generation) return;
                if (error != null) { Status = error; Changed?.Invoke(); return; }
                try
                {
                    var result = JObject.Parse(json);
                    if (result.Value<bool>("cancelled")) { Status = "Selection cancelled."; Changed?.Invoke(); return; }
                    if (movie) StartCoroutine(PrepareVideo(result.Value<string>("path"), request));
                    else
                    {
                        string encoded = result.Value<string>("image");
                        if (encoded == null || encoded.Length > 90 * 1024 * 1024) throw new InvalidDataException("Background image is too large.");
                        byte[] bytes = Convert.FromBase64String(encoded);
                        var texture = SakuraPmxModel.Decode(bytes);
                        if ((long)texture.width * texture.height > 16777216) { Destroy(texture); throw new InvalidDataException("Background image exceeds 16 megapixels."); }
                        try
                        {
                            string file = Path.Combine(StorageFolder, "stage-background.png"), temporary = file + ".tmp";
                            Directory.CreateDirectory(StorageFolder); File.WriteAllBytes(temporary, bytes);
                            if (File.Exists(file)) File.Replace(temporary, file, null); else File.Move(temporary, file);
                        }
                        catch { Destroy(texture); throw; }
                        ReleaseMedia(); picture = texture; image.image = picture;
                        if (!SakuraInstances.IsSecondary) PlayerPrefs.SetString(Key + "kind", "image");
                        Status = "Background image loaded in the stage and streaming output."; Changed?.Invoke();
                    }
                }
                catch (Exception failure) { Status = failure.Message; Changed?.Invoke(); }
            });
        }
        IEnumerator PrepareVideo(string path, int request)
        {
            if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path) || path.StartsWith("\\\\") || !File.Exists(path))
            { Status = "Choose a video stored on this PC."; Changed?.Invoke(); yield break; }
            var candidate = gameObject.AddComponent<VideoPlayer>();
            candidate.playOnAwake = false; candidate.isLooping = true;
            candidate.audioOutputMode = VideoAudioOutputMode.None;
            candidate.renderMode = VideoRenderMode.RenderTexture;
            candidate.url = new Uri(Path.GetFullPath(path)).AbsoluteUri;
            string failure = null;
            candidate.errorReceived += (_, message) =>
            {
                failure = message;
                if (player == candidate) { Status = "Video playback failed. " + message; Changed?.Invoke(); }
            };
            candidate.Prepare(); float deadline = Time.realtimeSinceStartup + 15;
            while (!candidate.isPrepared && failure == null && Time.realtimeSinceStartup < deadline && request == generation) yield return null;
            if (request != generation || !candidate.isPrepared || candidate.width == 0 || candidate.height == 0 || candidate.width > 4096 || candidate.height > 4096)
            {
                Destroy(candidate);
                if (request == generation) { Status = failure ?? "Video could not prepare within 15 seconds, or exceeds 4096 pixels."; Changed?.Invoke(); }
                yield break;
            }
            ReleaseMedia(); player = candidate;
            video = new RenderTexture((int)player.width, (int)player.height, 0, RenderTextureFormat.ARGB32);
            video.Create(); player.targetTexture = video; image.image = video; player.Play();
            if (!SakuraInstances.IsSecondary) { PlayerPrefs.SetString(Key + "video", path); PlayerPrefs.SetString(Key + "kind", "video"); }
            Status = "Video playing silently on repeat in the stage and streaming output."; Changed?.Invoke();
        }
        public void BuildPanel(SakuraBackend backend, VisualElement parent)
        {
            var card = new VisualElement(); card.AddToClassList("card"); parent.Add(card);
            var title = new Label("Stage background"); title.AddToClassList("card-title"); card.Add(title);
            var swatches = new VisualElement(); swatches.AddToClassList("row"); card.Add(swatches);
            foreach (var colour in new[] { Color.green, new Color(.05f,.55f,.85f), new Color(.94f,.9f,.96f), new Color(.05f,.07f,.1f) })
            { Color chosen = colour; var button = new Button(() => SetColour(chosen)) { text = "●", tooltip = "#" + ColorUtility.ToHtmlStringRGB(chosen) }; button.style.color = chosen; button.style.width = 36; swatches.Add(button); }
            var solid = new Toggle("Solid background for OBS") { value = SolidOutput }; card.Add(solid);
            solid.RegisterValueChangedCallback(change => SetSolid(change.newValue));
            var window = GetComponent<SakuraWindow>();
            if (window != null)
            {
                var transparent = new Toggle("Transparent desktop window") { value = window.TransparencyRequested }; card.Add(transparent);
                var windowStatus = new Label(window.Status); windowStatus.AddToClassList("description"); card.Add(windowStatus);
                transparent.RegisterValueChangedCallback(change => window.SetTransparent(change.newValue));
                void RefreshWindow() { transparent.SetValueWithoutNotify(window.TransparencyRequested); windowStatus.text = window.Status; }
                window.Changed += RefreshWindow; card.RegisterCallback<DetachFromPanelEvent>(_ => window.Changed -= RefreshWindow);
            }
            bool ready = false;
            new SakuraColourPicker(card, Colour, value => { if (ready) SetColour(value); }); ready = true;
            card.Add(new Button(() => Choose(backend, false)) { text = "Choose background image" });
            card.Add(new Button(() => Choose(backend, true)) { text = "Choose looping video" });
            card.Add(new Button(Clear) { text = "Remove background media" });
            var status = new Label(Status); status.style.whiteSpace = WhiteSpace.Normal; card.Add(status);
            void UpdateStatus() => status.text = Status;
            Changed += UpdateStatus;
            card.RegisterCallback<DetachFromPanelEvent>(_ => Changed -= UpdateStatus);
        }
        void OnDestroy() { generation++; ReleaseMedia(); }
    }
}
