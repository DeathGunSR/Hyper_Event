using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace PosDiscount
{
    /// <summary>تنظیمات برنامه: اتصال، قواعد تخفیف، پله‌ها و نمایش.</summary>
    public class SettingsForm : Form
    {
        private readonly AppSettings _s;

        private ComboBox _cmbServer, _cmbMode, _cmbCorner, _cmbRoundFavor, _cmbPointsBase;
        private TextBox _txtDb, _txtUser, _txtPass, _txtPercent, _txtFixed, _txtMinGross,
                        _txtMaxPercent, _txtMaxAmount, _txtMaxPerDay, _txtRoundTo, _txtFrom, _txtTo, _txtLog,
                        _txtPointsPer, _txtLocalDb;
        private CheckBox _chkWin, _chkEnabled, _chkRequirePerm, _chkPoints, _chkCapturePopup,
                         _chkSkip, _chkSound, _chkTop, _chkStartMin;
        private NumericUpDown _numTimeout, _numPoll, _numAutoClose;
        private DataGridView _gridTiers;
        private Label _lblConn;

        public SettingsForm(AppSettings s)
        {
            _s = s;
            BuildUi();
            LoadValues();
        }

        private void BuildUi()
        {
            Text = "تنظیمات — تخفیف لحظه‌ای صندوق هولو";
            ClientSize = new Size(620, 528);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            RightToLeft = RightToLeft.Yes;
            RightToLeftLayout = true;
            Font = new Font("Tahoma", 9F);

            TabControl tabs = new TabControl();
            tabs.SetBounds(10, 10, 600, 452);
            tabs.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            tabs.RightToLeftLayout = true;
            tabs.Controls.Add(BuildConnTab());
            tabs.Controls.Add(BuildDiscountTab());
            tabs.Controls.Add(BuildTierTab());
            tabs.Controls.Add(BuildLoyaltyTab());
            tabs.Controls.Add(BuildViewTab());

            Button btnSave = new Button();
            btnSave.Text = "ذخیره";
            btnSave.SetBounds(10, 474, 150, 34);
            btnSave.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            btnSave.Font = new Font("Tahoma", 9F, FontStyle.Bold);
            btnSave.Click += new EventHandler(OnSave);

            Button btnCancel = new Button();
            btnCancel.Text = "انصراف";
            btnCancel.SetBounds(170, 474, 120, 34);
            btnCancel.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            btnCancel.Click += new EventHandler(delegate { DialogResult = DialogResult.Cancel; Close(); });

            Controls.Add(tabs);
            Controls.Add(btnSave);
            Controls.Add(btnCancel);
            AcceptButton = btnSave;
        }

        // ------------------------------------------------------------------
        //  تب اتصال
        // ------------------------------------------------------------------
        private TabPage BuildConnTab()
        {
            TabPage p = NewPage("اتصال دیتابیس");

            p.Controls.Add(Lbl("نام سرور / اینستنس:", 12, 22, 200));
            _cmbServer = new ComboBox();
            _cmbServer.SetBounds(220, 20, 350, 24);
            _cmbServer.DropDownStyle = ComboBoxStyle.DropDown;
            p.Controls.Add(_cmbServer);

            p.Controls.Add(Lbl("نام دیتابیس:", 12, 58, 200));
            _txtDb = new TextBox();
            _txtDb.SetBounds(220, 56, 350, 24);
            p.Controls.Add(_txtDb);

            _chkWin = new CheckBox();
            _chkWin.Text = "اتصال با احراز هویت ویندوز (توصیه‌شده)";
            _chkWin.SetBounds(220, 90, 350, 24);
            _chkWin.CheckedChanged += new EventHandler(delegate
            {
                _txtUser.Enabled = !_chkWin.Checked;
                _txtPass.Enabled = !_chkWin.Checked;
            });
            p.Controls.Add(_chkWin);

            p.Controls.Add(Lbl("کاربر SQL:", 12, 126, 200));
            _txtUser = new TextBox();
            _txtUser.SetBounds(220, 124, 350, 24);
            p.Controls.Add(_txtUser);

            p.Controls.Add(Lbl("رمز SQL:", 12, 162, 200));
            _txtPass = new TextBox();
            _txtPass.SetBounds(220, 160, 350, 24);
            _txtPass.UseSystemPasswordChar = true;
            p.Controls.Add(_txtPass);

            p.Controls.Add(Lbl("مهلت اتصال (ثانیه):", 12, 198, 200));
            _numTimeout = new NumericUpDown();
            _numTimeout.SetBounds(220, 196, 80, 24);
            _numTimeout.Minimum = 2; _numTimeout.Maximum = 60;
            p.Controls.Add(_numTimeout);

            Button btnFind = new Button();
            btnFind.Text = "یافتن سرورهای محلی";
            btnFind.SetBounds(220, 236, 170, 28);
            btnFind.Click += new EventHandler(delegate { FindLocalServers(); });
            p.Controls.Add(btnFind);

            Button btnTest = new Button();
            btnTest.Text = "تست اتصال";
            btnTest.SetBounds(400, 236, 170, 28);
            btnTest.Click += new EventHandler(delegate { TestConnection(); });
            p.Controls.Add(btnTest);

            // اصلاح خودکار: اگر نام سرور خراب باشد (مثل شناسه اینستنس) یا سرور محلی جواب ندهد،
            // اولین اینستنس در دسترس با همان نام دیتابیس را پیدا می‌کند.
            Button btnFix = new Button();
            btnFix.Text = "اصلاح خودکار اتصال";
            btnFix.SetBounds(220, 270, 350, 28);
            btnFix.Font = new Font("Tahoma", 9F, FontStyle.Bold);
            btnFix.Click += new EventHandler(delegate { AutoFixConnection(); });
            p.Controls.Add(btnFix);

            _lblConn = new Label();
            _lblConn.SetBounds(12, 306, 558, 108);
            _lblConn.ForeColor = Color.DimGray;
            p.Controls.Add(_lblConn);

            return p;
        }

        // ------------------------------------------------------------------
        //  تب تخفیف
        // ------------------------------------------------------------------
        private TabPage BuildDiscountTab()
        {
            TabPage p = NewPage("قواعد تخفیف");

            _chkEnabled = new CheckBox();
            _chkEnabled.Text = "محاسبه تخفیف فعال باشد";
            _chkEnabled.SetBounds(220, 12, 350, 24);
            p.Controls.Add(_chkEnabled);

            p.Controls.Add(Lbl("حالت تخفیف:", 12, 48, 200));
            _cmbMode = new ComboBox();
            _cmbMode.SetBounds(220, 46, 350, 24);
            _cmbMode.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbMode.Items.Add("درصدی (درصد ثابت از مبلغ فاکتور)");
            _cmbMode.Items.Add("مبلغ ثابت (ریال)");
            _cmbMode.Items.Add("پله‌ای (بر اساس مبلغ فاکتور — تب «پله‌ها»)");
            p.Controls.Add(_cmbMode);

            p.Controls.Add(Lbl("درصد تخفیف (٪):", 12, 84, 200));
            _txtPercent = Txt(220, 82, 350);
            p.Controls.Add(_txtPercent);

            p.Controls.Add(Lbl("مبلغ ثابت تخفیف (ریال):", 12, 120, 200));
            _txtFixed = Txt(220, 118, 350);
            p.Controls.Add(_txtFixed);

            p.Controls.Add(Lbl("حداقل مبلغ فاکتور برای تخفیف (ریال):", 12, 156, 200));
            _txtMinGross = Txt(220, 154, 350);
            p.Controls.Add(_txtMinGross);

            p.Controls.Add(Lbl("سقف درصدی (٪، ۰=بی‌سقف):", 12, 192, 200));
            _txtMaxPercent = Txt(220, 190, 350);
            p.Controls.Add(_txtMaxPercent);

            p.Controls.Add(Lbl("سقف ریالی فاکتور (۰=بی‌سقف):", 12, 228, 200));
            _txtMaxAmount = Txt(220, 226, 350);
            p.Controls.Add(_txtMaxAmount);

            p.Controls.Add(Lbl("سقف روزانه کاربر (۰=بی‌سقف):", 12, 264, 200));
            _txtMaxPerDay = Txt(220, 262, 350);
            p.Controls.Add(_txtMaxPerDay);

            p.Controls.Add(Lbl("گرد کردن به (ریال، ۰=خاموش):", 12, 300, 200));
            _txtRoundTo = Txt(220, 298, 350);
            p.Controls.Add(_txtRoundTo);

            p.Controls.Add(Lbl("جهت گرد کردن:", 12, 336, 200));
            _cmbRoundFavor = new ComboBox();
            _cmbRoundFavor.SetBounds(220, 334, 350, 24);
            _cmbRoundFavor.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbRoundFavor.Items.Add("به نفع مشتری (رو به پایین)");
            _cmbRoundFavor.Items.Add("به نفع فروشگاه (رو به بالا)");
            p.Controls.Add(_cmbRoundFavor);

            p.Controls.Add(Lbl("بازه مجاز تخفیف (از ‑ تا، مثل 08:00):", 12, 372, 200));
            _txtFrom = Txt(220, 370, 120);
            _txtTo = Txt(350, 370, 120);
            p.Controls.Add(_txtFrom);
            p.Controls.Add(_txtTo);

            _chkRequirePerm = new CheckBox();
            _chkRequirePerm.Text = "فقط برای کاربرانی که در هولو مجوز تخفیف دارند";
            _chkRequirePerm.SetBounds(220, 404, 350, 24);
            p.Controls.Add(_chkRequirePerm);

            Label ro = new Label();
            ro.SetBounds(12, 436, 558, 24);
            ro.ForeColor = Color.FromArgb(0, 110, 60);
            ro.Text = "نکته: این برنامه هیچ‌چیزی در دیتابیس هولو درج یا تغییر نمی‌دهد (فقط قرائت).";
            p.Controls.Add(ro);

            return p;
        }

        // ------------------------------------------------------------------
        //  تب امتیاز مشتریان
        // ------------------------------------------------------------------
        private TabPage BuildLoyaltyTab()
        {
            TabPage p = NewPage("موبایل و امتیاز");

            Label intro = new Label();
            intro.SetBounds(12, 10, 558, 42);
            intro.ForeColor = Color.DimGray;
            intro.Text = "شماره موبایل مشتریان و امتیاز هر فاکتور فقط در دیتابیس محلی برنامه (SQLite) نگهداری می‌شود؛\r\n"
                + "هیچ‌کدام از این اطلاعات در دیتابیس هولو درج نمی‌شود.";
            p.Controls.Add(intro);

            _chkPoints = new CheckBox();
            _chkPoints.Text = "محاسبه و ثبت امتیاز برای فاکتورهای دارای موبایل";
            _chkPoints.SetBounds(220, 60, 350, 24);
            p.Controls.Add(_chkPoints);

            p.Controls.Add(Lbl("هر چند تومان = ۱ امتیاز:", 12, 96, 200));
            _txtPointsPer = Txt(220, 94, 350);   // مبلغ هر امتیاز (تومان)
            p.Controls.Add(_txtPointsPer);

            Label rule = new Label();
            rule.SetBounds(12, 124, 558, 66);
            rule.ForeColor = Color.FromArgb(20, 70, 130);
            rule.Text = "قاعده امتیاز (دقیقاً همان که تعیین شده):\r\n"
                + "• دو فاکتور ۲۵۰ هزار تومانی → ۰ امتیاز   •  ۵۰۰ هزار تومان → ۱ امتیاز\r\n"
                + "• ۷۵۰ هزار تومان → ۱ امتیاز   •  ۱ میلیون تومان → ۲ امتیاز\r\n"
                + "(امتیاز هر فاکتور جداگانه حساب می‌شود و فقط قسمت کامل لحاظ می‌گردد.)";
            p.Controls.Add(rule);

            p.Controls.Add(Lbl("پایه محاسبه امتیاز:", 12, 200, 200));
            _cmbPointsBase = new ComboBox();
            _cmbPointsBase.SetBounds(220, 198, 350, 24);
            _cmbPointsBase.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbPointsBase.Items.Add("جمع کل فاکتور");
            _cmbPointsBase.Items.Add("مبلغ قابل پرداخت (پس از تخفیف ثبت‌شده در هولو)");
            p.Controls.Add(_cmbPointsBase);

            p.Controls.Add(Lbl("فایل دیتابیس محلی:", 12, 236, 200));
            _txtLocalDb = Txt(220, 234, 350);
            p.Controls.Add(_txtLocalDb);

            _chkCapturePopup = new CheckBox();
            _chkCapturePopup.Text = "پاپ‌آپ تخفیف، کادر شماره موبایل مشتری را نشان دهد";
            _chkCapturePopup.SetBounds(220, 268, 350, 24);
            p.Controls.Add(_chkCapturePopup);

            Label note = new Label();
            note.SetBounds(12, 302, 558, 88);
            note.ForeColor = Color.DimGray;
            note.Text = "• پاپ‌آپ فقط برای فاکتورهایی نمایش داده می‌شود که مشمول تخفیف باشند.\r\n"
                + "• برای همه فاکتورها (حتی بدون تخفیف) می‌توانید از پنجره اصلی، دکمه «ثبت موبایل/امتیاز» را بزنید.\r\n"
                + "• جست‌وجوی فاکتورهای هر شماره و فهرست امتیازها از پنجره اصلی انجام می‌شود.\r\n"
                + "• اگر فایل دیتابیس محلی جابه‌جا شود، آدرس جدید را اینجا وارد کنید.";
            p.Controls.Add(note);

            return p;
        }

        // ------------------------------------------------------------------
        //  تب پله‌ها
        // ------------------------------------------------------------------
        private TabPage BuildTierTab()
        {
            TabPage p = NewPage("پله‌ها");

            Label l = new Label();
            l.SetBounds(12, 10, 558, 44);
            l.ForeColor = Color.DimGray;
            l.Text = "برای هر پله «از مبلغ» را وارد کنید و یکی از دو ستون درصد یا مبلغ ثابت را پر کنید.\r\n" +
                     "در حالت پله‌ای، بالاترین پله‌ای که مبلغ فاکتور بزرگ‌تر یا مساوی آن باشد اعمال می‌شود. اگر مبلغ ثابت پر باشد، اولویت دارد.";
            p.Controls.Add(l);

            _gridTiers = new DataGridView();
            _gridTiers.SetBounds(12, 60, 558, 340);
            _gridTiers.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            _gridTiers.AllowUserToAddRows = true;
            _gridTiers.AllowUserToDeleteRows = true;
            _gridTiers.RowHeadersVisible = false;
            _gridTiers.BackgroundColor = Color.White;
            _gridTiers.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _gridTiers.Columns.Add(TierCol("از مبلغ فاکتور (ریال)", 160));
            _gridTiers.Columns.Add(TierCol("درصد تخفیف (٪)", 120));
            _gridTiers.Columns.Add(TierCol("یا مبلغ ثابت (ریال)", 150));
            p.Controls.Add(_gridTiers);

            Label hint = new Label();
            hint.SetBounds(12, 404, 558, 20);
            hint.ForeColor = Color.DimGray;
            hint.Text = "برای حذف یک پله، سطر آن را انتخاب و کلید Delete را بزنید.";
            p.Controls.Add(hint);

            return p;
        }

        private static DataGridViewTextBoxColumn TierCol(string header, int weight)
        {
            DataGridViewTextBoxColumn c = new DataGridViewTextBoxColumn();
            c.HeaderText = header;
            c.FillWeight = weight;
            c.SortMode = DataGridViewColumnSortMode.NotSortable;
            return c;
        }

        // ------------------------------------------------------------------
        //  تب نمایش
        // ------------------------------------------------------------------
        private TabPage BuildViewTab()
        {
            TabPage p = NewPage("نمایش و پلینگ");

            p.Controls.Add(Lbl("فاصله بررسی فاکتور جدید (میلی‌ثانیه):", 12, 22, 220));
            _numPoll = new NumericUpDown();
            _numPoll.SetBounds(240, 20, 100, 24);
            _numPoll.Minimum = 300; _numPoll.Maximum = 60000; _numPoll.Increment = 100;
            p.Controls.Add(_numPoll);

            _chkSkip = new CheckBox();
            _chkSkip.Text = "شروع از آخرین فاکتور (فقط فاکتورهای جدید پاپ‌آپ شوند)";
            _chkSkip.SetBounds(240, 54, 330, 24);
            p.Controls.Add(_chkSkip);

            p.Controls.Add(Lbl("بستن خودکار پاپ‌آپ (ثانیه):", 12, 90, 220));
            _numAutoClose = new NumericUpDown();
            _numAutoClose.SetBounds(240, 88, 100, 24);
            _numAutoClose.Minimum = 0; _numAutoClose.Maximum = 600;
            p.Controls.Add(_numAutoClose);

            _chkSound = new CheckBox();
            _chkSound.Text = "پخش صدای هشدار هنگام نمایش پاپ‌آپ";
            _chkSound.SetBounds(240, 122, 330, 24);
            p.Controls.Add(_chkSound);

            _chkTop = new CheckBox();
            _chkTop.Text = "پاپ‌آپ روی همه پنجره‌ها نمایش داده شود (TopMost)";
            _chkTop.SetBounds(240, 152, 330, 24);
            p.Controls.Add(_chkTop);

            _chkStartMin = new CheckBox();
            _chkStartMin.Text = "شروع برنامه به‌صورت پنهان در سینی سیستم";
            _chkStartMin.SetBounds(240, 182, 330, 24);
            p.Controls.Add(_chkStartMin);

            p.Controls.Add(Lbl("محل نمایش پاپ‌آپ:", 12, 218, 220));
            _cmbCorner = new ComboBox();
            _cmbCorner.SetBounds(240, 216, 330, 24);
            _cmbCorner.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbCorner.Items.Add("وسط صفحه");
            _cmbCorner.Items.Add("بالا - راست");
            _cmbCorner.Items.Add("پایین - راست");
            _cmbCorner.Items.Add("پایین - چپ");
            p.Controls.Add(_cmbCorner);

            p.Controls.Add(Lbl("پوشه لاگ (نسبی یا کامل):", 12, 254, 220));
            _txtLog = Txt(240, 252, 330);
            p.Controls.Add(_txtLog);

            Label note = new Label();
            note.SetBounds(12, 290, 558, 100);
            note.ForeColor = Color.DimGray;
            note.Text = "لاگ روزانه CSV در پوشه لاگ ساخته می‌شود (مبلغ کل، تخفیف، مبلغ نهایی، شماره موبایل و امتیاز هر فاکتور).\r\n" +
                        "فایل تنظیمات در کنار فایل اجرایی برنامه با نام settings.xml ذخیره می‌شود.\r\n" +
                        "پاپ‌آپ فقط برای فاکتورهای مشمول تخفیف نمایش داده می‌شود؛ امکان نمایش برای فاکتورهای بدون تخفیف وجود ندارد.";
            p.Controls.Add(note);

            return p;
        }

        // ------------------------------------------------------------------
        //  کمکی‌های ساخت کنترل
        // ------------------------------------------------------------------
        private static TabPage NewPage(string title)
        {
            TabPage p = new TabPage();
            p.Text = title;
            p.BackColor = Color.White;
            p.RightToLeft = RightToLeft.Yes;
            return p;
        }

        private static Label Lbl(string text, int x, int y, int w)
        {
            Label l = new Label();
            l.Text = text;
            l.SetBounds(x, y, w, 22);
            l.TextAlign = ContentAlignment.MiddleRight;
            return l;
        }

        private static TextBox Txt(int x, int y, int w)
        {
            TextBox t = new TextBox();
            t.SetBounds(x, y, w, 24);
            return t;
        }

        // ------------------------------------------------------------------
        //  مقدار‌دهی اولیه
        // ------------------------------------------------------------------
        private void LoadValues()
        {
            _cmbServer.Text = _s.Server;
            _txtDb.Text = _s.Database;
            _chkWin.Checked = _s.UseWindowsAuth;
            _txtUser.Text = _s.SqlUser;
            _txtPass.Text = _s.SqlPassword;
            _txtUser.Enabled = !_s.UseWindowsAuth;
            _txtPass.Enabled = !_s.UseWindowsAuth;
            _numTimeout.Value = ClampNum(_s.ConnectTimeoutSeconds, 2, 60);

            _chkEnabled.Checked = _s.DiscountEnabled;
            _cmbMode.SelectedIndex = _s.DiscountMode == "fixed" ? 1 : (_s.DiscountMode == "tiered" ? 2 : 0);
            _txtPercent.Text = Num.P(_s.Percent);
            _txtFixed.Text = Num.F(_s.FixedAmount);
            _txtMinGross.Text = Num.F(_s.MinInvoiceAmount);
            _txtMaxPercent.Text = Num.P(_s.MaxPercent);
            _txtMaxAmount.Text = Num.F(_s.MaxAmount);
            _txtMaxPerDay.Text = Num.F(_s.MaxDiscountPerDay);
            _txtRoundTo.Text = Num.F(_s.RoundTo);
            _cmbRoundFavor.SelectedIndex = _s.RoundInFavorOfCustomer ? 0 : 1;
            _txtFrom.Text = _s.StartTime;
            _txtTo.Text = _s.EndTime;
            _chkRequirePerm.Checked = _s.RequireDiscountPermission;

            _chkPoints.Checked = _s.PointsEnabled;
            _txtPointsPer.Text = Num.F(_s.PointsPerAmountToman);
            _cmbPointsBase.SelectedIndex = _s.PointsBase == "net" ? 1 : 0;
            _txtLocalDb.Text = _s.LocalDbFile;
            _chkCapturePopup.Checked = _s.CaptureMobileInPopup;

            _gridTiers.Rows.Clear();
            for (int i = 0; i < _s.Tiers.Count; i++)
            {
                DiscountTier t = _s.Tiers[i];
                _gridTiers.Rows.Add(
                    t.MinAmount > 0 ? Num.F(t.MinAmount) : "",
                    t.Percent > 0 ? Num.P(t.Percent) : "",
                    t.Amount > 0 ? Num.F(t.Amount) : "");
            }

            _numPoll.Value = ClampNum(_s.PollIntervalMs, 300, 60000);
            _chkSkip.Checked = _s.SkipExistingOnStart;
            _numAutoClose.Value = ClampNum(_s.AutoCloseSeconds, 0, 600);
            _chkSound.Checked = _s.PlaySound;
            _chkTop.Checked = _s.AlwaysOnTop;
            _chkStartMin.Checked = _s.StartMinimizedToTray;
            switch (_s.PopupCorner)
            {
                case "topright": _cmbCorner.SelectedIndex = 1; break;
                case "bottomright": _cmbCorner.SelectedIndex = 2; break;
                case "bottomleft": _cmbCorner.SelectedIndex = 3; break;
                default: _cmbCorner.SelectedIndex = 0; break;
            }
            _txtLog.Text = _s.LogFolder;
        }

        private static decimal ClampNum(int v, int min, int max)
        {
            if (v < min) v = min;
            if (v > max) v = max;
            return v;
        }

        // ------------------------------------------------------------------
        //  ذخیره
        // ------------------------------------------------------------------
        private void OnSave(object sender, EventArgs e)
        {
            List<string> err = new List<string>();
            string okServer = _cmbServer.Text.Trim();
            if (okServer.Length == 0) err.Add("نام سرور خالی است.");
            if (_txtDb.Text.Trim().Length == 0) err.Add("نام دیتابیس خالی است.");
            if (!_chkWin.Checked && _txtUser.Text.Trim().Length == 0) err.Add("کاربر SQL برای اتصال غیرویندوزی لازم است.");

            double percent = Check(_txtPercent, "درصد تخفیف", err);
            double fixedAmt = Check(_txtFixed, "مبلغ ثابت تخفیف", err);
            double minGross = Check(_txtMinGross, "حداقل مبلغ فاکتور", err);
            double maxPercent = Check(_txtMaxPercent, "سقف درصدی", err);
            double maxAmount = Check(_txtMaxAmount, "سقف ریالی", err);
            double maxPerDay = Check(_txtMaxPerDay, "سقف تخفیف روزانه", err);
            double roundTo = Check(_txtRoundTo, "گرد کردن", err);
            double pointsPer = Check(_txtPointsPer, "مبلغ هر امتیاز (تومان)", err);
            if (pointsPer <= 0) { err.Add("مبلغ هر امتیاز (تومان) باید بزرگ‌تر از صفر باشد."); pointsPer = 500000; }
            _txtFrom.Text = _txtFrom.Text.Trim();
            _txtTo.Text = _txtTo.Text.Trim();

            TimeSpan ts;
            if (_txtFrom.Text.Length > 0 && !Num.TryParseHHmm(_txtFrom.Text, out ts)) err.Add("ساعت شروع بازه مجاز درست نیست (مثال: 08:00).");
            if (_txtTo.Text.Length > 0 && !Num.TryParseHHmm(_txtTo.Text, out ts)) err.Add("ساعت پایان بازه مجاز درست نیست (مثال: 23:59).");

            List<DiscountTier> tiers = new List<DiscountTier>();
            for (int i = 0; i < _gridTiers.Rows.Count; i++)
            {
                DataGridViewRow row = _gridTiers.Rows[i];
                if (row.IsNewRow) continue;
                string sMin = CellText(row, 0), sPct = CellText(row, 1), sAmt = CellText(row, 2);
                if (sMin.Length == 0 && sPct.Length == 0 && sAmt.Length == 0) continue;
                double tMin = Num.ParseD(sMin, -1);
                double tPct = Num.ParseD(sPct, 0);
                double tAmt = Num.ParseD(sAmt, 0);
                if (tMin < 0) { err.Add("سطر " + (i + 1) + " جدول پله‌ها: «از مبلغ» نامعتبر است."); continue; }
                if (tPct <= 0 && tAmt <= 0) { err.Add("سطر " + (i + 1) + " جدول پله‌ها: درصد یا مبلغ ثابت را وارد کنید."); continue; }
                tiers.Add(new DiscountTier(tMin, tPct, tAmt));
            }

            if (_cmbMode.SelectedIndex == 2 && tiers.Count == 0)
                err.Add("حالت پله‌ای انتخاب شده ولی هیچ پله‌ای تعریف نشده است.");

            if (err.Count > 0)
            {
                MessageBox.Show(this, string.Join("\r\n", err.ToArray()), "خطا در تنظیمات",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // محافظ پیش از ذخیره: یک «نام سرور» بی‌آزمون در فایل تنظیمات نوشته نمی‌شود
            // (رایج‌ترین اشتباه: نوشتن شناسه اینستنس مثل MSSQL17.SQLEXPRESS به‌جای نام SQLEXPRESS).
            string probeMsg;
            if (!ConnectionFixer.Probe(CurrentFormSettings(), okServer, out probeMsg))
            {
                DialogResult dr = MessageBox.Show(this,
                    "با این تنظیمات اتصال به دیتابیس برقرار نشد:\r\n\r\n" + probeMsg
                    + "\r\n\r\nسرور: " + okServer + "  |  دیتابیس: " + _txtDb.Text.Trim()
                    + "\r\n\r\nپیشنهاد: دکمه «اصلاح خودکار اتصال» را بزنید.\r\nبا این حال ذخیره شود؟",
                    "اتصال برقرار نشد", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (dr != DialogResult.Yes) return;
            }

            _s.Server = okServer;
            _s.Database = _txtDb.Text.Trim();
            _s.UseWindowsAuth = _chkWin.Checked;
            _s.SqlUser = _txtUser.Text.Trim();
            _s.SqlPassword = _txtPass.Text;
            _s.ConnectTimeoutSeconds = (int)_numTimeout.Value;

            _s.DiscountEnabled = _chkEnabled.Checked;
            _s.DiscountMode = _cmbMode.SelectedIndex == 1 ? "fixed" : (_cmbMode.SelectedIndex == 2 ? "tiered" : "percent");
            _s.Percent = percent;
            _s.FixedAmount = fixedAmt;
            _s.MinInvoiceAmount = minGross;
            _s.MaxPercent = maxPercent;
            _s.MaxAmount = maxAmount;
            _s.MaxDiscountPerDay = maxPerDay;
            _s.RoundTo = roundTo;
            _s.RoundInFavorOfCustomer = _cmbRoundFavor.SelectedIndex == 0;
            _s.StartTime = _txtFrom.Text;
            _s.EndTime = _txtTo.Text;
            _s.RequireDiscountPermission = _chkRequirePerm.Checked;
            _s.Tiers = tiers;

            _s.PointsEnabled = _chkPoints.Checked;
            _s.PointsPerAmountToman = pointsPer;
            _s.PointsBase = _cmbPointsBase.SelectedIndex == 1 ? "net" : "gross";
            _s.LocalDbFile = _txtLocalDb.Text.Trim().Length == 0 ? "pos-discount.db" : _txtLocalDb.Text.Trim();
            _s.CaptureMobileInPopup = _chkCapturePopup.Checked;

            _s.PollIntervalMs = (int)_numPoll.Value;
            _s.SkipExistingOnStart = _chkSkip.Checked;
            _s.AutoCloseSeconds = (int)_numAutoClose.Value;
            _s.PlaySound = _chkSound.Checked;
            _s.AlwaysOnTop = _chkTop.Checked;
            _s.StartMinimizedToTray = _chkStartMin.Checked;
            _s.PopupCorner = _cmbCorner.SelectedIndex == 1 ? "topright"
                : (_cmbCorner.SelectedIndex == 2 ? "bottomright" : (_cmbCorner.SelectedIndex == 3 ? "bottomleft" : "center"));
            _s.LogFolder = _txtLog.Text.Trim().Length == 0 ? "logs" : _txtLog.Text.Trim();

            DialogResult = DialogResult.OK;
            Close();
        }

        private static string CellText(DataGridViewRow row, int index)
        {
            object v = row.Cells[index].Value;
            return v == null ? "" : Convert.ToString(v).Trim();
        }

        private static double Check(TextBox t, string name, List<string> err)
        {
            string s = t.Text.Trim();
            if (s.Length == 0) { err.Add(name + " خالی است."); return 0; }
            string norm = Num.NormalizeDigits(s);
            double v;
            if (!double.TryParse(norm, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out v))
            {
                err.Add(name + " عدد معتبر نیست: " + s);
                return 0;
            }
            if (v < 0) { err.Add(name + " نمی‌تواند منفی باشد."); return 0; }
            return v;
        }

        // ------------------------------------------------------------------
        //  ابزارها
        // ------------------------------------------------------------------
        /// <summary>مقادیر فعلی تب اتصال به‌عنوان تنظیمات موقت (برای تست/جست‌وجو — چیزی ذخیره نمی‌شود).</summary>
        private AppSettings CurrentFormSettings()
        {
            AppSettings tmp = _s.Clone();
            tmp.Server = _cmbServer.Text.Trim();
            tmp.Database = _txtDb.Text.Trim();
            tmp.UseWindowsAuth = _chkWin.Checked;
            tmp.SqlUser = _txtUser.Text.Trim();
            tmp.SqlPassword = _txtPass.Text;
            tmp.ConnectTimeoutSeconds = (int)_numTimeout.Value;
            return tmp;
        }

        /// <summary>اولین اینستنس در دسترس را پیدا و در فیلد سرور می‌گذارد (کاربر باید «ذخیره» را بزند).</summary>
        private void AutoFixConnection()
        {
            string current = _cmbServer.Text.Trim();
            AppSettings tmp = CurrentFormSettings();
            UseWaitCursor = true;
            try
            {
                string sanitized = ConnectionFixer.Sanitize(current, ConnectionFixer.InstanceIdToName());
                StringBuilder head = new StringBuilder();
                if (!string.Equals(sanitized, current, StringComparison.Ordinal))
                    head.Append("«" + current + "» یک «شناسه اینستنس» بود (نه نام اینستنس) و به «" + sanitized + "» تبدیل شد.\r\n");

                List<string> candidates = ConnectionFixer.CandidateServers(sanitized);
                for (int i = 0; i < candidates.Count; i++)
                {
                    string m;
                    if (candidates[i].Length == 0) continue;
                    if (ConnectionFixer.Probe(tmp, candidates[i], out m))
                    {
                        _cmbServer.Text = candidates[i];
                        _lblConn.ForeColor = Color.FromArgb(0, 110, 60);
                        _lblConn.Text = head.ToString() + "سرور «" + candidates[i] + "» با دیتابیس «"
                            + tmp.Database + "» پاسخ داد و در فیلد سرور گذاشته شد.\r\n" + m
                            + "\r\nبرای اعمال، دکمه «ذخیره» را بزنید.";
                        return;
                    }
                }

                _lblConn.ForeColor = Color.Firebrick;
                _lblConn.Text = head.ToString() + "هیچ اینستنس محلی‌ای به دیتابیس «" + tmp.Database + "» دسترسی نداشت.\r\n"
                    + "اگر دیتابیس هولو روی رایانه دیگری است، آدرس آن را دستی وارد کنید (مثلاً 192.168.1.10 یا نام‌سرور\\اینستنس).";
            }
            catch (Exception ex)
            {
                _lblConn.ForeColor = Color.Firebrick;
                _lblConn.Text = "خطا در اصلاح اتصال: " + ex.Message;
            }
            finally
            {
                UseWaitCursor = false;
            }
        }

        private void TestConnection()
        {
            UseWaitCursor = true;
            try
            {
                AppSettings tmp = CurrentFormSettings();

                HolooDb db = new HolooDb(tmp.BuildConnectionString());
                DbStatus st = db.Test();
                if (st.Ok)
                {
                    _lblConn.ForeColor = Color.FromArgb(0, 110, 60);
                    _lblConn.Text = "موفق: " + st.Message
                        + "\r\nکاربران: " + st.Users + " | فاکتورهای جدول جاری: " + st.Invoices
                        + (st.Day.HasValue ? "\r\nروز کاری دیتابیس: " + st.JalaliDay + " (" + FmtDate.G(st.Day.Value) + ")" : "");
                }
                else
                {
                    _lblConn.ForeColor = Color.Firebrick;
                    _lblConn.Text = st.Message;
                }
            }
            catch (Exception ex)
            {
                _lblConn.ForeColor = Color.Firebrick;
                _lblConn.Text = HolooDb.DescribeConnectionError(ex);
            }
            finally
            {
                UseWaitCursor = false;
            }
        }

        /// <summary>
        /// اینستنس‌های SQL Server روی همین رایانه را از رجیستری می‌خواند و هرکدام را با همان نام دیتابیس
        /// واقعاً آزمایش می‌کند (✔ = پاسخ داد).
        /// نکته مهم: در رجیستری، *نام مقدار* همان «نام اینستنس» (SQLEXPRESS) و *مقدار* همان «شناسه اینستنس»
        /// (MSSQL17.SQLEXPRESS) است؛ استفاده اشتباهی از شناسه، خطای 26 «سرور پیدا نشد» می‌دهد.
        /// مقدار فعلی فیلد سرور هرگز خودکار عوض نمی‌شود.
        /// </summary>
        private void FindLocalServers()
        {
            string current = _cmbServer.Text.Trim();
            AppSettings tmp = CurrentFormSettings();
            UseWaitCursor = true;
            try
            {
                List<string> found = ConnectionFixer.CandidateServers(current);
                List<string> okList = new List<string>();
                StringBuilder tb = new StringBuilder();
                for (int i = 0; i < found.Count; i++)
                {
                    string m;
                    bool ok = ConnectionFixer.Probe(tmp, found[i], out m);
                    if (ok) okList.Add(found[i]);
                    tb.AppendLine((ok ? "✔ " : "✖ ") + found[i] + (ok ? "" : "  — " + OneLine(m)));
                }

                _cmbServer.Items.Clear();
                for (int i = 0; i < found.Count; i++) _cmbServer.Items.Add(found[i]);
                _cmbServer.Text = current.Length > 0 ? current : (okList.Count > 0 ? okList[0] : found[0]);

                _lblConn.ForeColor = okList.Count > 0 ? Color.FromArgb(0, 110, 60) : Color.Firebrick;
                _lblConn.Text = "اینستنس‌های SQL Server روی این رایانه (✔ = دیتابیس «" + tmp.Database
                    + "» با این نام باز می‌شود):\r\n" + tb.ToString().TrimEnd();
                if (okList.Count > 0)
                    _lblConn.Text += "\r\nپیشنهاد: " + okList[0] + "  (دکمه «اصلاح خودکار اتصال» همین را می‌گذارد)";
            }
            catch (Exception ex)
            {
                _lblConn.ForeColor = Color.Firebrick;
                _lblConn.Text = "خطا در یافتن سرورها: " + ex.Message;
            }
            finally
            {
                UseWaitCursor = false;
            }
        }

        private static string OneLine(string text)
        {
            if (text == null) return "";
            string t = text.Replace("\r", " ").Replace("\n", " ").Trim();
            while (t.IndexOf("  ") >= 0) t = t.Replace("  ", " ");
            return t.Length > 120 ? t.Substring(0, 120) + "…" : t;
        }
    }
}
