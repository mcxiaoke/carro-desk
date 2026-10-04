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
        public override string Description => Loc.T("Clipboard.ModuleDesc", "记录剪贴板文本历史，提供快速搜索与按需回写");
        public override string Version => "1.0.0";
        public override int Order => 35;
        public override bool DefaultEnabled => true;

        private ClipboardHistoryService _service;
        private IClipboardListener _listener;
        private ClipboardHistoryWindow _window;
        private ClipboardStorageCrypto _crypto;

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
                _crypto = new ClipboardStorageCrypto { Enabled = Config != null && Config.EncryptStorage };
                var storage = new JsonClipboardHistoryStorage(storagePath, _crypto);
                storage.DecryptionFailed += OnHistoryDecryptionFailed;
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

            if (Config.Enabled && Config.AutoRecord)
            {
                _listener?.Start();
            }

            RegisterManagedHotkey(() => Config?.Hotkey, SummonWindow);
        }

        protected override void OnStop()
        {
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

        /// <summary>
        /// 加密历史解密失败（Windows 密码被重置 / 换用户运行）：原文件已由存储层改名留档，
        /// 本会话以空历史运行，这里只负责告知用户与留痕。
        /// </summary>
        private void OnHistoryDecryptionFailed(string archivedPath)
        {
            string message = archivedPath != null
                ? Loc.T("Clipboard.EncryptUndecryptableArchived",
                    "加密的剪贴板历史无法在当前环境解密（可能 Windows 密码被重置或更换了用户），原文件已保留为：{0}。本次将以空历史运行。", archivedPath)
                : Loc.T("Clipboard.EncryptUndecryptableFrozen",
                    "加密的剪贴板历史无法在当前环境解密且留档失败，为防止数据丢失已暂停读写历史文件。");

            LogWarning(message);

            var dispatcher = Context?.Dispatcher;
            if (dispatcher != null)
            {
                dispatcher.BeginInvoke(new Action(() =>
                {
                    try { Context?.ShowNotification(message); } catch { }
                }));
            }
            else
            {
                try { Context?.ShowNotification(message); } catch { }
            }
        }

        /// <summary>
        /// 切换历史文件的加密格式（设置窗口调用）：立即用当前内存历史重写磁盘，
        /// Flush 保证确定性落盘；成功后由调用方保存模块配置。
        /// 磁盘与配置短暂不一致时，存储层按文件头自识别读取、下次落盘自愈。
        /// </summary>
        public bool EnableStorageEncryption(bool enable)
        {
            if (_service == null || _crypto == null) return false;
            if (_crypto.Enabled == enable) return true;

            _crypto.Enabled = enable;
            try
            {
                var storage = _ownedStorage as JsonClipboardHistoryStorage;
                if (storage != null)
                {
                    storage.Save(new List<ClipboardItem>(_service.GetItems()));
                    storage.Flush();
                }
                return true;
            }
            catch (Exception ex)
            {
                _crypto.Enabled = !enable;
                LogError("切换剪贴板历史加密存储失败", ex);
                return false;
            }
        }

        public override void OnConfigReloaded()
        {
            base.OnConfigReloaded();

            if (_crypto != null)
            {
                _crypto.Enabled = Config != null && Config.EncryptStorage;
            }

            _service?.ApplyConfig(Config);

            if (Config.Enabled && Config.AutoRecord)
            {
                _listener?.Start();
            }
            else
            {
                _listener?.Stop();
            }

            RequestTrayRefresh();
        }

        public override IEnumerable<TrayMenuItem> GetTrayMenuItems()
        {
            int count = _service?.Count ?? 0;
            var root = new TrayMenuItem
            {
                Id = "clipboard_root",
                Header = $"{Name} ({count})",
                ToolTip = Loc.T("Clipboard.TooltipWithHotkey", "{0} (快捷键: {1})", Name, Config?.Hotkey ?? "Win+Alt+V")
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
                Id = "clipboard_settings",
                Header = Loc.T("Clipboard.Settings", "剪贴板历史设置..."),
                ClickAction = OpenSettings
            });

            root.Children.Add(new TrayMenuItem
            {
                Id = "clipboard_clear_all",
                Header = Loc.T("Clipboard.ClearAll", "清空历史记录"),
                ClickAction = ClearAllHistory
            });

            yield return root;
        }

        public void OpenSettings()
        {
            try
            {
                var cfgMgr = Context?.GetService<IConfigManager>();
                var win = new ClipboardHistorySettingsWindow(this, cfgMgr, msg => Context?.ShowNotification(msg))
                {
                    WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen
                };
                win.ShowDialog();
                Context?.RequestTrayRefresh();
            }
            catch (Exception ex)
            {
                Context?.GetService<ILoggerService>()?.LogError(Id, "打开剪贴板历史设置窗口异常", ex);
            }
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
            bool previous = Config.AutoRecord;
            Config.AutoRecord = !previous;
            var configMgr = Context?.GetService<IConfigManager>();
            if (configMgr == null || !configMgr.SaveModuleConfig(Id, Config))
            {
                Config.AutoRecord = previous;
                Context?.RequestTrayRefresh();
                return;
            }
            if (Config.Enabled && Config.AutoRecord)
            {
                _listener?.Start();
            }
            else
            {
                _listener?.Stop();
            }

            Context?.RequestTrayRefresh();
        }

        private void ClearAllHistory()
        {
            int count = _service?.Count ?? 0;
            if (count == 0) return;

            if (System.Windows.MessageBox.Show(Loc.T("Clipboard.ClearConfirmKeepPinned", "确定要清空剪贴板历史记录吗？\n\n(已固定的记录将被安全保留)"), Loc.T("Clipboard.ClearConfirmCaption", "清空确认"), System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes)
            {
                _service?.ClearAll(preservePinned: true);
                Context?.RequestTrayRefresh();
            }
        }

        private void OnClipboardUpdated(string text)
        {
            if (Config == null || !Config.Enabled || !Config.AutoRecord || string.IsNullOrWhiteSpace(text)) return;

            Context?.Dispatcher?.InvokeAsync(() =>
            {
                try
                {
                    _service?.RecordText(text);
                    Context?.RequestTrayRefresh();
                }
                catch
                {
                    // 忽略剪贴板处理异常
                }
            });
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
            (_ownedStorage as IDisposable)?.Dispose();
            _ownedStorage = null;
        }
    }
}
