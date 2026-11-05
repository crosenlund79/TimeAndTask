"""Generates the visual-preview colour palettes (src/WorkTrace.App/Themes/Variants/<Variant>/Palette.xaml).

WPF's Fluent theme defines ~500 brushes, each from one of ~90 base colours (plus the accent ramp). Overriding only the
base colours does not work (the brushes resolve them statically), so this script re-creates every Fluent brush with a
variant's base colours. All standard controls (buttons, combo boxes, date pickers, check boxes, lists, scroll bars,
tooltips...) then follow the variant.

Usage: python3 tools/visual-preview/generate_palettes.py [folder with Light.xaml and Dark.xaml]
(the Fluent theme sources are downloaded from dotnet/wpf release/10.0 when no folder is given).
"""
import os
import re
import sys
import urllib.request

SOURCE = "https://raw.githubusercontent.com/dotnet/wpf/release/10.0/src/Microsoft.DotNet.Wpf/src/Themes/PresentationFramework.Fluent/Resources/Theme/{}.xaml"
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = os.path.join(ROOT, "src", "WorkTrace.App", "Themes", "Variants")


def load(name, folder):
    if folder:
        with open(os.path.join(folder, name + ".xaml"), encoding="utf-8") as f:
            return f.read()
    with urllib.request.urlopen(SOURCE.format(name)) as r:
        return r.read().decode("utf-8")


def parse(text):
    colors = dict(re.findall(r'<Color x:Key="([^"]+)">\s*(#[0-9A-Fa-f]+)\s*</Color>', text))
    brushes = []
    for attrs in re.findall(r"<SolidColorBrush\s+([^>]*?)/>", text, re.S):
        key = re.search(r'x:Key="([^"]+)"', attrs)
        color = re.search(r'Color="([^"]+)"', attrs)
        opacity = re.search(r'Opacity="([^"]+)"', attrs)
        if not key or not color:
            continue
        ref = re.match(r"\{(?:StaticResource|DynamicResource)\s+([^}]+)\}", color.group(1))
        brushes.append((key.group(1), ref.group(1).strip() if ref else None, None if ref else color.group(1), opacity.group(1) if opacity else None))
    return colors, brushes


def argb(hex_):
    if not hex_.startswith("#"):
        return hex_  # named colour (e.g. Transparent)
    h = hex_.lstrip("#")
    if len(h) == 6:
        h = "FF" + h
    return "#" + h.upper()


