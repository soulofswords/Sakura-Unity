using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Sakura.Unity.Editor
{
    public static class SakuraVmcChecks
    {
        public static void Verify()
        {
            var message = SakuraVmc.EncodeMessage("/test", "name", 1f, 1);
            if (Encoding.UTF8.GetString(message, 0, 8) != "/test\0\0\0" || message[message.Length - 4] != 0 || message[message.Length - 1] != 1) throw new Exception("OSC strings or big-endian integer failed.");
            var frame = new SakuraVmc.Frame { Root = new SakuraVmc.Pose(Vector3.one, new Quaternion(0, 0, 0, 2)) };
            frame.Bones["Head"] = new SakuraVmc.Pose(Vector3.up, Quaternion.identity);
            for (int i = 0; i < 256; i++) frame.Blendshapes["custom_" + i] = .5f;
            var packets = SakuraVmc.EncodeFrame(frame, new[] { "Blink" }, 12.5f);
            if (packets.Count < 2 || packets.Any(packet => packet.Length > 1400 || Encoding.ASCII.GetString(packet, 0, 8) != "#bundle\0")) throw new Exception("OSC bundle size/header failed.");
            var decoded = Decode(packets);
            if (!decoded.Any(row => row.address == "/VMC/Ext/Blend/Val" && (string)row.values[0] == "Blink" && (float)row.values[1] == 0)) throw new Exception("Removed expression was not reset.");
            var rootPose = decoded.Single(row => row.address == "/VMC/Ext/Root/Pos");
            if ((float)rootPose.values[7] != 1) throw new Exception("Outgoing quaternion was not normalized.");
            if (decoded.Count(row => row.address == "/VMC/Ext/Blend/Apply") != 1 || decoded.Last().address != "/VMC/Ext/T") throw new Exception("VMC frame terminators failed.");
            bool rejected = false;
            frame.Root = new SakuraVmc.Pose(Vector3.zero, new Quaternion());
            try { SakuraVmc.EncodeFrame(frame, Array.Empty<string>(), 0); } catch (ArgumentException) { rejected = true; }
            if (!rejected) throw new Exception("Invalid quaternion accepted.");
            var avatar = new GameObject("VMC fixture");
            var mesh = new Mesh();
            try
            {
                var head = new GameObject("頭"); head.transform.SetParent(avatar.transform); head.transform.localPosition = Vector3.up;
                var hand = new GameObject("左手首"); hand.transform.SetParent(avatar.transform); hand.transform.localPosition = Vector3.right;
                mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }; mesh.triangles = new[] { 0, 1, 2 };
                mesh.AddBlendShapeFrame("あ", 100, new Vector3[3], new Vector3[3], new Vector3[3]);
                var renderer = head.AddComponent<SkinnedMeshRenderer>(); renderer.sharedMesh = mesh; renderer.SetBlendShapeWeight(0, 75);
                var sample = SakuraVmc.Capture(avatar, "PMX", .08f, false);
                if (sample == null || sample.Bones["Head"].Position != Vector3.up || sample.Trackers["LeftHand"].Position != Vector3.right) throw new Exception("Native PMX mapping or existing .08 scale conversion failed.");
                if (sample.Blendshapes["A"] != .75f || sample.Blendshapes["あ"] != .75f) throw new Exception("Original PMX vowel alias was not emitted.");
                sample = SakuraVmc.Capture(avatar, "PMX", .16f, true);
                if (sample.Trackers["RightHand"].Position != Vector3.left * 2) throw new Exception("VMC mirror/units failed.");
                var origin = new SakuraVmc.Pose(Vector3.up, Quaternion.identity);
                sample = SakuraVmc.Capture(avatar, "PMX", .08f, false, origin);
                if (sample.Trackers["Head"].Position != Vector3.zero) throw new Exception("Tracker recenter failed.");
                if (SakuraVmc.Capture(avatar, "Live2D", .08f, false) != null) throw new Exception("Live2D was advertised as a humanoid pose.");
                Debug.Log("SAKURA_VMC_CHECKS_OK: OSC endian/strings, normalized transforms, 1400-byte bundles, blend reset, native PMX mapping/scale/mirror/recenter, invalid quaternion. No network traffic sent.");
            }
            finally { UnityEngine.Object.DestroyImmediate(avatar); UnityEngine.Object.DestroyImmediate(mesh); }
        }
        static List<(string address, object[] values)> Decode(IReadOnlyList<byte[]> packets)
        {
            var result = new List<(string, object[])>();
            foreach (var packet in packets)
            {
                int offset = 16;
                while (offset < packet.Length)
                {
                    int size = ReadInt(packet, offset); offset += 4; int end = offset + size;
                    string address = ReadString(packet, ref offset), tags = ReadString(packet, ref offset);
                    var values = new List<object>();
                    foreach (char tag in tags.Skip(1))
                    {
                        if (tag == 's') values.Add(ReadString(packet, ref offset));
                        else
                        {
                            var bytes = packet.Skip(offset).Take(4).ToArray(); if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
                            values.Add(tag == 'f' ? (object)BitConverter.ToSingle(bytes, 0) : BitConverter.ToInt32(bytes, 0)); offset += 4;
                        }
                    }
                    if (offset != end) throw new Exception("Malformed OSC element length.");
                    result.Add((address, values.ToArray()));
                }
            }
            return result;
        }
        static int ReadInt(byte[] bytes, int index) => bytes[index] << 24 | bytes[index + 1] << 16 | bytes[index + 2] << 8 | bytes[index + 3];
        static string ReadString(byte[] bytes, ref int offset)
        {
            int end = Array.IndexOf(bytes, (byte)0, offset); string value = Encoding.UTF8.GetString(bytes, offset, end - offset); offset = (end + 4) & ~3; return value;
        }
    }
}
