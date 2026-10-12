using System;
using System.Drawing;
using System.Windows.Forms;

namespace PosDiscount
{
    /// <summary>
    /// فرم ثبت/ویرایش شماره موبایل یک فاکتور + نمایش امتیاز آن.
    /// همه چیز فقط در دیتابیس محلی (SQLite) ذخیره می‌شود؛ دیتابیس هولو خوانده می‌شود و تغییر نمی‌کند.
    /// </summary>
    public class MobileEntryForm : Form
    {
        private readonly AppSettings _s;
        private readonly LocalStore _store;
        private readonly StoredInvoice _inv;

        private TextBox _txtMobile, _txtName;
        private Label _lblPoints, _lblMsg, _lblTotal, _lblInfo;

        /// <summary>رکورد به‌روزشده (پس از ذخیره).</summary>
        public StoredInvoice Result;

        public MobileEntryForm(AppSettings s, LocalStore store, StoredInvoice inv)
        {
            _s = s;
            _store = store;
            _inv = inv;
            Result = inv;

            BuildUi();
            RefreshTotals();
        }

        private void BuildUi()
        {
            Text = "ثبت شماره موبایل و امتیاز فاکتور";
            ClientSize = new Size(520, 344);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            RightToLeft = RightToLeft.Yes;
            RightToLeftLayout = true;
            Font = new Font("Tahoma", 9F);

            Panel head = new Panel();
            head.SetBounds(0, 0, 520, 40);
            head.BackColor = Color.FromArgb(30, 62, 104);
            Label lblHead = new Label();
            lblHead.Text = "فاکتور " + _inv.FCodeC + " — شماره موبایل و امتیاز";
            lblHead.ForeColor = Color.White;
            lblHead.Font = new Font("Tahoma", 11F, FontStyle.Bold);
            lblHead.SetBounds(10, 8, 500, 24);
            lblHead.TextAlign = ContentAlignment.MiddleRight;
            head.Controls.Add(lblHead);

            Label lblL1 = Lbl("تاریخ / ساعت فاکتور:", 260, 48, 250);
            Label lblV1 = Val(_inv.Jalali + "  " + _inv.Time);
            lblV1.SetBounds(10, 48, 240, 24);

            Label lblL2 = Lbl("جمع کل فاکتور:", 260, 76, 250);
            Label lblV2 = Val(Num.F(_inv.Gross) + " ریال");
            lblV2.SetBounds(10, 76, 240, 24);

            Label lblL3 = Lbl("تخفیف ثبت‌شده در هولو:", 260, 104, 250);
            Label lblV3 = Val(Num.F(_inv.HolooDiscount) + " ریال");
            lblV3.SetBounds(10, 104, 240, 24);

            Label lblL4 = Lbl("مبلغ قابل پرداخت:", 260, 132, 250);
            Label lblV4 = Val(Num.F(_inv.Net) + " ریال");
            lblV4.SetBounds(10, 132, 240, 24);

            Label lblL5 = Lbl("پایه محاسبه امتیاز:", 260, 160, 250);
            Label lblV5 = Val(PointsEngine.BaseLabel(_s.PointsBase));
            lblV5.SetBounds(10, 160, 240, 24);

            Label lblL6 = Lbl("صندوق‌دار:", 260, 188, 250);
            Label lblV6 = Val(_inv.Cashier.Length > 0 ? _inv.Cashier : _inv.UserCode.ToString());
            lblV6.SetBounds(10, 188, 240, 24);

            _lblInfo = new Label();
            _lblInfo.SetBounds(10, 214, 500, 20);
            _lblInfo.ForeColor = Color.DimGray;
            _lblInfo.Text = PointsEngine.RuleText(_s.PointsPerAmountToman);

            Panel p = new Panel();
            p.SetBounds(10, 236, 500, 66);
            p.BackColor = Color.FromArgb(250, 248, 240);
            p.BorderStyle = BorderStyle.FixedSingle;

            Label lblM = new Label();
            lblM.Text = "شماره موبایل:";
            lblM.SetBounds(330, 8, 160, 24);
            lblM.TextAlign = ContentAlignment.MiddleRight;
            p.Controls.Add(lblM);

            _txtMobile = new TextBox();
            _txtMobile.SetBounds(160, 8, 165, 24);
            _txtMobile.MaxLength = 20;
            _txtMobile.TextAlign = HorizontalAlignment.Center;
            _txtMobile.Font = new Font("Tahoma", 10F, FontStyle.Bold);
            _txtMobile.Text = _inv.Mobile;
            _txtMobile.TextChanged += new EventHandler(delegate { RefreshTotals(); });
            p.Controls.Add(_txtMobile);

            Label lblN = new Label();
            lblN.Text = "نام مشتری:";
            lblN.SetBounds(330, 36, 160, 24);
            lblN.TextAlign = ContentAlignment.MiddleRight;
            p.Controls.Add(lblN);

            _txtName = new TextBox();
            _txtName.SetBounds(160, 36, 165, 24);
            _txtName.Text = _inv.CustomerAlias;
            p.Controls.Add(_txtName);

            _lblPoints = new Label();
            _lblPoints.SetBounds(10, 8, 145, 24);
            _lblPoints.Font = new Font("Tahoma", 9F, FontStyle.Bold);
            _lblPoints.ForeColor = Color.FromArgb(20, 70, 130);
            _lblPoints.TextAlign = ContentAlignment.MiddleRight;
            p.Controls.Add(_lblPoints);

            _lblTotal = new Label();
            _lblTotal.SetBounds(10, 36, 145, 24);
            _lblTotal.ForeColor = Color.FromArgb(20, 70, 130);
            _lblTotal.TextAlign = ContentAlignment.MiddleRight;
            p.Controls.Add(_lblTotal);

            _lblMsg = new Label();
            _lblMsg.SetBounds(10, 308, 500, 22);
            _lblMsg.TextAlign = ContentAlignment.MiddleRight;

            Button btnSave = new Button();
            btnSave.Text = "ذخیره";
            btnSave.SetBounds(10, 268, 150, 34);
            btnSave.Font = new Font("Tahoma", 9F, FontStyle.Bold);
            btnSave.Click += new EventHandler(delegate { Save(); });

            Button btnClear = new Button();
            btnClear.Text = "حذف شماره از این فاکتور";
            btnClear.SetBounds(170, 268, 170, 34);
            btnClear.Click += new EventHandler(delegate
            {
                _txtMobile.Text = "";
                _txtName.Text = "";
                Save();
            });

            Button btnCancel = new Button();
            btnCancel.Text = "انصراف";
            btnCancel.SetBounds(350, 268, 160, 34);
            btnCancel.Click += new EventHandler(delegate { DialogResult = DialogResult.Cancel; Close(); });

            Controls.Add(head);
            Controls.Add(lblL1); Controls.Add(lblV1);
            Controls.Add(lblL2); Controls.Add(lblV2);
            Controls.Add(lblL3); Controls.Add(lblV3);
            Controls.Add(lblL4); Controls.Add(lblV4);
            Controls.Add(lblL5); Controls.Add(lblV5);
            Controls.Add(lblL6); Controls.Add(lblV6);
            Controls.Add(_lblInfo);
            Controls.Add(p);
            Controls.Add(btnSave);
            Controls.Add(btnClear);
            Controls.Add(btnCancel);
            Controls.Add(_lblMsg);
            AcceptButton = btnSave;
            CancelButton = btnCancel;
        }

