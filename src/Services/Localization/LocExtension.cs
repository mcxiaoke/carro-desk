using System;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace ScreenLock.Services.Localization
{
    [MarkupExtensionReturnType(typeof(string))]
    public class LocExtension : MarkupExtension
    {
        public string Key { get; set; }
        public string Default { get; set; }

        public LocExtension()
        {
        }

        public LocExtension(string key)
        {
            Key = key;
        }

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            if (string.IsNullOrEmpty(Key)) return Default ?? string.Empty;

            var target = serviceProvider?.GetService(typeof(IProvideValueTarget)) as IProvideValueTarget;
            if (target?.TargetObject is DependencyObject)
            {
                var binding = new Binding
                {
                    Source = I18nService.Instance,
                    Path = new PropertyPath($"[{Key}]"),
                    Mode = BindingMode.OneWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                    FallbackValue = Default ?? Key,
                    TargetNullValue = Default ?? Key
                };
                return binding.ProvideValue(serviceProvider);
            }

            // 非 DependencyObject（如 Setter 内部或设计器求值兜底）
            return I18nService.Instance.Get(Key, Default);
        }
    }
}
