using System;
using System.Net.WebSockets;

namespace Sakura.Unity
{
    public sealed partial class SakuraBackend
    {
        internal ClientWebSocket CreatePluginBridgeSocket(out Uri endpoint)
        {
            if (!Connected || identity == null || identity.port < 1 || identity.port > 65535 || string.IsNullOrEmpty(identity.token)) throw new InvalidOperationException("Connect the authenticated Sakura backend first.");
            string origin = "http://127.0.0.1:" + identity.port;
            endpoint = new Uri("ws://127.0.0.1:" + identity.port + "/api/plugins/bridge");
            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Origin", origin);
            socket.Options.SetRequestHeader("X-Sakura-Desktop", identity.token);
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            return socket;
        }
    }
}
