using System;
using System.Diagnostics;
using System.Windows.Forms;
using WTT.CryptoCompareDataFeed.Properties;

namespace WTT.CryptoCompareDataFeed
{
    public partial class ctlLogin : UserControl
    {
        public ctlLogin()
        {
            InitializeComponent();
        }

        public string BaseSymbol => txtBaseSymbol.Text.Trim();

        public string Exchange => txtExchange.Text.Trim();

        private void ctlLogin_Load(object sender, EventArgs e)
        {
            txtBaseSymbol.Text = Settings.Default.BaseSymbol;
            txtExchange.Text = Settings.Default.Exchange;
        }

        private void linkLabel1_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            // Navigate to a URL.
            Process.Start("https://www.cryptocompare.com/api/#");
        }
    }
}