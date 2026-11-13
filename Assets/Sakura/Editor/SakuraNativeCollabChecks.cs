using System;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Sakura.Unity.Editor
{
    public static class SakuraNativeCollabChecks
    {
        public static void Verify()
        {
            var secure = SakuraNativeCollab.RelayEndpoint("wss://relay.example.com", "ABCD2345");
            if (secure.Scheme != "wss" || secure.AbsolutePath != "/ws/ABCD2345") throw new Exception("Secure collab relay URL was not built correctly.");
            var local = SakuraNativeCollab.RelayEndpoint("ws://127.0.0.1:9000/", "room-1234");
            if (local.Scheme != "ws" || local.AbsolutePath != "/ws/room-1234") throw new Exception("Local collab relay URL was not built correctly.");
            Refused(() => SakuraNativeCollab.RelayEndpoint("http://relay.example.com", "ABCD2345"), "Insecure collab relay was accepted.");
            Refused(() => SakuraNativeCollab.RelayEndpoint("wss://relay.example.com/path?x=1", "ABCD2345"), "Relay URL with a query was accepted.");
            Refused(() => SakuraNativeCollab.RelayEndpoint("wss://relay.example.com", "short"), "Short room code was accepted.");
            Refused(() => SakuraNativeCollab.RelayEndpoint("wss://user:pass@relay.example.com", "ABCD2345"), "Relay URL with credentials was accepted.");
            string room = SakuraNativeCollab.GenerateRoom();
            if (room.Length != 10 || Array.Exists(room.ToCharArray(), value => !"ABCDEFGHJKLMNPQRSTUVWXYZ23456789".Contains(value))) throw new Exception("Generated room code is not valid.");

            if (SakuraNativeCollabPackage.SafeEntry("models/live2d/Amika/Amika.model3.json") != "models/live2d/Amika/Amika.model3.json") throw new Exception("A safe collab entry was rejected.");
            Refused(() => SakuraNativeCollabPackage.SafeEntry("../outside.model3.json"), "Collab entry traversal was accepted.");
            Refused(() => SakuraNativeCollabPackage.SafeEntry("/absolute.model3.json"), "Absolute collab entry was accepted.");
            Refused(() => SakuraNativeCollabPackage.SafeEntry("models//empty.model3.json"), "Empty collab path segment was accepted.");

            var pose = new JObject
            {
                ["kind"] = "3d", ["sakuraUnity"] = true,
                ["bones"] = new JArray(new JObject { ["name"] = "root", ["p"] = new JArray(0, 0, 0), ["q"] = new JArray(0, 0, 0, 1) }),
                ["morphs"] = new JArray()
            };
            SakuraNativeCollabPose.Validate(pose);
            var live2d = new JObject { ["kind"] = "live2d", ["parameters"] = new JObject { ["ParamAngleX"] = 1.0 } };
            SakuraNativeCollabPose.Validate(live2d);
            Refused(() => SakuraNativeCollabPose.Validate(new JObject { ["kind"] = "3d" }), "Collab pose without a bone list was accepted.");
            Refused(() => SakuraNativeCollabPose.Validate(new JObject { ["kind"] = "3d", ["sakuraUnity"] = true, ["bones"] = new JArray(new JObject { ["name"] = "root", ["p"] = new JArray(0, 0, 0), ["q"] = new JArray(0, 0, 0, 0) }), ["morphs"] = new JArray() }), "Zero-length collab quaternion was accepted.");
            Refused(() => SakuraNativeCollabPose.Validate(new JObject { ["kind"] = "live2d", ["parameters"] = new JObject { ["ParamAngleX"] = double.NaN } }), "Non-finite collab parameter was accepted.");

            var frame = new SakuraStageItems.AttachmentFrame(new Vector2(.5f, .4f), 2, 90);
            var offset = new Vector2(.1f, -.2f);
            var position = SakuraStageItems.ResolvePosition(frame, offset);
            var roundTrip = SakuraStageItems.ResolveOffset(frame, position);
            if ((roundTrip - offset).sqrMagnitude > .00001f) throw new Exception("Pinned stage attachment offset did not round-trip.");
            if (SakuraStageItems.RotateOffset(new Vector2(1, 0), 90).y < .99f) throw new Exception("Pinned stage attachment rotation is incorrect.");

            var item = new SakuraStageItems.Item { id = new string('a', 32), kind = "image", name = "Pinned", source = new string('a', 32) + ".png", follow = true, pinKind = "bone", pinTarget = "0:root", pinModel = "pmx/ringo/ringo.pmx" };
            if (!SakuraStageItems.Valid(item)) throw new Exception("A valid bone-pinned item was rejected.");
            item.pinKind = "part"; if (SakuraStageItems.Valid(item)) throw new Exception("Invalid pin kind was accepted.");
            item.pinKind = "artmesh"; item.pinTarget = ""; if (SakuraStageItems.Valid(item)) throw new Exception("Empty pin target was accepted.");
        }

        static void Refused(Action action, string message)
        {
            try { action(); } catch (ArgumentException) { return; } catch (InvalidOperationException) { return; } catch (System.IO.InvalidDataException) { return; }
            throw new Exception(message);
        }
    }
}
