using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PosDiscount
{
    /// <summary>فرم شروع: انتخاب صندوق‌دار/کاربر از لیست کاربران هولو.</summary>
    public class CashierSelectForm : Form
    {
        private readonly AppSettings _s;
        private List<CashierInfo> _all = new List<CashierInfo>();
        private DataGridView _grid;
        private CheckBox _chkInactive;
        private Button _btnOk, _btnReload, _btnSettings, _btnExit;
        private Label _lblTitle, _lblSub, _lblStatus;
        private DbStatus _st;

        public CashierInfo Selected;

        public CashierSelectForm(AppSettings s)
        {
            _s = s;
            BuildUi();
            Load += new EventHandler(OnLoad);
        }

        private void BuildUi()
        {
            Text = "تخفیف لحظه‌ای صندوق هولو — انتخاب صندوق‌دار";
            ClientSize = new Size(660, 470);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            RightToLeft = RightToLeft.Yes;
            RightToLeftLayout = true;
            Font = new Font("Tahoma", 9F);

            _lblTitle = new Label();
            _lblTitle.Text = "کاربر (صندوق‌دار) را انتخاب کنید";
            _lblTitle.Font = new Font("Tahoma", 12F, FontStyle.Bold);
            _lblTitle.SetBounds(12, 10, 636, 26);
            _lblTitle.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            _lblSub = new Label();
            _lblSub.ForeColor = Color.DimGray;
            _lblSub.SetBounds(12, 36, 636, 20);
            _lblSub.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            _chkInactive = new CheckBox();
            _chkInactive.Text = "نمایش کاربران غیرفعال";
            _chkInactive.SetBounds(12, 60, 220, 22);
            _chkInactive.CheckedChanged += new EventHandler(delegate { FillGrid(); });

            _grid = new DataGridView();
            _grid.SetBounds(12, 86, 636, 250);
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
            _grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(245, 245, 245);
            _grid.Columns.Add(Col("کد", 40));
            _grid.Columns.Add(Col("نام کاربر", 180));
            _grid.Columns.Add(Col("وضعیت", 70));
            _grid.Columns.Add(Col("مجوز تخفیف", 80));
            _grid.Columns.Add(Col("فاکتورهای روز", 90));
            _grid.Columns.Add(Col("آخرین فاکتور", 100));
            _grid.CellDoubleClick += new DataGridViewCellEventHandler(delegate (object o, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex >= 0) Accept();
            });

            _lblStatus = new Label();
            _lblStatus.SetBounds(12, 340, 636, 58);
            _lblStatus.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            _lblStatus.ForeColor = Color.DimGray;

            _btnOk = new Button();
            _btnOk.Text = "ورود به برنامه";
            _btnOk.SetBounds(12, 402, 150, 34);
            _btnOk.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            _btnOk.Font = new Font("Tahoma", 9F, FontStyle.Bold);
            _btnOk.Click += new EventHandler(delegate { Accept(); });

            _btnReload = new Button();
            _btnReload.Text = "بروزرسانی لیست";
            _btnReload.SetBounds(168, 402, 130, 34);
            _btnReload.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            _btnReload.Click += new EventHandler(delegate { LoadCashiers(); });

            _btnSettings = new Button();
            _btnSettings.Text = "تنظیمات اتصال";
            _btnSettings.SetBounds(304, 402, 150, 34);
            _btnSettings.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            _btnSettings.Click += new EventHandler(OpenSettings);

            _btnExit = new Button();
            _btnExit.Text = "خروج";
            _btnExit.SetBounds(460, 402, 90, 34);
            _btnExit.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            _btnExit.Click += new EventHandler(delegate { DialogResult = DialogResult.Cancel; Close(); });

            Controls.Add(_lblTitle);
            Controls.Add(_lblSub);
            Controls.Add(_chkInactive);
            Controls.Add(_grid);
            Controls.Add(_lblStatus);
            Controls.Add(_btnOk);
            Controls.Add(_btnReload);
            Controls.Add(_btnSettings);
            Controls.Add(_btnExit);
            AcceptButton = _btnOk;
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

        private void OnLoad(object sender, EventArgs e)
        {
            LoadCashiers();
        }

        private void OpenSettings(object sender, EventArgs e)
        {
            AppSettings tmp = _s.Clone();
            using (SettingsForm f = new SettingsForm(tmp))
            {
                if (f.ShowDialog(this) == DialogResult.OK)
                {
                    tmp.Save();
                    _s.Server = tmp.Server; _s.Database = tmp.Database;
                    _s.UseWindowsAuth = tmp.UseWindowsAuth; _s.SqlUser = tmp.SqlUser; _s.SqlPassword = tmp.SqlPassword;
                    _s.ConnectTimeoutSeconds = tmp.ConnectTimeoutSeconds;
                    LoadCashiers();
                }
            }
        }

        private void LoadCashiers()
        {
            UseWaitCursor = true;
            _btnOk.Enabled = false;
            try
            {
                HolooDb db = new HolooDb(_s.BuildConnectionString());
                _st = db.Test();

                // اصلاح خودکار اتصال: اگر نام سرور خراب باشد یا سرور محلی جواب ندهد،
                // برنامه نام درست را پیدا و ذخیره می‌کند و یک بار دیگر تلاش می‌کند.
                string healed = "";
                if (!_st.Ok)
                {
                    healed = ConnectionFixer.Heal(_s);
                    if (!string.IsNullOrEmpty(healed))
                    {
                        CsvLog.Info(_s, "اصلاح خودکار اتصال: " + healed);
                        db = new HolooDb(_s.BuildConnectionString());
                        _st = db.Test();
                        if (!_st.Ok) healed = "";   // اصلاح مؤثر نبود؛ فقط تعویض رشته سرور را گزارش نکن
                    }
                }

                if (!_st.Ok)
                {
                    _all = new List<CashierInfo>();
                    FillGrid();
                    _lblStatus.ForeColor = Color.Firebrick;
                    _lblStatus.Text = _st.Message;
                    _lblSub.Text = "سرور: " + _s.Server + " | دیتابیس: " + _s.Database;
                }
                else
                {
                    _all = db.GetCashiers();
                    FillGrid();
                    _lblStatus.ForeColor = healed.Length > 0 ? Color.FromArgb(0, 110, 60) : Color.DimGray;
                    _lblStatus.Text = _st.Message + "\r\n" + _st.Users + " کاربر در USERDB | " + _st.Invoices + " فاکتور در Res_Facture"
                        + (healed.Length > 0 ? "\r\nاصلاح خودکار: " + healed : "");
                    _lblSub.Text = "سرور: " + _s.Server + " | دیتابیس: " + _s.Database
                        + (_st.Day.HasValue ? " | روز کاری: " + _st.JalaliDay + " (" + FmtDate.G(_st.Day.Value) + ")" : "");
                    _btnOk.Enabled = _all.Count > 0;
                }
            }
            catch (Exception ex)
            {
                _lblStatus.ForeColor = Color.Firebrick;
                _lblStatus.Text = HolooDb.DescribeConnectionError(ex);
                CsvLog.Error(_s, "cashier list: " + ex.ToString());
            }
            finally
            {
                UseWaitCursor = false;
            }
        }

        private void FillGrid()
        {
            _grid.Rows.Clear();
            bool showInactive = _chkInactive.Checked;
            for (int i = 0; i < _all.Count; i++)
            {
                CashierInfo c = _all[i];
                if (!showInactive && !c.Active) continue;
                int idx = _grid.Rows.Add(
                    c.Code.ToString(),
                    c.Name,
                    c.Active ? "فعال" : "غیرفعال",
                    c.CanDiscount ? "دارد" : "ندارد",
                    c.DayCount.ToString(),
                    c.LastFCode > 0 ? c.LastFCode.ToString() : "—");
                _grid.Rows[idx].Tag = c;
                if (!c.Active) _grid.Rows[idx].DefaultCellStyle.ForeColor = Color.Gray;
            }
            // انتخاب پیش‌فرض: آخرین کاربر استفاده‌شده، وگرنه اولین کاربر فعال با فاکتور امروز
            int pick = -1;
            for (int i = 0; i < _grid.Rows.Count; i++)
            {
                CashierInfo c = _grid.Rows[i].Tag as CashierInfo;
                if (c == null) continue;
                if (c.Code == _s.LastCashierCode) { pick = i; break; }
            }
            if (pick < 0)
                for (int i = 0; i < _grid.Rows.Count; i++)
                {
                    CashierInfo c = _grid.Rows[i].Tag as CashierInfo;
                    if (c != null && c.Active && c.DayCount > 0) { pick = i; break; }
                }
            if (pick < 0 && _grid.Rows.Count > 0) pick = 0;
            if (pick >= 0)
            {
                _grid.Rows[pick].Selected = true;
                _grid.CurrentCell = _grid.Rows[pick].Cells[1];
            }
        }

        private void Accept()
        {
            if (_grid.SelectedRows.Count == 0)
            {
                MessageBox.Show(this, "ابتدا یک کاربر را از لیست انتخاب کنید.", "انتخاب کاربر",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            CashierInfo c = _grid.SelectedRows[0].Tag as CashierInfo;
            if (c == null) return;
            Selected = c;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
