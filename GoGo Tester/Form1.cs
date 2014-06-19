﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Cache;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Timers;
using System.Windows.Forms;
using GoGo_Tester.Properties;
using Timer = System.Timers.Timer;

namespace GoGo_Tester
{
    public partial class Form1 : Form
    {
        struct TestResult
        {
            public string addr;
            public bool ok;
            public string msg;
        }

        public Form1()
        {
            InitializeComponent();
        }

        private readonly Regex rxMatchIp =
            new Regex(@"((25[0-5]|2[0-4][0-9]|1\d\d|\d{1,2})\.){3}(25[0-5]|2[0-4][0-9]|1\d\d|\d{1,2})",
                RegexOptions.Compiled);

        private readonly DataTable IpTable = new DataTable();

        public static Queue<string> WaitQueue = new Queue<string>();
        public static Queue<int> TestQueue = new Queue<int>();

        private readonly Timer StdTestTimer = new Timer();
        private readonly Timer RndTestTimer = new Timer();

        private static Random random = new Random();
        public static int PingTimeout = 1000;
        public static int TestTimeout = 4000;
        public static int MaxThreads = 10;

        private bool StdIsTesting;
        private bool RndIsTesting;


        private void Form1_Load(object sender, EventArgs e)
        {
            int count = 0;
            foreach (var range in IpRange.Pool)
            {
                count += range.Count;
            }

            Text = string.Format("GoGo Tester 2 - Total {0} IPs", count);

            Icon = Resources.GoGo_logo;

            IpTable.Columns.Add(new DataColumn("addr", typeof(string))
            {
                Unique = true,
            });
            IpTable.Columns.Add(new DataColumn("std", typeof(string)));

            dgvIpData.DataSource = IpTable;
            dgvIpData.Columns[0].Width = 120;
            dgvIpData.Columns[0].HeaderText = "地址";
            dgvIpData.Columns[1].Width = 100;
            dgvIpData.Columns[1].HeaderText = "标准测试";

            /// Std
            ServicePointManager.ServerCertificateValidationCallback = (o, certificate, chain, errors) => true;

            StdTestTimer.Interval = 25;
            StdTestTimer.Elapsed += StdTestTimerElapsed;

            RndTestTimer.Interval = 25;
            RndTestTimer.Elapsed += RndTestTimerElapsed;
        }

        private static int SetRange(int val, int min, int max)
        {
            val = val > min ? val : min;
            val = val < max ? val : max;
            return val;
        }

        private void RndTestTimerElapsed(object sender, ElapsedEventArgs e)
        {
            var testCount = TestQueue.Count;
            var waitCount = dgvIpData.RowCount;

            SetRndProgress(testCount, waitCount);

            if (RndIsTesting && waitCount < Form2.RandomNumber && testCount < MaxThreads)
            {
                string addr;
                do
                {
                    IpRange iprange = IpRange.Pool[random.Next(0, IpRange.Pool.Count)];
                    addr = iprange.GetRandomIp();
                } while (WaitQueue.Contains(addr));

                var thread = new Thread(() =>
                {
                    Monitor.Enter(TestQueue);
                    TestQueue.Enqueue(0);
                    Monitor.Exit(TestQueue);

                    var result = StdTestProcess(addr);
                    if (result.ok)
                    {
                        ImportIp(addr);
                        SetStdTestResult(result);
                    }

                    Monitor.Enter(TestQueue);
                    TestQueue.Dequeue();
                    Monitor.Exit(TestQueue);
                });
                thread.Start();
            }
            else if (testCount == 0)
            {
                RndIsTesting = false;
                RndTestTimer.Stop();
            }
        }

        private void StdTestTimerElapsed(object sender, ElapsedEventArgs e)
        {
            var testCount = TestQueue.Count;
            var waitCount = WaitQueue.Count;

            SetStdProgress(testCount, waitCount);

            if (StdIsTesting && waitCount > 0 && testCount < MaxThreads)
            {
                var addr = WaitQueue.Dequeue();
                var thread = new Thread(() =>
                {
                    Monitor.Enter(TestQueue);
                    TestQueue.Enqueue(0);
                    Monitor.Exit(TestQueue);

                    var result = StdTestProcess(addr);
                    SetStdTestResult(result);

                    Monitor.Enter(TestQueue);
                    TestQueue.Dequeue();
                    Monitor.Exit(TestQueue);
                });
                thread.Start();
            }
            else if (waitCount == 0 && testCount == 0)
            {
                StdIsTesting = false;
                StdTestTimer.Stop();
            }
        }

        private void SetStdProgress(int testCount, int waitCount)
        {
            if (InvokeRequired)
            {
                Invoke(new MethodInvoker(() => SetStdProgress(testCount, waitCount)));
            }
            else
            {
                pbProgress.Value = SetRange(pbProgress.Maximum - waitCount - testCount, 0, pbProgress.Maximum);
                lProgress.Text = testCount + " / " + waitCount;
            }
        }

