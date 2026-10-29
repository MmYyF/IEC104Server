# -*- coding: utf-8 -*-
"""生成全部主题的资源字典（一次性脚本，保留在仓库里便于后续加主题）。

为什么用脚本生成而不是手写多份：
每套主题必须定义**完全相同的令牌集合**，漏一个键在运行时就是"某处颜色变成默认值"，
肉眼很难发现。用同一张表生成，键集合天然一致；新增主题只要在 THEMES 里加一项。

生成的字典被 ThemeService 在运行时替换（见 Services/ThemeService.cs），
所以样式里必须用 DynamicResource 引用这些键，用 StaticResource 换主题不会生效。
"""
import os

HERE = os.path.dirname(os.path.abspath(__file__))

# 令牌 → 说明。顺序即输出顺序，注释会写进生成的 XAML 里当文档。
TOKENS = [
    ("AppBackgroundBrush", "窗口底色（最底层）"),
    ("SurfaceBrush", "面板 / 卡片 / 表格行"),
    ("SurfaceAltBrush", "次级面（表头、分组标题条）"),
    ("ToolbarBrush", "菜单栏 / 工具条 / 状态栏"),
    ("BorderLineBrush", "分隔线、面板描边"),
    ("DividerBrush", "更淡的分隔线"),
    ("TextPrimaryBrush", "正文"),
    ("TextSecondaryBrush", "次要说明 / 表头 / 计数"),
    ("TextDisabledBrush", "禁用"),
    ("MenuIconBrush", "菜单线条图标（次要色压向主色一档，抗锯齿后仍清楚）"),
    ("PrimaryBrush", "主色（强调、启动、选中边）"),
    ("PrimaryHoverBrush", "主色悬停"),
    ("PrimaryPressedBrush", "主色按下"),
    ("PrimarySoftBrush", "主色浅底（选中块、悬停块、徽标底）"),
    ("PrimarySoftTextBrush", "浅底上的文字（必须与 PrimarySoft 对比足够）"),
    ("OnPrimaryBrush", "主色实心上的文字/图标"),
    ("HoverBrush", "通用悬停底"),
    ("SelectionBrush", "列表选中底"),
    ("SelectionTextBrush", "列表选中文字"),
    ("RowHoverBrush", "表格行悬停"),
    ("RowSelectedBrush", "表格行选中"),
    ("RowTextBrush", "表格行文字（悬停/选中后必须仍可读）"),
    ("GridRowAltBrush", "表格隔行底色"),
    ("SuccessBrush", "正常 / 已连接"),
    ("SuccessSoftBrush", "正常态浅底"),
    ("SuccessTextBrush", "正常态浅底上的文字"),
    ("WarningBrush", "警告 / 监听中"),
    ("WarningSoftBrush", "警告态浅底"),
    ("WarningTextBrush", "警告态浅底上的文字"),
    ("DangerBrush", "错误 / 停止 / 删除"),
    ("DangerSoftBrush", "错误态浅底"),
    ("DangerTextBrush", "错误态浅底上的文字"),
    ("ValueOnBrush", "值：合 / 有电"),
    ("ValueOffBrush", "值：分 / 无电"),
    ("ValueMidBrush", "值：中间态"),
    ("ValueBadBrush", "值：不确定"),
    ("ValueNeutralBrush", "值：模拟量中性色"),
    ("QualityOkBgBrush", "品质徽标：正常底"),
    ("QualityOkFgBrush", "品质徽标：正常字"),
    ("QualityWarnBgBrush", "品质徽标：警告底"),
    ("QualityWarnFgBrush", "品质徽标：警告字"),
    ("QualityBadBgBrush", "品质徽标：异常底"),
    ("QualityBadFgBrush", "品质徽标：异常字"),
]

LIGHT_STATUS = {
    # 注意：这里的每个"字色"都必须与它配套的"底色"≥4.5:1（WCAG AA 正文字号）。
    # ValueMidBrush 原来是 #B77912（3.65:1，白底上看数字发虚），
    # DangerBrush 原来是 #D64545（4.38:1，删除图标还行、配文字就不够），
    # 两个都压深到 5:1 附近。校验脚本见 check_contrast.py。
    "SuccessBrush": "#16A36A", "SuccessSoftBrush": "#E2F7EC", "SuccessTextBrush": "#0B6B47",
    "WarningBrush": "#B77912", "WarningSoftBrush": "#FFF2D6", "WarningTextBrush": "#7A4E00",
    "DangerBrush": "#CC3A3A", "DangerSoftBrush": "#FCE8E8", "DangerTextBrush": "#A32020",
    "ValueOnBrush": "#0F7A52", "ValueOffBrush": "#64748B", "ValueMidBrush": "#94620E",
    "ValueBadBrush": "#C0392B", "ValueNeutralBrush": "#263746",
    "QualityOkBgBrush": "#E2F7EC", "QualityOkFgBrush": "#0B6B47",
    "QualityWarnBgBrush": "#FFF2D6", "QualityWarnFgBrush": "#7A4E00",
    "QualityBadBgBrush": "#FCE8E8", "QualityBadFgBrush": "#A32020",
}

