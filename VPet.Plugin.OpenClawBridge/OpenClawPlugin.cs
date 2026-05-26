#nullable disable
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using VPet_Simulator.Core;
using VPet_Simulator.Windows.Interface;
using VPet.Plugin.OpenClawBridge.Models;

namespace VPet.Plugin.OpenClawBridge
{
    public class OpenClawPlugin : MainPlugin
    {
        public const string DefaultServerUrl = "ws://127.0.0.1:18789";

        public override string PluginName => "OpenClawBridge";
        public OpenClawClient Client { get; private set; }
        public OpenClawTalkBox TalkBox { get; private set; }
        private readonly Dictionary<string, SayInfoWithStream> _activeReplies = new Dictionary<string, SayInfoWithStream>();
        private SayInfoWithStream _currentReply;

        public OpenClawPlugin(IMainWindow mainwin) : base(mainwin)
        {
        }

        public override void LoadPlugin()
        {
            TalkBox = new OpenClawTalkBox(this);
            if (MW.TalkAPI.All(x => x.APIName != TalkBox.APIName))
            {
                MW.TalkAPI.Add(TalkBox);
            }

            Reconnect();
        }

        public override void Setting()
        {
            var win = new winOpenClawSetting(this);
            win.ShowDialog();
        }

        public void Reconnect()
        {
            Client?.Dispose();
            FinishActiveReplies();

            string url = MW.Set["OpenClaw"].GetString("URL", DefaultServerUrl);
            string token = MW.Set["OpenClaw"].GetString("Token", "");
            string sessionKey = MW.Set["OpenClaw"].GetString("SessionKey", "agent:main:main");
            if (sessionKey == "vpet-openclaw")
            {
                sessionKey = "agent:main:main";
            }

            var client = new OpenClawClient(url, sessionKey);
            client.OnMessageReceived += HandleMessage;
            client.OnError += HandleClientError;
            Client = client;
            
            // Push the connection to a background thread to prevent deadlocking the WPF UI thread during startup
            Task.Run(async () => 
            {
                await client.ConnectAsync(token);
            });
        }

        public void Notify(string text)
        {
            MW.Dispatcher.Invoke(() => MW.Main.Say(text));
        }

        private void HandleClientError(string error)
        {
            MW.Dispatcher.Invoke(() => MW.Main.Say($"OpenClaw connection failed: {error}"));
        }

        private void HandleMessage(OpenClawMessage msg)
        {
            MW.Dispatcher.Invoke(() =>
            {
                switch (msg.Type)
                {
                    case "working":
                        PlayAnimation("work");
                        break;
                    case "error":
                        PlayAnimation("ill");
                        FinishActiveReplies();
                        MW.Main.Say(string.IsNullOrWhiteSpace(msg.Payload) ? "OpenClaw request failed." : msg.Payload);
                        break;
                    case "success":
                        FinishReply(msg);
                        PlayAnimation("happy", () => MW.Main.DisplayToNomal());
                        break;
                    case "reply":
                        ShowReply(msg);
                        break;
                    case "tool_call":
                        HandleToolCall(msg);
                        break;
                }
            });
        }

        private void ShowReply(OpenClawMessage msg)
        {
            var text = msg.Payload;
            if (string.IsNullOrWhiteSpace(text))
            {
                if (msg.IsFinal)
                {
                    FinishReply(msg);
                }
                return;
            }

            var reply = GetOrCreateReply(msg);
            var currentText = reply.CurrentText.ToString();
            if (msg.Replace || text.Length < currentText.Length || !text.StartsWith(currentText, StringComparison.Ordinal))
            {
                reply.UpdateAllText(text);
            }
            else if (text.Length > currentText.Length)
            {
                reply.UpdateText(text.Substring(currentText.Length));
            }

            if (msg.IsFinal)
            {
                FinishReply(msg);
            }
        }

        private SayInfoWithStream GetOrCreateReply(OpenClawMessage msg)
        {
            var key = string.IsNullOrWhiteSpace(msg.RunId) ? "__current" : msg.RunId;
            if (_activeReplies.TryGetValue(key, out var reply))
            {
                return reply;
            }

            reply = new SayInfoWithStream("happy", true);
            _activeReplies[key] = reply;
            _currentReply = reply;
            TalkBox?.DisplayThinkToSayRnd(reply);
            return reply;
        }

        private void FinishReply(OpenClawMessage msg)
        {
            if (!string.IsNullOrWhiteSpace(msg.RunId) && _activeReplies.TryGetValue(msg.RunId, out var reply))
            {
                reply.FinishGenerate();
                _activeReplies.Remove(msg.RunId);
                return;
            }

            if (msg.Type != "success")
            {
                _currentReply?.FinishGenerate();
                _currentReply = null;
                _activeReplies.Clear();
            }
        }

        private void FinishActiveReplies()
        {
            foreach (var reply in _activeReplies.Values)
            {
                reply.FinishGenerate();
            }

            _activeReplies.Clear();
            _currentReply = null;
        }

        private void HandleToolCall(OpenClawMessage msg)
        {
            if (msg.Tool == "feed_pet")
            {
                // Try to find a food item by name or just grab a random one
                var food = MW.Foods.FirstOrDefault(f => f.Name == msg.Args) ?? MW.Foods.FirstOrDefault();
                if (food != null)
                {
                    MW.TakeItem(food);
                    MW.Main.Say($"Yum! Thanks for the {food.Name}!");
                }
            }
            else if (msg.Tool == "move_pet")
            {
                PlayAnimation("walk");
                // Note: actual screen movement depends on how the Core handles 'walk' display offsets
                // We'll just play the animation for this prototype
            }
        }

        private void PlayAnimation(string graphName, Action onComplete = null)
        {
            var anims = MW.Core.Graph.FindGraphs(graphName, GraphInfo.AnimatType.C_End, MW.Core.Save.Mode);
            if (anims.Count > 0)
            {
                MW.Main.Display(anims[0], onComplete);
            }
        }
    }
}
