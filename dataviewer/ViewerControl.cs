using System;
using System.Collections.Generic;
using System.Linq;
using DataTableAsync;
using System.Windows.Forms;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Reflection;
using System.Drawing.Imaging;
using System.IO;
using System.ComponentModel;
using System.Globalization;
using System.Diagnostics;

namespace dataviewer
{
    public enum CheckboxStyle { none, slide, check, radio };
    public enum CheckState { none, on, off, both };
    public enum SortOrder { none, asc, desc }
    public class ViewerControl : Control
    {
        public VScrollBar VScroll = new VScrollBar() { Minimum = 0, Visible = false };
        public HScrollBar HScroll = new HScrollBar() { Minimum = 0, Visible = false };
        public readonly Columns Columns;
        public readonly Rows Rows;
        private readonly static Dictionary<int, Dictionary<string, Rectangle>> visibleCells = new Dictionary<int, Dictionary<string, Rectangle>>();
        private readonly static Dictionary<byte, byte> sortLens = new Dictionary<byte, byte> { { 0, 7 }, { 1, 10 }, { 2, 13 } };
        internal readonly static CultureInfo enUS = new CultureInfo("en-US", false);
        internal readonly static NumberStyles nbrStyles = NumberStyles.AllowThousands | NumberStyles.AllowDecimalPoint | NumberStyles.AllowCurrencySymbol | NumberStyles.AllowParentheses | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite | NumberStyles.AllowLeadingSign;
        private bool stopMe = false;

        private Table datasource;
        public Table Datasource
        {
            get => datasource;
            set
            {
                if (datasource != value)
                {
                    Clear();
                    datasource = value;
                    if (datasource != null)
                    {
                        foreach (var col in datasource.Columns.Values.OrderBy(c => c.Index))
                            Columns.Add(col);
                        foreach (var row in datasource.AsEnumerable)
                            Rows.Add(row);
                    }
                    OnTheRebounds();
                }
            }
        }
        public void Clear()
        {
            Columns.Clear();
            Rows.Clear();
        }
        
        public Size SizeUnbounded { get; private set; }

        public ViewerControl()
        {
            Columns = new Columns(this);
            Rows = new Rows(this);

            Controls.Add(VScroll);
            Controls.Add(HScroll);
            SetStyle(ControlStyles.AllPaintingInWmPaint, true);
            SetStyle(ControlStyles.ContainerControl, true);
            SetStyle(ControlStyles.DoubleBuffer, true);
            SetStyle(ControlStyles.UserPaint, true);
            SetStyle(ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, true);
            SetStyle(ControlStyles.Opaque, true);
            SetStyle(ControlStyles.UserMouse, true);

            BackColor = Color.WhiteSmoke;
            Margin = new Padding(0);

            HScroll.Scroll += Scrolled_horizontal;
            VScroll.Scroll += Scrolled_vertical;
        }

        #region" ■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■ E V E N T S "
        public class ViewerEventArgs : EventArgs
        {
            public Column Column { get; private set; }
            public Row Row { get; private set; }
            public Point Location { get; private set; }

            public ViewerEventArgs(Column col, Row row, Point pt)
            {
                Column = col;
                Row = row;
                Location = pt;
            }
            public override string ToString() { return $"{Column}°{Row}°{Location}"; }
        }
        protected virtual void OnColumnClick(Column col, Row row, Point pt) { ColumnClick?.Invoke(this, new ViewerEventArgs(col, row, pt)); }
        public event EventHandler<ViewerEventArgs> ColumnClick;
        protected virtual void OnRowClick(Column col, Row row, Point pt) { RowClick?.Invoke(this, new ViewerEventArgs(col, row, pt)); }
        public event EventHandler<ViewerEventArgs> RowClick;
        #endregion

        #region " mice eventargs "
        private static readonly MouseOver mouseOver = new MouseOver();
        private MouseOver Moused(Point pt)
        {
            var moused = new MouseOver() { Location = pt };
            foreach (var col in Columns.Where(c => ClientRectangle.Contains(c.Bounds["all"])))
                if (col.Bounds["all"].Contains(pt))
                {
                    moused.Column = col;
                    return moused;
                }
            foreach (var row in visibleCells)
            {
                foreach (var cell in row.Value)
                {
                    if (cell.Value.Contains(pt))
                    {
                        moused.Row = Rows[row.Key];
                        moused.Cell = cell.Value;
                        moused.Column = Columns[cell.Key];
                        break;
                    }
                }
            }
            return moused;
        }
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            mouseOver.Location = e.Location;
            var moused = Moused(e.Location);
            if (mouseOver.Column != moused.Column | mouseOver.Row != moused.Row | mouseOver.Cell != moused.Cell)
            {
                mouseOver.Column = moused.Column;
                mouseOver.Row = moused.Row;
                mouseOver.Cell = moused.Cell;
                Invalidate();
            }
            base.OnMouseMove(e);
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            var col = mouseOver.Column;
            var rw = mouseOver.Row;
            var colWasClicked = col != null & rw == null;
            var rowWasClicked = mouseOver.Row != null;
            if (colWasClicked)
            {
                if (col.SortOrder == SortOrder.none)
                    col.SortOrder = SortOrder.asc;
                else if (col.Bounds["srt"].Contains(e.Location))
                    col.SortOrder = col.SortOrder == SortOrder.asc ? SortOrder.desc : SortOrder.asc;
                else if (col.Bounds["srtNbr"].Contains(e.Location))
                    col.SortOrder = SortOrder.none;
                
                OnColumnClick(col, rw, e.Location);
                
                OnTheRebounds(true);
                Parent.Parent.Text = col.Name + ": " + string.Join(" | ", col.Bounds.Select(b => $"{b.Key}_{b.Value}"));
                
                var colsToSort = Columns.Sorts.OrderBy(s => s.Value).Select(c => Columns[c.Key]).ToList();
                if (colsToSort.Any())
                {
                    Rows.Sort((r1, r2) => SortBy(r1, r2, colsToSort, 0));
                    Invalidate();
                }
            }
            else if (rowWasClicked)
            {
                if (col.CanEditValues)
                {
                    if (col.DataType == typeof(bool))
                    {
                        // risk of DatatableAsync not having a bool in a bool Column, so check
                        var isBool = bool.TryParse((rw[col.Name] ?? "false").ToString(), out bool cellTrueFalse);
                        if (isBool)
                        {
                            rw[col.Name] = !cellTrueFalse;
                            stopMe = true;
                            Invalidate();
                        }
                    }
                }
                OnRowClick(mouseOver.Column, mouseOver.Row, e.Location);
            }
            base.OnMouseDown(e);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
        }
        #endregion

