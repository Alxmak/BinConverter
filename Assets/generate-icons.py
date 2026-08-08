#!/usr/bin/env python3
"""
Генератор иконок программы: Assets/AppIcon.ico, AppIcon-256.png, AppIcon-64.png.

Рисунок здесь — тот же, что в Assets/AppLogo.svg: микросхема с выводами, тёмный
экран в светлой рамке и приглашение командной строки. Числа в блоке «Геометрия»
взяты из SVG один в один, в тех же условных единицах (сторона 256), поэтому
правка логотипа означает правку в двух местах — здесь и в SVG. Держать их в
согласии обязательно, иначе иконка и логотип разъедутся.

Почему не рендерим сам SVG: Pillow не умеет SVG, а тянуть в проект cairosvg или
браузер ради трёх файлов, которые пересобираются раз в год, — несоразмерно. Здесь
хватает одной зависимости, и она уже нужна для .ico.

Зачем вообще скриптом, а не картинками из редактора: .ico должен содержать все
размеры сразу. Когда внутри лежит только один, Windows растягивает его на всё
остальное — ярлык на рабочем столе рисуется в 48x48, а при масштабе 150% крупнее,
и шестнадцатипиксельная картинка превращается в кашу. Ровно это и было у прежней
иконки. Плюс каждый размер рисуется отдельно и уменьшается из восьмикратного —
так края получаются сглаженными, чего у прямой отрисовки Pillow нет.

Запуск: python3 Assets/generate-icons.py
"""

from PIL import Image, ImageChops, ImageDraw

# ============================== Геометрия (=SVG) ==============================
# Всё в единицах viewBox логотипа: сторона 256, кончики выводов лежат на границах.

VIEWBOX = 256.0

# Корпус: x, y, ширина, высота, радиус скругления.
BODY = (30.7, 30.7, 194.6, 194.6, 26.8)

# Выводы: толщина, четыре позиции вдоль стороны, центр торца от края,
# и докуда линия заходит под корпус (чтобы круглый торец не выглянул у основания).
PIN_WIDTH = 9.1
PIN_POSITIONS = (78.4, 111.5, 144.5, 177.6)
PIN_TIP = 4.55
PIN_INNER = 40.0

# Рамка экрана — белый с прозрачностью, а не отдельный цвет: так она остаётся
# светлее корпуса на всём протяжении градиента.
FRAME = (50.1, 58.2, 155.8, 139.6, 20.8)
FRAME_ALPHA = 0.36

SCREEN = (57.6, 65.6, 140.8, 124.8, 13.4)

# Уголок и подчёркивание. Толщина та же, что у выводов — в исходном рисунке это
# одно и то же значение.
GLYPH_WIDTH = 9.2
CHEVRON = ((82.3, 93.5), (116.4, 121.2), (82.3, 148.9))
UNDERSCORE = ((129.8, 153.7), (157.9, 153.7))

# Градиент корпуса и выводов — общий, поэтому выводы продолжают корпус без шва.
# Ось наклонена: светлее у левого верхнего угла, темнее у правого нижнего.
CHIP_AXIS = ((0.0, 0.0), (80.4, 302.7))
CHIP_FROM = (0x8D, 0xC9, 0xF6)
CHIP_TO = (0x43, 0x71, 0xD2)

# У экрана свой градиент, строго вертикальный, ровно по его собственной высоте.
SCREEN_FROM = (0x2A, 0x47, 0x78)
SCREEN_TO = (0x15, 0x2C, 0x54)

# ================================ Отрисовка ==================================

# Во сколько раз рисуем крупнее перед уменьшением: сглаживает края, которых
# у Pillow при прямом рисовании нет.
SUPERSAMPLE = 8

# Размеры внутри .ico. 16/20/24 — панель задач и мелкие списки, 32 — Проводник,
# 48 — рабочий стол, 64/128/256 — крупные плитки и высокий DPI.
ICO_SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]


def _axis_ramp(size: int, p1: tuple, p2: tuple) -> Image.Image:
    """
    Маска 'L' вдоль оси: 0 в начале, 255 в конце — то же, что linearGradient.

    Считается как сумма двух одномерных полос: t(x, y) = a*x + b*y — то есть
    строка растягивается по вертикали, столбец по горизонтали, и они складываются.
    Это точно (функция линейная) и быстро, в отличие от прохода по всем пикселям.
    """
    (x1, y1), (x2, y2) = p1, p2
    dx, dy = x2 - x1, y2 - y1
    length2 = dx * dx + dy * dy
    a, b = dx / length2, dy / length2
    shift = -(dx * x1 + dy * y1) / length2

    # Приём с двумя полосами верен, только пока обе растут от нуля. Ось логотипа
    # начинается в (0, 0) и идёт вправо-вниз, так что условие выполнено; если
    # ось однажды поменяют — пусть это упадёт здесь, а не тихо испортит градиент.
    if shift != 0 or a < 0 or b < 0:
        raise ValueError("ось градиента должна начинаться в (0,0) и идти вправо-вниз")

    row = Image.new("L", (size, 1))
    row.putdata([round(255 * a * x) for x in range(size)])
    column = Image.new("L", (1, size))
    column.putdata([round(255 * b * y) for y in range(size)])

    return ImageChops.add(
        row.resize((size, size), Image.NEAREST),
        column.resize((size, size), Image.NEAREST),
    )


