using System;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;

namespace PosDiscount
{
    /// <summary>
    /// وضعیت و تنظیمات همگام‌سازی با سرور مرکزی کمپین.
    /// این فرم هیچ منطق شمارشی ندارد؛ فقط صف/خطا/شمارنده را نشان می‌دهد.
    /// </summary>
    public class SyncForm : Form
    {
        private readonly AppSettings _s;
        private readonly LocalStore _store;
        private readonly Func<SyncRunResult> _runNow;

        private CheckBox _chkEnabled;
        private TextBox _txtUrl, _txtAgentId, _txtSecret, _txtStart;
        private NumericUpDown _numInterval, _numBatch;
        private Label _lblQueue, _lblRound, _lblMsg;
        private Button _btnSync, _btnSave, _btnClose;

        public SyncForm(AppSettings s, LocalStore store, Func<SyncRunResult> runNow)
        {
            _s = s;
            _store = store;
            _runNow = runNow;
            BuildUi();
            RefreshStatus();
        }

        private void BuildUi()
        {
            Text = "همگام‌سازی کمپین — سرور مرکزی";
            ClientSize = new Size(680, 540);
            MinimumSize = new Size(680, 540);
            StartPosition = FormStartPosition.CenterParent;
            RightToLeft = RightToLeft.Yes;
            RightToLeftLayout = true;
            Font = new Font("Tahoma", 9F);

            Panel head = new Panel();
            head.SetBounds(0, 0, 680, 46);
            head.BackColor = Color.FromArgb(30, 62, 104);
            head.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Label lblHead = new Label();
            lblHead.Text = "همگام‌سازی با سرور مرکزی کمپین";
            lblHead.ForeColor = Color.White;
            lblHead.Font = new Font("Tahoma", 12F, FontStyle.Bold);
            lblHead.SetBounds(12, 10, 656, 26);
            lblHead.TextAlign = ContentAlignment.MiddleRight;
            head.Controls.Add(lblHead);

            _chkEnabled = new CheckBox();
            _chkEnabled.Text = "همگام‌سازی فعال باشد (فاکتورها به سرور مرکزی ارسال شوند)";
            _chkEnabled.SetBounds(12, 58, 500, 24);
            _chkEnabled.Checked = _s.SyncEnabled;

            Label l1 = Lbl("آدرس سرور کمپین:", 12, 92, 170);
            _txtUrl = Txt(190, 92, 470);
            _txtUrl.Text = _s.ApiBaseUrl;

            Label l2 = Lbl("شناسهٔ این صندوق (AgentId):", 12, 126, 170);
            _txtAgentId = Txt(190, 126, 220);
            _txtAgentId.Text = _s.ApiAgentId;

            Label l3 = Lbl("کلید اختصاصی (Secret):", 12, 160, 170);
            _txtSecret = Txt(420, 160, 240);
            _txtSecret.PasswordChar = '●';
            _txtSecret.Text = _s.ApiAgentSecret;

            Label l4 = Lbl("تاریخ شروع کمپین (yyyy-MM-dd):", 12, 194, 170);
            _txtStart = Txt(190, 194, 140);
            _txtStart.Text = _s.CampaignStartDate;

            Label l5 = Lbl("فاصلهٔ همگام‌سازی (ثانیه):", 340, 194, 150);
            _numInterval = Num(500, 194, 70, 5, 3600, _s.SyncIntervalSeconds);

            Label l6 = Lbl("اندازهٔ دسته:", 340, 226, 150);
            _numBatch = Num(500, 226, 70, 1, 500, _s.SyncBatchSize);

            _lblQueue = new Label();
            _lblQueue.SetBounds(12, 266, 656, 44);
            _lblQueue.Font = new Font("Tahoma", 9.5F, FontStyle.Bold);

            _lblRound = new Label();
            _lblRound.SetBounds(12, 312, 656, 44);
            _lblRound.ForeColor = Color.FromArgb(30, 62, 104);

            _lblMsg = new Label();
            _lblMsg.SetBounds(12, 358, 656, 90);
            _lblMsg.ForeColor = Color.DimGray;

            _btnSave = new Button();
            _btnSave.Text = "ذخیره تنظیمات";
            _btnSave.SetBounds(480, 470, 180, 34);
            _btnSave.Click += new EventHandler(delegate { Save_(); });

            _btnSync = new Button();
            _btnSync.Text = "همگام‌سازی فوری";
            _btnSync.SetBounds(300, 470, 170, 34);
            _btnSync.Font = new Font("Tahoma", 9F, FontStyle.Bold);
            _btnSync.Click += new EventHandler(delegate { SyncNow(); });

            _btnClose = new Button();
            _btnClose.Text = "بستن";
            _btnClose.SetBounds(12, 470, 170, 34);
            _btnClose.Click += new EventHandler(delegate { Close(); });

            Controls.Add(head);
            Controls.Add(_chkEnabled);
            Controls.Add(l1); Controls.Add(_txtUrl);
            Controls.Add(l2); Controls.Add(_txtAgentId);
            Controls.Add(l3); Controls.Add(_txtSecret);
            Controls.Add(l4); Controls.Add(_txtStart);
            Controls.Add(l5); Controls.Add(_numInterval);
            Controls.Add(l6); Controls.Add(_numBatch);
            Controls.Add(_lblQueue);
            Controls.Add(_lblRound);
            Controls.Add(_lblMsg);
            Controls.Add(_btnSave);
            Controls.Add(_btnSync);
            Controls.Add(_btnClose);
        }