# Accent ramps: base, Light1-3, Dark1-3 (Fluent light uses Dark1 for fills and Dark2 for accent text; dark uses Light2/Light3).
VARIANTS = {
    "ModernFluent": dict(
        base="Light",
        accent=dict(SystemAccentColor="#7160E8", SystemAccentColorLight1="#8A7CF0", SystemAccentColorLight2="#A79DF5",
                    SystemAccentColorLight3="#C9C2FA", SystemAccentColorDark1="#6352D6", SystemAccentColorDark2="#5243B8",
                    SystemAccentColorDark3="#3F3391"),
        colors=dict(
            ApplicationBackgroundColor="#F3F4F8", SolidBackgroundFillColorBase="#F1F2F6", SolidBackgroundFillColorSecondary="#E9EBF1",
            SolidBackgroundFillColorTertiary="#F8F9FB", SolidBackgroundFillColorQuarternary="#FFFFFF", SolidBackgroundFillColorBaseAlt="#DCDFE7",
            CardBackgroundFillColorDefault="#FFFFFF", CardBackgroundFillColorSecondary="#F7F8FB", LayerFillColorDefault="#FAFBFD",
            LayerFillColorAlt="#FFFFFF", AcrylicBackgroundFillColorDefault="#FAFBFD", LayerOnMicaBaseAltFillColorDefault="#FFFFFF",
            ControlFillColorDefault="#FFFFFF", ControlFillColorSecondary="#F6F7FA", ControlFillColorTertiary="#EEF0F5",
            ControlFillColorInputActive="#FFFFFF", ControlSolidFillColorDefault="#FFFFFF",
            TextFillColorPrimary="#1B1D26", TextFillColorSecondary="#5B6072", TextFillColorTertiary="#868B9C", TextFillColorDisabled="#A8ACB8",
            ControlStrokeColorDefault="#141A2340", ControlStrokeColorSecondary="#2E1A2340", CardStrokeColorDefault="#121A2340",
            CardStrokeColorDefaultSolid="#E5E7EE", DividerStrokeColorDefault="#141A2340", SurfaceStrokeColorFlyout="#141A2340",
            ControlStrongStrokeColorDefault="#8A90A2", SubtleFillColorSecondary="#0C3B3F78", SubtleFillColorTertiary="#073B3F78",
            ControlAltFillColorSecondary="#08303A60", ControlAltFillColorTertiary="#10303A60", ControlAltFillColorQuarternary="#18303A60",
        ),
    ),
    "DarkProfessional": dict(
        base="Dark",
        accent=dict(SystemAccentColor="#8B5CF6", SystemAccentColorLight1="#9B73F8", SystemAccentColorLight2="#8B5CF6",
                    SystemAccentColorLight3="#B7A0FB", SystemAccentColorDark1="#7444E0", SystemAccentColorDark2="#5E33C2",
                    SystemAccentColorDark3="#4A27A0"),
        colors=dict(
            ApplicationBackgroundColor="#131317", SolidBackgroundFillColorBase="#18181D", SolidBackgroundFillColorSecondary="#141418",
            SolidBackgroundFillColorTertiary="#1F1F25", SolidBackgroundFillColorQuarternary="#26262D", SolidBackgroundFillColorBaseAlt="#0E0E11",
            CardBackgroundFillColorDefault="#212128", CardBackgroundFillColorSecondary="#1C1C22", LayerFillColorDefault="#1A1A20",
            LayerFillColorAlt="#24242B", AcrylicBackgroundFillColorDefault="#1E1E24", LayerOnMicaBaseAltFillColorDefault="#24242B",
            ControlFillColorDefault="#2A2A33", ControlFillColorSecondary="#31313B", ControlFillColorTertiary="#25252D",
            ControlFillColorInputActive="#1B1B21", ControlSolidFillColorDefault="#2E2E37", ControlFillColorDisabled="#1F1F25",
            TextFillColorPrimary="#EEEDF4", TextFillColorSecondary="#A9A7B8", TextFillColorTertiary="#7E7C8E", TextFillColorDisabled="#5F5D6E",
            TextOnAccentFillColorPrimary="#FFFFFF", TextOnAccentFillColorSecondary="#E0FFFFFF", TextOnAccentFillColorSelectedText="#FFFFFF",
            ControlStrokeColorDefault="#14FFFFFF", ControlStrokeColorSecondary="#22FFFFFF", CardStrokeColorDefault="#12FFFFFF",
            CardStrokeColorDefaultSolid="#2C2C35", DividerStrokeColorDefault="#14FFFFFF", SurfaceStrokeColorFlyout="#20FFFFFF",
            SubtleFillColorSecondary="#14B9A4FF", SubtleFillColorTertiary="#0CB9A4FF", ControlStrongStrokeColorDefault="#8C8A9E",
            SystemFillColorSuccess="#5FD08C", SystemFillColorCaution="#F2B84B", SystemFillColorCritical="#F2707A",
        ),
    ),
    "RefinedNeutral": dict(
        base="Light",
        accent=dict(SystemAccentColor="#6F7D52", SystemAccentColorLight1="#8A9770", SystemAccentColorLight2="#A8B293",
                    SystemAccentColorLight3="#CBD1BB", SystemAccentColorDark1="#5F6B45", SystemAccentColorDark2="#4F5939",
                    SystemAccentColorDark3="#3E462C"),
        colors=dict(
            ApplicationBackgroundColor="#F4EFE5", SolidBackgroundFillColorBase="#F2ECE1", SolidBackgroundFillColorSecondary="#EAE3D5",
            SolidBackgroundFillColorTertiary="#F8F4EC", SolidBackgroundFillColorQuarternary="#FFFDF8", SolidBackgroundFillColorBaseAlt="#DDD4C3",
            CardBackgroundFillColorDefault="#FFFCF6", CardBackgroundFillColorSecondary="#F7F2E8", LayerFillColorDefault="#FAF7F0",
            LayerFillColorAlt="#FFFDF8", AcrylicBackgroundFillColorDefault="#FAF6EE", LayerOnMicaBaseAltFillColorDefault="#FFFCF6",
            ControlFillColorDefault="#FFFDF9", ControlFillColorSecondary="#F7F2E9", ControlFillColorTertiary="#EFE8DB",
            ControlFillColorInputActive="#FFFFFF", ControlSolidFillColorDefault="#FFFDF9",
            TextFillColorPrimary="#2D2922", TextFillColorSecondary="#6B6355", TextFillColorTertiary="#958C7D", TextFillColorDisabled="#B5AC9C",
            ControlStrokeColorDefault="#1F5A4A30", ControlStrokeColorSecondary="#335A4A30", CardStrokeColorDefault="#1C5A4A30",
            CardStrokeColorDefaultSolid="#E6DECF", DividerStrokeColorDefault="#1F5A4A30", SurfaceStrokeColorFlyout="#1F5A4A30",
            ControlStrongStrokeColorDefault="#9A8F7C", SubtleFillColorSecondary="#0F6B5A30", SubtleFillColorTertiary="#096B5A30",
            ControlAltFillColorSecondary="#0A6B5A30", ControlAltFillColorTertiary="#126B5A30", ControlAltFillColorQuarternary="#1A6B5A30",
            SystemFillColorSuccess="#4E7A3A", SystemFillColorCaution="#A0682A", SystemFillColorCritical="#A8463A",
        ),
    ),
    "ModernGradient": dict(
        base="Light",
        accent=dict(SystemAccentColor="#7B5CFF", SystemAccentColorLight1="#957DFF", SystemAccentColorLight2="#B3A2FF",
                    SystemAccentColorLight3="#D3C9FF", SystemAccentColorDark1="#6A45F5", SystemAccentColorDark2="#5733D6",
                    SystemAccentColorDark3="#4425AE"),
        colors=dict(
            ApplicationBackgroundColor="#EEEBFB", SolidBackgroundFillColorBase="#F3F1FD", SolidBackgroundFillColorSecondary="#E9E5FA",
            SolidBackgroundFillColorTertiary="#F8F7FE", SolidBackgroundFillColorQuarternary="#FFFFFF", SolidBackgroundFillColorBaseAlt="#DAD4F5",
            CardBackgroundFillColorDefault="#FFFFFF", CardBackgroundFillColorSecondary="#F7F5FF", LayerFillColorDefault="#E6FFFFFF",
            LayerFillColorAlt="#FFFFFF", AcrylicBackgroundFillColorDefault="#F8F7FE", LayerOnMicaBaseAltFillColorDefault="#FFFFFF",
            ControlFillColorDefault="#FFFFFF", ControlFillColorSecondary="#F6F4FF", ControlFillColorTertiary="#EEEAFE",
            ControlFillColorInputActive="#FFFFFF", ControlSolidFillColorDefault="#FFFFFF",
            TextFillColorPrimary="#1C1838", TextFillColorSecondary="#5A5578", TextFillColorTertiary="#8A85A6", TextFillColorDisabled="#ABA7C2",
            ControlStrokeColorDefault="#1A4B3AA8", ControlStrokeColorSecondary="#334B3AA8", CardStrokeColorDefault="#164B3AA8",
            CardStrokeColorDefaultSolid="#E2DCF8", DividerStrokeColorDefault="#184B3AA8", SurfaceStrokeColorFlyout="#1A4B3AA8",
            ControlStrongStrokeColorDefault="#8D86B0", SubtleFillColorSecondary="#127B5CFF", SubtleFillColorTertiary="#0A7B5CFF",
            ControlAltFillColorSecondary="#0A6A45F5", ControlAltFillColorTertiary="#126A45F5", ControlAltFillColorQuarternary="#1C6A45F5",
        ),
    ),
    "GlassDepth": dict(
        base="Light",
        accent=dict(SystemAccentColor="#8B5CF6", SystemAccentColorLight1="#A07CF8", SystemAccentColorLight2="#BCA4FB",
                    SystemAccentColorLight3="#DCCFFD", SystemAccentColorDark1="#7C3AED", SystemAccentColorDark2="#6527D2",
                    SystemAccentColorDark3="#4F1FA6"),
        colors=dict(
            ApplicationBackgroundColor="#E7E2F6", SolidBackgroundFillColorBase="#F4F1FC", SolidBackgroundFillColorSecondary="#ECE7FA",
            SolidBackgroundFillColorTertiary="#F9F7FE", SolidBackgroundFillColorQuarternary="#FFFFFF", SolidBackgroundFillColorBaseAlt="#D9D1F2",
            CardBackgroundFillColorDefault="#A6FFFFFF", CardBackgroundFillColorSecondary="#80FFFFFF", LayerFillColorDefault="#59FFFFFF",
            LayerFillColorAlt="#B3FFFFFF", AcrylicBackgroundFillColorDefault="#F2F7F5FF", LayerOnMicaBaseAltFillColorDefault="#A6FFFFFF",
            ControlFillColorDefault="#B3FFFFFF", ControlFillColorSecondary="#CCFFFFFF", ControlFillColorTertiary="#80FFFFFF",
            ControlFillColorInputActive="#F2FFFFFF", ControlSolidFillColorDefault="#FFFFFF",
            TextFillColorPrimary="#1A1530", TextFillColorSecondary="#4E4868", TextFillColorTertiary="#7A7394", TextFillColorDisabled="#9E98B5",
            ControlStrokeColorDefault="#66FFFFFF", ControlStrokeColorSecondary="#2E3A2D6B", CardStrokeColorDefault="#8CFFFFFF",
            CardStrokeColorDefaultSolid="#E4DDF7", DividerStrokeColorDefault="#1F3A2D6B", SurfaceStrokeColorFlyout="#99FFFFFF",
            ControlStrongStrokeColorDefault="#8A82AA", SubtleFillColorSecondary="#59FFFFFF", SubtleFillColorTertiary="#33FFFFFF",
            ControlAltFillColorSecondary="#40FFFFFF", ControlAltFillColorTertiary="#59FFFFFF", ControlAltFillColorQuarternary="#73FFFFFF",
        ),
    ),
}


