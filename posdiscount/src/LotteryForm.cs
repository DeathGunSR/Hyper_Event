using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace PosDiscount
{
    /// <summary>
    /// قرعه‌کشی بین مشتریان دیتابیس محلی.
    /// قاعده: **هر ۱ امتیاز = ۱ شانس**.
    ///
    /// نوع‌های قرعه‌کشی (کاربر انتخاب می‌کند):
    ///  ۱) **ساده**: تعداد مشخصی برنده، به ترتیب انتخاب لیست می‌شوند.
    ///  ۲) **پله‌ای/چند جایزه**: برای هر جایزه تعداد برنده جداگانه؛ برندگان همه جایزه‌ها
    ///     از یک سبد مشترک و بدون تکرار انتخاب می‌شوند (جایزه اول اول، بعد جایزه دوم و ...).
    ///
    /// دامنه شرکت‌کنندگان هم قابل محدود کردن است: بازه تاریخ، صندوقدار خاص و حداقل تعداد فاکتور.
    /// نمایش نتیجه می‌تواند **مرحله‌به‌مرحله** باشد (برنده‌ها یکی‌یکی با فاصله زمانی ظاهر می‌شوند).
    /// نتیجه با همان seed عیناً بازتولید می‌شود و هر قرعه‌کشی خودکار در دیتابیس محلی ذخیره می‌شود.
    /// </summary>
    public class LotteryForm : Form
    {
        private sealed class ComboItem
        {
            public string Text;
            public int Value;
            public ComboItem(string text, int value) { Text = text; Value = value; }
            public override string ToString() { return Text; }
        }

        private readonly AppSettings _s;
        private readonly LocalStore _store;

        private ComboBox _cmbType, _cmbRange, _cmbCashier;
        private Panel _pnlSimple, _pnlPrizes;
        private NumericUpDown _numWinners, _numMinPoints, _numMinInvoices, _numSpeed;
        private DataGridView _gridPrizes;
        private DateTimePicker _dtFrom, _dtTo;
        private TextBox _txtSeed;
        private CheckBox _chkStep;
        private DataGridView _grid;
        private Label _lblCount, _lblLive, _lblSum, _lblMsg;
        private Button _btnCopy, _btnCsv, _btnPause;
        private System.Windows.Forms.Timer _reveal;

        private List<PhoneProfile> _people = new List<PhoneProfile>();
        private LotteryResult _last;
        private int _lastDrawId;
        private int _revealIndex;

        public LotteryForm(AppSettings s, LocalStore store)
        {
            _s = s;
            _store = store;
            BuildUi();
            LoadCashiers();
            UpdateCount();
        }

        // ==================================================================
        //  رابط کاربری
        // ==================================================================
        private void BuildUi()
        {
            Text = "قرعه‌کشی بین شرکت‌کنندگان — هر ۱ امتیاز = ۱ شانس";
            ClientSize = new Size(900, 664);
            MinimumSize = new Size(880, 600);
            StartPosition = FormStartPosition.CenterParent;
            RightToLeft = RightToLeft.Yes;
            RightToLeftLayout = true;
            Font = new Font("Tahoma", 9F);

            Panel head = new Panel();
            head.SetBounds(0, 0, 900, 46);
            head.BackColor = Color.FromArgb(30, 62, 104);
            head.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            Label lblHead = new Label();
            lblHead.Text = "قرعه‌کشی بین شرکت‌کنندگان";
            lblHead.ForeColor = Color.White;
            lblHead.Font = new Font("Tahoma", 12F, FontStyle.Bold);
            lblHead.SetBounds(12, 8, 500, 26);
            lblHead.TextAlign = ContentAlignment.MiddleRight;
            head.Controls.Add(lblHead);

            Label lblHead2 = new Label();
            lblHead2.Text = "هر ۱ امتیاز = ۱ شانس";
            lblHead2.ForeColor = Color.FromArgb(214, 228, 246);
            lblHead2.SetBounds(520, 10, 366, 24);
            lblHead2.TextAlign = ContentAlignment.MiddleLeft;
            head.Controls.Add(lblHead2);

            // ---- نوع قرعه‌کشی ----
            Label lblType = MkLabel("نوع قرعه‌کشی:", 12, 58, 200);
            _cmbType = new ComboBox();
            _cmbType.SetBounds(220, 56, 420, 24);
            _cmbType.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbType.Items.Add("۱- ساده: تعداد مشخصی برنده، به ترتیب انتخاب");
            _cmbType.Items.Add("۲- پله‌ای/چند جایزه: هر جایزه تعداد برنده جداگانه");
            _cmbType.SelectedIndex = 0;

            // ---- حالت ساده ----
            _pnlSimple = new Panel();
            _pnlSimple.SetBounds(0, 84, 900, 34);
            _pnlSimple.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _pnlSimple.Controls.Add(MkLabel("تعداد برندگان:", 12, 6, 200));
            _numWinners = new NumericUpDown();
            _numWinners.SetBounds(220, 4, 80, 24);
            _numWinners.Minimum = 1;
            _numWinners.Maximum = 1000;
            _numWinners.Value = 3;
            _pnlSimple.Controls.Add(_numWinners);
            _pnlSimple.Controls.Add(MkHint("برندگان به ترتیب انتخاب لیست می‌شوند؛ هر نفر فقط یک بار برنده می‌شود.", 312, 6, 568));

            // ---- حالت پله‌ای (جدول جایزه‌ها) ----
            _pnlPrizes = new Panel();
            _pnlPrizes.SetBounds(0, 84, 900, 92);
            _pnlPrizes.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _pnlPrizes.Visible = false;
            _pnlPrizes.Controls.Add(MkLabel("جایزه‌ها:", 12, 8, 200));
            _gridPrizes = new DataGridView();
            _gridPrizes.SetBounds(180, 4, 460, 84);
            _gridPrizes.AllowUserToAddRows = true;
            _gridPrizes.AllowUserToDeleteRows = true;
            _gridPrizes.AllowUserToResizeRows = false;
            _gridPrizes.RowHeadersVisible = false;
            _gridPrizes.MultiSelect = false;
            _gridPrizes.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _gridPrizes.BackgroundColor = Color.White;
            _gridPrizes.RowTemplate.Height = 22;
            DataGridViewTextBoxColumn cTitle = new DataGridViewTextBoxColumn();
            cTitle.HeaderText = "عنوان جایزه";
            cTitle.FillWeight = 150;
            DataGridViewTextBoxColumn cCount = new DataGridViewTextBoxColumn();
            cCount.HeaderText = "تعداد برنده";
            cCount.FillWeight = 70;
            cCount.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            _gridPrizes.Columns.Add(cTitle);
            _gridPrizes.Columns.Add(cCount);
            _gridPrizes.Rows.Add("جایزه اول", "1");
            _gridPrizes.Rows.Add("جایزه دوم", "3");
            _gridPrizes.Rows.Add("جایزه سوم", "5");
            _pnlPrizes.Controls.Add(_gridPrizes);
            _pnlPrizes.Controls.Add(MkHint("برندگان همه جایزه‌ها از یک سبد مشترک و بدون تکرار انتخاب می‌شوند (جایزه اول اول).", 648, 8, 240, 64));

            _cmbType.SelectedIndexChanged += new EventHandler(delegate
            {
                bool simple = _cmbType.SelectedIndex == 0;
                _pnlSimple.Visible = simple;
                _pnlPrizes.Visible = !simple;
            });

            // ---- حداقل امتیاز ----
            Label lblMin = MkLabel("حداقل امتیاز برای شرکت:", 12, 188, 200);
            _numMinPoints = new NumericUpDown();
            _numMinPoints.SetBounds(220, 186, 80, 24);
            _numMinPoints.Minimum = 1;
            _numMinPoints.Maximum = 1000000;
            _numMinPoints.Value = 1;
            _numMinPoints.ValueChanged += new EventHandler(delegate { UpdateCount(); });
            Label lblMinHint = MkHint("مثلاً ۲ یعنی کسانی که کمتر از ۲ امتیاز دارند شرکت نمی‌کنند.", 312, 188, 568);

            // ---- دامنه: بازه فاکتورها ----
            Label lblRange = MkLabel("بازه فاکتورها:", 12, 222, 200);
            _cmbRange = new ComboBox();
            _cmbRange.SetBounds(220, 220, 170, 24);
            _cmbRange.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbRange.Items.Add("همه تاریخ‌ها");
            _cmbRange.Items.Add("امروز");
            _cmbRange.Items.Add("۷ روز اخیر");
            _cmbRange.Items.Add("۳۰ روز اخیر");
            _cmbRange.Items.Add("سفارشی (دستی)");
            _cmbRange.SelectedIndex = 0;

            _dtFrom = new DateTimePicker();
            _dtFrom.SetBounds(400, 220, 115, 24);
            _dtFrom.Format = DateTimePickerFormat.Short;
            _dtFrom.Value = DateTime.Today.AddMonths(-1);
            _dtFrom.Enabled = false;
            _dtFrom.ValueChanged += new EventHandler(delegate { UpdateCount(); });

            Label lblTo = new Label();
            lblTo.Text = "تا";
            lblTo.SetBounds(518, 222, 26, 24);
            lblTo.TextAlign = ContentAlignment.MiddleCenter;

            _dtTo = new DateTimePicker();
            _dtTo.SetBounds(548, 220, 115, 24);
            _dtTo.Format = DateTimePickerFormat.Short;
            _dtTo.Value = DateTime.Today.AddDays(1);
            _dtTo.Enabled = false;
            _dtTo.ValueChanged += new EventHandler(delegate { UpdateCount(); });

            Label lblRangeHint = MkHint("از فاکتورهای همین بازه.", 671, 222, 217);

            _cmbRange.SelectedIndexChanged += new EventHandler(delegate
            {
                bool custom = _cmbRange.SelectedIndex == 4;
                _dtFrom.Enabled = custom;
                _dtTo.Enabled = custom;
                UpdateCount();
            });

            // ---- دامنه: صندوقدار و حداقل تعداد فاکتور ----
            Label lblCashier = MkLabel("صندوقدار:", 12, 256, 200);
            _cmbCashier = new ComboBox();
            _cmbCashier.SetBounds(220, 254, 220, 24);
            _cmbCashier.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbCashier.SelectedIndexChanged += new EventHandler(delegate { UpdateCount(); });

            Label lblMinInv = MkLabel("حداقل تعداد فاکتور:", 455, 256, 150);
            _numMinInvoices = new NumericUpDown();
            _numMinInvoices.SetBounds(610, 254, 70, 24);
            _numMinInvoices.Minimum = 1;
            _numMinInvoices.Maximum = 1000;
            _numMinInvoices.Value = 1;
            _numMinInvoices.ValueChanged += new EventHandler(delegate { UpdateCount(); });
            Label lblMinInvHint = MkHint("۱ = همه مشتریان", 690, 256, 198);

            // ---- seed ----
            Label lblSeed = MkLabel("seed (بذر تصادفی):", 12, 290, 200);
            _txtSeed = new TextBox();
            _txtSeed.SetBounds(220, 288, 140, 24);
            _txtSeed.TextAlign = HorizontalAlignment.Center;
            _txtSeed.KeyPress += new KeyPressEventHandler(delegate (object o, KeyPressEventArgs e)
            {
                if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true;
            });
            Label lblSeedHint = MkHint("خالی = تصادفی | همان seed = عیناً همان نتیجه (قابل بازتولید و مستندسازی)", 370, 290, 518);

            // ---- دکمه‌های قرعه‌کشی + نمایش مرحله‌به‌مرحله ----
            Button btnDraw = new Button();
            btnDraw.Text = "قرعه‌کشی";
            btnDraw.SetBounds(220, 320, 150, 32);
            btnDraw.Font = new Font("Tahoma", 10F, FontStyle.Bold);
            btnDraw.Click += new EventHandler(delegate { RunDraw(false); });

            Button btnNewSeed = new Button();
            btnNewSeed.Text = "seed جدید + قرعه‌کشی";
            btnNewSeed.SetBounds(378, 320, 170, 32);
            btnNewSeed.Click += new EventHandler(delegate { RunDraw(true); });

            _chkStep = new CheckBox();
            _chkStep.Text = "مرحله‌به‌مرحله";
            _chkStep.SetBounds(556, 324, 110, 24);
            _chkStep.Checked = false;

            Label lblSpeed = MkLabel("فاصله (ثانیه):", 666, 324, 80, false);
            _numSpeed = new NumericUpDown();
            _numSpeed.SetBounds(748, 320, 50, 24);
            _numSpeed.Minimum = 1;
            _numSpeed.Maximum = 30;
            _numSpeed.Value = 3;

            _btnPause = new Button();
            _btnPause.Text = "توقف";
            _btnPause.SetBounds(806, 320, 82, 32);
            _btnPause.Enabled = false;
            _btnPause.Click += new EventHandler(delegate { TogglePause(); });

            _reveal = new System.Windows.Forms.Timer();
            _reveal.Interval = 3000;
            _reveal.Tick += new EventHandler(delegate { RevealNext(); });
            FormClosing += new FormClosingEventHandler(delegate
            {
                try { _reveal.Stop(); _reveal.Dispose(); } catch (Exception) { }
            });

            _lblCount = new Label();
            _lblCount.SetBounds(12, 366, 200, 24);
            _lblCount.TextAlign = ContentAlignment.MiddleRight;
            _lblCount.ForeColor = Color.DimGray;

            _lblLive = new Label();
            _lblLive.SetBounds(220, 360, 668, 30);
            _lblLive.Font = new Font("Tahoma", 11F, FontStyle.Bold);
            _lblLive.ForeColor = Color.FromArgb(30, 62, 104);
            _lblLive.TextAlign = ContentAlignment.MiddleRight;
            _lblLive.Text = "آماده برای قرعه‌کشی";

            // ---- جدول نتیجه ----
            _grid = new DataGridView();
            _grid.SetBounds(12, 400, 876, 136);
            _grid.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            _grid.ReadOnly = true;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.AllowUserToResizeRows = false;
            _grid.RowHeadersVisible = false;
            _grid.MultiSelect = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _grid.BackgroundColor = Color.White;
            _grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(247, 248, 250);
            _grid.Columns.Add(Col("رتبه", 28));
            _grid.Columns.Add(Col("جایزه", 80));
            _grid.Columns.Add(Col("شماره موبایل", 95));
            _grid.Columns.Add(Col("نام", 105));
            _grid.Columns.Add(Col("امتیاز (شانس)", 70));
            _grid.Columns.Add(Col("شانس", 70));
            _grid.Columns.Add(Col("شانس در لحظه انتخاب", 90));
            _grid.Columns.Add(Col("تعداد فاکتور", 70));
            _grid.Columns.Add(Col("جمع خرید (ریال)", 110));
            _grid.CellDoubleClick += new DataGridViewCellEventHandler(delegate (object o, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex < 0 || _store == null) return;
                string mobile = Convert.ToString(_grid.Rows[e.RowIndex].Cells[2].Value);
                using (CustomerInvoicesForm f = new CustomerInvoicesForm(_s, _store, mobile)) f.ShowDialog(this);
            });

            _lblSum = new Label();
            _lblSum.SetBounds(12, 544, 876, 22);
            _lblSum.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            _lblSum.Font = new Font("Tahoma", 9F, FontStyle.Bold);
            _lblSum.ForeColor = Color.FromArgb(30, 62, 104);
            _lblSum.TextAlign = ContentAlignment.MiddleRight;

            _lblMsg = new Label();
            _lblMsg.SetBounds(12, 570, 876, 44);
            _lblMsg.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            _lblMsg.ForeColor = Color.DimGray;
            _lblMsg.TextAlign = ContentAlignment.TopRight;
            _lblMsg.Text = "هر قرعه‌کشی با seed، جایزه‌ها و فهرست برندگانش در دیتابیس محلی ذخیره می‌شود.\r\n"
                + "برای بازتولید عین همان نتیجه، همان seed را وارد کنید و «قرعه‌کشی» را بزنید.";

            _btnCopy = new Button();
            _btnCopy.Text = "کپی فهرست برندگان";
            _btnCopy.SetBounds(12, 622, 180, 30);
            _btnCopy.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            _btnCopy.Enabled = false;
            _btnCopy.Click += new EventHandler(delegate { CopyWinners(); });

            _btnCsv = new Button();
            _btnCsv.Text = "خروجی CSV";
            _btnCsv.SetBounds(200, 622, 150, 30);
            _btnCsv.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            _btnCsv.Enabled = false;
            _btnCsv.Click += new EventHandler(delegate { ExportCsv(); });

            Button btnClear = new Button();
            btnClear.Text = "پاک کردن نتیجه";
            btnClear.SetBounds(358, 622, 150, 30);
            btnClear.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            btnClear.Click += new EventHandler(delegate { ClearResult(); });

            Button btnClose = new Button();
            btnClose.Text = "بستن";
            btnClose.SetBounds(738, 622, 150, 30);
            btnClose.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            btnClose.Click += new EventHandler(delegate { Close(); });

            Controls.Add(head);
            Controls.Add(lblType);
            Controls.Add(_cmbType);
            Controls.Add(_pnlSimple);
            Controls.Add(_pnlPrizes);
            Controls.Add(lblMin);
            Controls.Add(_numMinPoints);
            Controls.Add(lblMinHint);
            Controls.Add(lblRange);
            Controls.Add(_cmbRange);
            Controls.Add(_dtFrom);
            Controls.Add(lblTo);
            Controls.Add(_dtTo);
            Controls.Add(lblRangeHint);
            Controls.Add(lblCashier);
            Controls.Add(_cmbCashier);
            Controls.Add(lblMinInv);
            Controls.Add(_numMinInvoices);
            Controls.Add(lblMinInvHint);
            Controls.Add(lblSeed);
            Controls.Add(_txtSeed);
            Controls.Add(lblSeedHint);
            Controls.Add(btnDraw);
            Controls.Add(btnNewSeed);
            Controls.Add(_chkStep);
            Controls.Add(lblSpeed);
            Controls.Add(_numSpeed);
            Controls.Add(_btnPause);
            Controls.Add(_lblCount);
            Controls.Add(_lblLive);
            Controls.Add(_grid);
            Controls.Add(_lblSum);
            Controls.Add(_lblMsg);
            Controls.Add(_btnCopy);
            Controls.Add(_btnCsv);
            Controls.Add(btnClear);
            Controls.Add(btnClose);
        }

        private static Label MkLabel(string text, int x, int y, int w)
        {
            return MkLabel(text, x, y, w, true);
        }

        private static Label MkLabel(string text, int x, int y, int w, bool rightAlign)
        {
            Label l = new Label();
            l.Text = text;
            l.SetBounds(x, y, w, 24);
            l.TextAlign = rightAlign ? ContentAlignment.MiddleRight : ContentAlignment.MiddleLeft;
            return l;
        }

        private static Label MkHint(string text, int x, int y, int w)
        {
            return MkHint(text, x, y, w, 22);
        }

        private static Label MkHint(string text, int x, int y, int w, int h)
        {
            Label l = new Label();
            l.Text = text;
            l.SetBounds(x, y, w, h);
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.ForeColor = Color.DimGray;
            return l;
        }

        private static DataGridViewTextBoxColumn Col(string header, int weight)
        {
            DataGridViewTextBoxColumn c = new DataGridViewTextBoxColumn();
            c.HeaderText = header;
            c.FillWeight = weight;
            c.SortMode = DataGridViewColumnSortMode.NotSortable;
            c.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            return c;
        }

        // ==================================================================
        //  دامنه شرکت‌کنندگان
        // ==================================================================
        private void LoadCashiers()
        {
            _cmbCashier.Items.Clear();
            _cmbCashier.Items.Add(new ComboItem("همه صندوق‌دارها", 0));
            if (_store != null)
            {
                List<CashierChoice> list = _store.LocalCashiers();
                for (int i = 0; i < list.Count; i++)
                    _cmbCashier.Items.Add(new ComboItem(list[i].Name.Length > 0 ? list[i].Name : ("کد " + list[i].UserCode), list[i].UserCode));
            }
            _cmbCashier.SelectedIndex = 0;
        }

        private int CashierCode
        {
            get
            {
                ComboItem it = _cmbCashier.SelectedItem as ComboItem;
                return it == null ? 0 : it.Value;
            }
        }

        private string RangeText()
        {
            if (_cmbRange.SelectedIndex < 0 || _cmbRange.SelectedIndex == 0) return "همه تاریخ‌ها";
            if (_cmbRange.SelectedIndex == 4)
                return "از " + FmtDate.G2(_dtFrom.Value.Date) + " تا " + FmtDate.G2(_dtTo.Value.Date);
            return Convert.ToString(_cmbRange.SelectedItem);
        }

        private string FromYmd
        {
            get
            {
                switch (_cmbRange.SelectedIndex)
                {
                    case 1: return FmtDate.G2(DateTime.Today);
                    case 2: return FmtDate.G2(DateTime.Today.AddDays(-6));
                    case 3: return FmtDate.G2(DateTime.Today.AddDays(-29));
                    case 4: return FmtDate.G2(_dtFrom.Value.Date);
                    default: return "";
                }
            }
        }

        private string ToYmd
        {
            get
            {
                switch (_cmbRange.SelectedIndex)
                {
                    case 1: return FmtDate.G2(DateTime.Today);
                    case 2: return FmtDate.G2(DateTime.Today);
                    case 3: return FmtDate.G2(DateTime.Today);
                    case 4: return FmtDate.G2(_dtTo.Value.Date);
                    default: return "";
                }
            }
        }

        private string ScopeText()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("بازه: " + RangeText());
            if (CashierCode != 0)
            {
                ComboItem it = _cmbCashier.SelectedItem as ComboItem;
                sb.Append(" | صندوق‌دار: " + (it == null ? CashierCode.ToString() : it.Text));
            }
            if (_numMinInvoices.Value > 1) sb.Append(" | حداقل فاکتور: " + _numMinInvoices.Value);
            sb.Append(" | حداقل امتیاز: " + _numMinPoints.Value);
            return sb.ToString();
        }

        private void UpdateCount()
        {
            if (_store == null)
            {
                _people = new List<PhoneProfile>();
                _lblCount.Text = "دیتابیس محلی باز نیست";
                return;
            }
            _people = _store.GetPointsTableFiltered(FromYmd, ToYmd, CashierCode, (int)_numMinInvoices.Value, 0);
            List<PhoneProfile> pool = LotteryEngine.Eligible(_people, (int)_numMinPoints.Value);
            long tickets = 0;
            for (int i = 0; i < pool.Count; i++) tickets += LotteryEngine.TicketsOf(pool[i]);
            _lblCount.Text = pool.Count + " شرکت‌کننده | " + tickets + " شانس";
        }

        /// <summary>خواندن جایزه‌ها از جدول؛ در حالت ساده یک جایزه با تعداد برندگانِ فرم.</summary>
        private List<Prize> ReadPrizes(out string error)
        {
            error = "";
            List<Prize> list = new List<Prize>();
            if (_cmbType.SelectedIndex == 0)
            {
                list.Add(new Prize("", (int)_numWinners.Value));
                return list;
            }

            for (int i = 0; i < _gridPrizes.Rows.Count; i++)
            {
                DataGridViewRow row = _gridPrizes.Rows[i];
                if (row.IsNewRow) continue;
                string title = row.Cells[0].Value == null ? "" : Convert.ToString(row.Cells[0].Value).Trim();
                string sCount = row.Cells[1].Value == null ? "" : Convert.ToString(row.Cells[1].Value).Trim();
                if (title.Length == 0 && sCount.Length == 0) continue;

                double v = Num.ParseD(sCount, -1);
                if (v < 1)
                {
                    error = "ردیف " + (i + 1) + " جدول جایزه‌ها: تعداد برنده باید عددی بزرگ‌تر از صفر باشد.";
                    return list;
                }
                if (title.Length == 0) title = "جایزه " + (list.Count + 1);
                list.Add(new Prize(title, (int)v));
            }

            if (list.Count == 0) error = "حداقل یک جایزه با تعداد برنده مشخص کنید.";
            return list;
        }

        // ==================================================================
        //  قرعه‌کشی
        // ==================================================================
        public void RunDraw(bool forceNewSeed)
        {
            if (_store == null)
            {
                MessageBox.Show(this, "دیتابیس محلی باز نیست؛ ابتدا از داشبورد آن را بررسی کنید.", "قرعه‌کشی",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string perr;
            List<Prize> prizes = ReadPrizes(out perr);
            if (perr.Length > 0)
            {
                MessageBox.Show(this, perr, "قرعه‌کشی", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            int seed;
            string seedText = _txtSeed.Text.Trim();
            if (forceNewSeed || seedText.Length == 0) seed = LotteryEngine.SuggestedSeed();
            else seed = Num.ParseI(seedText, LotteryEngine.SuggestedSeed());
            _txtSeed.Text = seed.ToString();

            UpdateCount();
            bool simple = _cmbType.SelectedIndex == 0;

            LotteryResult r = simple
                ? LotteryEngine.Draw(_people, (int)_numWinners.Value, seed, (int)_numMinPoints.Value)
                : LotteryEngine.DrawPrizes(_people, prizes, seed, (int)_numMinPoints.Value, "پله‌ای");
            r.FromYmd = FromYmd;
            r.ToYmd = ToYmd;
            r.Scope = ScopeText();
            r.MinPoints = (int)_numMinPoints.Value;

            _last = r;
            _reveal.Stop();
            _grid.Rows.Clear();
            _btnCopy.Enabled = false;
            _btnCsv.Enabled = false;

            _lastDrawId = _store.SaveDraw(r);
            _lblSum.Text = r.Summary() + (_lastDrawId > 0 ? " | شناسه ذخیره‌شده: " + _lastDrawId : " | ذخیره نشد!")
                + "\r\n" + r.Scope;
            CsvLog.Info(_s, "قرعه‌کشی: " + r.Summary() + " | " + r.Scope + " | ذخیره: " + _lastDrawId
                + " | برندگان: " + WinnersLine(r));

            if (r.Winners.Count == 0)
            {
                _lblLive.Text = "برنده‌ای انتخاب نشد";
                _lblMsg.ForeColor = Color.Firebrick;
                _lblMsg.Text = "هیچ شرکت‌کننده‌ای با این شرط پیدا نشد؛ حداقل امتیاز یا دامنه را تغییر دهید.";
                for (int i = 0; i < r.Notes.Count; i++) _lblMsg.Text += "\r\n• " + r.Notes[i];
                return;
            }

            if (_chkStep.Checked)
            {
                _revealIndex = 0;
                _reveal.Interval = (int)_numSpeed.Value * 1000;
                _btnPause.Enabled = true;
                _btnPause.Text = "توقف";
                _reveal.Start();
                RevealNext();
            }
            else
            {
                FillGrid(r);
                FinishReveal(false);
            }
        }

        private void TogglePause()
        {
            if (_last == null || _last.Winners.Count == 0) return;
            if (_reveal.Enabled)
            {
                _reveal.Stop();
                _btnPause.Text = "ادامه";
                _lblLive.Text = "نمایش متوقف شد — " + _revealIndex + " از " + _last.Winners.Count + " برنده";
            }
            else
            {
                if (_revealIndex >= _last.Winners.Count) { FinishReveal(true); return; }
                _btnPause.Text = "توقف";
                _reveal.Start();
                RevealNext();
            }
        }

        private void RevealNext()
        {
            if (_last == null) { _reveal.Stop(); return; }
            if (_revealIndex >= _last.Winners.Count) { FinishReveal(true); return; }

            Winner w = _last.Winners[_revealIndex];
            AddWinnerRow(w);
            _revealIndex++;
            _lblLive.Text = "برنده " + w.Rank + (_last.Prizes.Count > 1 && w.Prize.Length > 0 ? " (" + w.Prize + ")" : "")
                + ": " + PhoneUtil.Pretty(w.Mobile)
                + (w.Name.Length > 0 ? " — " + w.Name : "")
                + "  |  شانس: " + LotteryEngine.PercentText(w.ChanceInitial);

            if (_revealIndex >= _last.Winners.Count) FinishReveal(true);
        }

        private void FinishReveal(bool announce)
        {
            _reveal.Stop();
            _btnPause.Enabled = false;
            _btnPause.Text = "توقف";
            if (_last == null) return;

            _btnCopy.Enabled = _last.Winners.Count > 0;
            _btnCsv.Enabled = _last.Winners.Count > 0;

            StringBuilder msg = new StringBuilder();
            for (int i = 0; i < _last.Notes.Count; i++) msg.AppendLine("• " + _last.Notes[i]);
            msg.AppendLine("نتیجه در دیتابیس محلی ذخیره شد" + (_lastDrawId > 0 ? " (شناسه " + _lastDrawId + ")" : "")
                + ". با همین seed می‌توانید عیناً همین نتیجه را بازتولید کنید.");
            _lblMsg.ForeColor = Color.FromArgb(0, 110, 60);
            _lblMsg.Text = msg.ToString().TrimEnd();

            if (announce && _last.Winners.Count > 0)
                _lblLive.Text = "پایان قرعه‌کشی — " + _last.Winners.Count + " برنده انتخاب شد";
        }

        private void FillGrid(LotteryResult r)
        {
            _grid.SuspendLayout();
            try
            {
                _grid.Rows.Clear();
                for (int i = 0; i < r.Winners.Count; i++) AddWinnerRow(r.Winners[i]);
            }
            finally
            {
                _grid.ResumeLayout();
            }
        }

        private void AddWinnerRow(Winner w)
        {
            int idx = _grid.Rows.Add(
                w.Rank.ToString(),
                w.Prize,
                PhoneUtil.Pretty(w.Mobile),
                w.Name,
                w.Points.ToString(),
                LotteryEngine.OddsText(w.ChanceInitial),
                LotteryEngine.OddsText(w.ChanceAtPick),
                w.Invoices.ToString(),
                Num.F(w.Amount));
            _grid.Rows[idx].DefaultCellStyle.BackColor = Color.FromArgb(255, 249, 219);
            _grid.Rows[idx].DefaultCellStyle.Font = new Font("Tahoma", 9F, FontStyle.Bold);
            _grid.Rows[idx].Tag = w.Mobile;
            if (_grid.Rows.Count > 1) _grid.FirstDisplayedScrollingRowIndex = _grid.Rows.Count - 1;
        }

        private static string WinnersLine(LotteryResult r)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < r.Winners.Count; i++)
            {
                if (i > 0) sb.Append(" | ");
                Winner w = r.Winners[i];
                sb.Append((i + 1) + ") " + w.Mobile);
                if (w.Prize.Length > 0) sb.Append(" [" + w.Prize + "]");
                if (w.Name != null && w.Name.Length > 0) sb.Append(" (" + w.Name + ")");
                sb.Append(" — " + w.Points + " شانس");
            }
            return sb.ToString();
        }

        // ==================================================================
        //  پاک کردن، کپی و خروجی
        // ==================================================================
        private void ClearResult()
        {
            _reveal.Stop();
            _btnPause.Enabled = false;
            _btnPause.Text = "توقف";
            _grid.Rows.Clear();
            _last = null;
            _lastDrawId = 0;
            _revealIndex = 0;
            _btnCopy.Enabled = false;
            _btnCsv.Enabled = false;
            _lblSum.Text = "";
            _lblLive.Text = "آماده برای قرعه‌کشی";
            _lblMsg.ForeColor = Color.DimGray;
            _lblMsg.Text = "نتیجه پاک شد. (قرعه‌کشی‌های ذخیره‌شده در دیتابیس محلی باقی می‌مانند.)";
        }

        private void CopyWinners()
        {
            if (_last == null || _last.Winners.Count == 0) return;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("قرعه‌کشی " + _last.TypeName + " — " + FmtDate.G(_last.At));
            sb.AppendLine(_last.Summary());
            sb.AppendLine(_last.Scope);
            sb.AppendLine();
            for (int i = 0; i < _last.Winners.Count; i++)
            {
                Winner w = _last.Winners[i];
                sb.AppendLine((w.Rank) + ". " + PhoneUtil.Pretty(w.Mobile)
                    + (w.Prize.Length > 0 ? " [" + w.Prize + "]" : "")
                    + (w.Name.Length > 0 ? " — " + w.Name : "")
                    + " | امتیاز (شانس): " + w.Points
                    + " | شانس اولیه: " + LotteryEngine.PercentText(w.ChanceInitial)
                    + " (" + LotteryEngine.OddsText(w.ChanceInitial) + ")");
            }
            try
            {
                Clipboard.SetText(sb.ToString());
                _lblMsg.ForeColor = Color.DarkGreen;
                _lblMsg.Text = "فهرست برندگان در حافظه کپی شد (آماده چسباندن در واتس‌اپ/تلگرام/اکسل).";
            }
            catch (Exception ex)
            {
                _lblMsg.ForeColor = Color.Firebrick;
                _lblMsg.Text = "کپی نشد: " + ex.Message;
            }
        }

        private void ExportCsv()
        {
            if (_store == null || _last == null) return;
            try
            {
                string dir = Path.Combine(_s.LogFolderFull, "exports");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "draw-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv");
                _store.ExportDrawCsv(file, _last, _lastDrawId);
                _lblMsg.ForeColor = Color.DarkGreen;
                _lblMsg.Text = "خروجی ذخیره شد: " + file;
            }
            catch (Exception ex)
            {
                _lblMsg.ForeColor = Color.Firebrick;
                _lblMsg.Text = "خروجی گرفته نشد: " + ex.Message;
            }
        }
    }
}
