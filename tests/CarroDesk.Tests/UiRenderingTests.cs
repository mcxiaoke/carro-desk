using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CarroDesk.Modules.ClipboardHistory.Models;
using CarroDesk.Modules.ClipboardHistory.Services;
using CarroDesk.Modules.ClipboardHistory.Views;
using CarroDesk.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    [TestClass]
    public class UiRenderingTests
    {
        private class MemoryClipboardStorage : IClipboardHistoryStorage
        {
            public List<ClipboardItem> SavedItems = new List<ClipboardItem>();

            public List<ClipboardItem> Load()
            {
                return new List<ClipboardItem>(SavedItems);
            }

            public void Save(List<ClipboardItem> items)
            {
                SavedItems = new List<ClipboardItem>(items);
            }
        }

        private void RunInSta(Action action)
        {
            // 统一使用 TestEnvironment 的 STA 执行器：
            // 它会显式把 Application.ShutdownMode 设为 OnExplicitShutdown，
            // 避免"关闭最后一个窗口"关停 Application 后波及其它 UI 测试。
            TestEnvironment.RunInSta(action);
        }

        private void SaveWindowSnapshot(Window win, double width, double height, string filename)
        {
            win.Width = width;
            win.Height = height;
            win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            win.Show();

            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                (Action)(() => { frame.Continue = false; }));
            System.Windows.Threading.Dispatcher.PushFrame(frame);

            Thread.Sleep(150);

            int pxW = (int)Math.Max(1, win.ActualWidth > 0 ? win.ActualWidth : width);
            int pxH = (int)Math.Max(1, win.ActualHeight > 0 ? win.ActualHeight : height);
            var rtb = new RenderTargetBitmap(pxW, pxH, 96, 96, PixelFormats.Pbgra32);
            Visual target = win;
            if (win.AllowsTransparency && win.Content is Visual contentVisual)
            {
                target = contentVisual;
            }
            rtb.Render(target);

            string projectRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\.."));
            string dir = Path.Combine(projectRoot, @"temp\screenshots");
            Directory.CreateDirectory(dir);
            string fullPath = Path.Combine(dir, filename);

            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(fullPath))
            {
                enc.Save(fs);
            }

            win.Close();
        }

        [TestMethod]
        public void Render_ConfigEditorWindow_NoOverlaps_And_SavesSnapshot()
        {
            RunInSta(() =>
            {
                var win = new ConfigEditorWindow(null, null, null);
                SaveWindowSnapshot(win, 580, 720, "ConfigEditorWindow.png");
            });
        }

        [TestMethod]
        public void Render_ClipboardHistoryWindow_NoOverlaps_And_SavesSnapshot()
        {
            RunInSta(() =>
            {
                var storage = new MemoryClipboardStorage();
                var service = new ClipboardHistoryService(storage);
                service.Start(ClipboardHistoryConfig.CreateDefault());
                service.RecordText("第1条多行文本测试\r\n第二行内容预览\r\n第三行内容预览\r\n第四行省略...");
                service.RecordText("第2条短文本 📌 置顶测试");
                service.RecordText("第3条代码片段:\r\nfunction hello() {\r\n    console.log('world');\r\n}");

                var winNormal = new ClipboardHistoryWindow(service);
                winNormal.AutoCloseOnDeactivate = false;
                SaveWindowSnapshot(winNormal, 460, 540, "ClipboardHistoryWindow_Normal.png");

                var winEnlarged = new ClipboardHistoryWindow(service);
                winEnlarged.AutoCloseOnDeactivate = false;
                SaveWindowSnapshot(winEnlarged, 780, 720, "ClipboardHistoryWindow_Enlarged.png");
            });
        }

        [TestMethod]
        public void XamlLayout_AllGrids_HaveSufficientRowAndColumnDefinitions()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\.."));
            string srcDir = Path.Combine(projectRoot, "src");
            var xamlFiles = Directory.GetFiles(srcDir, "*.xaml", SearchOption.AllDirectories);

            var errors = new List<string>();
            foreach (var file in xamlFiles)
            {
                string text = File.ReadAllText(file);
                var gridMatches = Regex.Matches(text, @"<Grid\b.*?</Grid>", RegexOptions.Singleline);
                for (int i = 0; i < gridMatches.Count; i++)
                {
                    string g = gridMatches[i].Value;
                    int rowDefs = Regex.Matches(g, @"<RowDefinition\b").Count;
                    int colDefs = Regex.Matches(g, @"<ColumnDefinition\b").Count;

                    var rowMatches = Regex.Matches(g, @"Grid\.Row=""(\d+)""");
                    int maxRow = 0;
                    foreach (Match m in rowMatches)
                    {
                        if (int.TryParse(m.Groups[1].Value, out int r) && r > maxRow) maxRow = r;
                    }

                    var colMatches = Regex.Matches(g, @"Grid\.Column=""(\d+)""");
                    int maxCol = 0;
                    foreach (Match m in colMatches)
                    {
                        if (int.TryParse(m.Groups[1].Value, out int c) && c > maxCol) maxCol = c;
                    }

                    if (rowDefs > 0 && maxRow >= rowDefs)
                    {
                        errors.Add($"File: {Path.GetFileName(file)}, Grid #{i}: defined {rowDefs} rows, but element uses Grid.Row=\"{maxRow}\"");
                    }
                    if (colDefs > 0 && maxCol >= colDefs)
                    {
                        errors.Add($"File: {Path.GetFileName(file)}, Grid #{i}: defined {colDefs} cols, but element uses Grid.Column=\"{maxCol}\"");
                    }
                }
            }

            Assert.AreEqual(0, errors.Count, "Grid definitions overflow found:\n" + string.Join("\n", errors));
        }
    }
}
