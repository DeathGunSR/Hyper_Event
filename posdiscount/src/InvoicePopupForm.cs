using System;
using System.Drawing;
using System.Windows.Forms;

namespace PosDiscount
{
    /// <summary>
    /// پاپ‌آپ فاکتور جدید. فقط زمانی نمایش داده می‌شود که فاکتور مشمول تخفیف باشد.
    /// کارهای این پنجره:
    ///   • نمایش تخفیف پیشنهادی برنامه (فقط پیشنهاد؛ هیچ‌چیز در دیتابیس هولو ثبت نمی‌شود)
    ///   • گرفتن شماره موبایل مشتری و ثبت آن در «دیتابیس محلی» همراه با امتیاز فاکتور
    /// </summary>
    public class InvoicePopupForm : Form
    {
        private readonly AppSettings _s;
        private readonly LocalStore _store;
        private readonly InvoiceInfo _inv;
        private readonly DiscountResult _res;
        private StoredInvoice _rec;

        /// <summary>اطلاعات محلی (موبایل/امتیاز) در این پنجره تغییر کرده است.</summary>
        public bool DataChanged;

        private TextBox _txtMobile, _txtName;
        private Label _lblPoints, _lblSaved, _lblTotal, _lblName, _lblMobile;
        private Button _btnSave;
        private Timer _timer;
        private Label _lblCountdown;
        private int _left;

        public InvoicePopupForm(AppSettings s, LocalStore store, InvoiceInfo inv, DiscountResult res, StoredInvoice rec)
        {
            _s = s;
            _store = store;
            _inv = inv;
            _res = res;
            _rec = rec;

            BuildUi();
            Shown += new EventHandler(OnShown);
            FormClosed += new FormClosedEventHandler(OnClosed);
        }

        private bool HasDiscount { get { return _res != null && _res.HasDiscount; } }

        private int PointsOf(double amountGross, double holooDiscount)
        {
            double baseAmount = PointsEngine.BaseAmount(_s, amountGross, holooDiscount);
            return PointsEngine.ForAmount(baseAmount, _s.PointsPerAmountToman);
        }

