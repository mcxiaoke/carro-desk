using System;
using System.Globalization;

namespace CarroDesk.Services.Localization
{
    public static class Loc
    {
        public static string T(string key)
        {
            return I18nService.Instance.Get(key);
        }

        public static string T(string key, string defaultValue)
        {
            return I18nService.Instance.Get(key, defaultValue);
        }

        public static string T(string key, string defaultValue, params object[] args)
        {
            return I18nService.Instance.FormatWithDefault(key, defaultValue, args);
        }

        public static string T(string key, params object[] args)
        {
            return I18nService.Instance.Format(key, args);
        }

        public static string Format(string key, params object[] args)
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
