# -*- coding: utf-8 -*-
"""离线复算各套主题的 WCAG 对比度，和 UiCapture.RunThemeSelfCheck 用同一组组合。

为什么要单独有这个脚本：对比度不满意时要反复调色，
如果每次都改 axaml → 编译 → 跑 GUI 自检，一轮要几十秒；
用这个脚本按生成脚本里的色表直接算，秒级出结果，调好再生成。
"""
import re
import os

HERE = os.path.dirname(os.path.abspath(__file__))

PAIRS = [
    ("TextPrimaryBrush", "SurfaceBrush", "正文/面板"),
    ("TextPrimaryBrush", "AppBackgroundBrush", "正文/窗口底"),
    ("TextSecondaryBrush", "SurfaceBrush", "次要字/面板"),
    ("TextSecondaryBrush", "SurfaceAltBrush", "次要字/分组条"),
    ("TextSecondaryBrush", "ToolbarBrush", "次要字/工具条"),
    ("RowTextBrush", "SurfaceBrush", "行字/偶数行"),
    ("RowTextBrush", "GridRowAltBrush", "行字/奇数行"),
    ("RowTextBrush", "RowHoverBrush", "行字/悬停"),
    ("RowTextBrush", "RowSelectedBrush", "行字/选中"),
    ("PrimarySoftTextBrush", "PrimarySoftBrush", "主色浅底"),
    ("SelectionTextBrush", "SelectionBrush", "列表选中"),
    ("OnPrimaryBrush", "PrimaryBrush", "主色实心"),
    ("ValueOnBrush", "SurfaceBrush", "值-合"),
    ("ValueOffBrush", "SurfaceBrush", "值-分"),
    ("ValueMidBrush", "SurfaceBrush", "值-中间"),
    ("ValueBadBrush", "SurfaceBrush", "值-不确定"),
    ("ValueNeutralBrush", "SurfaceBrush", "值-模拟量"),
    ("QualityOkFgBrush", "QualityOkBgBrush", "品质-正常"),
    ("QualityWarnFgBrush", "QualityWarnBgBrush", "品质-警告"),
    ("QualityBadFgBrush", "QualityBadBgBrush", "品质-异常"),
    ("SuccessTextBrush", "SuccessSoftBrush", "正常态浅底"),
    ("WarningTextBrush", "WarningSoftBrush", "警告态浅底"),
    ("DangerTextBrush", "DangerSoftBrush", "异常态浅底"),
    ("DangerBrush", "SurfaceBrush", "删除红/面板"),
]

KEY_RE = re.compile(r'x:Key="(\w+)"\s+Color="(#\w{6})"')


def load(path):
    text = open(path, encoding="utf-8").read()
    return dict(KEY_RE.findall(text))


def channel(value):
    s = value / 255.0
    return s / 12.92 if s <= 0.03928 else ((s + 0.055) / 1.055) ** 2.4


def luminance(hexcolor):
    r = int(hexcolor[1:3], 16)
    g = int(hexcolor[3:5], 16)
    b = int(hexcolor[5:7], 16)
    return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b)


def ratio(fg, bg):
    a, b = luminance(fg), luminance(bg)
    hi, lo = max(a, b), min(a, b)
    return (hi + 0.05) / (lo + 0.05)


LIMIT = 4.5
total_bad = 0

for name in ["MintBlue", "IndustrialBlue", "WarmWhite", "FreshGreen", "GraphiteDark", "DeepSea"]:
    colors = load(os.path.join(HERE, name + ".axaml"))
    fails = []
    for fg, bg, label in PAIRS:
        value = ratio(colors[fg], colors[bg])
        if value < LIMIT:
            fails.append((value, label, fg, colors[fg], bg, colors[bg]))
    total_bad += len(fails)
    worst = min(ratio(colors[f], colors[b]) for f, b, _ in PAIRS)
    print(f"{name:16s} 不达标={len(fails)} 最差={worst:.2f}")
    for value, label, fg, fgc, bg, bgc in sorted(fails):
        print(f"    {value:5.2f}  {label:12s} {fg}={fgc} on {bg}={bgc}")

print("TOTAL", total_bad)
