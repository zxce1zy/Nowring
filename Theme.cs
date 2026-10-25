using System;
using System.Windows;
using System.Windows.Media;

namespace Nowring;

static class Theme
{
    public static bool Dark { get; private set; } = true;
    public static event Action? Changed;

    public static void Apply(bool dark)
    {
        Dark = dark;
        var r = Application.Current.Resources;
        void Set(string key, string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            r[key] = b;
        }

        if (dark)
        {
            Set("Bg", "#16161B"); Set("Edge", "#2A2A33"); Set("Text", "#E6E6EA"); Set("Title", "#FFFFFF");
            Set("Sub", "#A0A0AC"); Set("Muted", "#7C7C88"); Set("Card", "#22222A"); Set("CardHover", "#2E2E38");
            Set("Track", "#2C2C35"); Set("Off", "#3A3A44"); Set("Chip", "#2B3066"); Set("ChipText", "#B9C0FF");
            Set("Divider", "#26262E"); Set("CoverBg", "#24242C");
        }
        else
        {
            Set("Bg", "#F7F7FA"); Set("Edge", "#DADAE2"); Set("Text", "#1C1C22"); Set("Title", "#0E0E12");
            Set("Sub", "#5C5C68"); Set("Muted", "#84848F"); Set("Card", "#ECECF1"); Set("CardHover", "#E0E0E7");
            Set("Track", "#DCDCE4"); Set("Off", "#C4C4CE"); Set("Chip", "#E3E5FF"); Set("ChipText", "#3B45C4");
            Set("Divider", "#E4E4EA"); Set("CoverBg", "#E6E6EC");
        }
        Set("Accent", "#5865F2");
        Changed?.Invoke();
    }
}