        private void BuildUi()
        {
            Text = "فاکتور جدید صندوق";
            ClientSize = new Size(500, 596);
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            ShowInTaskbar = false;
            MaximizeBox = false;
            MinimizeBox = false;
            TopMost = _s.AlwaysOnTop;
            RightToLeft = RightToLeft.Yes;
            RightToLeftLayout = true;
            Font = new Font("Tahoma", 9F);
            StartPosition = FormStartPosition.Manual;
            Location = PopupLocation(ClientSize);

            // ---------------- سربرگ ----------------
            Panel head = new Panel();
            head.SetBounds(0, 0, 500, 48);
            head.BackColor = HasDiscount ? Color.FromArgb(0, 110, 60) : Color.FromArgb(120, 70, 20);
            head.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            Label lblHead = new Label();
            lblHead.Text = HasDiscount ? "فاکتور جدید — مشمول تخفیف" : "فاکتور جدید";
            lblHead.ForeColor = Color.White;
            lblHead.Font = new Font("Tahoma", 12F, FontStyle.Bold);
            lblHead.SetBounds(10, 11, 480, 26);
            lblHead.TextAlign = ContentAlignment.MiddleRight;
            head.Controls.Add(lblHead);

            // ---------------- اطلاعات فاکتور ----------------
            Label lblWho = new Label();
            lblWho.Text = "صندوق‌دار: " + (_inv.CashierName.Length > 0 ? _inv.CashierName : _inv.UserCode.ToString())
                + "   |   ساعت: " + _inv.Time + (string.IsNullOrEmpty(_inv.JalaliDate) ? "" : "   |   تاریخ: " + _inv.JalaliDate);
            lblWho.SetBounds(12, 54, 476, 20);
            lblWho.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            Label lblNo = new Label();
            lblNo.Text = "شماره فاکتور: " + _inv.FCodeC + "        تعداد اقلام: " + _inv.Items;
            lblNo.Font = new Font("Tahoma", 10F, FontStyle.Bold);
            lblNo.SetBounds(12, 76, 476, 22);
            lblNo.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            Panel box = new Panel();
            box.SetBounds(12, 102, 476, 150);
            box.BackColor = Color.FromArgb(244, 248, 244);
            box.BorderStyle = BorderStyle.FixedSingle;
            box.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            box.Controls.Add(Row("جمع کل فاکتور: " + Num.F(_inv.Gross) + " ریال", 8, 26, 11F, Color.Black));
            box.Controls.Add(Row(HasDiscount
                ? "تخفیف پیشنهادی: " + Num.F(_res.Discount) + " ریال  (" + Num.P(_res.Percent) + "٪)"
                : "تخفیف پیشنهادی: ندارد", 36, 30, 12F, HasDiscount ? Color.DarkGreen : Color.Firebrick));
            box.Controls.Add(Row(HasDiscount
                ? "مبلغ قابل پرداخت پس از تخفیف:"
                : "مبلغ قابل پرداخت:", 70, 24, 11F, Color.Black));
            box.Controls.Add(Row(Num.F(HasDiscount ? _res.Net : (_inv.Gross - _inv.Discount)) + " ریال",
                96, 44, 15F, HasDiscount ? Color.FromArgb(178, 28, 28) : Color.Black));

            Label lblRule = new Label();
            lblRule.SetBounds(12, 256, 476, 20);
            lblRule.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            lblRule.ForeColor = HasDiscount ? Color.DarkGreen : Color.Firebrick;
            lblRule.Text = HasDiscount
                ? ("قاعده تخفیف: " + _res.RuleName)
                : ("توضیح: " + (_res == null ? "—" : _res.Reason));

            Label lblWarn = new Label();
            lblWarn.SetBounds(12, 276, 476, 36);
            lblWarn.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            lblWarn.ForeColor = Color.FromArgb(150, 60, 0);
            lblWarn.Text = "این مبلغ فقط پیشنهاد برنامه است.\r\nثبت تخفیف باید توسط خودتان در نرم‌افزار هولو انجام شود.";

            Label lblPay = new Label();
            lblPay.SetBounds(12, 314, 476, 20);
            lblPay.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            lblPay.ForeColor = Color.DimGray;
            lblPay.Text = "روش پرداخت ثبت‌شده: " + _inv.PayText()
                + (string.IsNullOrEmpty(_inv.PosTerminal) ? "" : "   |   کد کارتخوان: " + _inv.PosTerminal);

            // ---------------- امتیاز و موبایل ----------------
            Panel pnlLoyal = new Panel();
            pnlLoyal.SetBounds(12, 336, 476, 168);
            pnlLoyal.BackColor = Color.FromArgb(250, 248, 240);
            pnlLoyal.BorderStyle = BorderStyle.FixedSingle;
            pnlLoyal.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            int pts = PointsOf(_inv.Gross, _inv.Discount);

            _lblPoints = new Label();
            _lblPoints.SetBounds(8, 8, 456, 22);
            _lblPoints.Font = new Font("Tahoma", 10F, FontStyle.Bold);
            _lblPoints.ForeColor = Color.FromArgb(20, 70, 130);
            _lblPoints.Text = "امتیاز این فاکتور: " + pts + "  ("
                + Num.F(PointsEngine.BaseAmount(_s, _inv.Gross, _inv.Discount)) + " ریال = "
                + Num.F(PointsEngine.BaseAmount(_s, _inv.Gross, _inv.Discount) / PointsEngine.RialPerToman) + " تومان)";
            pnlLoyal.Controls.Add(_lblPoints);

            Label lblRulePts = new Label();
            lblRulePts.SetBounds(8, 32, 456, 34);
            lblRulePts.ForeColor = Color.DimGray;
            lblRulePts.Text = "قاعده: هر " + Num.F(_s.PointsPerAmountToman) + " تومان خرید در یک فاکتور = ۱ امتیاز\r\n"
                + "(امتیاز هر فاکتور جداگانه حساب می‌شود؛ چند فاکتور با هم جمع نمی‌شوند)";
            pnlLoyal.Controls.Add(lblRulePts);

            bool capture = _s.CaptureMobileInPopup;

            _lblMobile = new Label();
            _lblMobile.Text = "شماره موبایل مشتری:";
            _lblMobile.SetBounds(300, 72, 164, 24);
            _lblMobile.TextAlign = ContentAlignment.MiddleRight;
            _lblMobile.Visible = capture;
            pnlLoyal.Controls.Add(_lblMobile);

            _txtMobile = new TextBox();
            _txtMobile.SetBounds(120, 72, 175, 24);
            _txtMobile.MaxLength = 20;
            _txtMobile.TextAlign = HorizontalAlignment.Center;
            _txtMobile.Font = new Font("Tahoma", 10F, FontStyle.Bold);
            _txtMobile.Text = _rec == null ? "" : _rec.Mobile;
            _txtMobile.Visible = capture;
            pnlLoyal.Controls.Add(_txtMobile);

            _lblName = new Label();
            _lblName.Text = "نام مشتری (اختیاری):";
            _lblName.SetBounds(300, 102, 164, 24);
            _lblName.TextAlign = ContentAlignment.MiddleRight;
            _lblName.Visible = capture;
            pnlLoyal.Controls.Add(_lblName);

            _txtName = new TextBox();
            _txtName.SetBounds(90, 102, 205, 24);
            _txtName.Text = _rec == null ? "" : _rec.CustomerAlias;
            _txtName.Visible = capture;
            pnlLoyal.Controls.Add(_txtName);

            _btnSave = new Button();
            _btnSave.SetBounds(90, 130, 205, 28);
            _btnSave.Text = "ثبت موبایل و امتیاز در دیتابیس محلی";
            _btnSave.BackColor = Color.FromArgb(226, 236, 248);
            _btnSave.Click += new EventHandler(delegate { SaveMobile(); });
            _btnSave.Visible = capture;
            pnlLoyal.Controls.Add(_btnSave);

            _lblSaved = new Label();
            _lblSaved.SetBounds(8, 130, 78, 28);
            _lblSaved.TextAlign = ContentAlignment.MiddleCenter;
            _lblSaved.ForeColor = Color.DarkGreen;
            _lblSaved.Visible = capture;
            pnlLoyal.Controls.Add(_lblSaved);

            _lblTotal = new Label();
            _lblTotal.SetBounds(8, 102, 78, 24);
            _lblTotal.TextAlign = ContentAlignment.MiddleCenter;
            _lblTotal.ForeColor = Color.FromArgb(20, 70, 130);
            _lblTotal.Visible = capture;
            pnlLoyal.Controls.Add(_lblTotal);

            if (!capture)
            {
                Label lblOff = new Label();
                lblOff.SetBounds(8, 72, 456, 88);
                lblOff.ForeColor = Color.FromArgb(120, 60, 0);
                lblOff.TextAlign = ContentAlignment.MiddleCenter;
                lblOff.Text = "ثبت شماره موبایل در این پنجره از تنظیمات خاموش شده است.\r\n"
                    + "برای ثبت موبایل و امتیاز، از دکمه «ثبت موبایل/امتیاز» در پنجره اصلی استفاده کنید.";
                pnlLoyal.Controls.Add(lblOff);
            }

            // ---------------- دکمه‌ها ----------------
            Button btnClose = new Button();
            btnClose.SetBounds(12, 512, 150, 36);
            btnClose.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            btnClose.Text = "بستن";
            btnClose.Click += new EventHandler(delegate { Close(); });

            Label lblCloseHint = new Label();
            lblCloseHint.SetBounds(170, 512, 318, 36);
            lblCloseHint.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            lblCloseHint.ForeColor = Color.DimGray;
            lblCloseHint.TextAlign = ContentAlignment.MiddleRight;
            lblCloseHint.Text = "با بستن پنجره، موبایل واردشده (اگر معتبر باشد) ذخیره می‌شود.";

            _lblCountdown = new Label();
            _lblCountdown.SetBounds(12, 556, 476, 24);
            _lblCountdown.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            _lblCountdown.TextAlign = ContentAlignment.MiddleCenter;
            _lblCountdown.ForeColor = Color.DimGray;
            _lblCountdown.Visible = false;
            Controls.Add(_lblCountdown);

            Controls.Add(head);
            Controls.Add(lblWho);
            Controls.Add(lblNo);
            Controls.Add(box);
            Controls.Add(lblRule);
            Controls.Add(lblWarn);
            Controls.Add(lblPay);
            Controls.Add(pnlLoyal);
            Controls.Add(btnClose);
            Controls.Add(lblCloseHint);

            if (_s.AutoCloseSeconds > 0)
            {
                _left = _s.AutoCloseSeconds;
                _lblCountdown.Visible = true;
                _lblCountdown.Text = "بستن خودکار پس از " + _left + " ثانیه";
                _timer = new Timer();
                _timer.Interval = 1000;
                _timer.Tick += new EventHandler(OnTick);
                _timer.Start();
            }

            RefreshTotals();
        }

