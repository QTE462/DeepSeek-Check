// DeepSeek API Key 输入框：改 key、测连通性，不用再手改 config.json

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DeepSeekBalance
{
    internal static class Dpi
    {
        [DllImport("user32.dll")]
        private static extern uint GetDpiForSystem();

        public static float Factor
        {
            get
            {
                try
                {
                    float f = GetDpiForSystem() / 96f;
                    if (f < 0.5f) f = 1f;
                    return f;
                }
                catch { return 1f; }
            }
        }
    }

    internal sealed class ApiKeyDialog : Form
    {
        private readonly TextBox _keyBox;
        private readonly CheckBox _showBox;
        private readonly Label _statusLabel;
        private readonly Button _testButton;
        private readonly Button _saveButton;
        private readonly Button _cancelButton;
        private readonly string _apiBase;
        private readonly float _s;

        public string KeyValue { get { return _keyBox.Text.Trim(); } }
        public bool Saved { get; private set; }

        public ApiKeyDialog(string apiBase, string currentKey)
        {
            _apiBase = apiBase;
            _s = Dpi.Factor;

            Text = "DeepSeek API Key";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(250, 250, 252);
            Font = new Font("Microsoft YaHei UI", 12f * _s, FontStyle.Regular, GraphicsUnit.Pixel);

            ClientSize = new Size(I(430), I(214));

            Label title = new Label();
            title.Text = "DeepSeek API Key（sk- 开头，在 platform.deepseek.com 生成）";
            title.SetBounds(I(18), I(16), I(394), I(20));
            title.ForeColor = Color.FromArgb(27, 42, 94);
            Controls.Add(title);

            _keyBox = new TextBox();
            _keyBox.SetBounds(I(18), I(42), I(394), I(26));
            _keyBox.Font = new Font("Consolas", 13f * _s, FontStyle.Regular, GraphicsUnit.Pixel);
            _keyBox.Text = currentKey;
            _keyBox.UseSystemPasswordChar = true;
            _keyBox.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(_keyBox);

            _showBox = new CheckBox();
            _showBox.Text = "显示明文";
            _showBox.SetBounds(I(18), I(76), I(120), I(22));
            _showBox.ForeColor = Color.FromArgb(90, 100, 130);
            _showBox.CheckedChanged += delegate { _keyBox.UseSystemPasswordChar = !_showBox.Checked; };
            Controls.Add(_showBox);

            _statusLabel = new Label();
            _statusLabel.SetBounds(I(18), I(104), I(394), I(36));
            _statusLabel.ForeColor = Color.FromArgb(110, 120, 150);
            _statusLabel.Text = "填好后可以先点「测试」验证，再点「保存」。";
            Controls.Add(_statusLabel);

            _testButton = new Button();
            _testButton.Text = "测试";
            _testButton.SetBounds(I(18), I(156), I(96), I(34));
            _testButton.Click += OnTestClick;
            Controls.Add(_testButton);

            _cancelButton = new Button();
            _cancelButton.Text = "取消";
            _cancelButton.SetBounds(I(214), I(156), I(96), I(34));
            _cancelButton.DialogResult = DialogResult.Cancel;
            Controls.Add(_cancelButton);

            _saveButton = new Button();
            _saveButton.Text = "保存";
            _saveButton.SetBounds(I(316), I(156), I(96), I(34));
            _saveButton.Click += OnSaveClick;
            Controls.Add(_saveButton);

            AcceptButton = _saveButton;
            CancelButton = _cancelButton;
        }

        private int I(float value)
        {
            return (int)Math.Round(value * _s);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
            BringToFront();
            _keyBox.Focus();
            _keyBox.SelectAll();
        }

        private void OnTestClick(object sender, EventArgs e)
        {
            string key = KeyValue;
            if (key.Length == 0)
            {
                SetStatus("请先填入 API Key。", Color.FromArgb(200, 60, 60));
                return;
            }
            _testButton.Enabled = false;
            SetStatus("正在连接 DeepSeek…", Color.FromArgb(110, 120, 150));
            BalanceApi.BeginFetch(_apiBase, key, delegate(BalanceSnapshot snap)
            {
                Action apply = delegate
                {
                    _testButton.Enabled = true;
                    if (snap.Ok)
                    {
                        SetStatus("连通正常，当前余额 " + SymbolOf(snap.Currency) + snap.Total.ToString("0.00")
                            + "。可以点「保存」了。", Color.FromArgb(20, 150, 90));
                    }
                    else
                    {
                        SetStatus("失败：" + snap.Error, Color.FromArgb(200, 60, 60));
                    }
                };
                try { BeginInvoke(apply); }
                catch { }
            });
        }

        private void OnSaveClick(object sender, EventArgs e)
        {
            if (KeyValue.Length == 0)
            {
                SetStatus("API Key 不能为空。", Color.FromArgb(200, 60, 60));
                return;
            }
            Saved = true;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void SetStatus(string text, Color color)
        {
            _statusLabel.Text = text;
            _statusLabel.ForeColor = color;
        }

        private static string SymbolOf(string currency)
        {
            if (string.IsNullOrEmpty(currency)) return "¥";
            if (currency.ToUpperInvariant() == "USD") return "$";
            return "¥";
        }

        // 离屏渲染一张图，用来检查版式（窗口不显示到屏幕上）
        public static void RenderPreview(string path, string key)
        {
            using (ApiKeyDialog dialog = new ApiKeyDialog("https://api.deepseek.com", key))
            {
                // 必须真正 Show 一次，子控件才会被绘制；摆到屏幕外，用户看不到
                dialog.StartPosition = FormStartPosition.Manual;
                dialog.Location = new Point(-6000, -6000);
                dialog.Show();
                Application.DoEvents();
                using (Bitmap bmp = new Bitmap(dialog.Width, dialog.Height))
                {
                    dialog.DrawToBitmap(bmp, new Rectangle(0, 0, dialog.Width, dialog.Height));
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
                dialog.Close();
            }
        }
    }
}