        private void SetRndProgress(int testCount, int waitCount)
        {
            if (InvokeRequired)
            {
                Invoke(new MethodInvoker(() => SetRndProgress(testCount, waitCount)));
            }
            else
            {
                pbProgress.Value = SetRange(waitCount, 0, pbProgress.Maximum);
                lProgress.Text = testCount + " / " + waitCount;
            }
        }

        private TestResult StdTestProcess(string addr)
        {
            long pingTime = 0;

            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var stopwatch = new Stopwatch();

            try
            {
                var ar = socket.BeginConnect(addr, 443, x =>
                     {
                         try
                         {
                             socket.EndConnect(x);
                             pingTime = stopwatch.ElapsedMilliseconds;
                         }
                         catch (Exception) { }

                     }, null);

                stopwatch.Start();

                while (!ar.IsCompleted)
                {
                    if (stopwatch.ElapsedMilliseconds > PingTimeout)
                    {
                        stopwatch.Stop();
                        socket.Close();

                        return new TestResult()
                        {
                            addr = addr,
                            ok = false,
                            msg = "Timeout"
                        };
                    }

                    Thread.Sleep(20);
                }

                stopwatch.Stop();

                if (!socket.Connected)
                {
                    socket.Close();
                    return new TestResult()
                    {
                        addr = addr,
                        ok = false,
                        msg = "Invalid"
                    };
                }

                socket.Close();
            }
            catch (Exception)
            {
                socket.Close();

                return new TestResult()
                {
                    addr = addr,
                    ok = false,
                    msg = "Failed"
                };
            }

            TestResult result;

            var sbd = new StringBuilder();

            for (int i = 0; i < 150; i++)
                sbd.Append(random.Next().ToString("D10"));

            var url = "https://" + addr + "/?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(sbd.ToString()));

            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = TestTimeout;
            req.Method = "HEAD";
            req.AllowAutoRedirect = false;
            req.KeepAlive = false;
            req.CachePolicy = new RequestCachePolicy(RequestCacheLevel.NoCacheNoStore);

            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    if (resp.Server == "gws")
                    {
                        result = new TestResult()
                        {
                            addr = addr,
                            ok = true,
                            msg = "_OK " + pingTime.ToString("D4")
                        };
                    }
                    else
                    {
                        result = new TestResult()
                          {
                              addr = addr,
                              ok = false,
                              msg = "Invalid"
                          };
                    }
                    resp.Close();
                }
            }
            catch (Exception)
            {
                result = new TestResult()
                         {
                             addr = addr,
                             ok = false,
                             msg = "Failed"
                         };
            }