def _shade(ramp: Image.Image, colour_from: tuple, colour_to: tuple) -> Image.Image:
    """Заливка по готовой маске-оси: colour_from там, где 0, colour_to там, где 255."""
    size = ramp.size
    return Image.composite(
        Image.new("RGB", size, colour_to), Image.new("RGB", size, colour_from), ramp
    ).convert("RGBA")


def _vertical_shade(width: int, height: int, colour_from: tuple, colour_to: tuple):
    ramp = Image.new("L", (1, height))
    ramp.putdata([round(255 * y / max(1, height - 1)) for y in range(height)])
    return _shade(ramp.resize((width, height), Image.NEAREST), colour_from, colour_to)


def _stroke(draw: ImageDraw.ImageDraw, points, width: float, fill) -> None:
    """
    Линия с круглыми торцами и круглым стыком: сами отрезки плюс круг в каждой
    точке. У Pillow торцы прямые, а в логотипе все концы скруглены.
    """
    thickness = max(1, round(width))
    for (x0, y0), (x1, y1) in zip(points, points[1:]):
        draw.line((x0, y0, x1, y1), fill=fill, width=thickness)

    radius = thickness / 2
    for x, y in points:
        draw.ellipse((x - radius, y - radius, x + radius, y + radius), fill=fill)


def _draw_pins(draw: ImageDraw.ImageDraw, k: float) -> None:
    far = VIEWBOX * k
    for position in PIN_POSITIONS:
        p = position * k
        for line in (
            ((p, PIN_TIP * k), (p, PIN_INNER * k)),
            ((p, far - PIN_TIP * k), (p, far - PIN_INNER * k)),
            ((PIN_TIP * k, p), (PIN_INNER * k, p)),
            ((far - PIN_TIP * k, p), (far - PIN_INNER * k, p)),
        ):
            _stroke(draw, line, PIN_WIDTH * k, 255)


def render(size: int) -> Image.Image:
    """Иконка нужного размера: рисуем в SUPERSAMPLE раз крупнее и уменьшаем."""
    big = size * SUPERSAMPLE
    k = big / VIEWBOX
    canvas = Image.new("RGBA", (big, big), (0, 0, 0, 0))

    # Корпус и выводы — одной маской по одному градиенту, поэтому без стыка.
    shape = Image.new("L", (big, big), 0)
    draw = ImageDraw.Draw(shape)
    x, y, w, h, r = (v * k for v in BODY)
    draw.rounded_rectangle((x, y, x + w, y + h), radius=r, fill=255)
    _draw_pins(draw, k)
    axis = tuple((px * k, py * k) for px, py in CHIP_AXIS)
    canvas.paste(_shade(_axis_ramp(big, *axis), CHIP_FROM, CHIP_TO), (0, 0), shape)

    # Рамка экрана — полупрозрачная, поэтому отдельным слоем поверх корпуса.
    frame = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    x, y, w, h, r = (v * k for v in FRAME)
    ImageDraw.Draw(frame).rounded_rectangle(
        (x, y, x + w, y + h), radius=r, fill=(255, 255, 255, round(255 * FRAME_ALPHA))
    )
    canvas = Image.alpha_composite(canvas, frame)

    # Экран
    x, y, w, h, r = (v * k for v in SCREEN)
    sw, sh = round(w), round(h)
    screen_mask = Image.new("L", (sw, sh), 0)
    ImageDraw.Draw(screen_mask).rounded_rectangle((0, 0, sw - 1, sh - 1), radius=r, fill=255)
    canvas.paste(
        _vertical_shade(sw, sh, SCREEN_FROM, SCREEN_TO), (round(x), round(y)), screen_mask
    )

    # Уголок и подчёркивание
    glyphs = ImageDraw.Draw(canvas)
    for shape_points in (CHEVRON, UNDERSCORE):
        _stroke(
            glyphs,
            [(px * k, py * k) for px, py in shape_points],
            GLYPH_WIDTH * k,
            (255, 255, 255, 255),
        )

    return canvas.resize((size, size), Image.LANCZOS)


def main() -> None:
    frames = [render(size) for size in ICO_SIZES]

    # Pillow сам сохраняет все переданные кадры, но порядок и набор размеров
    # задаём явно — от него зависит, что Windows возьмёт для ярлыка.
    frames[-1].save(
        "Assets/AppIcon.ico",
        format="ICO",
        sizes=[(s, s) for s in ICO_SIZES],
        append_images=frames[:-1],
    )

    render(256).save("Assets/AppIcon-256.png")
    render(64).save("Assets/AppIcon-64.png")

    print("Assets/AppIcon.ico:", ", ".join(f"{s}x{s}" for s in ICO_SIZES))
    print("Assets/AppIcon-256.png, Assets/AppIcon-64.png")


if __name__ == "__main__":
    main()
