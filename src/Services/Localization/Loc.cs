using System;
using System.Globalization;

namespace ScreenLock.Services.Localization
{
    public static class Loc
    {
        public static string T(string key)
        {
            return I18nService.Instance.Get(key);
        }

        public static string T(string key, params object[] args)
        {
            return I18nService.Instance.Format(key, args);
        }

        public static void SetLanguage(string langCode)
        {
            I18nService.Instance.SetLanguage(langCode);
        }

        public static string CurrentLanguage => I18nService.Instance.CurrentLanguage;
        public static CultureInfo CurrentCulture => I18nService.Instance.CurrentCulture;
    }
}
