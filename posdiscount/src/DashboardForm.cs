using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace PosDiscount
{
    /// <summary>
    /// پنجره اصلی: پلینگ لحظه‌ای فاکتورهای صندوق‌دار، پاپ‌آپ تخفیف (فقط برای فاکتورهای مشمول تخفیف)
    /// و مدیریت شماره موبایل و امتیاز مشتریان در دیتابیس محلی.
    /// دیتابیس هولو فقط خوانده می‌شود؛ هیچ داده‌ای در آن درج یا تغییر نمی‌کند.
    /// </summary>
    public class DashboardForm : Form
    {
        private const int InitialPopupLimit = 15;   // سقف پاپ‌آپ‌های اولیه (فقط وقتی «شروع از فاکتورهای امروز» فعال باشد)
        private const int GridRowLimit = 1000;

        private AppSettings _s;
        private HolooDb _db;
        private LocalStore _store;
        private CashierInfo _cashier;

        private Thread _poller;
        private volatile bool _stop;
        private long _lastFCode;
        private readonly object _sync = new object();
        private readonly Queue<PendingInv> _queue = new Queue<PendingInv>();
        private bool _popupShowing;
        private double _todayDiscountSum;
        private DaySummary _day;
        private DateTime _lastPoll = DateTime.MinValue;
        private string _lastError = "";
        private int _errorCount;
        private DateTime _lastHeal = DateTime.MinValue;   // آخرین «اصلاح خودکار اتصال» (برای جلوگیری از تلاش پشت‌سرهم)
        private bool _firstBatch = true;
        private bool _reallyExit;
        private bool _localOk;
        private string _localError = "";
        private bool _searchMode;
        private string _searchMobile = "";
        private bool _noMobileMode;

        private Label _lblHead, _lblSub, _lblFoot, _lblLocal;
        private Label[] _statValue = new Label[5];
        private DataGridView _grid;
        private TextBox _txtSearch;
        private System.Windows.Forms.Timer _refresh;
        private NotifyIcon _tray;

        /// <summary>کاربر دکمه «تغییر صندوق‌دار» را زده است (برنامه به فرم انتخاب کاربر برمی‌گردد).</summary>
        public bool ChangeUserRequested;

        private class PendingInv
        {
            public InvoiceInfo Inv;
            public DiscountResult Res;
            public StoredInvoice Rec;
        }

        public DashboardForm(AppSettings s, CashierInfo cashier)
        {
            _s = s;
            _cashier = cashier;
            _db = new HolooDb(_s.BuildConnectionString());
            OpenLocalStore();
            BuildUi();
            Shown += new EventHandler(OnShown);
            FormClosing += new FormClosingEventHandler(OnClosing);
        }

        // ==================================================================
        //  دیتابیس محلی
        // ==================================================================
        private void OpenLocalStore()
        {
            try
            {
                _store = new LocalStore(_s.LocalDbFullPath);
                string err;
                _localOk = _store.Open(out err);
                _localError = _localOk ? "" : err;
                if (_localOk)
                    CsvLog.Info(_s, "دیتابیس محلی باز شد: " + _store.FilePath);
                else
                    CsvLog.Error(_s, "دیتابیس محلی باز نشد: " + err);
            }
            catch (Exception ex)
            {
                _localOk = false;
                _store = null;
                _localError = LocalStore.Describe(ex);
                CsvLog.Error(_s, "local store: " + ex.ToString());
            }
        }

        // ==================================================================
        //  ساخت رابط کاربری
        // ==================================================================
        private void BuildUi()
        {
            Text = "تخفیف لحظه‌ای صندوق هولو — موبایل و امتیاز مشتریان";
            ClientSize = new Size(984, 664);
            MinimumSize = new Size(880, 600);
            StartPosition = FormStartPosition.CenterScreen;
            RightToLeft = RightToLeft.Yes;
            RightToLeftLayout = true;
            Font = new Font("Tahoma", 9F);

            Panel head = new Panel();
            head.SetBounds(0, 0, 984, 56);
            head.BackColor = Color.FromArgb(30, 62, 104);
            head.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            _lblHead = new Label();
            _lblHead.Text = "تخفیف لحظه‌ای صندوق هولو + باشگاه مشتریان (موبایل و امتیاز)";
            _lblHead.ForeColor = Color.White;
            _lblHead.Font = new Font("Tahoma", 12F, FontStyle.Bold);
            _lblHead.SetBounds(10, 6, 964, 26);
            _lblHead.TextAlign = ContentAlignment.MiddleRight;
            _lblHead.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            _lblSub = new Label();
            _lblSub.ForeColor = Color.FromArgb(215, 225, 240);
            _lblSub.SetBounds(10, 30, 964, 22);
            _lblSub.TextAlign = ContentAlignment.MiddleRight;
            _lblSub.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            head.Controls.Add(_lblHead);
            head.Controls.Add(_lblSub);

            Panel stats = new Panel();
            stats.SetBounds(10, 62, 964, 54);
            stats.BackColor = Color.FromArgb(246, 246, 246);
            stats.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            string[] titles = new string[] { "فاکتورهای امروز", "جمع کل", "تخفیف امروز", "قابل پرداخت", "امتیاز امروز" };
            int cellW = 188;
            for (int i = 0; i < 5; i++)
            {
                int x = 772 - (i * cellW);
                Label t = new Label();
                t.Text = titles[i];
                t.ForeColor = Color.DimGray;
                t.SetBounds(x, 4, cellW - 6, 18);
                t.TextAlign = ContentAlignment.MiddleRight;

                _statValue[i] = new Label();
                _statValue[i].Text = "—";
                _statValue[i].Font = new Font("Tahoma", 10.5F, FontStyle.Bold);
                _statValue[i].SetBounds(x, 22, cellW - 6, 26);
                _statValue[i].TextAlign = ContentAlignment.MiddleRight;

                stats.Controls.Add(t);
                stats.Controls.Add(_statValue[i]);
            }

            // ---------------- ردیف جست‌وجو ----------------
            Panel search = new Panel();
            search.SetBounds(10, 120, 964, 36);
            search.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            Label lblSearch = new Label();
            lblSearch.Text = "جست‌وجو با شماره موبایل مشتری:";
            lblSearch.SetBounds(8, 6, 196, 24);
            lblSearch.TextAlign = ContentAlignment.MiddleRight;
            search.Controls.Add(lblSearch);

            _txtSearch = new TextBox();
            _txtSearch.SetBounds(208, 6, 160, 24);
            _txtSearch.TextAlign = HorizontalAlignment.Center;
            _txtSearch.Font = new Font("Tahoma", 10F, FontStyle.Bold);
            _txtSearch.KeyDown += new KeyEventHandler(delegate (object o, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Search(); }
            });
            search.Controls.Add(_txtSearch);

            Button btnSearch = new Button();
            btnSearch.Text = "جست‌وجو";
            btnSearch.SetBounds(372, 5, 84, 26);
            btnSearch.Click += new EventHandler(delegate { Search(); });
            search.Controls.Add(btnSearch);

            Button btnToday = new Button();
            btnToday.Text = "فاکتورهای امروز";
            btnToday.SetBounds(460, 5, 130, 26);
            btnToday.Click += new EventHandler(delegate { ShowToday(); });
            search.Controls.Add(btnToday);

            Button btnNoMobile = new Button();
            btnNoMobile.Text = "بدون موبایل";
            btnNoMobile.SetBounds(594, 5, 110, 26);
            btnNoMobile.Click += new EventHandler(delegate { ShowNoMobile(); });
            search.Controls.Add(btnNoMobile);

            Button btnPoints = new Button();
            btnPoints.Text = "امتیاز مشتریان (قرعه‌کشی)";
            btnPoints.SetBounds(708, 5, 180, 26);
            btnPoints.Font = new Font("Tahoma", 9F, FontStyle.Bold);
            btnPoints.Click += new EventHandler(delegate { OpenPoints(); });
            search.Controls.Add(btnPoints);

            _grid = new DataGridView();
            _grid.SetBounds(10, 160, 964, 404);
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
            _grid.Columns.Add(Col("شماره فاکتور", 68));
            _grid.Columns.Add(Col("تاریخ", 82));
            _grid.Columns.Add(Col("ساعت", 52));
            _grid.Columns.Add(Col("اقلام", 44));
            _grid.Columns.Add(Col("جمع کل", 96));
            _grid.Columns.Add(Col("تخفیف هولو", 84));
            _grid.Columns.Add(Col("قابل پرداخت", 96));
            _grid.Columns.Add(Col("تخفیف پیشنهادی برنامه", 112));
            _grid.Columns.Add(Col("شماره موبایل", 100));
            _grid.Columns.Add(Col("امتیاز", 54));
            _grid.Columns.Add(Col("وضعیت", 70));
            _grid.CellDoubleClick += new DataGridViewCellEventHandler(OnGridDoubleClick);

            _lblFoot = new Label();
            _lblFoot.SetBounds(10, 566, 964, 44);
            _lblFoot.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            _lblFoot.ForeColor = Color.DimGray;

            Button btnSettings = MkButton("تنظیمات", 10, 612, 120);
            btnSettings.Click += new EventHandler(delegate { OpenSettings(); });

            Button btnUser = MkButton("تغییر صندوق‌دار", 140, 612, 140);
            btnUser.Click += new EventHandler(delegate
            {
                ChangeUserRequested = true;
                Close();
            });

            Button btnMobile = MkButton("ثبت موبایل/امتیاز", 290, 612, 140);
            btnMobile.Font = new Font("Tahoma", 9F, FontStyle.Bold);
            btnMobile.Click += new EventHandler(delegate { EditSelectedInvoice(); });

            Button btnTest = MkButton("آزمایش پاپ‌آپ", 440, 612, 120);
            btnTest.Click += new EventHandler(delegate { TestPopup(); });

            Button btnRefresh = MkButton("بروزرسانی", 570, 612, 110);
            btnRefresh.Click += new EventHandler(delegate { RefreshAll(); });

            _lblLocal = new Label();
            _lblLocal.SetBounds(690, 612, 148, 32);
            _lblLocal.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            _lblLocal.ForeColor = Color.DimGray;
            _lblLocal.TextAlign = ContentAlignment.MiddleRight;

            Button btnHide = MkButton("پنهان در سینی", 848, 612, 126);
            btnHide.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            btnHide.Click += new EventHandler(delegate { HideToTray(true); });

            Controls.Add(head);
            Controls.Add(stats);
            Controls.Add(search);
            Controls.Add(_grid);
            Controls.Add(_lblFoot);
            Controls.Add(btnSettings);
            Controls.Add(btnUser);
            Controls.Add(btnMobile);
            Controls.Add(btnTest);
            Controls.Add(btnRefresh);
            Controls.Add(_lblLocal);
            Controls.Add(btnHide);

            _refresh = new System.Windows.Forms.Timer();
            _refresh.Interval = 10000;
            _refresh.Tick += new EventHandler(delegate { RefreshAll(); });

            _tray = new NotifyIcon();
            _tray.Icon = SystemIcons.Application;
            _tray.Text = "تخفیف لحظه‌ای صندوق هولو";
            _tray.Visible = true;
            _tray.DoubleClick += new EventHandler(delegate { RestoreFromTray(); });
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.RightToLeft = RightToLeft.Yes;
            menu.Items.Add("نمایش پنجره", null, new EventHandler(delegate { RestoreFromTray(); }));
            menu.Items.Add("بروزرسانی", null, new EventHandler(delegate { RefreshAll(); }));
            menu.Items.Add("اصلاح خودکار اتصال", null, new EventHandler(delegate { HealFromUi(); }));
            menu.Items.Add("امتیاز مشتریان", null, new EventHandler(delegate { OpenPoints(); }));
            menu.Items.Add("قرعه‌کشی", null, new EventHandler(delegate { OpenLottery(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("خروج", null, new EventHandler(delegate { _reallyExit = true; Close(); }));
            _tray.ContextMenuStrip = menu;
        }

        private Button MkButton(string text, int x, int y, int w)
        {
            Button b = new Button();
            b.Text = text;
            b.SetBounds(x, y, w, 32);
            b.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            return b;
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
        //  شروع کار و حلقه پلینگ
        // ==================================================================
        private void OnShown(object sender, EventArgs e)
        {
            UpdateSubTitle();

            StartPolling();
            if (_s.StartMinimizedToTray) HideToTray(false);
        }

        private void UpdateSubTitle()
        {
            _lblSub.Text = "سرور: " + _s.Server + " | دیتابیس: " + _s.Database
                + " | صندوق‌دار: " + _cashier.Name + " (کد " + _cashier.Code + ")"
                + " | تخفیف: " + DiscountEngine.ModePersian(_s.DiscountMode)
                + " | دیتابیس هولو فقط خوانده می‌شود (هیچ درج/تغییری انجام نمی‌شود)";
        }

        private void StartPolling()
        {
            try
            {
                _day = _db.GetDaySummary(_cashier.Code);
                _todayDiscountSum = _db.GetTodayDiscountSum(_cashier.Code);
                long maxF = _db.GetMaxFCode();
                _lastFCode = _s.SkipExistingOnStart ? maxF : _db.GetLastFCodeBeforeDay(_db.GetWorkDay());
                _lastError = "";
            }
            catch (Exception ex)
            {
                _lastError = HolooDb.DescribeConnectionError(ex);
                CsvLog.Error(_s, "start: " + ex.ToString());
            }

            RefreshAll();

            _stop = false;
            _poller = new Thread(new ThreadStart(PollLoop));
            _poller.IsBackground = true;
            _poller.Name = "PosDiscountPoller";
            _poller.Start();
            _refresh.Start();
        }

        private void PollLoop()
        {
            while (!_stop)
            {
                int interval = _s.PollIntervalMs;
                if (interval < 300) interval = 300;
                try
                {
                    Thread.Sleep(interval);
                    if (_stop) break;

                    long after;
                    lock (_sync) { after = _lastFCode; }

                    List<InvoiceInfo> list = _db.GetNewInvoices(after, _cashier.Code, 20);
                    _lastPoll = DateTime.Now;

                    if (list.Count > 0)
                    {
                        long seen = after;
                        for (int i = 0; i < list.Count; i++)
                            if (list[i].FCode > seen) seen = list[i].FCode;
                        lock (_sync) { _lastFCode = seen; }

                        if (_stop) break;
                        if (IsHandleCreated && !IsDisposed)
                            BeginInvoke(new MethodInvoker(delegate { OnNewInvoices(list); }));
                    }
                    else if (IsHandleCreated && !IsDisposed)
                    {
                        BeginInvoke(new MethodInvoker(delegate { UpdateFoot(); }));
                    }
                }
                catch (Exception ex)
                {
                    _errorCount++;
                    _lastError = HolooDb.DescribeConnectionError(ex);
                    CsvLog.Error(_s, "poll: " + ex.ToString());
                    TryAutoHeal(ex);   // تا وقتی سرور پیدا نشود، برنامه با نام خراب «قفل» نمی‌ماند
                    if (IsHandleCreated && !IsDisposed)
                        BeginInvoke(new MethodInvoker(delegate { UpdateFoot(); }));
                    try { Thread.Sleep(3000); } catch (Exception) { }
                }
            }
        }

        // ==================================================================
        //  فاکتور جدید
        // ==================================================================
        private void OnNewInvoices(List<InvoiceInfo> list)
        {
            if (_firstBatch)
            {
                _firstBatch = false;
                if (list.Count > InitialPopupLimit)
                    list = list.GetRange(list.Count - InitialPopupLimit, InitialPopupLimit);
            }

            int queued = 0;
            for (int i = 0; i < list.Count; i++)
            {
                InvoiceInfo inv = list[i];
                DiscountResult res = DiscountEngine.Compute(_s, inv.Gross, _todayDiscountSum, _cashier.CanDiscount);

                // همیشه در دیتابیس محلی ذخیره می‌شود (موبایل + امتیاز)، ولی هیچ‌چیز در هولو نوشته نمی‌شود.
                StoredInvoice rec = SaveLocal(inv);

                string action = res.HasDiscount ? "نمایش پاپ‌آپ تخفیف" : "بدون تخفیف (بدون پاپ‌آپ)";
                CsvLog.Row(_s, _cashier.Name, inv, res, action,
                    rec == null ? "" : rec.Mobile, rec == null ? 0 : rec.Points);

                // پاپ‌آپ فقط زمانی که فاکتور مشمول تخفیف باشد.
                if (res.HasDiscount)
                {
                    PendingInv p = new PendingInv();
                    p.Inv = inv;
                    p.Res = res;
                    p.Rec = rec;
                    _queue.Enqueue(p);
                    queued++;
                }
            }

            UpdateFoot();
            if (queued > 0) ShowNextPopup();
            else RefreshAll();
        }

        /// <summary>ذخیره/به‌روزرسانی فاکتور در دیتابیس محلی (بدون هیچ نوشتنی در هولو).</summary>
        private StoredInvoice SaveLocal(InvoiceInfo inv)
        {
            if (!_localOk || _store == null) return null;
            try
            {
                return _store.SaveInvoice(inv, 0, _s);
            }
            catch (Exception ex)
            {
                CsvLog.Error(_s, "local save: " + ex.ToString());
                return null;
            }
        }

        private void ShowNextPopup()
        {
            if (_popupShowing) return;
            if (_queue.Count == 0) return;

            PendingInv p = _queue.Dequeue();
            _popupShowing = true;

            try
            {
                using (InvoicePopupForm f = new InvoicePopupForm(_s, _store, p.Inv, p.Res, p.Rec))
                {
                    f.ShowDialog(this);
                    if (f.DataChanged && p.Inv != null)
                        CsvLog.Row(_s, _cashier.Name, p.Inv, p.Res, "ثبت موبایل/امتیاز در دیتابیس محلی");
                }
            }
            catch (Exception ex)
            {
                CsvLog.Error(_s, "popup: " + ex.ToString());
            }
            finally
            {
                _popupShowing = false;
            }

            UpdateFoot();
            if (_queue.Count > 0) BeginInvoke(new MethodInvoker(ShowNextPopup));
            else RefreshAll();
        }

        // ==================================================================
        //  نمایش و بروزرسانی
        // ==================================================================
        private void RefreshAll()
        {
            try
            {
                _day = _db.GetDaySummary(_cashier.Code);
                _todayDiscountSum = _db.GetTodayDiscountSum(_cashier.Code);
                _lastError = "";
            }
            catch (Exception ex)
            {
                _errorCount++;
                _lastError = HolooDb.DescribeConnectionError(ex);
                CsvLog.Error(_s, "refresh: " + ex.ToString());
            }

            if (_searchMode) Search();
            else if (_noMobileMode) ShowNoMobile();
            else ShowToday();
        }

        /// <summary>فاکتورهای امروز صندوق‌دار (به همراه موبایل/امتیاز از دیتابیس محلی).</summary>
        private void ShowToday()
        {
            _searchMode = false;
            _noMobileMode = false;
            _searchMobile = "";
            if (_txtSearch != null && _txtSearch.Text.Length > 0) _txtSearch.Text = "";

            List<InvoiceInfo> list = null;
            try
            {
                list = _db.GetWorkDayInvoices(_cashier.Code, GridRowLimit);
            }
            catch (Exception ex)
            {
                _errorCount++;
                _lastError = HolooDb.DescribeConnectionError(ex);
                CsvLog.Error(_s, "today: " + ex.ToString());
            }

            if (list == null)
            {
                FillToday(new List<InvoiceInfo>(), new Dictionary<long, StoredInvoice>());
                UpdateFoot();
                return;
            }

            // موبایل/امتیاز: ابتدا فاکتورهای تازه در دیتابیس محلی ثبت می‌شوند
            long min = long.MaxValue;
            for (int i = 0; i < list.Count; i++) if (list[i].FCode < min) min = list[i].FCode;
            if (list.Count == 0) min = 0;

            Dictionary<long, StoredInvoice> map = _store != null && _localOk
                ? _store.ReadFrom(min) : new Dictionary<long, StoredInvoice>();

            for (int i = 0; i < list.Count; i++)
            {
                InvoiceInfo inv = list[i];
                StoredInvoice rec;
                bool has = map.TryGetValue(inv.FCode, out rec);
                if (!has
                    || rec.Gross != inv.Gross
                    || rec.HolooDiscount != inv.Discount
                    || (rec.Mobile.Length == 0 && PhoneUtil.Normalize(inv.Mobile).Length > 0))
                {
                    StoredInvoice saved = SaveLocal(inv);
                    if (saved != null) map[saved.FCode] = saved;
                }
            }

            FillToday(list, map);
            UpdateFoot();
        }

        /// <summary>جست‌وجوی فاکتورهای یک شماره موبایل در دیتابیس محلی.</summary>
        private void Search()
        {
            string digits = PhoneUtil.Digits(_txtSearch == null ? "" : _txtSearch.Text);
            if (digits.Length == 0)
            {
                MessageBox.Show(this, "برای جست‌وجو، شماره موبایل مشتری را وارد کنید (مثال: 09123456789).",
                    "جست‌وجو", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!_localOk || _store == null)
            {
                MessageBox.Show(this, "دیتابیس محلی باز نیست؛ جست‌وجوی موبایل ممکن نیست.\r\n" + _localError,
                    "دیتابیس محلی", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _searchMode = true;
            _noMobileMode = false;
            _searchMobile = PhoneUtil.Normalize(digits);

            List<StoredInvoice> list = _store.FindByMobile(_searchMobile, GridRowLimit);
            List<PhoneProfile> prof = _store.ProfilesByQuery(_searchMobile, 1);

            _lblHead.Text = "جست‌وجوی شماره " + PhoneUtil.Pretty(_searchMobile)
                + (prof.Count > 0 && prof[0].Name.Length > 0 ? " (" + prof[0].Name + ")" : "")
                + " — " + list.Count + " فاکتور"
                + (prof.Count > 0 ? " | امتیاز کل: " + prof[0].Points : "");

            FillStored(list, _searchMobile);
            UpdateFoot();
        }

        /// <summary>فاکتورهای بدون شماره موبایل (برای تکمیل دستی).</summary>
        private void ShowNoMobile()
        {
            if (!_localOk || _store == null)
            {
                MessageBox.Show(this, "دیتابیس محلی باز نیست.\r\n" + _localError,
                    "دیتابیس محلی", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _noMobileMode = true;
            _searchMode = false;
            _searchMobile = "";
            List<StoredInvoice> list = _store.GetWithoutMobile(GridRowLimit);
            _lblHead.Text = "فاکتورهای بدون شماره موبایل — " + list.Count
                + " فاکتور (روی سطر دوقلیک کنید تا موبایل ثبت و امتیاز محاسبه شود)";
            FillStored(list, "");
            UpdateFoot();
        }

        private void FillToday(List<InvoiceInfo> list, Dictionary<long, StoredInvoice> map)
        {
            _grid.SuspendLayout();
            try
            {
                _grid.Rows.Clear();
                for (int i = 0; i < list.Count; i++)
                {
                    InvoiceInfo inv = list[i];
                    DiscountResult res = DiscountEngine.Compute(_s, inv.Gross, _todayDiscountSum, _cashier.CanDiscount);

                    string propose;
                    if (res.HasDiscount) propose = "تخفیف پیشنهادی: " + Num.F(res.Discount) + " (" + Num.P(res.Percent) + "٪)";
                    else propose = "—";

                    StoredInvoice rec;
                    bool has = map.TryGetValue(inv.FCode, out rec);
                    string mobile = has ? rec.Mobile : PhoneUtil.Normalize(inv.Mobile);
                    int points = has ? rec.Points : 0;

                    int idx = _grid.Rows.Add(
                        inv.FCodeC.ToString(),
                        inv.JalaliDate,
                        inv.Time,
                        inv.Items.ToString(),
                        Num.F(inv.Gross),
                        Num.F(inv.Discount),
                        Num.F(inv.Net),
                        propose,
                        PhoneUtil.Pretty(mobile),
                        mobile.Length == 0 ? "—" : points.ToString(),
                        res.HasDiscount ? "مشمول تخفیف" : "بدون تخفیف");
                    _grid.Rows[idx].Tag = inv;
                    if (mobile.Length == 0) _grid.Rows[idx].Cells[8].Style.ForeColor = Color.Firebrick;
                }
            }
            finally
            {
                _grid.ResumeLayout();
            }
        }

        private void FillStored(List<StoredInvoice> list, string highlight)
        {
            _grid.SuspendLayout();
            try
            {
                _grid.Rows.Clear();
                for (int i = 0; i < list.Count; i++)
                {
                    StoredInvoice v = list[i];
                    int idx = _grid.Rows.Add(
                        v.FCodeC.ToString(),
                        v.Jalali,
                        v.Time,
                        "",
                        Num.F(v.Gross),
                        Num.F(v.HolooDiscount),
                        Num.F(v.Net),
                        v.OfferedDiscount > 0 ? "پیشنهاد: " + Num.F(v.OfferedDiscount) : "—",
                        PhoneUtil.Pretty(v.Mobile),
                        v.Mobile.Length == 0 ? "—" : v.Points.ToString(),
                        v.Mobile.Length == 0 ? "بدون موبایل" : "ثبت‌شده");
                    _grid.Rows[idx].Tag = v;
                }
            }
            finally
            {
                _grid.ResumeLayout();
            }
        }

        private void UpdateFoot()
        {
            string day = _day == null ? "—" : FmtDate.G(_day.Day);
            int pointsToday = 0;
            int withMobile = 0;
            int totalLocal = 0;
            int totalMobiles = 0;
            int noMobile = 0;
            if (_localOk && _store != null)
            {
                DateTime d = _day == null ? DateTime.Today : _day.Day.Date;
                pointsToday = _store.SumPointsOnDay(d);
                withMobile = _store.CountWithMobileOnDay(d);
                totalLocal = _store.CountInvoices();
                totalMobiles = _store.CountMobiles();
                noMobile = _store.CountWithoutMobile();
            }

            if (_day != null)
            {
                _statValue[0].Text = _day.Count + " فاکتور";
                _statValue[1].Text = Num.F(_day.Gross);
                _statValue[2].Text = Num.F(_day.Discount);
                _statValue[3].Text = Num.F(_day.Net);
                _statValue[4].Text = pointsToday + " امتیاز (" + withMobile + " فاکتور با موبایل)";
            }

            if (_lblLocal != null)
            {
                _lblLocal.ForeColor = _localOk ? Color.DimGray : Color.Firebrick;
                _lblLocal.Text = _localOk
                    ? ("دیتابیس محلی: " + totalLocal + " فاکتور | " + totalMobiles + " شماره\r\nبدون موبایل: " + noMobile)
                    : "دیتابیس محلی باز نیست!";
            }

            string last = _lastPoll == DateTime.MinValue ? "—" : Num.T(_lastPoll);
            long f = 0;
            lock (_sync) { f = _lastFCode; }

            _lblFoot.Text = "روز کاری دیتابیس: " + day
                + "  |  آخرین بررسی: " + last
                + "  |  آخرین کد فاکتور دیده‌شده: " + f
                + "  |  تخفیف ثبت‌شده در هولو امروز: " + Num.F(_todayDiscountSum)
                + (_errorCount > 0 ? "  |  خطاها: " + _errorCount : "");
            _lblFoot.Text += "\r\nقاعده امتیاز: " + PointsEngine.RuleText(_s.PointsPerAmountToman)
                + "  |  پایه امتیاز: " + PointsEngine.BaseLabel(_s.PointsBase);
            if (!_localOk) _lblFoot.Text += "\r\n" + _localError;
            if (string.IsNullOrEmpty(_localError) && !string.IsNullOrEmpty(_lastError))
            {
                _lblFoot.Text += "\r\n" + _lastError;
                if (ConnectionFixer.LooksLocal(_s.Server))
                    _lblFoot.Text += "\r\nراهنما: منوی سینی ← «اصلاح خودکار اتصال» (نام سرورِ خراب خودکار درست می‌شود).";
            }
        }

        // ==================================================================
        //  انتخاب سطر و پنجره‌های موبایل/امتیاز
        // ==================================================================
        private void OnGridDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;

            StoredInvoice st = _grid.Rows[e.RowIndex].Tag as StoredInvoice;
            if (st != null) { EditRecord(st); return; }

            InvoiceInfo inv = _grid.Rows[e.RowIndex].Tag as InvoiceInfo;
            if (inv == null) return;

            // فاکتور امروز: ابتدا موبایل/امتیاز، سپس پاپ‌آپ تخفیف
            StoredInvoice rec = _store != null && _localOk ? _store.Read(inv.FCode) : null;
            if (rec == null) rec = SaveLocal(inv);
            if (rec != null && (rec.Mobile.Length == 0 || Control.ModifierKeys == Keys.Shift))
                EditRecord(rec);

            DiscountResult res = DiscountEngine.Compute(_s, inv.Gross, _todayDiscountSum, _cashier.CanDiscount);
            if (!res.HasDiscount) return;
            using (InvoicePopupForm f = new InvoicePopupForm(_s, _store, inv, res, _store != null && _localOk ? _store.Read(inv.FCode) : null))
                f.ShowDialog(this);
            RefreshAll();
        }

        private void EditSelectedInvoice()
        {
            if (_grid.SelectedRows.Count == 0)
            {
                MessageBox.Show(this, "اول یک سطر از جدول را انتخاب کنید.", "ثبت موبایل",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            DataGridViewRow row = _grid.SelectedRows[0];

            StoredInvoice st = row.Tag as StoredInvoice;
            if (st != null) { EditRecord(st); return; }

            InvoiceInfo inv = row.Tag as InvoiceInfo;
            if (inv == null) return;
            if (!_localOk || _store == null)
            {
                MessageBox.Show(this, "دیتابیس محلی باز نیست.\r\n" + _localError, "دیتابیس محلی",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            StoredInvoice rec = _store.Read(inv.FCode);
            if (rec == null) rec = SaveLocal(inv);
            if (rec == null)
            {
                MessageBox.Show(this, "این فاکتور در دیتابیس محلی ثبت نشد.", "دیتابیس محلی",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            EditRecord(rec);
        }

        private void EditRecord(StoredInvoice rec)
        {
            if (!_localOk || _store == null)
            {
                MessageBox.Show(this, "دیتابیس محلی باز نیست.\r\n" + _localError, "دیتابیس محلی",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            using (MobileEntryForm f = new MobileEntryForm(_s, _store, rec))
            {
                if (f.ShowDialog(this) == DialogResult.OK)
                {
                    CsvLog.Info(_s, "ثبت موبایل برای فاکتور " + rec.FCodeC
                        + " — موبایل: " + (f.Result == null ? "" : f.Result.Mobile)
                        + " | امتیاز: " + (f.Result == null ? 0 : f.Result.Points));
                    RefreshAll();
                }
            }
        }

        /// <summary>قرعه‌کشی بین شرکت‌کنندگان (هر ۱ امتیاز = ۱ شانس).</summary>
        private void OpenLottery()
        {
            if (!_localOk || _store == null)
            {
                MessageBox.Show(this, "دیتابیس محلی باز نیست.\r\n" + _localError, "قرعه‌کشی",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            using (LotteryForm f = new LotteryForm(_s, _store))
                f.ShowDialog(this);
            RefreshAll();
        }

        private void OpenPoints()
        {
            if (!_localOk || _store == null)
            {
                MessageBox.Show(this, "دیتابیس محلی باز نیست.\r\n" + _localError, "دیتابیس محلی",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            using (PointsForm f = new PointsForm(_s, _store))
                f.ShowDialog(this);
            RefreshAll();
        }

        private void TestPopup()
        {
            string mobile = "";
            try
            {
                if (_store != null && _localOk && _store.CountInvoices() > 0)
                {
                    List<StoredInvoice> w = _store.GetWithoutMobile(1);
                    if (w.Count > 0) mobile = "";
                }
            }
            catch (Exception) { }

            InvoiceInfo inv = new InvoiceInfo();
            inv.FCode = 0;
            inv.FCodeC = 0;
            inv.Time = DateTime.Now.ToString("HH:mm");
            inv.JalaliDate = FmtDate.G(DateTime.Today);
            inv.CashierName = _cashier.Name + " (آزمایشی)";
            inv.Items = 3;
            inv.Gross = 1234000;
            inv.Pos = 1234000;
            inv.Mobile = mobile;
            DiscountResult res = DiscountEngine.Compute(_s, inv.Gross, _todayDiscountSum, _cashier.CanDiscount);

            using (InvoicePopupForm f = new InvoicePopupForm(_s, null, inv, res, null))
                f.ShowDialog(this);
        }

        // ==================================================================
        //  تنظیمات و سینی
        // ==================================================================
        private void OpenSettings()
        {
            AppSettings tmp = _s.Clone();
            using (SettingsForm f = new SettingsForm(tmp))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;

                bool reconnect = tmp.Server != _s.Server || tmp.Database != _s.Database
                    || tmp.UseWindowsAuth != _s.UseWindowsAuth || tmp.SqlUser != _s.SqlUser
                    || tmp.SqlPassword != _s.SqlPassword;
                bool reopenLocal = LocalStore.ResolveFile(tmp.BaseDir, tmp.LocalDbFile)
                    != LocalStore.ResolveFile(_s.BaseDir, _s.LocalDbFile);
                bool pointsRuleChanged = tmp.PointsEnabled != _s.PointsEnabled
                    || tmp.PointsPerAmountToman != _s.PointsPerAmountToman
                    || tmp.PointsBase != _s.PointsBase;

                tmp.FilePath = _s.FilePath;
                tmp.Save();

                // انتقال مقادیر به نمونه جاری (پلینگ از همان نمونه می‌خواند)
                _s.Server = tmp.Server; _s.Database = tmp.Database;
                _s.UseWindowsAuth = tmp.UseWindowsAuth; _s.SqlUser = tmp.SqlUser; _s.SqlPassword = tmp.SqlPassword;
                _s.ConnectTimeoutSeconds = tmp.ConnectTimeoutSeconds;
                _s.PollIntervalMs = tmp.PollIntervalMs; _s.SkipExistingOnStart = tmp.SkipExistingOnStart;
                _s.DiscountEnabled = tmp.DiscountEnabled; _s.DiscountMode = tmp.DiscountMode;
                _s.Percent = tmp.Percent; _s.FixedAmount = tmp.FixedAmount;
                _s.MaxPercent = tmp.MaxPercent; _s.MaxAmount = tmp.MaxAmount;
                _s.MinInvoiceAmount = tmp.MinInvoiceAmount; _s.MaxDiscountPerDay = tmp.MaxDiscountPerDay;
                _s.RequireDiscountPermission = tmp.RequireDiscountPermission;
                _s.RoundTo = tmp.RoundTo; _s.RoundInFavorOfCustomer = tmp.RoundInFavorOfCustomer;
                _s.StartTime = tmp.StartTime; _s.EndTime = tmp.EndTime;
                _s.Tiers = tmp.Tiers;
                _s.PointsEnabled = tmp.PointsEnabled; _s.PointsPerAmountToman = tmp.PointsPerAmountToman;
                _s.PointsBase = tmp.PointsBase; _s.LocalDbFile = tmp.LocalDbFile;
                _s.CaptureMobileInPopup = tmp.CaptureMobileInPopup;
                _s.AutoCloseSeconds = tmp.AutoCloseSeconds; _s.PlaySound = tmp.PlaySound;
                _s.PopupCorner = tmp.PopupCorner; _s.AlwaysOnTop = tmp.AlwaysOnTop;
                _s.StartMinimizedToTray = tmp.StartMinimizedToTray; _s.LogFolder = tmp.LogFolder;

                if (reconnect) _db = new HolooDb(_s.BuildConnectionString());
                if (reopenLocal)
                {
                    try { if (_store != null) _store.Dispose(); } catch (Exception) { }
                    OpenLocalStore();
                }

                // تغییر آستانه/پایه امتیاز ⇒ بازمحاسبه امتیاز همه فاکتورهای دیتابیس محلی
                if (pointsRuleChanged && _localOk && _store != null)
                {
                    int changed = _store.RecalculatePoints(_s);
                    CsvLog.Info(_s, "بازمحاسبه امتیازها با آستانه " + Num.F(_s.PointsPerAmountToman)
                        + " تومان و پایه " + _s.PointsBase + " → " + changed + " فاکتور تغییر کرد");
                    if (changed > 0)
                        MessageBox.Show(this,
                            "قاعده امتیاز تغییر کرد و امتیاز " + changed + " فاکتور در دیتابیس محلی بازمحاسبه شد.",
                            "بازمحاسبه امتیاز", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }

                UpdateSubTitle();
                RefreshAll();
            }
        }

        /// <summary>
        /// اگر خطا از نوع «سرور/اینستنس پیدا نشد» باشد و سرور روی همین رایانه باشد،
        /// نام سرور خودکار اصلاح و اتصال دوباره ساخته می‌شود (حداکثر یک بار در دقیقه).
        /// روی رشته پلینگ صدا زده می‌شود تا رابط کاربری معطل نشود.
        /// </summary>
        private void TryAutoHeal(Exception ex)
        {
            if (ex == null) return;
            if (!ConnectionFixer.LooksLikeServerNotFound(ex)) return;
            if (!ConnectionFixer.LooksLocal(_s.Server)) return;
            if (DateTime.Now - _lastHeal < TimeSpan.FromSeconds(60)) return;
            _lastHeal = DateTime.Now;
            try
            {
                string fix = ConnectionFixer.Heal(_s);
                if (string.IsNullOrEmpty(fix)) return;
                CsvLog.Info(_s, "اصلاح خودکار اتصال: " + fix);
                _db = new HolooDb(_s.BuildConnectionString());
                _errorCount = 0;
                _lastError = "";
            }
            catch (Exception hex) { CsvLog.Error(_s, "auto heal: " + hex.ToString()); }
        }

        /// <summary>از منوی سینی: بررسی و اصلاح اتصال + گزارش به کاربر</summary>
        private void HealFromUi()
        {
            string fix = "";
            UseWaitCursor = true;
            try
            {
                fix = ConnectionFixer.Heal(_s);
                if (!string.IsNullOrEmpty(fix)) CsvLog.Info(_s, "اصلاح خودکار اتصال (دستی): " + fix);
                _db = new HolooDb(_s.BuildConnectionString());
                _lastHeal = DateTime.Now;
            }
            catch (Exception ex)
            {
                CsvLog.Error(_s, "heal: " + ex.ToString());
                MessageBox.Show(this, HolooDb.DescribeConnectionError(ex), "اصلاح اتصال",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            finally
            {
                UseWaitCursor = false;
            }

            UpdateSubTitle();
            RefreshAll();
            MessageBox.Show(this,
                string.IsNullOrEmpty(fix)
                    ? "اتصال با همان تنظیمات بررسی شد و نام سرور نیازی به تغییر نداشت.\r\nسرور فعلی: " + _s.Server
                    : "اتصال اصلاح شد:\r\n\r\n" + fix + "\r\n\r\nسرور جدید: " + _s.Server,
                "اصلاح خودکار اتصال", MessageBoxButtons.OK,
                string.IsNullOrEmpty(fix) ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        private void HideToTray(bool notify)
        {
            Hide();
            ShowInTaskbar = false;
            if (notify && _tray != null)
            {
                try
                {
                    _tray.BalloonTipTitle = "تخفیف لحظه‌ای فعال است";
                    _tray.BalloonTipText = "برنامه در سینی سیستم اجرا می‌شود و پاپ‌آپ تخفیف نمایش داده می‌شود.";
                    _tray.ShowBalloonTip(3000);
                }
                catch (Exception) { }
            }
        }

        private void RestoreFromTray()
        {
            Show();
            ShowInTaskbar = true;
            WindowState = FormWindowState.Normal;
            Activate();
            RefreshAll();
        }

        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (!_reallyExit && !ChangeUserRequested && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray(true);
                return;
            }

            _stop = true;
            _refresh.Stop();
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
            Thread t = _poller;
            if (t != null && t.IsAlive) { try { t.Join(1500); } catch (Exception) { } }
            try { if (_store != null) _store.Dispose(); } catch (Exception) { }
        }
    }
}