        private static Label Lbl(string text, int x, int y, int w)
        {
            Label l = new Label();
            l.Text = text;
            l.SetBounds(x, y, w, 24);
            l.TextAlign = ContentAlignment.MiddleRight;
            return l;
        }

        private static Label Val(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = new Font("Tahoma", 9F, FontStyle.Bold);
            l.TextAlign = ContentAlignment.MiddleLeft;
            return l;
        }

        private int PointsFor(string mobile)
        {
            if (!_s.PointsEnabled) return 0;
            if (mobile.Length == 0) return 0;
            double baseAmount = PointsEngine.BaseAmount(_s, _inv.Gross, _inv.HolooDiscount);
            return PointsEngine.ForAmount(baseAmount, _s.PointsPerAmountToman);
        }

        private void RefreshTotals()
        {
            string mobile = PhoneUtil.Normalize(_txtMobile.Text);
            int pts = PointsFor(mobile);
            _lblPoints.Text = "امتیاز این فاکتور: " + pts;

            if (_store == null || mobile.Length == 0)
            {
                _lblTotal.Text = "";
                return;
            }
            try
            {
                System.Collections.Generic.List<PhoneProfile> list = _store.ProfilesByQuery(mobile, 1);
                _lblTotal.Text = list.Count == 0 ? "" : ("امتیاز کل این شماره: " + list[0].Points);
            }
            catch (Exception) { _lblTotal.Text = ""; }
        }

        private void Save()
        {
            if (_store == null)
            {
                _lblMsg.ForeColor = Color.Firebrick;
                _lblMsg.Text = "دیتابیس محلی باز نیست.";
                return;
            }

            string typed = _txtMobile.Text.Trim();
            string normalized = PhoneUtil.Normalize(typed);
            if (typed.Length > 0 && !PhoneUtil.IsMobile(normalized))
            {
                _lblMsg.ForeColor = Color.Firebrick;
                _lblMsg.Text = "شماره موبایل معتبر نیست (نمونه درست: 09123456789).";
                return;
            }

            StoredInvoice rec = _store.SetMobile(_inv.FCode, typed, _txtName.Text.Trim(), _s);
            if (rec == null)
            {
                _lblMsg.ForeColor = Color.Firebrick;
                _lblMsg.Text = "ذخیره نشد. " + _store.LastError;
                return;
            }

            Result = rec;
            _lblMsg.ForeColor = Color.DarkGreen;
            _lblMsg.Text = rec.Mobile.Length == 0
                ? "شماره از این فاکتور حذف شد و امتیاز آن صفر شد."
                : "ذخیره شد — " + PhoneUtil.Pretty(rec.Mobile) + " | امتیاز این فاکتور: " + rec.Points;

            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