def main():
    folder = sys.argv[1] if len(sys.argv) > 1 else None
    themes = {name: parse(load(name, folder)) for name in ("Light", "Dark")}
    for variant, spec in VARIANTS.items():
        colors, brushes = themes[spec["base"]]
        palette = {k: argb(v) for k, v in colors.items()}
        palette.update({k: argb(v) for k, v in spec["accent"].items()})
        palette.update({k: argb(v) for k, v in spec["colors"].items()})
        lines = [
            "<!-- Generated by tools/visual-preview/generate_palettes.py from the WPF Fluent "
            f"{spec['base']} theme. Do not edit by hand. -->",
            '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"',
            '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">',
        ]
        for key in sorted(palette):
            lines.append(f'  <Color x:Key="{key}">{palette[key]}</Color>')
        missing = []
        for key, ref, literal, opacity in brushes:
            color = palette.get(ref) if ref else argb(literal)
            if color is None:
                missing.append(f"{key}->{ref}")
                continue
            op = f' Opacity="{opacity}"' if opacity else ""
            lines.append(f'  <SolidColorBrush x:Key="{key}" Color="{color}"{op} />')
        lines.append("</ResourceDictionary>")
        target = os.path.join(OUT, variant)
        os.makedirs(target, exist_ok=True)
        with open(os.path.join(target, "Palette.xaml"), "w", encoding="utf-8", newline="\n") as f:
            f.write("\n".join(lines) + "\n")
        print(f"{variant}: {len(brushes) - len(missing)} brushes" + (f", skipped {missing}" if missing else ""))


if __name__ == "__main__":
    main()
