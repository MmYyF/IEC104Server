# -*- coding: utf-8 -*-
"""生成 IEC104-Server 的软件图标候选。

为什么要脚本生成而不是找现成图标：
  1. 这个工具是"电力远动从站仿真器"，图标要一眼能看出"机架/从站 + 电力"，
     通用图标库里没有贴合的；
  2. 图标必须在 16×16（窗口标题栏、任务栏小图标）下还认得出，
     这要靠反复改比例后按 16px 渲染出来看，手工在画图软件里改太慢；
  3. 5 个候选要共用同一套几何参数（圆角、机架条厚度、灯位），
     只有"配色 + 右侧符号"不同，选起来才有可比性。

输出：
  doc-agent/icons/candidate-X-256.png   候选原图（256）
  doc-agent/icons/candidate-X-64.png    中间尺寸预览
  doc-agent/icons/candidate-X-16.png    小尺寸预览（关键：能不能认出来）
  doc-agent/icons/candidate-X.ico       多尺寸 ico（16/24/32/48/64/128/256）
  doc-agent/icons/候选对照.png          一排候选 + 名称，供选择

  输出目录不进仓库（选型过程产物，程序真正用的图标在
  src/IEC104.App/Assets/app.ico），克隆下来自己跑一次就有了。

用法： python tools/gen_app_icons.py
"""
import os

from PIL import Image, ImageChops, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
CODE = os.path.dirname(HERE)
OUT = os.path.join(CODE, "doc-agent", "icons")

# 先在 1024 画，再降采样到各尺寸。原因：
#   Pillow 的圆角、圆、斜线都没有抗锯齿参数，只有超采样后 LANCZOS 缩下来才干净。
S = 1024
RADIUS = 232          # 圆角占边长的 22.6%，Windows 11 图标观感
BAR_X0, BAR_X1 = 196, 632
BAR_H = 104
BAR_R = 30
BAR_YS = (268, 460, 652)


def diag_gradient(size, top, bottom):
    """沿左上→右下做线性渐变。

    做法：一张竖向灰度 + 一张横向灰度相加取平均，就得到 (x+y) 方向的线性坡度。
    一开始是拿竖向灰度直接 rotate(-45°) 当蒙版，结果旋转不带 expand 会把四个角裁掉，
    图标左上/右下出现一条硬边斜切线（对照图上一眼能看出来），所以改成加法合成。
    """
    vertical = Image.linear_gradient("L")                       # 0 在上，255 在下
    horizontal = vertical.transpose(Image.ROTATE_90)            # 0 在左，255 在右
    ramp = ImageChops.add(vertical, horizontal, scale=2.0)       # (a+b)/2，斜向坡度
    mask = ramp.resize((size, size), Image.BICUBIC)
    base = Image.new("RGB", (size, size), top)
    over = Image.new("RGB", (size, size), bottom)
    return Image.composite(over, base, mask)


def rounded_mask(size, radius):
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, size - 1, size - 1), radius=radius, fill=255)
    return mask


def load_bold_font(px, cjk=False):
    # 中文标签必须用带 CJK 字形的字体，Segoe UI 没有汉字，会渲染成方块
    names = ("msyhbd.ttc", "msyh.ttc", "simhei.ttf") if cjk else ("segoeuib.ttf", "arialbd.ttf", "msyhbd.ttc")
    for name in names:
        path = os.path.join(os.environ.get("WINDIR", r"C:\Windows"), "Fonts", name)
        if os.path.exists(path):
            try:
                return ImageFont.truetype(path, px)
            except OSError:
                continue
    return ImageFont.load_default()


def draw_rack(layer, accent, led_color, vent_alpha=88):
    """三层机架 + 每层一颗状态灯 + 右侧散热格栅。

    这三样是"从站"最稳的视觉隐喻：叠起来的机箱就是装置，
    灯是链路状态，格栅用来把细长条从"汉堡"里救出来。
    """
    draw = ImageDraw.Draw(layer)
    for cy_top in BAR_YS:
        draw.rounded_rectangle(
            (BAR_X0, cy_top, BAR_X1, cy_top + BAR_H), radius=BAR_R, fill=(255, 255, 255, 235))

    # 状态灯画在机架左侧（实心小圆），比白底上的描边更抗缩放
    for cy_top in BAR_YS:
        cy = cy_top + BAR_H // 2
        draw.ellipse((BAR_X0 + 42, cy - 23, BAR_X0 + 88, cy + 23), fill=led_color)
    return accent


def draw_vents(layer, alpha=70):
    draw = ImageDraw.Draw(layer)
    for cy_top in BAR_YS:
        cy = cy_top + BAR_H // 2
        for i in range(3):
            x0 = 400 + i * 62
            draw.rounded_rectangle((x0, cy - 6, x0 + 40, cy + 6), radius=6,
                                   fill=(255, 255, 255, alpha))


def draw_bolt(layer, color):
    """闪电：电力行业的通用符号，压在机架右侧。"""
    pts = [(756, 258), (664, 540), (742, 540), (712, 766), (846, 452), (762, 452)]
    ImageDraw.Draw(layer).polygon(pts, fill=color)


def draw_wave(layer, rgb):
    """通信波形：以机架右侧为圆心向外扩散的三条弧，示意 104 报文交互。"""
    draw = ImageDraw.Draw(layer)
    cx = 636
    for radius, alpha, thick in ((150, 235, 26), (216, 180, 24), (282, 130, 22)):
        draw.arc((cx - radius, 512 - radius, cx + radius, 512 + radius),
                 start=-52, end=52, fill=rgb + (alpha,), width=thick)