DARK_STATUS = {
    "SuccessBrush": "#4ECB8F", "SuccessSoftBrush": "#17352A", "SuccessTextBrush": "#8DE9BF",
    "WarningBrush": "#E0A642", "WarningSoftBrush": "#38301B", "WarningTextBrush": "#F3CE84",
    "DangerBrush": "#F07A7A", "DangerSoftBrush": "#3A1F22", "DangerTextBrush": "#FFAFAF",
    "ValueOnBrush": "#5FD3A0", "ValueOffBrush": "#A7B2C3", "ValueMidBrush": "#E9B15E",
    "ValueBadBrush": "#F2857F", "ValueNeutralBrush": "#E7EDF5",
    "QualityOkBgBrush": "#17352A", "QualityOkFgBrush": "#8DE9BF",
    "QualityWarnBgBrush": "#38301B", "QualityWarnFgBrush": "#F3CE84",
    "QualityBadBgBrush": "#3A1F22", "QualityBadFgBrush": "#FFAFAF",
}

# 清新绿（主题 F）的状态色单独一份：它的"正常"要跟主色同一个绿色家族，
# 用 LIGHT_STATUS 那套偏青的 #16A36A 会显得是"另一个绿"。
GREEN_STATUS = {
    "SuccessBrush": "#218449", "SuccessSoftBrush": "#E3F5E9", "SuccessTextBrush": "#176E3D",
    "WarningBrush": "#B77912", "WarningSoftBrush": "#FFF2D6", "WarningTextBrush": "#7A4E00",
    "DangerBrush": "#CC3A3A", "DangerSoftBrush": "#FCE8E8", "DangerTextBrush": "#A32020",
    "ValueOnBrush": "#218449", "ValueOffBrush": "#64748B", "ValueMidBrush": "#94620E",
    "ValueBadBrush": "#C0392B", "ValueNeutralBrush": "#25352D",
    "QualityOkBgBrush": "#E3F5E9", "QualityOkFgBrush": "#176E3D",
    "QualityWarnBgBrush": "#FFF2D6", "QualityWarnFgBrush": "#7A4E00",
    "QualityBadBgBrush": "#FCE8E8", "QualityBadFgBrush": "#A32020",
}