        #region" sorting "
        private static int SortBy(Row r1, Row r2, List<Column> levels, int level)
        {
            if (levels.Count > level)
            {
                var colName = levels[level].Name;
                var srtOrdr = levels[level].SortOrder;
                var lvl = SortBy(r1, r2, colName, srtOrdr);
                if (lvl != 0)
                    return lvl;
                return SortBy(r1, r2, levels, level + 1);
            }
            return 0;
        }
        internal static int SortBy(Row r1, Row r2, Column col) => SortBy(r1, r2, col.Name, col.SortOrder); // custom sorting
        private static int SortBy(Row r1, Row r2, string colName, SortOrder sortOrder = SortOrder.asc) => SortBy(r1.Parent.Viewer.Columns[colName].DataType, r1[colName], r2[colName], sortOrder);
        private static int SortBy(Type type, object obj1, object obj2, SortOrder sortOrder = SortOrder.asc)
        {
            var str1 = ((sortOrder == SortOrder.asc ? obj1 : obj2) ?? "").ToString();
            var str2 = ((sortOrder == SortOrder.asc ? obj2 : obj1) ?? "").ToString();

            if (type == typeof(string))
                return StringComparer.OrdinalIgnoreCase.Compare(str1, str2);
            else if (type == typeof(bool))
            {
                bool.TryParse(str1, out bool b1);
                bool.TryParse(str2, out bool b2);
                return b1.CompareTo(b2);
            }
            else if (type == typeof(Bitmap) | type == typeof(Image))
            {
                var img1 = Functions.ImageToBase64((Bitmap)obj1);
                var img2 = Functions.ImageToBase64((Bitmap)obj2);
                if (sortOrder == SortOrder.asc)
                    return StringComparer.OrdinalIgnoreCase.Compare(img1, img2);
                else
                    return StringComparer.OrdinalIgnoreCase.Compare(img2, img1);
            }
            else if (type == typeof(DateTime))
            {
                // 2023-08-01 12:00:00 AM
                var dateFormats = new string[] { "yyyy-MM-dd", "yyyy-MM-dd hh:mm:ss tt" };
                DateTime.TryParseExact(str1, dateFormats, enUS, DateTimeStyles.None, out DateTime d1);
                DateTime.TryParseExact(str2, dateFormats, enUS, DateTimeStyles.None, out DateTime d2);
                return d1.CompareTo(d2);
            }
            else if (type == typeof(long) | type == typeof(int) | type == typeof(short) | type == typeof(byte))
            {
                var n1 = long.Parse(str1, nbrStyles, enUS);
                var n2 = long.Parse(str2, nbrStyles, enUS);
                return n1.CompareTo(n2);
            }
            else if (type == typeof(double) | type == typeof(decimal) | type == typeof(float))
            {
                var n1 = double.Parse(str1, nbrStyles, enUS);
                var n2 = double.Parse(str2, nbrStyles, enUS);
                return n1.CompareTo(n2);
            }
            else
                return 0;
        }
        #endregion

        protected override void OnSizeChanged(EventArgs e)
        {
            SetScrolls();
            base.OnSizeChanged(e);
        }