            return result;
        }

        private void SetStdTestResult(TestResult result)
        {
            if (InvokeRequired)
            {
                Invoke(new MethodInvoker(() => SetStdTestResult(result)));
            }
            else
            {
                var rows = SelectByIp(result.addr);
                if (rows.Length > 0)
                    rows[0][1] = result.msg;
            }
        }

        private void ImportIp(string addr)
        {
            if (InvokeRequired)
            {
                Invoke(new MethodInvoker(() => ImportIp(addr)));
            }
            else
            {
                try
                {
                    var row = IpTable.NewRow();
                    row[0] = addr;
                    row[1] = "n/a";
                    IpTable.Rows.Add(row);
                }
                catch (Exception) { }
            }

        }

        private void RemoveIp(string addr)
        {
            var row = SelectByIp(addr);
            if (row.Length > 0)
            {
                IpTable.Rows.Remove(row[0]);
            }
        }

        private DataRow[] SelectByExpr(string expr, string order = null)
        {
            if (InvokeRequired)
            {
                return (DataRow[])Invoke(new MethodInvoker(() => SelectByExpr(expr, order)));
            }

            return IpTable.Select(expr, order);
        }

        private DataRow[] SelectByIp(string addr)
        {

            if (InvokeRequired)
            {
                return (DataRow[])Invoke(new MethodInvoker(() => SelectByIp(addr)));
            }

            return IpTable.Select(string.Format("addr = '{0}'", addr));
        }

        private DataRow[] SelectNa()
        {
            if (InvokeRequired)
            {
                return (DataRow[])Invoke(new MethodInvoker(() => SelectNa()));
            }

            return IpTable.Select("std = 'n/a'");
        }

        private void Tip_MouseEnter(object sender, EventArgs e)
        {
            var control = sender as Control;

            if (control != null)
            {
                lTip.Text = control.Tag.ToString();
            }
            else
            {
                var menu = sender as ToolStripMenuItem;
                if (menu != null)
                {
                    lTip.Text = menu.Tag.ToString();
                }
            }

        }

        private bool IsTesting()
        {
            if (StdIsTesting || RndIsTesting)
            {
                MessageBox.Show("有测试正在进行，无法完成操作！");
                return true;
            }
            return false;
        }

        private void bAddIpRange_Click(object sender, EventArgs e)
        {
            if (IsTesting())
            {
                return;
            }

            var str = tbIpRange.Text.Trim();
            tbIpRange.ResetText();
            if (str == "")
            {
                return;
            }

            var ranges = str.Split(@"`~!?@#$%^&*()=+,<>;:'，。；：“”‘’？、".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);

            foreach (var range in ranges)
            {
                var iprange = IpRange.CreateIpRange(range);
                if (iprange == null)
                {
                    continue;
                }

                for (int a = iprange.Cope[0, 0]; a <= iprange.Cope[0, 1]; a++)
                {
                    for (int b = iprange.Cope[1, 0]; b <= iprange.Cope[1, 1]; b++)
                    {
                        for (int c = iprange.Cope[2, 0]; c <= iprange.Cope[2, 1]; c++)
                        {
                            for (int d = iprange.Cope[3, 0]; d <= iprange.Cope[3, 1]; d++)
                            {
                                ImportIp(a + "." + b + "." + c + "." + d);
                            }
                        }
                    }
                }
            }
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            RemoveAllIps();
            while (TestQueue.Count > 0)
            {
                Application.DoEvents();
            }
        }

        private void mImportIpsInClipbord_Click(object sender, EventArgs e)
        {
            if (IsTesting())
            {
                return;
            }

            var str = "";
            try
            {
                str = Clipboard.GetText();
            }
            catch (Exception)
            {
                MessageBox.Show("操作剪切板可能失败！再试一次吧！");
                return;
            }

            if (str == "")
            {
                MessageBox.Show("剪切板是空的！");
                return;
            }

            var mc = rxMatchIp.Matches(str);

            foreach (Match m in mc)
            {
                ImportIp(m.Value);
            }
        }


        private void mStartStdTest_Click(object sender, EventArgs e)
        {
            if (RndIsTesting)
            {
                MessageBox.Show("正在运行随机测试！");
                return;
            }

            if (StdIsTesting)
            {
                MessageBox.Show("标准测试已运行！");
                return;
            }

            WaitQueue.Clear();

            var rows = SelectNa();

            if (rows.Length == 0)
            {
                MessageBox.Show("没有要测试的IP！");
                return;
            }

            pbProgress.Maximum = rows.Length;
            pbProgress.Value = 0;

            foreach (var row in rows)
            {
                WaitQueue.Enqueue(row[0].ToString());
            }

            StdIsTesting = true;
            StdTestTimer.Start();
        }


        private void RemoveAllIps()
        {
            IpTable.Clear();
            WaitQueue.Clear();
        }

        private void mRemoveAllIps_Click(object sender, EventArgs e)
        {
            if (IsTesting())
            {
                return;
            }

            RemoveAllIps();
        }

        private DataGridViewCell[] GetSelectdIpCells()
        {
            var cells = new List<DataGridViewCell>();

            foreach (DataGridViewCell cell in dgvIpData.SelectedCells)
            {
                if (cell.ColumnIndex == 0)
                {
                    cells.Add(cell);
                }
            }

            cells.Sort((x, y) =>
            {
                if (x.RowIndex > y.RowIndex)
                    return 1;

                if (x.RowIndex == y.RowIndex)
                    return 0;

                return -1;
            });

            return cells.ToArray();
        }

        private DataGridViewCell[] GetAllIpCells()
        {
            var cells = new List<DataGridViewCell>();

            foreach (DataGridViewRow row in dgvIpData.Rows)
            {
                cells.Add(row.Cells[0]);
            }

            cells.Sort((x, y) =>
            {
                if (x.RowIndex > y.RowIndex)
                    return 1;

                if (x.RowIndex == y.RowIndex)
                    return 0;
                else
                    return -1;
            });

            return cells.ToArray();
        }

        private string BuildIpString(string[] strs)
        {
            var sbd = new StringBuilder(strs[0]);

            for (int i = 1; i < strs.Length; i++)
            {
                sbd.Append("|" + strs[i]);
            }

            return sbd.ToString();
        }

        private string BuildIpString(DataGridViewCell[] cells)
        {
            var sbd = new StringBuilder(cells[0].Value.ToString());

            for (int i = 1; i < cells.Length; i++)
            {
                sbd.Append("|" + cells[i].Value);
            }

            return sbd.ToString();
        }

        private void mExportSelectedIps_Click(object sender, EventArgs e)
        {
            if (IsTesting())
            {
                return;
            }

            var cells = GetSelectdIpCells();

            if (cells.Length == 0)
            {
                MessageBox.Show("没有选中的IP！");
                return;
            }

            try
            {
                Clipboard.SetText(BuildIpString(cells));
            }
            catch (Exception) { MessageBox.Show("操作剪切板可能失败！再试一次吧！"); }
        }

        private void nPingTimeout_ValueChanged(object sender, EventArgs e)
        {
            PingTimeout = Convert.ToInt32(nPingTimeout.Value);
        }

        private void nTestTimeout_ValueChanged(object sender, EventArgs e)
        {
            TestTimeout = Convert.ToInt32(nTestTimeout.Value);
        }

        private void nMaxTest_ValueChanged(object sender, EventArgs e)
        {
            MaxThreads = Convert.ToInt32(nMaxThreads.Value);
        }

        private void mStopTest_Click(object sender, EventArgs e)
        {
            StdIsTesting = false;
            RndIsTesting = false;
        }

        private void mExportAllIps_Click(object sender, EventArgs e)
        {
            if (IsTesting())
            {
                return;
            }

            var cells = GetAllIpCells();

            if (cells.Length == 0)
            {
                MessageBox.Show("IP列表是空的！");
                return;
            }

            try
            {
                Clipboard.SetText(BuildIpString(cells));
            }
            catch (Exception) { MessageBox.Show("操作剪切板可能失败！再试一次吧！"); }
        }

        private void mRemoveSelectedIps_Click(object sender, EventArgs e)
        {
            if (IsTesting())
            {
                return;
            }

            foreach (DataGridViewRow row in dgvIpData.SelectedRows)
            {
                dgvIpData.Rows.Remove(row);
            }
        }

        private void mRemoveIpsInClipbord_Click(object sender, EventArgs e)
        {
            if (IsTesting())
            {
                return;
            }

            var str = "";
            try
            {
                str = Clipboard.GetText();
            }
            catch (Exception)
            {
                MessageBox.Show("操作剪切板可能失败！再试一次吧！");
                return;
            }

            if (str == "")
            {
                MessageBox.Show("剪切板是空的！");
                return;
            }

            var mc = rxMatchIp.Matches(str);

            foreach (Match m in mc)
            {
                RemoveIp(m.Value);
            }
        }

        private void mStartRndTest_Click(object sender, EventArgs e)
        {
            if (StdIsTesting)
            {
                MessageBox.Show("正在运行标准测试！");
                return;
            }

            if (RndIsTesting)
            {
                MessageBox.Show("随机测试已运行！");
                return;
            }

            if (MessageBox.Show(this, "随机测试前会清除IP列表，是否继续操作？", "请确认操作！", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Cancel)
            {
                return;
            }

            var form = new Form2();
            form.ShowDialog(this);

            if (Form2.RandomNumber == 0)
            {
                return;
            }

            pbProgress.Maximum = Form2.RandomNumber;
            pbProgress.Value = 0;

            RemoveAllIps();

            RndIsTesting = true;
            RndTestTimer.Start();
        }

        private void mApplyValidIpsToUserConfig_Click(object sender, EventArgs e)
        {
            if (IsTesting())
            {
                return;
            }

            if (!File.Exists("proxy.py"))
            {
                MessageBox.Show("请将本程序放入GOAgent目录内！");
                return;
            }

            var vips = GetStdValidIps();

            if (vips.Length == 0)
            {
                MessageBox.Show("没有可用的IP！");
                return;
            }

            var ipstr = BuildIpString(vips);

            ApplyToUserConfig(ipstr);
        }

        private void ApplyToUserConfig(string ipstr)
        {
            if (!File.Exists("proxy.user.ini"))
            {
                File.WriteAllText("proxy.user.ini", "");
            }

            var inifile = new IniFile("proxy.user.ini");

            inifile.WriteValue("iplist", "google_cn", ipstr);
            inifile.WriteValue("iplist", "google_hk", ipstr);

            inifile.WriteFile();

            MessageBox.Show("已写入proxy.user.ini！重新载入GoAgent就可生效！");
        }
        
        private string[] GetStdValidIps()
        {
            var ls = new List<string>();
            var rows = SelectByExpr(string.Format("std like '_OK%'"), "std asc");
            foreach (var row in rows)
            {
                ls.Add(row[0].ToString());
            }
            return ls.ToArray();
        }

        private void mApplySelectedIpsToUserConfig_Click(object sender, EventArgs e)
        {
            if (IsTesting())
            {
                return;
            }

            if (!File.Exists("proxy.py"))
            {
                MessageBox.Show("请将本程序放入GOAgent目录内！");
                return;
            }

            var cells = GetSelectdIpCells();

            if (cells.Length == 0)
            {
                MessageBox.Show("没有选中的IP！");
                return;
            }

            var ipstr = BuildIpString(cells);

            ApplyToUserConfig(ipstr);
        }
    }
}