        /// <summary>یک سطر تمام‌عرض داخل کادر مبالغ (برچسب و مبلغ در یک متن، تا ترتیب راست‌به‌چپ حفظ شود).</summary>
        private static Label Row(string text, int y, int h, float size, Color color)
        {
            Label l = new Label();
            l.Text = text;
            l.SetBounds(8, y, 458, h);
            l.TextAlign = ContentAlignment.MiddleCenter;
            l.ForeColor = color;
            l.Font = new Font("Tahoma", size, FontStyle.Bold);
            l.AutoEllipsis = false;
            return l;
        }

        private Point PopupLocation(Size size)
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            string corner = _s.PopupCorner == null ? "center" : _s.PopupCorner.ToLowerInvariant();
            switch (corner)
            {
                case "topright": return new Point(wa.Right - size.Width - 12, wa.Top + 12);
                case "bottomright": return new Point(wa.Right - size.Width - 12, wa.Bottom - size.Height - 12);
                case "bottomleft": return new Point(wa.Left + 12, wa.Bottom - size.Height - 12);
                default:
                    return new Point(wa.Left + (wa.Width - size.Width) / 2, wa.Top + (wa.Height - size.Height) / 2);
            }
        }

        private void OnShown(object sender, EventArgs e)
        {
            Activate();
            BringToFront();
            if (_s.PlaySound) { try { System.Media.SystemSounds.Exclamation.Play(); } catch (Exception) { } }
        }

