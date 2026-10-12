using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PosDiscount
{
    /// <summary>
    /// همه فاکتورهای یک شماره موبایل + امتیاز هر فاکتور و مجموع امتیاز مشتری.
    /// منبع داده: دیتابیس محلی برنامه (اطلاعات موبایل/امتیاز) و خواندن دیتابیس هولو برای مبالغ.
    /// </summary>
    public class CustomerInvoicesForm : Form
    {
        private readonly AppSettings _s;
        private readonly LocalStore _store;
        private string _mobile;

        private Label _lblHead, _lblFoot, _lblMsg;
        private DataGridView _grid;

        /// <summary>اگر چیزی در دیتابیس محلی تغییر کرده باشد.</summary>
        public bool DataChanged;

        public CustomerInvoicesForm(AppSettings s, LocalStore store, string mobile)
        {
            _s = s;
            _store = store;
            _mobile = PhoneUtil.Normalize(mobile);
            BuildUi();
            Load_();
        }

        private void BuildUi()
        {
            Text = "فاکتورهای یک شماره موبایل — دیتابیس محلی";
            ClientSize = new Size(960, 600);
            MinimumSize = new Size(840, 520);
            StartPosition = FormStartPosition.CenterParent;
            RightToLeft = RightToLeft.Yes;
            RightToLeftLayout = true;
            Font = new Font("Tahoma", 9F);

            Panel head = new Panel();
            head.SetBounds(0, 0, 960, 46);
            head.BackColor = Color.FromArgb(30, 62, 104);
            head.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            _lblHead = new Label();
            _lblHead.ForeColor = Color.White;
            _lblHead.Font = new Font("Tahoma", 11F, FontStyle.Bold);
            _lblHead.SetBounds(12, 10, 936, 26);
            _lblHead.TextAlign = ContentAlignment.MiddleRight;
            head.Controls.Add(_lblHead);

            Label lblSearch = new Label();
            lblSearch.Text = "شماره موبایل:";
            lblSearch.SetBounds(770, 62, 178, 24);
            lblSearch.TextAlign = ContentAlignment.MiddleRight;

            TextBox txtMobile = new TextBox();
            txtMobile.SetBounds(600, 62, 160, 24);
            txtMobile.TextAlign = HorizontalAlignment.Center;
            txtMobile.Font = new Font("Tahoma", 10F, FontStyle.Bold);
            txtMobile.Text = PhoneUtil.Pretty(_mobile);
            txtMobile.KeyDown += new KeyEventHandler(delegate (object o, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    _mobile = PhoneUtil.Normalize(txtMobile.Text);
                    Load_();
                }
            });

            Button btnFind = new Button();
            btnFind.Text = "جست‌وجو";
            btnFind.SetBounds(500, 61, 92, 26);
            btnFind.Click += new EventHandler(delegate
            {
                _mobile = PhoneUtil.Normalize(txtMobile.Text);
                Load_();
            });

            Button btnAll = new Button();
            btnAll.Text = "همه شماره‌های بدون موبایل";
            btnAll.SetBounds(330, 61, 162, 26);
            btnAll.Click += new EventHandler(delegate
            {
                _mobile = "";
                txtMobile.Text = "";
                LoadNoMobile();
            });

            _grid = new DataGridView();
            _grid.SetBounds(12, 98, 936, 420);
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
            _grid.Columns.Add(Col("شماره فاکتور", 80));
            _grid.Columns.Add(Col("تاریخ", 90));
            _grid.Columns.Add(Col("ساعت", 60));
            _grid.Columns.Add(Col("جمع کل (ریال)", 120));
            _grid.Columns.Add(Col("تخفیف (ریال)", 110));
            _grid.Columns.Add(Col("قابل پرداخت (ریال)", 120));
            _grid.Columns.Add(Col("امتیاز فاکتور", 90));
            _grid.Columns.Add(Col("شماره موبایل", 110));
            _grid.Columns.Add(Col("صندوق‌دار", 120));
            _grid.CellDoubleClick += new DataGridViewCellEventHandler(delegate (object o, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex < 0) return;
                EditRow(e.RowIndex);
            });

            _lblFoot = new Label();
            _lblFoot.SetBounds(12, 522, 936, 30);
            _lblFoot.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            _lblFoot.ForeColor = Color.DimGray;

            _lblMsg = new Label();
            _lblMsg.SetBounds(12, 566, 936, 22);
            _lblMsg.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            _lblMsg.TextAlign = ContentAlignment.MiddleRight;

            Button btnEdit = new Button();
            btnEdit.Text = "ثبت / ویرایش موبایل فاکتور انتخابی (دوقلیک)";
            btnEdit.SetBounds(340, 524, 300, 32);
            btnEdit.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            btnEdit.Font = new Font("Tahoma", 9F, FontStyle.Bold);
            btnEdit.Click += new EventHandler(delegate
            {
                if (_grid.SelectedRows.Count == 0)
                {
                    _lblMsg.ForeColor = Color.Firebrick;
                    _lblMsg.Text = "اول یک سطر را انتخاب کنید.";
                    return;
                }
                EditRow(_grid.SelectedRows[0].Index);
            });

            Button btnRefresh = new Button();
            btnRefresh.Text = "بروزرسانی";
            btnRefresh.SetBounds(180, 524, 150, 32);
            btnRefresh.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            btnRefresh.Click += new EventHandler(delegate { Load_(); });

            Button btnClose = new Button();
            btnClose.Text = "بستن";
            btnClose.SetBounds(12, 524, 150, 32);
            btnClose.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            btnClose.Click += new EventHandler(delegate { Close(); });

            Controls.Add(head);
            Controls.Add(lblSearch);
            Controls.Add(txtMobile);
            Controls.Add(btnFind);
            Controls.Add(btnAll);
            Controls.Add(_grid);
            Controls.Add(_lblFoot);
            Controls.Add(_lblMsg);
            Controls.Add(btnEdit);
            Controls.Add(btnRefresh);
            Controls.Add(btnClose);
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
                _lblMsg.ForeColor = Color.Firebrick;
                _lblMsg.Text = "دیتابیس محلی باز نیست.";
                return;
            }
            if (_mobile.Length == 0) { LoadNoMobile(); return; }

            List<StoredInvoice> list = _store.FindByMobile(_mobile, 1000);
            List<PhoneProfile> prof = _store.ProfilesByQuery(_mobile, 1);

            int points = 0, count = 0;
            double amount = 0, disc = 0;
            if (prof.Count > 0)
            {
                points = prof[0].Points;
                count = prof[0].Count;
                amount = prof[0].Amount;
                disc = prof[0].Discount;
            }

            _lblHead.Text = "فاکتورهای شماره " + PhoneUtil.Pretty(_mobile)
                + (prof.Count > 0 && prof[0].Name.Length > 0 ? "  (" + prof[0].Name + ")" : "")
                + "   |   تعداد فاکتور: " + count
                + "   |   مجموع امتیاز: " + points;

            Fill(list);

            _lblFoot.Text = "مجموع خرید: " + Num.F(amount) + " ریال"
                + "   |   مجموع تخفیف ثبت‌شده در هولو: " + Num.F(disc) + " ریال"
                + "   |   امتیاز قابل استفاده در قرعه‌کشی: " + points;
            if (list.Count == 0)
            {
                _lblMsg.ForeColor = Color.Firebrick;
                _lblMsg.Text = "برای این شماره فاکتوری در دیتابیس محلی پیدا نشد. "
                    + "شماره‌ها فقط برای فاکتورهایی ذخیره می‌شوند که برنامه آن‌ها را دیده است "
                    + "(از زمان نصب/اجرای برنامه به بعد).";
            }
            else _lblMsg.Text = "";
        }

        private void LoadNoMobile()
        {
            if (_store == null) return;
            List<StoredInvoice> list = _store.GetWithoutMobile(500);
            _lblHead.Text = "فاکتورهای بدون شماره موبایل — برای تخصیص امتیاز به مشتری، موبایل آن را ثبت کنید";
            Fill(list);
            _lblFoot.Text = list.Count + " فاکتور بدون شماره موبایل (از دیتابیس محلی برنامه)";
            _lblMsg.ForeColor = Color.FromArgb(150, 60, 0);
            _lblMsg.Text = "روی هر سطر دوقلیک کنید تا شماره موبایل آن ثبت و امتیازش محاسبه شود.";
        }

        private void Fill(List<StoredInvoice> list)
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
                        Num.F(v.Gross),
                        Num.F(v.HolooDiscount),
                        Num.F(v.Net),
                        v.Points.ToString(),
                        PhoneUtil.Pretty(v.Mobile),
                        v.Cashier);
                    _grid.Rows[idx].Tag = v;
                }
            }
            finally
            {
                _grid.ResumeLayout();
            }
        }

        private void EditRow(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= _grid.Rows.Count) return;
            StoredInvoice v = _grid.Rows[rowIndex].Tag as StoredInvoice;
            if (v == null) return;
            using (MobileEntryForm f = new MobileEntryForm(_s, _store, v))
            {
                if (f.ShowDialog(this) == DialogResult.OK && f.Result != null)
                {
                    DataChanged = true;
                    _mobile = PhoneUtil.Normalize(f.Result.Mobile);
                    Load_();
                }
            }
        }
    }
}
