using VPet_Simulator.Windows.Interface;

namespace VPet.Plugin.OpenClawBridge
{
    public class OpenClawTalkBox : TalkBox
    {
        private readonly OpenClawPlugin _plugin;
        public override string APIName => "OpenClaw";

        public OpenClawTalkBox(OpenClawPlugin plugin) : base(plugin)
        {
            _plugin = plugin;
        }

        public override void Setting()
        {
            var win = new winOpenClawSetting(_plugin);
            win.ShowDialog();
        }

        public override async void Responded(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (_plugin.Client == null || !_plugin.Client.IsConnected)
            {
                _plugin.Notify("OpenClaw is not connected. Reconnecting...");
                _plugin.Reconnect();
                return;
            }

            _plugin.MW.Dispatcher.Invoke(DisplayThink);

            if (!await _plugin.Client.SendMessageAsync(text))
            {
                _plugin.Notify("OpenClaw is not connected.");
            }
        }
    }
}
