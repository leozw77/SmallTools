using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Threading;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}

internal sealed class MainForm : Form
{
    private readonly ComboBox waveformBox;
    private readonly Button triggerButton;
    private readonly Label statusLabel;

    public MainForm()
    {
        Text = "MX Haptic Test";
        ClientSize = new Size(360, 150);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        var title = new Label();
        title.Text = "测试 HapticWebPlugin 是否能被本地 EXE 调用";
        title.AutoSize = true;
        title.Location = new Point(16, 16);
        Controls.Add(title);

        waveformBox = new ComboBox();
        waveformBox.DropDownStyle = ComboBoxStyle.DropDownList;
        waveformBox.Items.AddRange(new object[] {
            "knock",
            "sharp_collision",
            "completed",
            "damp_state_change",
            "subtle_collision"
        });
        waveformBox.SelectedIndex = 0;
        waveformBox.Location = new Point(18, 50);
        waveformBox.Width = 180;
        Controls.Add(waveformBox);

        triggerButton = new Button();
        triggerButton.Text = "震一下";
        triggerButton.Location = new Point(216, 48);
        triggerButton.Size = new Size(110, 30);
        triggerButton.Click += TriggerButton_Click;
        Controls.Add(triggerButton);

        statusLabel = new Label();
        statusLabel.Text = "状态：待测试";
        statusLabel.AutoSize = false;
        statusLabel.Location = new Point(18, 94);
        statusLabel.Size = new Size(320, 38);
        Controls.Add(statusLabel);
    }

    private void TriggerButton_Click(object sender, EventArgs e)
    {
        string waveform = Convert.ToString(waveformBox.SelectedItem);
        triggerButton.Enabled = false;
        statusLabel.Text = "状态：正在请求 " + waveform + " ...";

        ThreadPool.QueueUserWorkItem(delegate
        {
            string result;
            try
            {
                // HapticWebPlugin REST API uses HTTPS on local.jmw.nz:41443.
                // Explicitly enable TLS 1.2 for compatibility with the .NET Framework runtime.
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;

                string url = "https://local.jmw.nz:41443/haptic/" + Uri.EscapeDataString(waveform);
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "POST";
                request.ContentLength = 0; // Required by HapticWebPlugin for POST requests.
                request.Timeout = 1500;
                request.ReadWriteTimeout = 1500;
                request.KeepAlive = false;

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream))
                {
                    string body = reader.ReadToEnd();
                    result = "状态：成功，鼠标应该已经震动。HTTP " + (int)response.StatusCode;
                    if (!string.IsNullOrEmpty(body) && body.IndexOf("success", StringComparison.OrdinalIgnoreCase) < 0)
                        result += "\r\n返回：" + body;
                }
            }
            catch (Exception ex)
            {
                result = "状态：失败 - " + ex.Message;
            }

            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    statusLabel.Text = result;
                    triggerButton.Enabled = true;
                });
            }
            catch
            {
            }
        });
    }
}
