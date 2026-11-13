using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    public sealed class SakuraTwitchEffects : MonoBehaviour
    {
        sealed class Falling
        {
            public Image Image;
            public float Top, Left, Speed, Drift;
        }
        readonly Dictionary<string, Texture2D> cache = new Dictionary<string, Texture2D>();
        readonly HashSet<string> loading = new HashSet<string>();
        readonly List<Falling> falling = new List<Falling>();
        VisualElement overlay;
        Label banner;
        float bannerUntil;

        public void Initialize(VisualElement stage)
        {
            if (stage == null || overlay != null) return;
            overlay = new VisualElement { pickingMode = PickingMode.Ignore };
            overlay.style.position = Position.Absolute;
            overlay.style.left = 0; overlay.style.top = 0; overlay.style.right = 0; overlay.style.bottom = 0;
            overlay.style.overflow = Overflow.Hidden;
            stage.Add(overlay);
            banner = new Label { pickingMode = PickingMode.Ignore };
            banner.style.position = Position.Absolute;
            banner.style.top = 12; banner.style.left = Length.Percent(8); banner.style.right = Length.Percent(8);
            banner.style.unityTextAlign = TextAnchor.MiddleCenter;
            banner.style.fontSize = 18; banner.style.color = Color.white;
            banner.style.backgroundColor = new Color(0, 0, 0, .62f);
            banner.style.paddingTop = 8; banner.style.paddingBottom = 8; banner.style.paddingLeft = 12; banner.style.paddingRight = 12;
            banner.style.borderTopLeftRadius = 10; banner.style.borderTopRightRadius = 10;
            banner.style.borderBottomLeftRadius = 10; banner.style.borderBottomRightRadius = 10;
            banner.style.display = DisplayStyle.None;
            overlay.Add(banner);
        }

        public void DropEmotes(IEnumerable<string> ids)
        {
            if (overlay == null || ids == null) return;
            foreach (string id in ids.Where(value => !string.IsNullOrEmpty(value)).Take(8)) Load(id);
        }

        public void DropRewardEmotes() => DropEmotes(new[] { "25", "305954156", "425618", "86", "41" });

        public void ShowBanner(string text, float seconds = 6)
        {
            if (banner == null || string.IsNullOrWhiteSpace(text)) return;
            banner.text = text.Length > 160 ? text.Substring(0, 160) : text;
            banner.style.display = DisplayStyle.Flex;
            bannerUntil = Time.unscaledTime + Mathf.Clamp(seconds, 2, 15);
        }

        void Load(string id)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(id, @"\A\d{1,20}\z")) return;
            if (cache.TryGetValue(id, out var texture)) { Spawn(texture); return; }
            if (!loading.Add(id)) return;
            var request = UnityWebRequestTexture.GetTexture("https://static-cdn.jtvnw.net/emoticons/v2/" + id + "/default/dark/3.0");
            request.timeout = 10;
            var operation = request.SendWebRequest();
            operation.completed += _ =>
            {
                if (this == null) return;
                if (request.result == UnityWebRequest.Result.Success)
                {
                    var loaded = DownloadHandlerTexture.GetContent(request);
                    if (loaded != null)
                    {
                        if (cache.Count >= 64) { var oldest = cache.First(); cache.Remove(oldest.Key); Destroy(oldest.Value); }
                        cache[id] = loaded; Spawn(loaded);
                    }
                }
                request.Dispose(); loading.Remove(id);
            };
        }

        void Spawn(Texture2D texture)
        {
            if (overlay == null || texture == null) return;
            var image = new Image { image = texture, scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
            image.style.position = Position.Absolute;
            image.style.width = 56; image.style.height = 56;
            float left = UnityEngine.Random.Range(2f, 86f), top = -60;
            image.style.left = Length.Percent(left); image.style.top = top;
            overlay.Add(image);
            falling.Add(new Falling { Image = image, Left = left, Top = top, Speed = UnityEngine.Random.Range(90f, 180f), Drift = UnityEngine.Random.Range(-20f, 20f) });
        }

        void Update()
        {
            float height = overlay != null && overlay.resolvedStyle.height > 1 ? overlay.resolvedStyle.height : 640;
            for (int index = falling.Count - 1; index >= 0; index--)
            {
                var item = falling[index];
                item.Top += item.Speed * Time.unscaledDeltaTime;
                item.Left = Mathf.Clamp(item.Left + item.Drift * Time.unscaledDeltaTime, 0, 90);
                item.Image.style.top = item.Top; item.Image.style.left = Length.Percent(item.Left);
                if (item.Top > height + 60) { item.Image.RemoveFromHierarchy(); falling.RemoveAt(index); }
            }
            if (banner != null && bannerUntil > 0 && Time.unscaledTime >= bannerUntil) { banner.style.display = DisplayStyle.None; bannerUntil = 0; }
        }
    }
}