def draw_arrows(layer, rgb):
    """一对反向箭头：主站下发 ↔ 从站上送，是"问答关系"最直白的画法。"""
    draw = ImageDraw.Draw(layer)
    # 上箭头向右
    draw.rounded_rectangle((664, 428, 800, 458), radius=15, fill=rgb)
    draw.polygon([(800, 398), (866, 443), (800, 488)], fill=rgb)
    # 下箭头向左
    draw.rounded_rectangle((724, 566, 860, 596), radius=15, fill=rgb)
    draw.polygon([(724, 536), (658, 581), (724, 626)], fill=rgb)


def draw_text104(layer, color):
    font = load_bold_font(300)
    draw = ImageDraw.Draw(layer)
    box = draw.textbbox((0, 0), "104", font=font)
    x = (S - (box[2] - box[0])) // 2 - box[0]
    y = 512 - (box[3] - box[1]) // 2 - box[1]
    draw.text((x, y), "104", font=font, fill=color)


def build(kind):
    """按候选编号返回一张 RGBA 图标。"""
    if kind == "A":
        bg = diag_gradient(S, (74, 150, 214), (22, 70, 116))
        accent = (255, 194, 75, 255)
        led = accent
    elif kind == "B":
        bg = diag_gradient(S, (34, 186, 158), (9, 92, 82))
        accent = (255, 255, 255, 255)
        led = (255, 214, 120, 255)
    elif kind == "C":
        bg = diag_gradient(S, (52, 74, 96), (16, 26, 36))
        accent = (255, 255, 255, 255)
        led = (86, 214, 160, 255)
    elif kind == "D":
        bg = diag_gradient(S, (104, 120, 226), (40, 48, 132))
        accent = (255, 255, 255, 255)
        led = (120, 226, 255, 255)
    else:
        bg = diag_gradient(S, (44, 118, 178), (30, 96, 150))
        accent = (255, 255, 255, 255)
        led = (126, 226, 160, 255)

    canvas = bg.convert("RGBA")
    glyph = Image.new("RGBA", (S, S), (0, 0, 0, 0))

    if kind == "C":
        # 只放"104" + 三颗小灯，走极简路线
        draw_text104(glyph, (255, 255, 255, 255))
        for i in range(3):
            cx = 372 + i * 140
            ImageDraw.Draw(glyph).ellipse((cx - 16, 772, cx + 16, 804), fill=accent)
    else:
        draw_rack(glyph, accent, led)
        draw_vents(glyph)
        if kind == "A":
            draw_bolt(glyph, accent)
        elif kind == "B":
            draw_wave(glyph, (255, 255, 255))
        elif kind == "D":
            draw_arrows(glyph, accent)
        # E 保持纯机架，不加符号（最小尺寸下最干净）

    canvas = Image.alpha_composite(canvas, glyph)

    # 顶部落一层很淡的白色竖向渐变当高光（加在整幅图上，不是只加在图形上）。
    # 第一版用椭圆做的高光，椭圆边缘在大图标上是一条可见的斜切线，像贴纸；
    # 改成整幅竖向渐变（上 12% 白 → 下 0），看不出分界。
    ramp = Image.linear_gradient("L").resize((S, S), Image.BICUBIC)
    alpha = Image.eval(ramp, lambda v: int((255 - v) * 0.12))
    gloss = Image.new("RGBA", (S, S), (255, 255, 255, 255))
    gloss.putalpha(alpha)
    canvas = Image.alpha_composite(canvas, gloss)

    canvas.putalpha(rounded_mask(S, RADIUS))
    return canvas


CANDIDATES = [
    ("A", "A 蓝 / 机架+闪电"),
    ("B", "B 青绿 / 机架+波形"),
    ("C", "C 石墨 / 104"),
    ("D", "D 靛蓝 / 机架+双向箭头"),
    ("E", "E 蓝 / 纯机架"),
]


def main():
    os.makedirs(OUT, exist_ok=True)
    thumbs = []
    for kind, label in CANDIDATES:
        full = build(kind)
        base = os.path.join(OUT, f"candidate-{kind}")
        full.resize((256, 256), Image.LANCZOS).save(base + "-256.png")
        full.resize((64, 64), Image.LANCZOS).save(base + "-64.png")
        full.resize((16, 16), Image.LANCZOS).save(base + "-16.png")
        full.resize((256, 256), Image.LANCZOS).save(
            base + ".ico",
            sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
        thumbs.append((label, full))
        print(f"OK {base}.ico")

    # 对照图：一排 256 原图 + 下面 64/32/16 实际大小，方便判断"小尺寸还认不认得出"
    pad, cell = 40, 256
    width = pad * (len(thumbs) + 1) + cell * len(thumbs)
    height = 40 + cell + 150
    sheet = Image.new("RGB", (width, height), (246, 248, 251))
    draw = ImageDraw.Draw(sheet)
    label_font = load_bold_font(26, cjk=True)
    for i, (label, img) in enumerate(thumbs):
        x = pad + i * (cell + pad)
        sheet.paste(img.resize((cell, cell), Image.LANCZOS), (x, 40), img.resize((cell, cell), Image.LANCZOS))
        draw.text((x, 40 + cell + 30), label, font=label_font, fill=(40, 52, 68))
        small = img.resize((48, 48), Image.LANCZOS)
        sheet.paste(small, (x, 40 + cell + 82), small)
        tiny = img.resize((16, 16), Image.LANCZOS)
        sheet.paste(tiny.resize((32, 32), Image.NEAREST), (x + 70, 40 + cell + 90), tiny.resize((32, 32), Image.NEAREST))
    sheet.save(os.path.join(OUT, "候选对照.png"))
    print("OK 候选对照.png")


if __name__ == "__main__":
    main()
