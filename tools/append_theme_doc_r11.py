# -*- coding: utf-8 -*-
"""把第十一轮的色彩相关结论追加到《主题色彩规范》的落地附录。

新增三块内容：MenuIconBrush 令牌、关于窗口的主题渐变、exe 图标不跟主题走。
"""
import os

HERE = os.path.dirname(os.path.abspath(__file__))
PATH = os.path.join(HERE, "..", "主题色彩规范.md")

SECTION = """
### 线条图标的颜色：MenuIconBrush（第十一轮新增）

菜单图标原来是直接借用 `TextSecondaryBrush`。问题不在深浅，在**线宽**——
1.5 的描边（24 坐标系）缩进 16px 的框，实际只有 1 个物理像素左右，
半覆盖的灰色像素让整个图标"发虚"，用户反馈是"看着比较糊"。
线宽已改（见 `AppTheme.axaml` 的注释），颜色也单独开了一个令牌：

```
MenuIconBrush = mix(TextSecondaryBrush, PrimaryBrush, 0.30)
```

即"次要文字色往主色压 30%"。由 `gen_themes.py` 统一算，五套主题各一份：

| 主题 | TextSecondary | Primary | MenuIconBrush | 白底/面板底对比度 |
| --- | --- | --- | --- | --- |
| Mint Blue | `#5A6A80` | `#2775BC` | `#4B6D92` | ≈5.3:1 |
| Industrial Blue | `#56677D` | `#3170C8` | `#486795` | ≈5.5:1 |
| Warm White | `#5F646B` | `#4E7194` | `#586B82` | ≈5.2:1 |
| Graphite Dark | `#A7B2C3` | `#60A5FA` | `#92AED4` | ≈6.9:1（面板 `#1E2938`）|
| Deep Sea | `#A7C0D1` | `#65B8FF` | `#91BCE0` | ≈7.3:1（面板 `#142638`）|

为什么不是直接用主色：下拉菜单里图标和文字挨着，图标用主色会和"当前选中项"
（主色浅底 + 主色文字）抢注意力，一排下来全是蓝的也分不出层级。
压 30% 刚好"比灰蓝更有色相、比主色更安静"。

顶级菜单项（标题栏那一行）仍用 `PrimaryBrush`：它没有底色、旁边就是主题胶囊和状态灯，
跟主色走反而更像一套。悬停时下拉项图标也会切到 `PrimaryBrush`。

### 关于窗口的渐变背景：用令牌现场混，不用位图

原版用 `bk.png` 铺底。位图自带白底，切到暗色主题后整块发灰，和窗口其它区域不是一个调子。
现在改成在 `AboutWindow.axaml.cs` 里按主题算斜向线性渐变：

```
起点（左上）mix(SurfaceBrush, PrimaryBrush, 0.26)
0.46        mix(..., 0.10)
0.74        mix(..., 0.03)
终点（右下）mix(SurfaceBrush, PrimaryBrush, 0.17)
```

只用两个已有令牌，所以加主题不用为它单独配色。
第一版取的是 0.17→0.02，截图出来几乎一片白、肉眼看不出是渐变，调重了一档才成立。

### 不跟主题走的颜色：只有两处

| 位置 | 为什么不跟主题 | 处理 |
| --- | --- | --- |
| 主题选择器里的代表色圆点 | 三套浅色主题主色都是蓝，五个点摆一起分不出来 | `ThemeService.Options[].Swatch` 单独记 |
| 关闭按钮悬停的红底白字 | Windows 惯例；暗色主题的 `OnPrimaryBrush` 是深色，压在红底上看不见 | `Button.caption.close:pointerover` 写死 `#E81123` + `#FFFFFF` |

除此之外，**exe 图标与窗口图标是图片资源**，本来就不参与主题切换；
它们用的是同一个 `Assets/app.ico`（多尺寸 ico，16/24/32/48/64/128/256），
由 `tools/gen_app_icons.py` 生成，配色固定为蓝底 + 琥珀闪电，不随界面主题变。
"""


def main():
    text = open(PATH, encoding="utf-8").read()
    if "MenuIconBrush（第十一轮新增）" in text:
        print("already exists, skip")
        return
    text = text.rstrip("\n") + "\n" + SECTION
    open(PATH, "w", encoding="utf-8", newline="\n").write(text)
    print("主题色彩规范.md updated, lines:", text.count("\n") + 1)


if __name__ == "__main__":
    main()