        private void OnTick(object sender, EventArgs e)
        {
            _left--;
            if (_left <= 0)
            {
                if (_timer != null) _timer.Stop();
                Close();
                return;
            }
            _lblCountdown.Text = "بستن خودکار پس از " + _left + " ثانیه";
        }

        /// <summary>ثبت موبایل/نام در دیتابیس محلی و محاسبه امتیاز همان فاکتور.</summary>
        private void SaveMobile()
        {
            if (_store == null || _inv == null || _inv.FCode <= 0)
            {
                _lblSaved.ForeColor = Color.Firebrick;
                _lblSaved.Text = "آزمایشی";
                return;
            }

            string typed = _txtMobile.Text.Trim();
            string digits = PhoneUtil.Digits(typed);
            if (digits.Length == 0)
            {
                _lblSaved.ForeColor = Color.Firebrick;
                _lblSaved.Text = "خالی";
                MessageBox.Show(this, "برای ثبت امتیاز، شماره موبایل مشتری را وارد کنید.", "شماره موبایل",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                _txtMobile.Focus();
                return;
            }
            if (!PhoneUtil.IsMobile(PhoneUtil.Normalize(typed)))
            {
                _lblSaved.ForeColor = Color.Firebrick;
                _lblSaved.Text = "نامعتبر";
                MessageBox.Show(this,
                    "شماره موبایل معتبر نیست. نمونه درست: 09123456789\r\n(ارقام فارسی هم پذیرفته می‌شود)",
                    "شماره موبایل", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _txtMobile.Focus();
                return;
            }

            StoredInvoice rec = _store.SetMobile(_inv.FCode, typed, _txtName.Text.Trim(), _s);
            if (rec == null)
            {
                _lblSaved.ForeColor = Color.Firebrick;
                _lblSaved.Text = "خطا";
                MessageBox.Show(this, "ثبت در دیتابیس محلی انجام نشد.\r\n" + _store.LastError,
                    "دیتابیس محلی", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _rec = rec;
            _txtMobile.Text = rec.Mobile;
            DataChanged = true;
            _lblSaved.ForeColor = Color.DarkGreen;
            _lblSaved.Text = "ثبت شد";
            RefreshTotals();
            try { CsvLog.Info(_s, "ثبت موبایل " + rec.Mobile + " برای فاکتور " + rec.FCodeC + " — امتیاز فاکتور: " + rec.Points); }
            catch (Exception) { }
        }

        /// <summary>نمایش امتیاز فاکتور و امتیاز کل شماره واردشده.</summary>
        private void RefreshTotals()
        {
            int pts = PointsOf(_inv.Gross, _inv.Discount);
            _lblPoints.Text = "امتیاز این فاکتور: " + pts + "  ("
                + Num.F(PointsEngine.BaseAmount(_s, _inv.Gross, _inv.Discount)) + " ریال = "
                + Num.F(PointsEngine.BaseAmount(_s, _inv.Gross, _inv.Discount) / PointsEngine.RialPerToman) + " تومان)";

            string digits = PhoneUtil.Digits(_txtMobile.Text);
            if (_store == null || digits.Length == 0)
            {
                _lblTotal.Text = "";
                return;
            }
            try
            {
                System.Collections.Generic.List<PhoneProfile> p = _store.ProfilesByQuery(digits, 1);
                _lblTotal.Text = p.Count == 0 ? "" : (p[0].Points + " امتیاز");
            }
            catch (Exception) { _lblTotal.Text = ""; }
        }

        private void OnClosed(object sender, FormClosedEventArgs e)
        {
            if (_timer != null) { _timer.Stop(); _timer.Dispose(); _timer = null; }
            if (DataChanged) return;

            // اگر کاربر شماره‌ای وارد کرده ولی دکمه ثبت را نزده، همان لحظه بسته شدن ذخیره می‌شود
            if (_store == null || _inv == null || _inv.FCode <= 0) return;
            string typed = _txtMobile.Text.Trim();
            string normalized = PhoneUtil.Normalize(typed);
            if (!PhoneUtil.IsMobile(normalized)) return;
            if (_rec != null && _rec.Mobile == normalized) return;

            try
            {
                StoredInvoice rec = _store.SetMobile(_inv.FCode, normalized, _txtName.Text.Trim(), _s);
                if (rec != null)
                {
                    _rec = rec;
                    DataChanged = true;
                    CsvLog.Info(_s, "ثبت خودکار موبایل " + rec.Mobile + " هنگام بستن پاپ‌آپ — فاکتور " + rec.FCodeC
                        + " — امتیاز فاکتور: " + rec.Points);
                }
            }
            catch (Exception) { }
        }
    }
}
