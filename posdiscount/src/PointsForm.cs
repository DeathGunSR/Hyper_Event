using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PosDiscount
{
    /// <summary>
    /// فهرست امتیاز مشتریان (برای قرعه‌کشی): هر شماره موبایل با تعداد فاکتور، جمع خرید،
    /// جمع تخفیف و مجموع امتیاز — همه از دیتابیس محلی برنامه.
    /// </summary>
    public class PointsForm : Form
    {
        private readonly AppSettings _s;
        private readonly LocalStore _store;

        private TextBox _txtSearch;
        private DataGridView _grid;
        private Label _lblFoot, _lblMsg;
        private DateTimePicker _dtFrom, _dtTo;
        private CheckBox _chkAll;
        private List<PhoneProfile> _rows = new List<PhoneProfile>();

        public PointsForm(AppSettings s, LocalStore store)
        {
            _s = s;
            _store = store;
            BuildUi();
            Load_();
        }

        private void BuildUi()
        {
            Text = "امتیاز مشتریان — دیتابیس محلی برنامه";
            ClientSize = new Size(940, 600);
            MinimumSize = new Size(820, 520);
            StartPosition = FormStartPosition.CenterParent;
            RightToLeft = RightToLeft.Yes;
            RightToLeftLayout = true;
            Font = new Font("Tahoma", 9F);

            Panel head = new Panel();
            head.SetBounds(0, 0, 940, 46);
            head.BackColor = Color.FromArgb(30, 62, 104);
            head.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Label lblHead = new Label();
            lblHead.Text = "امتیاز مشتریان (قرعه‌کشی)";
            lblHead.ForeColor = Color.White;
            lblHead.Font = new Font("Tahoma", 12F, FontStyle.Bold);
            lblHead.SetBounds(12, 10, 916, 26);
            lblHead.TextAlign = ContentAlignment.MiddleRight;
            head.Controls.Add(lblHead);

            Label lblSearch = new Label();
            lblSearch.Text = "جست‌وجو با شماره موبایل:";
            lblSearch.SetBounds(746, 60, 182, 24);
            lblSearch.TextAlign = ContentAlignment.MiddleRight;

            _txtSearch = new TextBox();
            _txtSearch.SetBounds(546, 60, 190, 24);
            _txtSearch.TextAlign = HorizontalAlignment.Center;
            _txtSearch.Font = new Font("Tahoma", 10F, FontStyle.Bold);
            _txtSearch.KeyDown += new KeyEventHandler(delegate (object o, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Load_(); }
            });

            Button btnSearch = new Button();
            btnSearch.Text = "جست‌وجو";
            btnSearch.SetBounds(456, 59, 84, 26);
            btnSearch.Click += new EventHandler(delegate { Load_(); });

            Button btnClear = new Button();
            btnClear.Text = "نمایش همه";
            btnClear.SetBounds(366, 59, 84, 26);
            btnClear.Click += new EventHandler(delegate { _txtSearch.Text = ""; Load_(); });

            _chkAll = new CheckBox();
            _chkAll.Text = "همه تاریخ‌ها (کامل)";
            _chkAll.SetBounds(746, 90, 182, 24);
            _chkAll.CheckedChanged += new EventHandler(delegate { Load_(); });

            _dtFrom = new DateTimePicker();
            _dtFrom.SetBounds(546, 90, 190, 24);
            _dtFrom.Format = DateTimePickerFormat.Short;
            _dtFrom.Value = new DateTime(DateTime.Today.Year, DateTime.Today.Month, DateTime.Today.Day).AddMonths(-1);
            _dtFrom.ValueChanged += new EventHandler(delegate { Load_(); });

            Label lblTo = new Label();
            lblTo.Text = "تا تاریخ:";
            lblTo.SetBounds(440, 90, 100, 24);
            lblTo.TextAlign = ContentAlignment.MiddleRight;

            _dtTo = new DateTimePicker();
            _dtTo.SetBounds(330, 90, 104, 24);
            _dtTo.Format = DateTimePickerFormat.Short;
            _dtTo.Value = DateTime.Today.AddDays(1);
            _dtTo.ValueChanged += new EventHandler(delegate { Load_(); });

            Label lblFrom = new Label();
            lblFrom.Text = "از تاریخ:";
            lblFrom.SetBounds(220, 90, 104, 24);
            lblFrom.TextAlign = ContentAlignment.MiddleRight;

            _grid = new DataGridView();
            _grid.SetBounds(12, 124, 916, 400);
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
            _grid.Columns.Add(Col("رتبه", 40));
            _grid.Columns.Add(Col("شماره موبایل", 110));
            _grid.Columns.Add(Col("نام", 130));
            _grid.Columns.Add(Col("امتیاز (شانس قرعه‌کشی)", 110));
            _grid.Columns.Add(Col("تعداد فاکتور", 90));
            _grid.Columns.Add(Col("جمع خرید (ریال)", 130));
            _grid.Columns.Add(Col("جمع تخفیف (ریال)", 120));
            _grid.Columns.Add(Col("اولین خرید", 90));
            _grid.Columns.Add(Col("آخرین خرید", 90));
            _grid.CellDoubleClick += new DataGridViewCellEventHandler(delegate (object o, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex < 0) return;
                PhoneProfile p = _grid.Rows[e.RowIndex].Tag as PhoneProfile;
                if (p == null || _store == null) return;
                using (CustomerInvoicesForm f = new CustomerInvoicesForm(_s, _store, p.Mobile))
                    f.ShowDialog(this);
            });

            _lblFoot = new Label();
            _lblFoot.SetBounds(12, 528, 560, 40);
            _lblFoot.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            _lblFoot.ForeColor = Color.DimGray;

            _lblMsg = new Label();
            _lblMsg.SetBounds(12, 566, 916, 22);
            _lblMsg.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            _lblMsg.TextAlign = ContentAlignment.MiddleRight;

            Button btnCsv = new Button();
            btnCsv.Text = "خروجی CSV (اکسل)";
            btnCsv.SetBounds(700, 530, 160, 32);
            btnCsv.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            btnCsv.Click += new EventHandler(delegate { Export(); });

            Button btnFiles = new Button();
            btnFiles.Text = "نمایش پوشه خروجی";
            btnFiles.SetBounds(870, 530, 58, 32);
            btnFiles.Visible = false;

            Button btnRefresh = new Button();
            btnRefresh.Text = "بروزرسانی";
            btnRefresh.SetBounds(530, 530, 160, 32);
            btnRefresh.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            btnRefresh.Click += new EventHandler(delegate { Load_(); });

            // قرعه‌کشی بین همین شرکت‌کنندگان (هر ۱ امتیاز = ۱ شانس)
            Button btnDraw = new Button();
            btnDraw.Text = "قرعه‌کشی";
            btnDraw.SetBounds(330, 530, 190, 32);
            btnDraw.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            btnDraw.Font = new Font("Tahoma", 9F, FontStyle.Bold);
            btnDraw.Click += new EventHandler(delegate { OpenLottery(); });

            Button btnClose = new Button();
            btnClose.Text = "بستن";
            btnClose.SetBounds(12, 530, 160, 32);
            btnClose.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            btnClose.Click += new EventHandler(delegate { Close(); });

            Controls.Add(head);
            Controls.Add(lblSearch);
            Controls.Add(_txtSearch);
            Controls.Add(btnSearch);
            Controls.Add(btnClear);
            Controls.Add(lblFrom);
            Controls.Add(_dtFrom);
            Controls.Add(lblTo);
            Controls.Add(_dtTo);
            Controls.Add(_chkAll);
            Controls.Add(_grid);
            Controls.Add(_lblFoot);
            Controls.Add(btnCsv);
            Controls.Add(btnRefresh);
            Controls.Add(btnDraw);
            Controls.Add(btnClose);
            Controls.Add(_lblMsg);
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

        private void Load_()
        {
            if (_store == null)
            {
                _lblFoot.Text = "دیتابیس محلی باز نیست.";
                return;
            }

            string digits = PhoneUtil.Digits(_txtSearch.Text);
            string from = _chkAll.Checked ? "" : FmtDate.G2(_dtFrom.Value.Date);
            string to = _chkAll.Checked ? "" : FmtDate.G2(_dtTo.Value.Date);

            _rows = digits.Length > 0
                ? _store.ProfilesByQuery(digits, 200)
                : _store.GetPointsTableOnDays(from, to, 5000);

            _grid.SuspendLayout();
            try
            {
                _grid.Rows.Clear();
                int sumPoints = 0;
                double sumAmount = 0;
                for (int i = 0; i < _rows.Count; i++)
                {
                    PhoneProfile p = _rows[i];
                    sumPoints += p.Points;
                    sumAmount += p.Amount;
                    int idx = _grid.Rows.Add(
                        (i + 1).ToString(),
                        PhoneUtil.Pretty(p.Mobile),
                        p.Name,
                        p.Points.ToString(),
                        p.Count.ToString(),
                        Num.F(p.Amount),
                        Num.F(p.Discount),
                        p.First,
                        p.Last);
                    _grid.Rows[idx].Tag = p;
                }

                _lblFoot.Text = _rows.Count + " شماره"
                    + (digits.Length > 0 ? " (نتیجه جست‌وجو)" : "")
                    + "  |  مجموع امتیازها: " + sumPoints
                    + "  |  مجموع خرید: " + Num.F(sumAmount) + " ریال"
                    + "\r\nروی هر سطر دوقلیک کنید تا فاکتورهای همان شماره دیده شود.";
            }
            finally
            {
                _grid.ResumeLayout();
            }
        }

        /// <summary>باز کردن فرم قرعه‌کشی با همان شرکت‌کنندگان (شمول بازه تاریخ انتخاب‌شده).</summary>
        private void OpenLottery()
        {
            if (_store == null)
            {
                _lblMsg.ForeColor = Color.Firebrick;
                _lblMsg.Text = "دیتابیس محلی باز نیست.";
                return;
            }
            using (LotteryForm f = new LotteryForm(_s, _store))
                f.ShowDialog(this);
            Load_();
        }

        private void Export()
        {
            if (_store == null) return;
            try
            {
                string dir = Path.Combine(_s.LogFolderFull, "exports");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "points-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv");
                _store.ExportPointsCsv(file, _chkAll.Checked ? "" : FmtDate.G2(_dtFrom.Value.Date),
                    _chkAll.Checked ? "" : FmtDate.G2(_dtTo.Value.Date));
                _lblMsg.ForeColor = Color.DarkGreen;
                _lblMsg.Text = "خروجی در فایل زیر ذخیره شد: " + file;
            }
            catch (Exception ex)
            {
                _lblMsg.ForeColor = Color.Firebrick;
                _lblMsg.Text = "خروجی گرفته نشد: " + ex.Message;
            }
        }
    }
}
