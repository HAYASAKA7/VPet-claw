using System.Windows;

namespace VPet.Plugin.OpenClawBridge
{
    public partial class winOpenClawSetting : Window
    {
        private OpenClawPlugin _plugin;

        public winOpenClawSetting(OpenClawPlugin plugin)
        {
            InitializeComponent();
            _plugin = plugin;

            tbUrl.Text = _plugin.MW.Set["OpenClaw"].GetString("URL", OpenClawPlugin.DefaultServerUrl);
            tbToken.Password = _plugin.MW.Set["OpenClaw"].GetString("Token", "");
            tbSessionKey.Text = _plugin.MW.Set["OpenClaw"].GetString("SessionKey", "agent:main:main");
            if (tbSessionKey.Text == "vpet-openclaw")
            {
                tbSessionKey.Text = "agent:main:main";
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            _plugin.MW.Set["OpenClaw"].SetString("URL", tbUrl.Text.Trim());
            _plugin.MW.Set["OpenClaw"].SetString("Token", tbToken.Password);
            _plugin.MW.Set["OpenClaw"].SetString("SessionKey", tbSessionKey.Text.Trim());

            _plugin.MW.Save();
            _plugin.Reconnect();

            this.Close();
        }
    }
}
