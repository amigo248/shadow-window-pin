"""生成 Shadow 的应用图标（多尺寸 .ico）。

为什么要用脚本生成而不是塞一个二进制进来：
 1. 图标要同时存在于 16/24/32/48/64/128/256 七个尺寸，手绘很难保证小尺寸可读；
 2. 换了主色调能一键重出，评审时也能看懂图标是怎么来的。

设计意图：三张叠在一起的窗口卡片，最前面一张是青色（= 最上层的那一层），
卡片上用三根短横线暗示「层级列表」。16px 下细节会糊，所以只保留「叠三层 + 最上面是青色」这个形状。

用法：
    python tools/make-icon.py
输出：
    src/Shadow/Resources/shadow.ico
"""

from __future__ import annotations

import os

from PIL import Image, ImageDraw

# 与界面配色一致：深底色 + 青色强调（层号徽标与拾取框用的就是这支青）
BACKGROUND = (16, 22, 28, 255)
BORDER = (44, 58, 70, 255)
CARD_BACK = (58, 72, 84, 255)
CARD_MIDDLE = (107, 124, 138, 255)
CARD_FRONT = (56, 189, 248, 255)
CARD_FRONT_LINE = (240, 249, 255, 255)

SUPERSAMPLE = 4
SIZES = (256, 128, 64, 48, 32, 24, 16)


def make_image(size: int) -> Image.Image:
    """在 4 倍画布上作图再缩小，得到边缘干净的图标。"""
    canvas = SUPERSAMPLE * size
    image = Image.new("RGBA", (canvas, canvas), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    # 底板：圆角方块。半径取边长的 22%，与 Win11 的图标圆角观感接近。
    radius = canvas * 0.22
    draw.rounded_rectangle((0, 0, canvas - 1, canvas - 1), radius=radius,
                           fill=BACKGROUND, outline=BORDER, width=max(1, int(canvas * 0.012)))

    def card(x: float, y: float, w: float, h: float, fill: tuple[int, int, int, int]) -> None:
        draw.rounded_rectangle(
            (canvas * x, canvas * y, canvas * (x + w), canvas * (y + h)),
            radius=canvas * 0.055, fill=fill,
        )

    # 三张卡片斜着错开，越靠前的越靠左上（= 层级越高）
    card(0.20, 0.32, 0.60, 0.44, CARD_BACK)
    card(0.26, 0.25, 0.60, 0.44, CARD_MIDDLE)
    card(0.32, 0.18, 0.60, 0.44, CARD_FRONT)

    # 最上层卡片上的三根短横线：暗示「层级列表」。16px 时它们会自然糊成一条灰带，
    # 形状仍然读得出来，所以不额外做小尺寸分支。
    line_h = canvas * 0.030
    for index in range(3):
        top = canvas * (0.285 + index * 0.085)
        left = canvas * 0.395
        right = canvas * (0.395 + (0.30 if index == 0 else 0.22))
        draw.rounded_rectangle((left, top, right, top + line_h),
                               radius=line_h / 2, fill=CARD_FRONT_LINE)

    return image.resize((size, size), Image.LANCZOS)


def main() -> int:
    here = os.path.dirname(os.path.abspath(__file__))
    out_dir = os.path.join(here, "..", "src", "Shadow", "Resources")
    out_dir = os.path.normpath(out_dir)
    os.makedirs(out_dir, exist_ok=True)
    target = os.path.join(out_dir, "shadow.ico")

    largest = make_image(max(SIZES))
    # Pillow 的 ICO 保存器会按 sizes 逐尺寸重采样；先自己算好最大图，其余尺寸由它缩放。
    largest.save(target, format="ICO", sizes=[(s, s) for s in SIZES])

    print(f"已生成 {target}（{os.path.getsize(target)} 字节，尺寸 {', '.join(str(s) for s in SIZES)}）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
