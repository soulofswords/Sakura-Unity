using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    /// <summary>
    /// Companion camera pane: object recognition or face/hand tracking, mirroring
    /// the Python app's "Give Sakura the camera" card. Object frames are sent only
    /// when the user asks for a look or enables occasional idle comments.
    /// </summary>
    public sealed class SakuraCompanionCameraPanel
    {
        private readonly SakuraTracking tracking;
        private readonly SakuraBackend backend;
        private readonly VisualElement owner, objectHost, trackingHost;
        private readonly Button objectsButton, trackingButton;
        private readonly Toggle access, comments, cameraStyle;
        private readonly DropdownField cameraChoice;
        private readonly Label status, styleStatus;
        private IVisualElementScheduledItem timer;
        private bool objectMode;
        private string selectedCamera;

        public SakuraCompanionCameraPanel(SakuraTracking tracking, SakuraBackend backend, VisualElement parent)
        {
            this.tracking = tracking; this.backend = backend;
            owner = new VisualElement(); parent.Add(owner);

            var card = Card("Give Sakura the camera", owner);
            Description("Choose one camera task. Object recognition shares camera images with your selected AI provider. Face and hand tracking stay on this PC.", card);

            var choices = new VisualElement(); choices.AddToClassList("camera-choices"); card.Add(choices);
            objectsButton = Choice(choices, "See objects", "Lens-style: show her something and she describes it", () => SetMode(false));
            trackingButton = Choice(choices, "Track my face & hands", "Add-on: your movement drives the avatar while chat stays on", () => SetMode(true));

            objectHost = new VisualElement(); card.Add(objectHost);
            trackingHost = new VisualElement(); card.Add(trackingHost);

            var devices = WebCamTexture.devices.Select(device => device.name).ToList();
            selectedCamera = PlayerPrefs.GetString("sakura-camera-device", devices.FirstOrDefault() ?? "");
            cameraChoice = new DropdownField("Camera", devices.Count > 0 ? devices : new List<string> { "No camera found" }, Math.Max(0, devices.IndexOf(selectedCamera)));
            cameraChoice.name = "companion-camera-device";
            cameraChoice.SetEnabled(devices.Count > 0);
            cameraChoice.RegisterValueChangedCallback(_ =>
            {
                selectedCamera = cameraChoice.value; PlayerPrefs.SetString("sakura-camera-device", selectedCamera); PlayerPrefs.Save();
                if (objectMode && access.value) tracking.StartObjectCamera(selectedCamera);
            });
            objectHost.Add(cameraChoice);

            access = new Toggle("Enable camera access") { name = "companion-camera-enabled", value = false };
            access.SetEnabled(devices.Count > 0 || tracking.Source == SakuraTracking.InputSource.Phone);
            access.RegisterValueChangedCallback(change => SetAccess(change.newValue));
            objectHost.Add(access);

            comments = new Toggle("Occasional camera comments while idle") { name = "companion-camera-comments", value = PlayerPrefs.GetInt("sakura-camera-comments", 1) == 1 };
            comments.RegisterValueChangedCallback(change => { PlayerPrefs.SetInt("sakura-camera-comments", change.newValue ? 1 : 0); PlayerPrefs.Save(); });
            objectHost.Add(comments);

            status = new Label("Camera access off"); status.AddToClassList("description"); status.name = "companion-camera-status"; objectHost.Add(status);

            var actions = new VisualElement(); actions.AddToClassList("row"); objectHost.Add(actions);
            AddButton("Look now", LookNow, actions);
            AddButton("Connect to Phone", () => tracking.PhoneLink(), actions);

            Description("Camera access is off each launch. Object frames go to your AI provider; face tracking stays local. No camera images are saved to conversation memory.", objectHost);

            cameraStyle = new Toggle("Camera-aware conversation") { name = "camera-aware-conversation", value = false };
            cameraStyle.RegisterValueChangedCallback(change =>
            {
                cameraStyle.SetEnabled(false);
                backend.Api("/api/features/camera-style", new JObject { ["enabled"] = change.newValue }.ToString(Newtonsoft.Json.Formatting.None), (_, error) =>
                {
                    if (owner.panel == null) return;
                    cameraStyle.SetEnabled(true);
                    if (error != null) { cameraStyle.SetValueWithoutNotify(!change.newValue); styleStatus.text = error; }
                    else styleStatus.text = change.newValue ? "Enabled · fresh visible cues may gently inform reply delivery." : "Off · camera delivery hints cleared.";
                });
            });
            objectHost.Add(cameraStyle);
            styleStatus = new Label(""); styleStatus.AddToClassList("description"); objectHost.Add(styleStatus);
            AddButton("Refresh camera-aware setting", LoadCameraStyle, objectHost);

            objectMode = PlayerPrefs.GetString("sakura-camera-mode", "objects") != "tracking";
            SetMode(objectMode);
            LoadCameraStyle();

            tracking.Changed += Refresh;
            owner.RegisterCallback<DetachFromPanelEvent>(_ => { timer?.Pause(); tracking.Changed -= Refresh; });
            timer = owner.schedule.Execute(IdleComment).Every(60000);
            Refresh();
        }

        private void SetMode(bool faceTracking)
        {
            objectMode = !faceTracking;
            PlayerPrefs.SetString("sakura-camera-mode", objectMode ? "objects" : "tracking"); PlayerPrefs.Save();
            objectsButton.EnableInClassList("camera-mode-active", objectMode);
            trackingButton.EnableInClassList("camera-mode-active", !objectMode);
            objectHost.style.display = objectMode ? DisplayStyle.Flex : DisplayStyle.None;
            trackingHost.style.display = objectMode ? DisplayStyle.None : DisplayStyle.Flex;
            if (objectMode)
            {
                tracking.StopTracking();
                if (trackingHost.childCount > 0) trackingHost.Clear();
            }
            else
            {
                tracking.StopObjectCamera();
                if (trackingHost.childCount == 0) new SakuraTrackingPanel(tracking, trackingHost);
                if (access.value) tracking.StartTracking();
            }
            access.SetValueWithoutNotify(faceTracking ? tracking.Source != SakuraTracking.InputSource.Off : tracking.CameraActive);
        }

        private void SetAccess(bool enabled)
        {
            if (!objectMode)
            {
                if (enabled) tracking.StartTracking(); else tracking.StopTracking();
                return;
            }
            if (enabled)
            {
                tracking.StartObjectCamera(selectedCamera);
                if (!tracking.CameraActive)
                {
                    access.SetValueWithoutNotify(false);
                    status.text = "Camera unavailable. Choose another camera or close apps using it.";
                }
            }
            else tracking.StopObjectCamera();
        }

        private void LookNow()
        {
            if (backend.Mode != "Companion") { status.text = "Camera observations are available in Companion mode."; return; }
            if (!tracking.CameraActive && objectMode) SetAccess(true);
            byte[] jpeg = tracking.CaptureJpeg();
            if (jpeg == null) { status.text = "Camera has not produced a frame yet. Wait a moment, then try again."; return; }
            status.text = "Sakura is looking…";
            backend.SendCameraReaction(jpeg, (reply, error) =>
            {
                if (owner.panel == null) return;
                status.text = error ?? "Camera observation ready.";
            });
        }

        private void IdleComment()
        {
            if (!objectMode || !access.value || !comments.value || backend.Mode != "Companion" || backend.Busy || !tracking.CameraActive) return;
            byte[] jpeg = tracking.CaptureJpeg();
            if (jpeg == null) return;
            backend.SendCameraReaction(jpeg, (reply, error) => { if (owner.panel != null && error != null) status.text = error; });
        }

        private void Refresh()
        {
            if (owner.panel == null) return;
            bool on = objectMode ? tracking.CameraActive : tracking.Source != SakuraTracking.InputSource.Off;
            access.SetValueWithoutNotify(on);
            if (!objectMode) status.text = tracking.Status;
            else if (tracking.CameraActive) status.text = "Camera access on · images shared with your selected AI provider";
            else if (string.IsNullOrEmpty(status.text) || status.text.StartsWith("Camera access", StringComparison.Ordinal) || status.text.StartsWith("Camera observation", StringComparison.Ordinal)) status.text = "Camera access off";
        }

        private void LoadCameraStyle()
        {
            backend.Api("/api/features/status", null, (json, error) =>
            {
                if (owner.panel == null) return;
                if (error != null) { styleStatus.text = "Camera preference could not be loaded."; return; }
                try
                {
                    bool enabled = JObject.Parse(json).Value<bool?>("camera_style") == true;
                    cameraStyle.SetValueWithoutNotify(enabled);
                    styleStatus.text = enabled ? "Enabled · uses fresh cues only while a camera task is active." : "Off · replies do not use facial delivery hints.";
                }
                catch (Newtonsoft.Json.JsonException) { styleStatus.text = "Camera preference could not be read."; }
            });
        }

        private static VisualElement Card(string title, VisualElement parent)
        {
            var card = new VisualElement(); card.AddToClassList("card"); parent.Add(card);
            var label = new Label(title); label.AddToClassList("card-title"); card.Add(label); return card;
        }
        private static void Description(string text, VisualElement parent) { var label = new Label(text); label.AddToClassList("description"); parent.Add(label); }
        private static void AddButton(string text, Action action, VisualElement parent) => parent.Add(new Button(action) { text = text });

        private static Button Choice(VisualElement parent, string title, string description, Action action)
        {
            var button = new Button(action) { text = "" }; button.AddToClassList("camera-choice");
            var heading = new Label(title); heading.AddToClassList("camera-choice-title"); button.Add(heading);
            var detail = new Label(description); detail.AddToClassList("camera-choice-description"); button.Add(detail);
            parent.Add(button); return button;
        }
    }
}
