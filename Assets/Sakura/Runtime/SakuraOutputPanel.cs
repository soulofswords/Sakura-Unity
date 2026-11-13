using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sakura.Unity
{
    public sealed class SakuraOutputPanel
    {
        public SakuraOutputPanel(SakuraOutput output, VisualElement parent)
        {
            var owner = new VisualElement(); parent.Add(owner);
            var capture = Card("Output quality", owner);
            var quality = new DropdownField("Quality", new List<string> { "Low — older GPUs", "Balanced — GTX 10 series target", "High — RTX 3060+ target" }, (int)output.Preset); capture.Add(quality);
            quality.RegisterValueChangedCallback(_ => output.SetQuality((SakuraOutput.Quality)quality.index));
            var rate = new DropdownField("Capture target FPS", new List<string> { "15", "30", "60" }, output.FrameRate == 15 ? 0 : output.FrameRate == 30 ? 1 : 2); capture.Add(rate);
            rate.RegisterValueChangedCallback(_ => output.SetFrameRate(int.Parse(rate.value)));
            var name = new TextField("Sender name") { value = "Sakura" }; capture.Add(name);
            var states = new List<Label>(); var buttons = new List<Button>();
            foreach (var channel in output.Channels)
            {
                var row = Card(channel.Id == "spout" ? "Spout 2" : channel.Id == "ndi" ? "NDI" : "Virtual camera", owner);
                var state = new Label(); states.Add(state); row.Add(state);
                var button = new Button(() => output.Configure(channel.Id, !channel.Enabled, name.value)); buttons.Add(button); row.Add(button);
            }
            var recording = Card("Screenshots & recording", owner);
            recording.Add(new Button(output.Screenshot) { text = "Save PNG screenshot" });
            var encoder = new TextField("FFmpeg executable") { value = output.EncoderPath ?? "" }; recording.Add(encoder);
            encoder.RegisterValueChangedCallback(evt => output.EncoderPath = evt.newValue);
            var record = new Button(() => { if (output.Recording) output.StopRecording(); else output.StartRecording(); }); recording.Add(record);
            var hint = new Label("Record the avatar stage with transparency. Audio is routed separately in your streaming app."); hint.AddToClassList("description"); recording.Add(hint);
            recording.Add(new Button(() => { try { System.IO.Directory.CreateDirectory(output.MediaFolder); Application.OpenURL(new Uri(output.MediaFolder + System.IO.Path.DirectorySeparatorChar).AbsoluteUri); } catch (Exception error) { Debug.LogWarning(error.Message); } }) { text = "Open captures folder" });
            var status = new Label(); status.style.whiteSpace = WhiteSpace.Normal; owner.Add(status);
            void Refresh()
            {
                if (output == null) return;
                quality.SetValueWithoutNotify(quality.choices[(int)output.Preset]); rate.SetValueWithoutNotify(output.FrameRate.ToString());
                for (int i = 0; i < output.Channels.Length; i++)
                {
                    var channel = output.Channels[i]; states[i].text = channel.Id + ": " + (channel.Enabled ? "sending; " + channel.Frames + " accepted frames" : "off");
                    buttons[i].text = channel.Busy ? "Working…" : channel.Enabled ? "Stop " + channel.Id : "Start " + channel.Id; buttons[i].SetEnabled(!channel.Busy);
                }
                record.text = output.SavingRecording ? "Finishing…" : output.Recording ? "Stop recording" : "Record WebM"; record.SetEnabled(!output.SavingRecording);
                status.text = output.Status;
            }
            output.Changed += Refresh; owner.RegisterCallback<DetachFromPanelEvent>(_ => { if (output != null) output.Changed -= Refresh; }); Refresh();
        }

        private static VisualElement Card(string title, VisualElement parent)
        {
            var card = new VisualElement(); card.AddToClassList("card"); parent.Add(card);
            var label = new Label(title); label.AddToClassList("card-title"); card.Add(label); return card;
        }
    }
}