THEMES = [
    {
        "file": "MintBlue",
        "name": "Mint Blue",
        "dark": False,
        "comment": "默认明亮主题：白底 + 薄荷/水蓝点缀，最贴近原版工具的中性观感。",
        "colors": {
            "AppBackgroundBrush": "#F5F8FA", "SurfaceBrush": "#FFFFFF", "SurfaceAltBrush": "#F0F5F8",
            "ToolbarBrush": "#FFFFFF", "BorderLineBrush": "#E4EAF0", "DividerBrush": "#E9EEF3",
            "TextPrimaryBrush": "#263746", "TextSecondaryBrush": "#5A6A80", "TextDisabledBrush": "#9AA6B2",
            # 主色同时是"实心按钮底色"，白字压在上面必须 ≥4.5:1，
            # 原来的 #4B9DE8 只有 2.88:1（浅蓝配白字，界面上确实看不清）
            "PrimaryBrush": "#2775BC", "PrimaryHoverBrush": "#1F67A8", "PrimaryPressedBrush": "#195587",
            "PrimarySoftBrush": "#DDEEFF", "PrimarySoftTextBrush": "#1F5F9E", "OnPrimaryBrush": "#FFFFFF",
            "HoverBrush": "#F0F7FC", "SelectionBrush": "#E5F5F2", "SelectionTextBrush": "#166B5A",
            "RowHoverBrush": "#F2F8FD", "RowSelectedBrush": "#E3F1FB", "RowTextBrush": "#263746",
            "GridRowAltBrush": "#F7FAFC",
            **LIGHT_STATUS,
        },
    },
    {
        "file": "IndustrialBlue",
        "name": "Industrial Blue",
        "dark": False,
        "comment": "专业工业蓝：更冷的蓝灰，适合控制室强光环境。",
        "colors": {
            "AppBackgroundBrush": "#F3F6FA", "SurfaceBrush": "#FFFFFF", "SurfaceAltBrush": "#EDF2F8",
            "ToolbarBrush": "#FFFFFF", "BorderLineBrush": "#DCE4EE", "DividerBrush": "#E6EBF2",
            "TextPrimaryBrush": "#243247", "TextSecondaryBrush": "#56677D", "TextDisabledBrush": "#9AA6B2",
            "PrimaryBrush": "#3170C8", "PrimaryHoverBrush": "#2860B0", "PrimaryPressedBrush": "#204F93",
            "PrimarySoftBrush": "#DCE8F8", "PrimarySoftTextBrush": "#1E4E8C", "OnPrimaryBrush": "#FFFFFF",
            "HoverBrush": "#EEF3FA", "SelectionBrush": "#E4EDFC", "SelectionTextBrush": "#1E4E8C",
            "RowHoverBrush": "#F2F6FC", "RowSelectedBrush": "#E4EDFC", "RowTextBrush": "#243247",
            "GridRowAltBrush": "#F6F9FD",
            **LIGHT_STATUS,
        },
    },
    {
        "file": "WarmWhite",
        "name": "Warm White",
        "dark": False,
        "comment": "暖白工程风：米白底 + 灰蓝主色，长时间盯屏不刺眼。",
        "colors": {
            "AppBackgroundBrush": "#F7F5F0", "SurfaceBrush": "#FFFFFF", "SurfaceAltBrush": "#F1EEE7",
            "ToolbarBrush": "#FFFEFC", "BorderLineBrush": "#E3DED5", "DividerBrush": "#ECE8E0",
            "TextPrimaryBrush": "#353B43", "TextSecondaryBrush": "#5F646B", "TextDisabledBrush": "#A8A9AC",
            "PrimaryBrush": "#4E7194", "PrimaryHoverBrush": "#406283", "PrimaryPressedBrush": "#345470",
            "PrimarySoftBrush": "#E6EDF5", "PrimarySoftTextBrush": "#35526E", "OnPrimaryBrush": "#FFFFFF",
            "HoverBrush": "#F1F4F8", "SelectionBrush": "#E8EFF5", "SelectionTextBrush": "#35526E",
            "RowHoverBrush": "#F6F3EC", "RowSelectedBrush": "#E8EFF5", "RowTextBrush": "#353B43",
            "GridRowAltBrush": "#FAF8F3",
            **LIGHT_STATUS,
        },
    },
    {
        "file": "FreshGreen",
        "name": "Fresh Green",
        "dark": False,
        # 配色来自用户给的 主题F.md（Pure White Fresh Green）。
        # 规格里有三处按本项目的"≥4.5:1"硬指标做了调整，全部记录在下面：
        #   · primary #239653 压白字只有 3.78:1 → 令牌取规格里的 hover 值 #20864A（4.59:1），
        #     hover/pressed 顺延下压；色相没动，观感上仍是同一支绿。
        #   · text-secondary #748278 只有 4.03:1（白底）/3.82:1（浅绿底）→ 压深到 #66766C（4.80/4.55）。
        #   · error #D64545 只有 4.38:1 → 用 #CC3A3A（4.96:1），与其他浅色主题同值。
        # 另外在绿色主题里"正常"和"主色"天然同族，状态判读因此不靠色相，
        # 靠文字与位置（这条本来就写进了 主题色彩规范.md 第 5 节）。
        "comment": "纯白清新绿：白底 + 工业绿点缀，几乎没有大面积着色，长时间盯屏最省眼。",
        "colors": {
            "AppBackgroundBrush": "#FFFFFF", "SurfaceBrush": "#FFFFFF", "SurfaceAltBrush": "#F5FAF6",
            "ToolbarBrush": "#FFFFFF", "BorderLineBrush": "#E1E9E3", "DividerBrush": "#E8EEE9",
            "TextPrimaryBrush": "#25352D", "TextSecondaryBrush": "#66766C", "TextDisabledBrush": "#9BA69E",
            "PrimaryBrush": "#20864A", "PrimaryHoverBrush": "#1B7A44", "PrimaryPressedBrush": "#17683A",
            "PrimarySoftBrush": "#E0F3E6", "PrimarySoftTextBrush": "#186B3B", "OnPrimaryBrush": "#FFFFFF",
            "HoverBrush": "#EDF8F0", "SelectionBrush": "#E0F3E6", "SelectionTextBrush": "#186B3B",
            "RowHoverBrush": "#EDF8F0", "RowSelectedBrush": "#E0F3E6", "RowTextBrush": "#25352D",
            "GridRowAltBrush": "#F7FBF8",
            **GREEN_STATUS,
        },
    },
    {
        "file": "GraphiteDark",
        "name": "Graphite Dark",
        "dark": True,
        "comment": "石墨暗黑：深蓝灰底 + 亮蓝主色，夜班值守首选。",
        "colors": {
            "AppBackgroundBrush": "#171F2B", "SurfaceBrush": "#1E2938", "SurfaceAltBrush": "#263548",
            "ToolbarBrush": "#1A2432", "BorderLineBrush": "#344256", "DividerBrush": "#2A3748",
            "TextPrimaryBrush": "#E7EDF5", "TextSecondaryBrush": "#A7B2C3", "TextDisabledBrush": "#6F7D90",
            "PrimaryBrush": "#60A5FA", "PrimaryHoverBrush": "#83B7FF", "PrimaryPressedBrush": "#3B82D6",
            "PrimarySoftBrush": "#24374D", "PrimarySoftTextBrush": "#9CC8FF", "OnPrimaryBrush": "#0B1220",
            "HoverBrush": "#263548", "SelectionBrush": "#294153", "SelectionTextBrush": "#CFE6FF",
            "RowHoverBrush": "#223248", "RowSelectedBrush": "#2A4257", "RowTextBrush": "#E7EDF5",
            "GridRowAltBrush": "#1C2735",
            **DARK_STATUS,
        },
    },
    {
        "file": "DeepSea",
        "name": "Deep Sea",
        "dark": True,
        "comment": "深海蓝绿：比石墨更冷更沉，蓝绿点缀引导视线。",
        "colors": {
            "AppBackgroundBrush": "#101B2A", "SurfaceBrush": "#142638", "SurfaceAltBrush": "#1B3549",
            "ToolbarBrush": "#122132", "BorderLineBrush": "#2C4659", "DividerBrush": "#20394C",
            "TextPrimaryBrush": "#E3F1FA", "TextSecondaryBrush": "#A7C0D1", "TextDisabledBrush": "#6F8B9E",
            "PrimaryBrush": "#65B8FF", "PrimaryHoverBrush": "#8ACBFF", "PrimaryPressedBrush": "#4599E0",
            "PrimarySoftBrush": "#1E3A4F", "PrimarySoftTextBrush": "#A6D6FF", "OnPrimaryBrush": "#0A1622",
            "HoverBrush": "#1B3549", "SelectionBrush": "#214456", "SelectionTextBrush": "#D3EBFF",
            "RowHoverBrush": "#17303F", "RowSelectedBrush": "#1F4152", "RowTextBrush": "#E3F1FA",
            "GridRowAltBrush": "#15293B",
            **DARK_STATUS,
        },
    },
]

