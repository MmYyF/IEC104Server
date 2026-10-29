# -*- coding: utf-8 -*-
"""一次性补丁：把打包说明里的路径/断言数/图标说明更新到第十一轮。

用脚本改而不是手改：这些路径在文中出现 4 次，漏一处就会导致文档和产物对不上。
"""
import os

HERE = os.path.dirname(os.path.abspath(__file__))
PATH = os.path.join(HERE, "..", "docs", "打包说明.md")

s = open(PATH, encoding="utf-8").read()

pairs = [
    # 顶部结论表
    ("> | 界面版（日常使用） | `D:\\01_File\\03_rea\\tools\\IEC104Simulator-GUI\\IEC104Simulator.exe` | 45 MB |",
     "> | 界面版（日常使用） | `D:\\01_File\\03_rea\\tools\\IEC104-Server\\IEC104-Server.exe` | 45 MB |"),
    # 手工命令的输出目录
    ("  -o D:/01_File/03_rea/tools/IEC104Simulator-GUI",
     "  -o D:/01_File/03_rea/tools/IEC104-Server"),
    # 产物表
    ("| 界面版 | `tools\\IEC104Simulator-GUI\\IEC104Simulator.exe` | 45 MB | 无，双击即用 |",
     "| 界面版 | `tools\\IEC104-Server\\IEC104-Server.exe` | 45 MB | 无，双击即用 |"),
    # 自检命令
    ("IEC104Simulator.exe --capture-ui D:/temp/shots",
     "IEC104-Server.exe --capture-ui D:/temp/shots"),
    # 断言条数
    ("自检除了截图（20 张），还会真跑一遍关键链路并做**45 条断言**",
     "自检除了截图（20 张），还会真跑一遍关键链路并做**52 条断言**"),
]

for old, new in pairs:
    if old not in s:
        raise SystemExit("NOT FOUND: " + old[:60])
    s = s.replace(old, new, 1)

# 断言样例里补上第十一轮新增的 7 条
old = "titlebar-buttons:   GitHub=True@x1040 更新=True@x1074.4                       => PASS"
new = """titlebar-buttons:   GitHub=True@x1040 更新=True@x1074.4                       => PASS
titlebar-center:    栏高=40 中线=20 偏差 logo=+0.4 菜单=0 主题胶囊=0 ...       => PASS
win-icons:          15/15 个窗口带图标                                        => PASS
win-icons-code:     代码提示框图标=有                                         => PASS
menu-icon-weight:   顶级图标描边=2.15 框=16 颜色=主题主色True                 => PASS
menu-icon-color:    下拉项描边=2 颜色=MenuIconBrushTrue                       => PASS
about-window:       渐变背景=True 位图背景已移除=True 文案字数=258            => PASS
app-name:           窗口标题=IEC104-Server 状态栏=IEC104-Server ...           => PASS"""
if old not in s:
    raise SystemExit("NOT FOUND: titlebar-buttons")
s = s.replace(old, new, 1)

# 部署注意补三条
old = "- 绿色分发：整个目录只有一个 exe，拷走即可；卸载就是删掉它"
new = """- 绿色分发：整个目录只有一个 exe，拷走即可；卸载就是删掉它
- exe 名字、属性里的"产品名称"、任务栏名字、界面标题统一是 `IEC104-Server`；
  程序集名仍是 `IEC104Simulator`（`avares://` 资源路径依赖它），
  所以 `build.bat gui` 打完包会把产物改名成 `IEC104-Server.exe`
- exe 自带图标（csproj 的 `<ApplicationIcon>`），资源管理器里不再是 .NET 默认图标
- 旧的 `tools\\IEC104Simulator-GUI` 目录是上一版产物，可以整个删掉"""
if old not in s:
    raise SystemExit("NOT FOUND: 绿色分发")
s = s.replace(old, new, 1)

open(PATH, "w", encoding="utf-8", newline="\n").write(s)
print("打包说明.md updated")