        private static Label Lbl(string text, int x, int y, int w)
        {
            Label l = new Label();
            l.Text = text;
            l.SetBounds(x, y, w, 24);
            l.TextAlign = ContentAlignment.MiddleRight;
            return l;
        }

        private static TextBox Txt(int x, int y, int w)
        {
            TextBox t = new TextBox();
            t.SetBounds(x, y, w, 24);
            return t;
        }

        private static NumericUpDown Num(int x, int y, int w, int min, int max, int value)
        {
            NumericUpDown n = new NumericUpDown();
            n.SetBounds(x, y, w, 24);
            n.Minimum = min;
            n.Maximum = max;
            n.Value = value < min ? min : (value > max ? max : value);
            return n;
        }

        private void Save_()
        {
            string url = _txtUrl.Text.Trim();
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "آدرس سرور باید با http:// یا https:// شروع شود.", "تنظیمات",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            DateTime start;
            if (!DateTime.TryParseExact(_txtStart.Text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out start))
            {
                MessageBox.Show(this, "تاریخ شروع کمپین را به شکل 2026-02-09 وارد کنید.", "تنظیمات",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (_txtAgentId.Text.Trim().Length == 0)
            {
                MessageBox.Show(this, "شناسهٔ صندوق (AgentId) الزامی است.", "تنظیمات",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (_chkEnabled.Checked && _txtSecret.Text.Trim().Length == 0)
            {
                MessageBox.Show(this, "برای فعال‌کردن همگام‌سازی، کلید اختصاصی لازم است.", "تنظیمات",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _s.SyncEnabled = _chkEnabled.Checked;
            _s.ApiBaseUrl = url;
            _s.ApiAgentId = _txtAgentId.Text.Trim();
            _s.ApiAgentSecret = _txtSecret.Text.Trim();
            _s.CampaignStartDate = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            _s.SyncIntervalSeconds = (int)_numInterval.Value;
            _s.SyncBatchSize = (int)_numBatch.Value;
            try
            {
                _s.Save();
                _lblMsg.ForeColor = Color.DarkGreen;
                _lblMsg.Text = "تنظیمات ذخیره شد. تغییرات از چرخهٔ بعدی همگام‌سازی اعمال می‌شود.";
            }
            catch (Exception ex)
            {
                _lblMsg.ForeColor = Color.Firebrick;
                _lblMsg.Text = "ذخیرهٔ تنظیمات ناموفق بود: " + ex.Message;
            }
            RefreshStatus();
        }

        private void SyncNow()
        {
            if (_runNow == null) return;
            _btnSync.Enabled = false;
            _lblMsg.ForeColor = Color.DimGray;
            _lblMsg.Text = "در حال همگام‌سازی...";
            Thread t = new Thread(new ThreadStart(delegate
            {
                SyncRunResult r = null;
                string roundText = "";
                try
                {
                    r = _runNow();
                    try
                    {
                        RoundSnapshot snap = new ApiClient(_s).GetCurrentRound();
                        roundText = "دورهٔ " + snap.RoundNo + ": " + snap.Count + " از " + snap.Target
                            + " | باقی‌مانده " + snap.Remaining + " | وضعیت " + snap.Status
                            + (snap.Held > 0 ? " | در انتظار " + snap.Held : "");
                    }
                    catch (Exception) { }
                }
                catch (Exception ex)
                {
                    r = new SyncRunResult();
                    r.Ok = false;
                    r.Message = ex.Message;
                }
                try
                {
                    if (IsHandleCreated && !IsDisposed)
                    {
                        BeginInvoke(new MethodInvoker(delegate
                        {
                            _btnSync.Enabled = true;
                            _lblMsg.ForeColor = r != null && r.Ok ? Color.DarkGreen : Color.Firebrick;
                            _lblMsg.Text = r == null ? "" : r.Message;
                            if (roundText.Length > 0) _lblRound.Text = roundText;
                            RefreshStatus();
                        }));
                    }
                }
                catch (Exception) { }
            }));
            t.IsBackground = true;
            t.Start();
        }

        private void RefreshStatus()
        {
            if (_store == null)
            {
                _lblQueue.Text = "دیتابیس محلی باز نیست.";
                return;
            }
            SyncStats st = _store.GetSyncStats();
            string watermark = _store.GetMetaValue("SyncLastFCode");
            _lblQueue.Text = "در صف ارسال: " + st.Pending + " | ناموفق/در انتظار تلاش: " + st.Failed
                + " | آخرین FCode پردازش‌شده: " + (watermark.Length == 0 ? "—" : watermark);

            string err = st.LastError;
            if (string.IsNullOrEmpty(err))
            {
                _lblMsg.ForeColor = Color.DimGray;
                _lblMsg.Text = "هیچ خطای همگام‌سازی ثبت نشده است.";
            }
            else if (_lblMsg.Text.Length == 0 || _lblMsg.ForeColor != Color.DarkGreen)
            {
                _lblMsg.ForeColor = Color.Firebrick;
                _lblMsg.Text = "آخرین خطا: " + err;
            }
        }
    }
}
