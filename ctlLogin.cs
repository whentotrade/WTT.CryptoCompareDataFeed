using System.Windows.Forms;

namespace WTT.CryptoDataFeed
{
    public partial class ctlLogin : UserControl
    {
        public string BaseSymbol
        {
            get { return txtBaseSymbol.Text.Trim(); }
        }

        public string Exchange
        {
            get { return txtExchange.Text.Trim(); }
        }

        public ctlLogin()
        {
            InitializeComponent();
        }

        private void ctlLogin_Load(object sender, System.EventArgs e)
        {
            txtBaseSymbol.Text = Properties.Settings.Default.BaseSymbol;
            txtExchange.Text = Properties.Settings.Default.Exchange;
        }

        private void linkLabel1_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            // Navigate to a URL.
            System.Diagnostics.Process.Start("https://www.cryptocompare.com/api/#");
        }

      
    }
}