        private void Scrolled_vertical(object sender, ScrollEventArgs e)
        {
            var deltaY = e.OldValue - e.NewValue;
            //foreach (var col in rectsCells.ToList())
            //    foreach (var cell in col.Value.ToList())
            //        rectsCells[col.Key][cell.Key] = new Rectangle(cell.Value.X, cell.Value.Y + deltaY, cell.Value.Width, cell.Value.Height);
            Invalidate();
        }
        private void Scrolled_horizontal(object sender, ScrollEventArgs e)
        {
            var deltaX = e.OldValue - e.NewValue;
            //foreach (var col in rectsCells.ToList())
            //    foreach (var cell in col.Value.ToList())
            //        rectsCells[col.Key][cell.Key] = new Rectangle(cell.Value.X + deltaX, cell.Value.Y, cell.Value.Width, cell.Value.Height);
            Invalidate();
        }
        private void SetScrolls()
        {
            // vscroll
            SafeThread.SetControlPropertyValue(VScroll, "Maximum", SizeUnbounded.Height);
            var visibleV = SizeUnbounded.Height > ClientRectangle.Height;
            SafeThread.SetControlPropertyValue(VScroll, "Visible", visibleV);
            if (visibleV)
            {
                SafeThread.SetControlPropertyValue(VScroll, "Top", 2);
                SafeThread.SetControlPropertyValue(VScroll, "Left", SizeUnbounded.Width + VScroll.Width < ClientSize.Width ? SizeUnbounded.Width : ClientSize.Width - VScroll.Width);
                SafeThread.SetControlPropertyValue(VScroll, "Height", ClientRectangle.Height - 2);
                SafeThread.SetControlPropertyValue(VScroll, "SmallChange", Convert.ToInt32(Rows.Average(r => r.Style.Height)));
                SafeThread.SetControlPropertyValue(VScroll, "LargeChange", ClientRectangle.Height);
            }
            // hscroll
            SafeThread.SetControlPropertyValue(HScroll, "Maximum", SizeUnbounded.Width);
            var visibleH = SizeUnbounded.Width > ClientRectangle.Width;
            SafeThread.SetControlPropertyValue(HScroll, "Visible", visibleH);
            if (visibleH)
            {
                SafeThread.SetControlPropertyValue(HScroll, "Top", ClientRectangle.Bottom - HScroll.Height);
                SafeThread.SetControlPropertyValue(HScroll, "Left", 0);
                SafeThread.SetControlPropertyValue(HScroll, "Width", VScroll.Visible ? ClientRectangle.Width - VScroll.Width : ClientRectangle.Width);
                SafeThread.SetControlPropertyValue(HScroll, "SmallChange", 20);
                SafeThread.SetControlPropertyValue(HScroll, "LargeChange", ClientRectangle.Width);
            }
        }
        internal void OnTheRebounds(bool stopMe = false)
        {
            // set up variables
            const int hPad = 3;
            const double two = 2.00;
            Size szSort = new Size(18, 13);
            int lftCol = 0;
            int ttlWidth = 0;
            int ttlHeight = Columns.Style.Height + Rows.Sum(r => r.Style.Height);

            // populate dictionaries
            foreach (var col in Columns.OrderBy(c => c.index))
            {
                col.Bounds["img"] = new Rectangle(lftCol, 0, 0, col.Style.Height);
                col.Bounds["txt"] = new Rectangle(lftCol, 0, 0, col.Style.Height);
                col.Bounds["srt"] = new Rectangle(lftCol, 0, 0, col.Style.Height);
                col.Bounds["srtNbr"] = new Rectangle(lftCol, 0, 0, col.Style.Height);
                col.Bounds["all"] = new Rectangle(lftCol, 0, 0, col.Style.Height);

                try
                {
                    if (col.Visible)
                    {
                        var objects = new Dictionary<string, Tuple<byte, Size>>();
                        if (col.Image != null)
                            objects["img"] = Tuple.Create((byte)objects.Count, col.Image.Size);

                        objects["txt"] = Tuple.Create((byte)objects.Count, new Size(new int[] { col.WidthHead, col.WidthContent }.Max(), col.Style.Height));

                        var widthSrts = 0;
                        if (col.SortOrder != SortOrder.none)
                        {
                            objects["srt"] = Tuple.Create((byte)objects.Count, szSort);
                            var srtNbrSz = Functions.MeasureText(Columns.Sorts[col.Name].ToString(), col.Style.Font); // <-- 3 to pad a bit
                            objects["srtNbr"] = Tuple.Create((byte)objects.Count, srtNbrSz);
                            widthSrts = szSort.Width + hPad + srtNbrSz.Width + hPad;
                        }
                        if (widthSrts > 0 & col.WidthContent - col.WidthHead > widthSrts)
                            objects["txt"] = Tuple.Create((byte)objects.Count, new Size(col.WidthContent - widthSrts, col.Style.Height));

                        var sumWidths = objects.Sum(o => o.Value.Item2.Width) + (objects.Count - 1) * hPad;
                        if (sumWidths < col.WidthMinimum)
                        {
                            objects["txt"] = Tuple.Create(objects["txt"].Item1, new Size(objects["txt"].Item2.Width + col.WidthMinimum - sumWidths, col.Style.Height));
                            sumWidths = col.WidthMinimum;
                        }
                        if (sumWidths > col.WidthMaximum)
                        {
                            objects["txt"] = Tuple.Create(objects["txt"].Item1, new Size(objects["txt"].Item2.Width + col.WidthMaximum - sumWidths, col.Style.Height));
                            sumWidths = col.WidthMaximum;
                        }

                        var lftObj = lftCol;
                        foreach (var obj in objects)
                        {
                            var objWidth = obj.Value.Item2.Width;
                            var y = Convert.ToInt32((col.Style.Height - obj.Value.Item2.Height) / two);
                            col.Bounds[obj.Key] = new Rectangle(lftObj, y, objWidth, obj.Value.Item2.Height);
                            lftObj += objWidth;
                        }
                        col.Bounds["all"] = new Rectangle(lftCol, 0, sumWidths, col.Style.Height);
                        lftCol = col.Bounds["all"].Right;
                        ttlWidth += col.Bounds["all"].Width;
                    }
                }
                catch { }
            }
            SizeUnbounded = new Size(ttlWidth, ttlHeight);

            SetScrolls();
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            e.Graphics.FillRectangle(new SolidBrush(BackColor), ClientRectangle);

            #region " draw header and row cells "
            visibleCells.Clear();
            foreach (var col in Columns.Where(c => ClientRectangle.Contains(c.Bounds["all"])))
            {
                var boundsCol = col.Bounds["all"];

                // background color - shaded
                if (boundsCol.Width > 0)
                {
                    using (var brshBackLinear = new LinearGradientBrush(boundsCol, col.Style.BackColor, col.Style.BackColorAccent, LinearGradientMode.Vertical))
                        e.Graphics.FillRectangle(brshBackLinear, boundsCol);
                }

                if (col.Image != null)
                    e.Graphics.DrawImage(col.Image, col.Bounds["img"]);

                if (boundsCol.Contains(mouseOver.Location))
                {
                    // background color - shaded
                    using (var brshBackLinear = new SolidBrush(Color.FromArgb(128, col.Style.BackColor)))
                        e.Graphics.FillRectangle(brshBackLinear, boundsCol);
                }

                if (col.SortOrder != SortOrder.none)
                {
                    using (var penSrt = new Pen(col.Style.ForeColor, 3)
                    {
                        StartCap = LineCap.Round,
                        EndCap = LineCap.Round
                    })
                    {
                        var colSrtRect = col.Bounds["srt"];
                        foreach (var len in sortLens)
                        {
                            var colPtlft = new Point(colSrtRect.X + 2, colSrtRect.Y + col.sortYs[len.Key]);
                            var colPtRgt = new Point(colSrtRect.X + len.Value, colSrtRect.Y + col.sortYs[len.Key]);
                            e.Graphics.DrawLine(penSrt, colPtlft, colPtRgt);
                        }
                    }
                    var srtNbr = col.Bounds["srtNbr"];
                    srtNbr.Inflate(0, 0);
                    using (var backBrsh = new SolidBrush(col.Style.ForeColor))
                        e.Graphics.FillRectangle(backBrsh, srtNbr);
                    srtNbr.Inflate(0, 0);
                    using (var txtBrsh = new SolidBrush(col.Style.BackColor))
                    {
                        using (var sf = new StringFormat() { Alignment = col.Style.AlignHead_horizontal, LineAlignment = col.Style.AlignHead_vertical })
                            e.Graphics.DrawString(Columns.Sorts[col.Name].ToString(), col.Style.Font, txtBrsh, srtNbr, sf);
                    }
                }

                // forecolor
                using (var brshFore = new SolidBrush(col.Selected ? col.Style.ForeColorSelect : col.Style.ForeColor))
                {
                    using (var sf = new StringFormat() { Alignment = col.Style.AlignHead_horizontal, LineAlignment = col.Style.AlignHead_vertical })
                        e.Graphics.DrawString(col.Name.ToUpper(), col.Style.Font, brshFore, col.Bounds["txt"], sf);
                }
                // border
                ControlPaint.DrawBorder3D(e.Graphics, boundsCol, Border3DStyle.SunkenOuter);

                var colsHeadRect = new Rectangle(0, 0, ClientRectangle.Width, Columns.Style.Height);
                e.Graphics.ExcludeClip(colsHeadRect);

                // rows
                var indxRw = 0;
                var yRw = -VScroll.Value + Columns.Style.Height;
                foreach (var rw in Rows.Where(r => r.Visible).OrderBy(r => r.Index))
                {
                    var boundsCell = new Rectangle(col.Bounds["all"].Left, yRw, col.Bounds["all"].Width, rw.Style.Height);
                    if (ClientRectangle.IntersectsWith(boundsCell))
                    {
                        if (!visibleCells.ContainsKey(indxRw))
                            visibleCells[indxRw] = new Dictionary<string, Rectangle>();
                        visibleCells[indxRw][col.Name] = boundsCell;
                        var rwIsOdd = indxRw % 2 == 1;

                        // background
                        using (var brshBack = new SolidBrush(rwIsOdd ? rw.Style.BackColorAlternate : rw.Style.BackColor))
                            e.Graphics.FillRectangle(brshBack, boundsCell);

                        // border
                        e.Graphics.DrawRectangle(Pens.Silver, boundsCell);

                        var cellStr = rw.CellStrings[col.Name];
                        if (col.DataType == typeof(bool))
                        {
                            var stateChk = cellStr.ToLower() == "true" ? CheckState.on : CheckState.off;
                            var imgChk = Functions.ImgChk(CheckboxStyle.check, stateChk);
                            var xx = (boundsCell.Width - imgChk.Width) / 2;
                            var yy = (boundsCell.Height - imgChk.Height) / 2;
                            var boundsChk = new Rectangle(boundsCell.X + xx, boundsCell.Y + yy, imgChk.Width, imgChk.Height);
                            if (cellStr.ToLower() == "null")
                                e.Graphics.DrawRectangle(Pens.Red, boundsChk);
                            else
                                e.Graphics.DrawImage(imgChk, boundsChk);
                            //if (stopMe) { Debugger.Break(); }
                        }
                        else
                        {
                            /// 4 options for background and forecolors
                            /// a) alternating plain
                            /// b) alternating shaded
                            /// c) selected
                            /// d) mousover
                            var cellFont = rw.Style.Font;
                            using (var brshFore = new SolidBrush(rw.Style.ForeColor))
                            {
                                using (var sf = new StringFormat() { Alignment = col.Style.AlignContent_horizontal, LineAlignment = col.Style.AlignContent_vertical })
                                {
                                    e.Graphics.DrawString(cellStr,
                                           cellFont,
                                           brshFore,
                                           boundsCell,
                                           sf);
                                }
                            }
                        }

                        // mouse over cell
                        if (boundsCell.Contains(mouseOver.Location))
                        {
                            var colorBack = rwIsOdd ? rw.Style.BackColorAlternate : rw.Style.BackColor;
                            using (var brshBack = new SolidBrush(Color.FromArgb(128, Color.Yellow)))
                                e.Graphics.FillRectangle(brshBack, boundsCell);
                        }
                    }

                    indxRw++;
                    yRw += rw.Style.Height;
                }
                e.Graphics.ResetClip();
            }
            #endregion
        }
    }
    public class Columns : List<Column>
    {
        private readonly System.Threading.Timer timer;
        internal ViewerControl Viewer { get; }
        public ColumnStyle Style { get; set; }
        public int Width => this.Sum(c => c.Width);
        public Dictionary<string, int> Sorts { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Filters { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public Columns(ViewerControl vwr)
        {
            Viewer = vwr;
            timer = new System.Threading.Timer(new TimerCallback(Timer_tick), null, Timeout.Infinite, Timeout.Infinite);
            Style = new ColumnStyle(this);
        }
        public Column this[string key]=> this.Where(c => c.Name.ToLower() == key.ToLower()).FirstOrDefault();
        public new Column this[int index] => this.Where(c => c.Index == index).FirstOrDefault();

        public Column Add(Table.Column tblCol)
        {
            var newCol = new Column(tblCol.Name) { DataType = tblCol.DataType, Index = Count, Parent = this };
            Add(newCol);
            timer.Change(0, 100);
            return newCol;
        }
        private void Timer_tick(object sender)
        {
            timer.Change(Timeout.Infinite, Timeout.Infinite);
            Style.StyleChanged += Cols_StyleChanged;
            ApplyStyleToChildren();
            Viewer?.Invalidate();
        }
        private bool ApplyStyleToChildren()
        {
            var impactsBounds = false;
            foreach (var col in this.ToList())
            {
                if (Style.Font != col.Style.Font | Style.Height != col.Style.Height)
                    impactsBounds = true;
                col.Style.StyleChanged -= Col_StyleChanged;
                Reflection.CopyProperties(Style, col.Style);
                var datatype = col.DataType;
                var alignH = StringAlignment.Near;
                // https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/built-in-types
                if (datatype == typeof(bool))
                    alignH = StringAlignment.Center;
                else if (datatype == typeof(byte) | datatype == typeof(sbyte) | datatype == typeof(int) | datatype == typeof(uint) | datatype == typeof(long) | datatype == typeof(ulong) | datatype == typeof(short) | datatype == typeof(ushort))
                    alignH = StringAlignment.Center;
                else if (datatype == typeof(decimal) | datatype == typeof(double) | datatype == typeof(float))
                    alignH = StringAlignment.Far;
                else if (datatype == typeof(char) | datatype == typeof(string))
                    alignH = StringAlignment.Near;
                else if (datatype == typeof(DateTime))
                    alignH = StringAlignment.Center;
                col.Style.AlignContent_horizontal = alignH;
                col.Style.StyleChanged += Col_StyleChanged;
            }
            return impactsBounds;
        }

        private void Col_StyleChanged(object sender, StyleChangedEventArgs e)
        {
            var style = (ColumnStyle)sender;
            var col = style.Parent[0];
            var impactsBounds = Style.Font != col.Style.Font | Style.Height != col.Style.Height;
            if (impactsBounds)
            {
                col.SetHeadWidth();
                Viewer.OnTheRebounds();
            }

            else
                Viewer.Invalidate();
        }
        private void Cols_StyleChanged(object sender, StyleChangedEventArgs e)
        {
            var styleChange_impactsBounds = ApplyStyleToChildren();
            if (styleChange_impactsBounds)
                Viewer.OnTheRebounds();
            else
                Viewer.Invalidate();
        }
    }
    public class Column : IDisposable
    {
        private readonly System.Threading.Timer timer;
        public Columns Parent { get; internal set; }
        public ColumnStyle Style { get; set; }
        public string Name { get; }
        internal int index;
        public int Index
        {
            get => index;
            set
            {
                if (Parent != null)
                {
                    List<Column> orderedCols = new List<Column>(Parent.OrderBy(c => c.Index));
                    List<int> ints = new List<int>(orderedCols.Select(c => c.Index));
                    ints.Remove(index);
                    ints.Insert(value, index);
                    foreach (var col in orderedCols)
                        Parent[col.Name].index = ints.IndexOf(col.Index);
                    timer.Change(0, 100);
                }
                else
                    index = value;
            }
        }
        private Image image;
        public Image Image
        {
            get => image;
            set
            {
                if (!Functions.SameImage(value, image))
                {
                    var imgSzChanged = (value == null ? new Size(0, 0) : value.Size) != (image == null ? new Size(0, 0) : image.Size);
                    image = value;
                    if (imgSzChanged)
                        Parent?.Viewer?.OnTheRebounds();
                    else
                        Parent?.Viewer?.Invalidate();
                }
            }
        }
        internal readonly List<byte> sortYs = new List<byte> { 2, 6, 10 };
        private SortOrder sortOrder;
        public SortOrder SortOrder
        {
            get => sortOrder;
            set
            {
                if (sortOrder != value)
                {
                    var colBoundsWillChange = value == SortOrder.none & sortOrder != SortOrder.none | sortOrder == SortOrder.none & value != SortOrder.none;
                    sortOrder = value;
                    if (value == SortOrder.asc)
                        sortYs.Sort((y1, y2) => y1.CompareTo(y2));
                    else if (value == SortOrder.desc)
                        sortYs.Sort((y1, y2) => y2.CompareTo(y1));
                    if (Parent != null)
                    {
                        var sorts = Parent.Sorts.ToDictionary(k => k.Key, v => v.Value);
                        if (value == SortOrder.none)
                        {
                            foreach (var srtNbr in sorts.Where(v => v.Value > sorts[Name]))
                                Parent.Sorts[srtNbr.Key]--;
                            Parent.Sorts.Remove(Name);
                        }
                        else
                        {
                            if (!sorts.ContainsKey(Name))
                                Parent.Sorts[Name] = 1 + sorts.Count;
                        }
                        Parent?.Viewer?.Invalidate();
                    }
                }
            }
        }
        private int width = 100;
        public int Width
        {
            get => width;
            set
            {
                width = value;
                Parent?.Viewer?.OnTheRebounds();
            }
        }
        private int widthMin = 100;
        public int WidthMinimum
        {
            get => widthMin;
            set
            {
                widthMin = value;
                Parent?.Viewer?.OnTheRebounds();
            }
        }
        private int widthMax = 600;
        public int WidthMaximum
        {
            get => widthMax;
            set
            {
                widthMax = value;
                Parent?.Viewer?.OnTheRebounds();
            }
        }
        public int WidthContent => Parent.Viewer.Rows.Max(r => r.CellWidths[Name]);
        public int WidthHead { get; internal set; } = -1;
        private bool visible = true;
        public bool Visible
        {
            get => visible;
            set
            {
                if (visible != value)
                {
                    visible = value;
                    Parent?.Viewer?.Invalidate();
                }
            }
        }
        private Type datatype = null;
        public Type DataType
        {
            get => datatype;
            set
            {
                // to do!
                // change the formatting, horizontal alignment and the cell values
                datatype = value;
            }
        }
        public bool Selected { get; set; }
        public bool CanEditValues { get; set; } = true;
        public Dictionary<string, Rectangle> Bounds = new Dictionary<string, Rectangle>(StringComparer.OrdinalIgnoreCase)
        {
            { "img", Rectangle.Empty },
            { "txt", Rectangle.Empty },
            { "srt", Rectangle.Empty },
            { "srtNbr", Rectangle.Empty },
            { "all", Rectangle.Empty }
        };

        public Column(string name)
        {
            timer = new System.Threading.Timer(new TimerCallback(Timer_tick), this, Timeout.Infinite, Timeout.Infinite);
            Name = name;
            Style = new ColumnStyle(this);
            SetHeadWidth();
        }

        internal void SetHeadWidth()
        {
            WidthHead = 3 + Functions.MeasureText(Name.ToUpper(), Style.Font).Width;
        }
        private void Timer_tick(object sender)
        {
            timer.Change(Timeout.Infinite, Timeout.Infinite);
            Parent.Viewer.OnTheRebounds(); // definitely has a Parent and the Viewer != null ( since coming from Index change )
        }
        public override string ToString() => $"{Name}, {DataType}, Width: {Width}";
        //=================
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Dispose managed resources.
                Style?.Dispose();
            }
            // Free unmanaged resources
        }
    }
    public class Rows: List<Row>
    {
        internal ViewerControl Viewer { get; }
        private readonly System.Threading.Timer timer;
        public RowStyle Style { get; set; }

        public Rows(ViewerControl viewer)
        {
            Viewer = viewer;
            Style = new RowStyle();
            Style.StyleChanged += StyleChanged;
            timer = new System.Threading.Timer(new TimerCallback(Timer_tick), null, Timeout.Infinite, Timeout.Infinite);
        }

        private void StyleChanged(object sender, StyleChangedEventArgs e)
        {
            var style = (Style)sender;
            Viewer.Invalidate();
        }
        public Row Add(Table.Row row)
        {
            var newRow = new Row(row, this);
            Add(newRow);
            return newRow;
        }
        private void Timer_tick(object sender)
        {
            timer.Change(Timeout.Infinite, Timeout.Infinite);
            Viewer?.Invalidate();
        }

        public override string ToString()
        {
            return Count.ToString();
        }
    }
    public class Row
    {
        public Rows Parent { get; internal set; }
        public int Index => Parent.IndexOf(this);
        public int IndexSource { get; private set; }
        private bool visible = true;
        public bool Visible
        {
            get => visible;
            set
            {
                if (visible != value)
                {
                    visible = value;
                    Parent?.Viewer.Invalidate();
                }
            }
        }
        public void StyleReset() => rowStyle = null;
        private RowStyle rowStyle = null;
        public RowStyle Style
        {
            get
            {
                if (rowStyle == null)
                    return Parent.Style;
                return rowStyle;
            }
            set
            {
                rowStyle = value;
                Parent.Viewer.Invalidate();
            }
        }
        private CaseInSensitiveDictionary<string, object> Cells { get; } = new CaseInSensitiveDictionary<string, object>();
        internal CaseInSensitiveDictionary<string, int> CellWidths { get; } = new CaseInSensitiveDictionary<string, int>();
        internal CaseInSensitiveDictionary<string, string> CellStrings { get; } = new CaseInSensitiveDictionary<string, string>();

        public Row(Table.Row row, Rows parent)
        {
            Parent = parent;
            IndexSource = row.Index;
            rowStyle = new RowStyle();
            foreach (var col in parent.Viewer.Columns)
                ThisValue(col.Name, row.Cells[col.Name], true);
        }

        private void StyleChanged(object sender, StyleChangedEventArgs e) => Parent.Viewer.Invalidate();
        public object this[string key]
        {
            get => Cells.ContainsKey(key) ? Cells[key] : null;
            set
            {
                if (Cells.ContainsKey(key))
                    ThisValue(key, value);
            }
        }
        public object this[int index]
        {
            get
            {
                var colName = ColNameFromIndex(index);
                return colName == null ? null : Cells[colName];
            }
            set
            {
                var colName = ColNameFromIndex(index);
                if (colName != null)
                    ThisValue(colName, value); // newAdd = false since new goes through --> public object this[string key, bool newAdd = false]
            }
        }
        private void ThisValue(string key, object value, bool newAdd = false)
        {
            if (newAdd || Cells[key] != value)
            {
                Cells[key] = value;
                var col = Parent.Viewer.Columns[key];
                var colValStr = Table.ObjectToString(col.DataType, value, Parent.Viewer.Datasource.ObjectFormat(col.Name));
                CellStrings[col.Name] = colValStr;
                CellWidths[col.Name] = Functions.MeasureText(colValStr, Style.Font).Width;
            }
            if (!newAdd)
            {
                // update the source table
                Parent.Viewer.Datasource.Rows[IndexSource][key] = value;
                Parent.Viewer.Invalidate();
            }
        }
        private string ColNameFromIndex(int index)
        {
            string colName = null;
            foreach (var col in Parent?.Viewer?.Columns)
                if (col.index == index)
                {
                    colName = col.Name;
                    break;
                }
            return colName;
        }

        public override string ToString() => Index.ToString() + "_" + string.Join("|", Cells.Select(c => (c.Value ?? "null").ToString()));
    }

    #region "■■■■■■■■■■■■■ S T Y L E S   C L A S S E S "
    internal class StyleChangedEventArgs : EventArgs
    {
        public string PropertyName { get; }
        public object PropertyValue { get; }
        public StyleChangedEventArgs(string propertyName, object propertyValue)
        {
            PropertyName = propertyName;
            PropertyValue = propertyValue;
        }
    }
    public class ColumnStyle : Style
    {
        public List<Column> Parent;
        public StringAlignment AlignHead_horizontal { get; set; } = StringAlignment.Center;
        public StringAlignment AlignHead_vertical { get; set; } = StringAlignment.Center;
        public StringAlignment AlignContent_horizontal { get; set; } = StringAlignment.Near;
        public StringAlignment AlignContent_vertical { get; set; } = StringAlignment.Center;

        public ColumnStyle(Columns parent)
        {
            Parent = parent.OfType<Column>().ToList();
            Font = new Font(Font.FontFamily, Font.Size + 1);
            BackColor = Color.GhostWhite;
        }
        public ColumnStyle(Column parent)
        {
            Parent = new List<Column> { parent };
            Font = new Font(Font.FontFamily, Font.Size + 1);
            BackColor = Color.GhostWhite;
        }

        public override string ToString() => $"ForeColor:{ForeColor}, BackColor:{BackColor}, Font:{Font}, Height{Height:N0}";
    }
    public class RowStyle : Style
    {
        private Color backColorAlternate = Color.Gainsboro;
        public Color BackColorAlternate
        {
            get => backColorAlternate;
            set
            {
                if (backColorAlternate != value)
                {
                    backColorAlternate = value;
                    OnStyleChanged("backcoloralternate", value);
                }
            }
        }
        private Color backColorAlternateAccent = Color.Empty;
        public Color BackColorAlternateAccent
        {
            get => backColorAlternateAccent;
            set
            {
                if (backColorAlternateAccent != value)
                {
                    backColorAlternateAccent = value;
                    OnStyleChanged("backcoloralternateaccent", value);
                }
            }
        }
        private Color foreColorAlternate = Color.Black;
        public Color ForeColorAlternate
        {
            get => foreColorAlternate;
            set
            {
                if (foreColorAlternate != value)
                {
                    foreColorAlternate = value;
                    OnStyleChanged("forecoloralternate", value);
                }
            }
        }

        public RowStyle()
        {
        }

        public override string ToString() => $"ForeColor:{ForeColor}, BackColor:{BackColor}, Font:{Font}, Height{Height:N0}";
    }
    public abstract class Style : IDisposable
    {
        protected virtual void OnStyleChanged(string propertyName, object propertyValue) { StyleChanged?.Invoke(this, new StyleChangedEventArgs(propertyName, propertyValue)); }
        internal event EventHandler<StyleChangedEventArgs> StyleChanged;

        private Font font = new Font("Cascadia Code", 9);
        public Font Font
        {
            get => font;
            set
            {
                if (font != value)
                {
                    font = value;
                    OnStyleChanged("font", value);
                }
            }
        }
        private Color backColor = Color.GhostWhite;
        public Color BackColor
        {
            get => backColor;
            set
            {
                if (backColor != value)
                {
                    backColor = value;
                    OnStyleChanged("backcolor", value);
                }
            }
        }
        private Color backColorAccent = Color.Empty;
        public Color BackColorAccent
        {
            get => backColorAccent;
            set
            {
                if (backColorAccent != value)
                {
                    backColorAccent = value;
                    OnStyleChanged("backcoloraccent", value);
                }
            }
        }
        private Color backColorSelect = Color.Empty;
        public Color BackColorSelect
        {
            get => backColorSelect;
            set
            {
                if (backColorSelect != value)
                {
                    backColorSelect = value;
                    OnStyleChanged("backcolorselect", value);
                }
            }
        }
        private Color foreColor = Color.Black;
        public Color ForeColor
        {
            get => foreColor;
            set
            {
                if (foreColor != value)
                {
                    foreColor = value;
                    OnStyleChanged("forecolor", value);
                }
            }
        }
        private Color foreColorSelect = Color.Empty;
        public Color ForeColorSelect
        {
            get => foreColorSelect;
            set
            {
                if (foreColorSelect != value)
                {
                    foreColorSelect = value;
                    OnStyleChanged("forecolorselect", value);
                }
            }
        }
        private int height = 15;
        public int Height
        {
            get => height;
            set
            {
                if (height != value)
                {
                    height = value;
                    OnStyleChanged("height", value);
                }
            }
        }

        public Style()
        {
            height = 2 + Functions.MeasureText("█", Font).Height + 2;
        }

        public override string ToString() => $"ForeColor:{ForeColor}, BackColor:{BackColor}, Font:{Font}, Height{Height:N0}";
        //=================
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Dispose managed resources.
                Font?.Dispose();
            }
            // Free unmanaged resources
        }
    }
    #endregion

    public class CaseInSensitiveDictionary<TKey, TValue>: Dictionary<string, TValue>
    {
        public CaseInSensitiveDictionary() : base(StringComparer.OrdinalIgnoreCase) { }
    }
    public static class Reflection
    {
        public static Dictionary<string, object> CopyProperties(this object source, object destination)
        {
            if (source == null || destination == null)
                throw new Exception("Source or/and Destination Objects are null");
            Type typeDest = destination.GetType();
            Type typeSrc = source.GetType();
            var propDict = new Dictionary<string, object>();
            // Iterate the Properties of the source instance and populate them from their desination counterparts
            PropertyInfo[] srcProps = typeSrc.GetProperties();
            foreach (PropertyInfo srcProp in srcProps)
            {
                if (!srcProp.CanRead)
                    continue;
                PropertyInfo targetProperty = typeDest.GetProperty(srcProp.Name);
                if (targetProperty == null)
                    continue;
                if (!targetProperty.CanWrite)
                    continue;
                if (targetProperty.GetSetMethod(true) != null && targetProperty.GetSetMethod(true).IsPrivate)
                    continue;
                if ((targetProperty.GetSetMethod().Attributes & MethodAttributes.Static) != 0)
                    continue;
                if (!targetProperty.PropertyType.IsAssignableFrom(srcProp.PropertyType))
                    continue;
                // Passed all tests, now set the value
                var srcVal = srcProp.GetValue(source, null);
                targetProperty.SetValue(destination, srcVal, null);
                propDict[targetProperty.Name] = srcVal;
            }
            return propDict;
        }
    }
    public static class Functions
    {
        #region" sort order / node image styles "
        // sort order
        internal static Image ImgSrt(SortOrder sort) => sort == SortOrder.none ? null : Base64ToImage(sort == SortOrder.asc ? sortUp : sortDown);
        internal const string sortUp = "iVBORw0KGgoAAAANSUhEUgAAABEAAAALCAYAAACZIGYHAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAABFSURBVChTY2SAgAYoTQ4A6z0PxP8pwPep4hLG//9BhlEGyHFJAzaLSQ2T+yBDkDFVXEL3MMEaFjBAbJhghAUMU8ElDAwAvNhdwMSXsO4AAAAASUVORK5CYII=";
        internal const string sortDown = "iVBORw0KGgoAAAANSUhEUgAAABEAAAALCAYAAACZIGYHAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAABLSURBVChTY2SAgAYojQ80/P//H8rEBOeBGCRLCN8HGYINU8UljLgkSAGkuAQGsLqI2DCBYYywoYpLBixM0AFYL6lhgo7vU8ElDA0AaFFdwFj1ubQAAAAASUVORK5CYII=";

        // checkbox styles
        internal static Image ImgChk(CheckboxStyle style, CheckState state)
        {
            if (style == CheckboxStyle.none || style == CheckboxStyle.check)
                return Base64ToImage(state == CheckState.on ? checkOn : state == CheckState.both ? checkBoth : checkOff);
            else if (style == CheckboxStyle.slide)
                return Base64ToImage(state == CheckState.on ? slideOn : state == CheckState.both ? slideBoth : slideOff);
            else if (style == CheckboxStyle.radio)
                return Base64ToImage(state == CheckState.on ? radioOn : state == CheckState.both ? radioBoth : radioOff);
            else
                return null;
        }
        internal const string slideOff = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAA8AAAAJCAYAAADtj3ZXAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsIAAA7CARUoSoAAAADQSURBVChTbZBBCoJQEIYnoY1GJOJCFxmI1+iEURtRcFMbO4E38QwFQbqrReH7m9EXkb4PPngD7x9mhgQQeWwK122RJFDLJbg22bAZ6/2Ci0WtTkfg/UbP8wl12APzuamBWLOehFOcyyE0ZrczBb/mpKL1HV2nf494PADHMQXF1ppFG48sq19hgm0TBYEuJrgWXS+NLqa8XkS3my4mNLJzhqrSc44oCtO4XzMJr+D79V8DuUHJR7RtU0gcri3Ig80Rxy22WyAMTQHxzqbsiojoA7SB/10DSCsdAAAAAElFTkSuQmCC";
        internal const string slideBoth = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAA8AAAAJCAYAAADtj3ZXAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsIAAA7CARUoSoAAAAEBSURBVChTbZFNToRAEIWrETAhwCwgXKC3HsJTGOcqJuPG8TBzCw/hYjYcgACz4k/+tF6looz6JQ/qdfeDSrUhJs/zhF9H1p61w5rjOOS6Ls3zTOu6YglcWCfWwVpbGw2+se5YZIyhJEk+oii6RQ2apqGqqrYfeWfdO/x4YUkQpGm6xHH8HQRhGFKWZeoEnD8i/CCW4VYX/iPW/hAEAfm+r07Y4yDaFnTz55e/8DxPK2GHMIYgTNP0qeW/8L5WwgVhTE9YlsVt23ZWe8UwDDSOozrhhPAT6yyWKcvypuu6q1N931NRFOoETPuwvedX1iNL7hl3jBmg1U27NQudPltr6y/qhVPZdZIpbAAAAABJRU5ErkJggg==";
        internal const string slideOn = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAA8AAAAJCAYAAADtj3ZXAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsIAAA7CARUoSoAAAAFfSURBVChTY2QAgsl3Onif/31a+5vxV8w/hn+S//+DRCEApECQSYhBjFWS4fPfz8/f/nq5UIZJpS1LveAz4+QbXbwPmG7v+c3024yBAUkXEHAwcjBEiCQzGPCZAA0B28Nw/csVhnUvlp4S+iXmwmyUo9XynflrGFgGCmAKo0VSGYz4zeB8EBBlE2OQYpOVPvR5NyPTL6YfMVBxOPjH+I9BgEGIwRCoERtQ4VEHGiATw/Tv/39JqBgcMP5nZBBlFkexER0IMYvKMDExMj2B8lHA259voCzs4NPPD0+Y2P6zLQf5EjWoGBje/H/JcPnNBSgPFTz8eJ/hwed7y5nk/ig2c/zhOAV2ICPMCEYGJhYmhoVPZzLceHMVKgYBDz7cY1h0a/YpdgbuBrCeSec7uJ5zPqkFRlfM////Zf7D3PHvP8OvL78ZJP/JMohzSDJ8+vXx+ZNvj5bIcyk055tWfAYAO3x5Yc/jBVIAAAAASUVORK5CYII=";

        internal const string checkOff = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABEAAAARCAYAAAA7bUf6AAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsEAAA7BAbiRa+0AAACDSURBVDhPY0w0Z/BbdJKh7i8DgzADA8N/IAYBRigGARgNAiA2SM0vEFuGheGTqzFDAwMPK1iAbMDNyvCT6d9vFJtIBv//MDAxQdnkA6DnKDcECEYNwQSjhmCCwWEIMOMxMjGxwrM/eYCV4R+4PNl7jqHj0W8GHqAQKQYyA/EbBgaGMgDluRlgVls5wgAAAABJRU5ErkJggg==";
        internal const string checkBoth = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABEAAAARCAYAAAA7bUf6AAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADr8AAA6/ATgFUyQAAAHSSURBVDhPY0w0Z/Dbe46h49FvBh4GBob/QIwPsAPxPiCOAvOggDHHmOHqlLMMDUD2FSBmA4viBkxA/AGI74N5QLApTUqEMZ6d4dHCnwxuQP4NiDDx4GiRDN9uvpSPTP9/Ar3ACHYmSeBwgSzYABAb5DxQSBAKCxRwpFCGb69AMtiAhoZpChBDSAB7SmR49/BDXNDQMEOWgeHVQ3RDmBkYuLUYGCS4oXwUcLhCjvcIT8onEBtiwIsnIDYLiICBFidB1j92+VdB7IaGk0ALtsO9uSJOjmcvRxLMACWQAWvzZNmvPGYURHFJzb73P37vm2YIYjc0mP9jYDAAuoyBYXOaHM8NpaTPEPHpckADwFF89/IHvad336/ACJPWQ68uMB6Yrg1iNzQE/LFVl5M+KwU3QIGB4eVjEBsEXu7/zPru0mcBrAFbf+DltX/7pxmA2M6RSWB/Aw1QAWp7CGLDACMwXoH4L1ZDQKDp4KuLf/dNNQaxgdEIDIOXd8ES2EAcAwPIdD0IjzRQwsBgGcrAcBbiEkaQy0gHME1MjOxA9n+Gn1A+SQCYW38DDWJhtjVgSDn1nOE6UAyUJqSBWIJILARMZEbAaNMmtTxBBqA09IaBgaEMAG4qlK7af5IBAAAAAElFTkSuQmCC";
        internal const string checkOn = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABEAAAARCAYAAAA7bUf6AAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADr8AAA6/ATgFUyQAAAGgSURBVDhPY0w0Z/Dbe46h49FvBh4GBob/QIwPsAPxPiCOAvOggDHHmOHqlLMMDUD2FSBmA4viBkxA/AGI74N5QLApTUqEIZ6d4RGQrQERIg0cLZLhY1D7/5/p/0+gFxjBziQJHC6Q5bPe8vgjiA1yHigkCIUFCjhSKMNnu+0R2ACGW+IKDHEMDA+BTD2wABFgT4kML8gLYMwgIQMWRDOEmYGBWwsoyQ3lo4DDFXKYBoAAsiEtToIcCEWejCAxGFgRJ8eDZIAiSGxtnix7Y6CcBIZ3qu3EDBCKDYAuY2DYnIZsgLgsWCEQdDnymqbp8R7AGiaNDuJaME226nLSSAbIQ5WAQTEDg1UIA8MFSOyggfoDL6/VSYobgNiH/z98Aha8JaHCwPASZCEcAP37H4j/YjUEBJoOvrpYKyFmDObcElcCGnAXzMYCcBoCAs2HXp9juMUItOwVPJljAxBDGEEuIx3ANDExsgPZ/xl+QvkkAWBu/Q00iIXZ1oAh5dRzhutAMWDoM0gDsQSRWIiFgcHoMwODNqnlCTIApaE3DAwMZQClbIXPFxNDUAAAAABJRU5ErkJggg==";

        internal const string radioOff = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsIAAA7CARUoSoAAAAKsSURBVDhPjVNbSBRRGD6Xubq16G5lslKwSmQPosnmqpm3IqQiqN6CXqUIiYLqIYLwLUrKol569MEL4VtglKtbViS1hYUvQa1aXiCt1p2dmTNzTv+4m/Wg5AdzO/P933z/d/7B6B9UVdcpmUymgmB03HV5M8a4nAthC8ETmqoOS5I8UBIOf+zr6bZzJWhFoKq6NpROG2dsm7UJIYIYCYEwXgICEUj4kEBIluV5TdO6JEW+/+bV6IJXR71TZSQaMgzjBrPZaXhUZFl6IStyr3/jhh6fzxcDoU/gBDPHKbUZa7FMSwkVb3s9Pzdj4kNHjuUlJ5MdRsY4D4YWVVW9EyjIvzcaj8154n8QidaFUun0OXB4VnCu6bp+uSRc0kl9fv8+07I6OXwC+rw+MZ64NpX8ks7VreDb9FSqvnF/7OePhXzXdWugtXJYjhEI7QRjTIfAYpsCwbtZ+uoY6Ot2AoGC25IkjUE7W6Dto4Rgchje2WApHh95spilro3nw0OTEGbcuzdNs5EwhxUTQlKgOrHMWAc0VfG4KUJJhOR2EhIQjnezHkDLSx4fDkRkSZrmnPs5d8ty7/8LLlAlZJYP9e+J47qDsKbC3ja1HGj1ZylrA2amEIKvg69jSsiQ56Af1EwQapiZnW3L8VbFlasd1DStdnAchZrvrhAP6fZw6Sx33a2O40ThWlFYFEI7du76MJX8bOXqlrG3sXlzIvHuEkxiOzzqsBO38nStdznBqj21pbCnXZZttUKoGUrpCCQdI5ROOI6LHMbKIOWDMED1QJclSntVTbswnhj7+vdnApG0YVyEhE+BRRWWLBiSXxAU8kIGyyoIGzCtDxRFufl27OWkV7ci4GF3pMZnWmYDzPpJjEkT2C0iGCP4saahxUFd0/uDweCzp48fGdkKhH4DR/pBYOFcAWEAAAAASUVORK5CYII=";
        internal const string radioBoth = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsIAAA7CARUoSoAAAADCSURBVDhPY6AUMEJpBFD7LwkkcxqiGqsgAhDQsKy+DUhNYbjF+BwiAgGoBqj9jwBqXA7lYQVAg6KAhsDVIAwgQjMMIBsCMQDobKDmZ2A2kQBoiDTQkGdMUH4OlCYF5IEIsAsaGhr+g2hSAVAfI8wFZINBYgAwRDvAPBIAUE8XiIa5YDKUJgVMAhEQA4DxCTQxBswmAgDVxgL1PAWxEWFwi3EpWIIAgGpeAuVizUzSQDIPmDLLIAIQAPXzJJjNVAIMDABtrT35JWCVnwAAAABJRU5ErkJggg==";
        internal const string radioOn = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsIAAA7CARUoSoAAAADmSURBVDhPrZLPEsEwGMRTZ9xxpeNlOJsx0ytmjIcxLu7+3PVlKFc8APfYTfJNVXUq+M3spl+abNMk6lcC16aEugGfQn1W7AIJtIMWKgmupseRDQj1AL6E6qbOc4NGCNna8jnATt5A+VVl0dBQQuxgu+w9VPTlV7iSLkIuFVubf/50MqlBMz5IADfMlx5NAmS3fejQJOBrJODoWh9ONAmIXeuDmSPH2IQfoKqpy7lDPMazXQHOEz6GeEnK4JgJJ7NINzEJ1vAIYnoRfBdh7MqW765tqFtwXhKec5tdgBvGf57Ll/+EUg/D5DEC4V7+lQAAAABJRU5ErkJggg==";
#endregion
        internal static string Spin3 = "data:image/png;base64,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";
        public static string ImageToBase64(Image image, ImageFormat ImageFormat = null/* TODO Change to default(_) if this is not a reference type */)
        {
            if (image == null)
                return null;
            else
            {
                if (ImageFormat == null)
                    ImageFormat = ImageFormat.Bmp;
                string base64String;
                try
                {
                    using (MemoryStream ms = new MemoryStream())
                    {
                        image.Save(ms, ImageFormat);
                        byte[] imageBytes = ms.ToArray();
                        base64String = Convert.ToBase64String(imageBytes);
                    }
                    return base64String;
                }
                catch
                {
                    return null;
                }
            }
        }
        public static Image Base64ToImage(string imgStr, bool MakeTransparent = false)
        {
            if (imgStr == null)
                return null;
            imgStr = imgStr.Split(',').Last().Trim();
            if (imgStr.Any())
            {
                byte[] b = Convert.FromBase64String(imgStr);
                Image img;
                try
                {
                    using (MemoryStream MemoryStream = new MemoryStream())
                    {
                        MemoryStream.Position = 0;
                        MemoryStream.Write(b, 0, b.Length);
                        img = Image.FromStream(MemoryStream);
                        if (MakeTransparent)
                        {
                            Bitmap Bmp = new Bitmap(img);
                            Bmp.MakeTransparent(Bmp.GetPixel(0, 0));
                            img = Bmp;
                        }
                    }
                    return img;
                }
                finally { }
            }
            else
                return null;
        }
        public static bool SameImage(Image Image1, Image Image2)
        {
            if (Image1 == null)
                return Image2 == null;
            else if (Image2 == null)
                return Image1 == null;
            else
                return ImageToBase64(Image1, ImageFormat.Bmp) == ImageToBase64(Image2, ImageFormat.Bmp);
        }
        public static Bitmap RotateImage(Bitmap b, float angle)
        {
            if (b == null)
                return b;
            else
            {
                // create a New empty bitmap to hold rotated image
                Bitmap returnBitmap = new Bitmap(b.Width, b.Height);
                // make a graphics object from the empty bitmap
                using (Graphics g = Graphics.FromImage(returnBitmap))
                {
                    // move rotation point to center of image
                    float dx = System.Convert.ToSingle(b.Width / (double)2);
                    float dy = System.Convert.ToSingle(b.Height / (double)2);
                    g.TranslateTransform(dx, dy);

                    // rotate
                    g.RotateTransform(angle);

                    // move image back
                    g.TranslateTransform(-dx, -dy);

                    // draw passed in image onto graphics object
                    g.DrawImage(b, new Point(0, 0));
                }
                return returnBitmap;
            }
        }
        public static Size MeasureText(string textIn, Font txtFnt, double adjustmentFactor = 1.03)
        {
            if ((textIn ?? "").Any() & txtFnt != null)
            {
                var gTextSize = TextRenderer.MeasureText(textIn, txtFnt);

                //using (Graphics g = Graphics.FromImage(new Bitmap(1000, 1000)))
                //{
                //    g.TextRenderingHint = TextRenderingHint.AntiAlias;
                //    using (StringFormat sf = new StringFormat() { Trimming = StringTrimming.None })
                //        gTextSize = g.MeasureString(textIn, txtFnt, RectangleF.Empty.Size, sf);
                //}
                return new Size(Convert.ToInt32(adjustmentFactor * gTextSize.Width), Convert.ToInt32(adjustmentFactor * gTextSize.Height));
            }
            else
                return new Size(0, 0);
        }
    }
    public static class SafeThread
    {
        private delegate void SetPropertyCallback(Control c, string n, object v);
        private delegate void SetPanelPropertyCallback(TableLayoutPanel tlp, int index, int width);
        private delegate void SetToolPropertyCallback(ToolStripItem tsi, string n, object v);
        private delegate object GetPropertyCallback(Control c, string n);
        public static object GetControlPropertyValue(Control Item, string PropertyName)
        {
            if (Item == null)
                return null;
            else
                try
                {
                    Type t = Item.GetType();
                    if (Item.InvokeRequired)
                    {
                        GetPropertyCallback d = new GetPropertyCallback(GetControlPropertyValue);
                        return Item.Invoke(d, new object[] { Item, PropertyName });
                    }
                    else
                    {
                        PropertyInfo pi = t.GetProperty(PropertyName);
                        return pi.GetValue(Item);
                    }
                }
                catch (ObjectDisposedException ex)
                {
                    Console.WriteLine(ex.Message);
                    return null;
                }
                catch (InvalidAsynchronousStateException ex)
                {
                    Console.WriteLine(ex.Message);
                    return null;
                }
                catch (TargetInvocationException ex)
                {
                    Console.WriteLine(ex.Message);
                    return null;
                }
        }
        public static void SetControlPropertyValue(Control Item, string PropertyName, object PropertyValue)
        {
            if (Item != null)
            {
                try
                {
                    if (Item.InvokeRequired)
                    {
                        SetPropertyCallback d = new SetPropertyCallback(SetControlPropertyValue);
                        Item.Invoke(d, new object[] { Item, PropertyName, PropertyValue });
                    }
                    else
                    {
                        Type t = Item.GetType();
                        PropertyInfo pi = t.GetProperty(PropertyName);
                        pi.SetValue(Item, PropertyValue);
                    }
                }
                catch (ObjectDisposedException ex)
                {
                    Console.WriteLine(ex.Message);
                }
                catch (InvalidAsynchronousStateException ex)
                {
                    Console.WriteLine(ex.Message);
                }
                catch (TargetInvocationException ex)
                {
                    Console.WriteLine(ex.Message);
                }
            }
        }
        public static void SetColumnStylesWidth(TableLayoutPanel Item, int indexOfColumn, int widthOfColumn)
        {
            if (Item != null)
            {
                try
                {
                    if (Item.InvokeRequired)
                    {
                        SetPanelPropertyCallback d = new SetPanelPropertyCallback(SetColumnStylesWidth);
                        Item.Invoke(d, new object[] { Item, "Width", widthOfColumn });
                    }
                    else
                        // Dim t As Type = Item.GetType
                        // Dim pi As PropertyInfo = t.GetProperty(PropertyName)
                        // pi.SetValue(Item, PropertyValue)
                        Item.ColumnStyles[indexOfColumn].Width = widthOfColumn;
                }
                catch (ObjectDisposedException ex)
                {
                    Console.WriteLine(ex.Message);
                }
                catch (InvalidAsynchronousStateException ex)
                {
                    Console.WriteLine(ex.Message);
                }
                catch (TargetInvocationException ex)
                {
                    Console.WriteLine(ex.Message);
                }
            }
        }
        public static void SetToolStripItemPropertyValue(ToolStripItem Item, string PropertyName, object PropertyValue)
        {
            if (Item != null)
            {
                try
                {
                    if (Item.Owner != null)
                    {
                        if (Item.Owner.InvokeRequired)
                        {
                            SetToolPropertyCallback d = new SetToolPropertyCallback(SetToolStripItemPropertyValue);
                            Item?.Owner?.Invoke(d, new object[] { Item, PropertyName, PropertyValue });
                        }
                        else
                        {
                            Type t = Item.GetType();
                            PropertyInfo pi = t.GetProperty(PropertyName);
                            pi.SetValue(Item, PropertyValue);
                        }
                    }
                }
                catch (ObjectDisposedException ex)
                {
                    Console.WriteLine(ex.Message);
                }
                catch (InvalidAsynchronousStateException ex)
                {
                    Console.WriteLine(ex.Message);
                }
                catch (TargetInvocationException ex)
                {
                    Console.WriteLine(ex.Message);
                }
            }
        }
    }
    partial class MouseOver
    {
        public Point Location { get; set; }
        public Column Column { get; set; }
        public Row Row { get; set; }
        public Rectangle Cell { get; set; }
        public MouseOver() { }
    }
}