using System;
using System.Collections.Generic;
using System.IO;
using CarroDesk.Core;
using CarroDesk.Core.Models;
using CarroDesk.Modules.ClipboardHistory.Models;
using CarroDesk.Modules.ClipboardHistory.Services;
using CarroDesk.Modules.ClipboardHistory.Views;
using CarroDesk.Services;
using CarroDesk.Services.Localization;

namespace CarroDesk.Modules.ClipboardHistory
{
    public class ClipboardHistoryModule : ModuleBase<ClipboardHistoryConfig>
    {
        public override string Id => "ClipboardHistory";
        public override string Name => Loc.T("Tray.ClipboardHistory", "剪贴板历史");
        public override string Description => "记录剪贴板文本历史，提供快速搜索与按需回写";
        public override string Version => "1.0.0";
        public override int Order => 35;
        public override bool DefaultEnabled => true;

        private ClipboardHistoryService _service;
        private IClipboardListener _listener;
        private ClipboardHistoryWindow _window;
        private int _hotkeyId;

        /// <summary>模块自建存储（测试注入 service 时为 null），仅用于退出前 Flush。</summary>
        private IClipboardHistoryStorage _ownedStorage;

        public ClipboardHistoryModule()
        {
        }

        // 供单元测试注入模拟 listener 与 service
        public ClipboardHistoryModule(ClipboardHistoryService service, IClipboardListener listener)
        {
            _service = service;
            _listener = listener;
        }

        public override void Initialize(IModuleContext context)
        {
            base.Initialize(context);

            if (_service == null)
            {
                string storagePath = Path.Combine(ConfigService.DirPath, "data", "ClipboardHistory", "history.json");
                var storage = new JsonClipboardHistoryStorage(storagePath);
                // 保存模块自建的存储引用：其写入是后台合并落盘（不阻塞 UI），
                // 退出前必须 Flush，否则最后一批剪贴板记录会丢失。
                _ownedStorage = storage;
                _service = new ClipboardHistoryService(storage);
            }

            if (_listener == null)
            {
                _listener = new Win32ClipboardListener();
            }

            _listener.ClipboardUpdated += OnClipboardUpdated;
        }

        protected override void OnStart()
        {
            _service?.Start(Config);

            if (Config.AutoRecord)
            {
                _listener?.Start();
            }

            RegisterHotkey();
        }

        protected override void OnStop()
        {
            UnregisterHotkey();

            _listener?.Stop();

            Context?.Dispatcher?.Invoke(() =>
            {
                if (_window != null)
                {
                    try
                    {
                        _window.Close();
                    }
                    catch
                    {
                    }
                    _window = null;
                }
            });

            // 等待后台合并写入落盘
            FlushStorage();
        }

        private void FlushStorage()
        {
            try
            {
                var disposable = _ownedStorage as JsonClipboardHistoryStorage;
                if (disposable != null) disposable.Flush();
            }
            catch
            {
                // 落盘等待失败不应影响模块停止流程
            }
        }

        public override void OnConfigReloaded()
        {
            base.OnConfigReloaded();

            _service?.ApplyConfig(Config);

            if (Config.AutoRecord)
            {
                _listener?.Start();
            }
            else
            {
                _listener?.Stop();
            }

            RegisterHotkey();
            Context?.RequestTrayRefresh();
        }

        public override IEnumerable<TrayMenuItem> GetTrayMenuItems()
        {
            int count = _service?.Count ?? 0;
            var root = new TrayMenuItem
            {
                Id = "clipboard_root",
                Header = $"{Name} ({count})",
                ToolTip = $"{Name} (快捷键: {Config?.Hotkey ?? "Win+Alt+V"})"
            };

            root.Children.Add(new TrayMenuItem
            {
                Id = "clipboard_open",
                Header = Loc.T("Clipboard.OpenWindow", "打开剪贴板历史..."),
                ClickAction = SummonWindow
            });

            root.Children.Add(new TrayMenuItem
            {
                Id = "clipboard_auto_record",
                Header = Loc.T("Clipboard.AutoRecord", "自动记录剪贴板"),
                IsChecked = Config?.AutoRecord ?? true,
                ClickAction = ToggleAutoRecord
            });

            root.Children.Add(TrayMenuItem.Separator());

            root.Children.Add(new TrayMenuItem
            {
                Id = "clipboard_clear_all",
                Header = Loc.T("Clipboard.ClearAll", "清空历史记录"),
                ClickAction = ClearAllHistory
            });

            yield return root;
        }

        public void SummonWindow()
        {
            var dispatcher = Context?.Dispatcher;
            if (dispatcher == null) return;

            dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (_window == null)
                    {
                        _window = new ClipboardHistoryWindow(_service);
                        _window.Closed += (s, e) => _window = null;
                    }
                    _window.ShowAndActivate();
                }
                catch (Exception ex)
                {
                    Context?.GetService<ILoggerService>()?.LogError(Id, "唤出剪贴板历史窗口异常", ex);
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void ToggleAutoRecord()
        {
            Config.AutoRecord = !Config.AutoRecord;
            if (Config.AutoRecord)
            {
                _listener?.Start();
            }
            else
            {
                _listener?.Stop();
            }

            Context?.GetService<IConfigManager>()?.SaveModuleConfig(Id, Config);
            Context?.RequestTrayRefresh();
        }

        private void ClearAllHistory()
        {
            int count = _service?.Count ?? 0;
            if (count == 0) return;

            if (System.Windows.MessageBox.Show("确定要清空剪贴板历史记录吗？\n\n(已固定的记录将被安全保留)", "清空确认", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes)
            {
                _service?.ClearAll(preservePinned: true);
                Context?.RequestTrayRefresh();
            }
        }

        private void OnClipboardUpdated()
        {
            if (Config == null || !Config.AutoRecord) return;

            Context?.Dispatcher?.InvokeAsync(() =>
            {
                try
                {
                    if (ClipboardHelper.TryGetText(out string text))
                    {
                        _service?.RecordText(text);
                        Context?.RequestTrayRefresh();
                    }
                }
                catch
                {
                    // 忽略剪贴板争夺异常
                }
            });
        }

        private void RegisterHotkey()
        {
            UnregisterHotkey();

            var hotkeys = Context?.GetService<IHotkeyService>();
            if (hotkeys != null && !string.IsNullOrWhiteSpace(Config?.Hotkey))
            {
                _hotkeyId = hotkeys.Register(Id, Config.Hotkey, SummonWindow, out _);
            }
        }

        private void UnregisterHotkey()
        {
            if (_hotkeyId != 0)
            {
                var hotkeys = Context?.GetService<IHotkeyService>();
                hotkeys?.Unregister(Id, _hotkeyId);
                _hotkeyId = 0;
            }
        }

        public override void Dispose()
        {
            if (_listener != null)
            {
                _listener.ClipboardUpdated -= OnClipboardUpdated;
                _listener.Dispose();
                _listener = null;
            }

            // base.Dispose() 会触发 Stop()，但 Stop 在未运行状态下会直接返回，
            // 这里再兜底一次，确保后台合并写入一定落盘。
            FlushStorage();

            base.Dispose();
        }
    }
}