HEADER = """<ResourceDictionary xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

  <!--
    {name} 主题{fav}
    {comment}

    本文件由 gen_themes.py 生成，各套主题的键集合必须完全一致（当前 6 套）。
    新增主题：改脚本里的 THEMES 再重跑，别手改这里。
  -->
"""

def mix(hex_a, hex_b, ratio):
    """把 b 按 ratio 混进 a，返回 #RRGGBB。

    菜单图标颜色不是随手挑的：直接拿 TextSecondary 画 1~2px 细线，
    在浅底上偏灰、缩放后发虚；直接用 Primary 又抢文字。
    统一取"次要色往主色压 30%"，既跟着主题走，又比纯灰更抓得住线条。
    """
    a = [int(hex_a[i:i + 2], 16) for i in (1, 3, 5)]
    b = [int(hex_b[i:i + 2], 16) for i in (1, 3, 5)]
    return "#" + "".join(f"{round(x + (y - x) * ratio):02X}" for x, y in zip(a, b))


for theme in THEMES:
    colors = theme["colors"]
    colors["MenuIconBrush"] = mix(colors["TextSecondaryBrush"], colors["PrimaryBrush"], 0.30)
    missing = [key for key, _ in TOKENS if key not in colors]
    if missing:
        raise SystemExit(f"{theme['file']} 缺令牌: {missing}")

    lines = [HEADER.format(
        name=theme["name"],
        fav="（暗色）" if theme["dark"] else "",
        comment=theme["comment"])]

    for key, desc in TOKENS:
        lines.append(f'  <SolidColorBrush x:Key="{key}" Color="{colors[key]}" />   <!-- {desc} -->\n')

    lines.append("</ResourceDictionary>\n")

    path = os.path.join(HERE, theme["file"] + ".axaml")
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        handle.writelines(lines)
    print("wrote", path, len(colors), "tokens")
