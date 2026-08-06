#!/usr/bin/env python3
"""
Генератор иконок программы: Assets/AppIcon.ico, AppIcon-256.png, AppIcon-64.png.

Зачем скриптом, а не картинкой в редакторе: у прежней иконки было две ошибки,
которые в редакторе на большом холсте не видны вовсе, а на рабочем столе и в
панели задач превращали логотип в кашу.

1. В .ico лежал ровно ОДИН размер — 16x16. Ярлык на рабочем столе Windows рисует
   в 48x48 (а при 150% масштабе и крупнее), то есть система РАСТЯГИВАЛА
   шестнадцатипиксельную картинку в четыре раза. В разделе "О программе" всё
   выглядело нормально просто потому, что там берётся отдельный PNG на 256.

2. Толщина линий рисунка была 3px при плитке 234px — 1.2% ширины. При сжатии до
   16px это 0.19 пикселя: линия физически не может быть нарисована и вырождается
   в серую дымку. Здесь толщина задана в долях размера (STROKE_*), поэтому
   остаётся видимой на любом размере.

Плюс третье, менее очевидное: маленькие размеры рисуются ДРУГИМ, упрощённым
рисунком (COMPACT_MAX_SIZE). Уменьшать детальную версию до 16px бессмысленно —
стрелки-уголки сливаются; вместо них берутся сплошные треугольники, которые
переживают уменьшение.

Запуск: python3 Assets/generate-icons.py
"""

from PIL import Image, ImageDraw

# ============================= Параметры рисунка =============================

# Градиент плитки — сняты с прежней иконки, чтобы стиль не поехал.
GRADIENT_FROM = (0x61, 0xB7, 0xF7)   # верхний левый угол
GRADIENT_TO = (0x00, 0x77, 0xD9)     # нижний правый угол

CORNER_RADIUS = 0.22                 # в долях стороны

# Ниже этого размера (включительно) рисуется упрощённый вариант.
COMPACT_MAX_SIZE = 24

# Толщина линий в долях стороны. Порог видимости — примерно 1px на итоговом
# размере: 0.088 * 16 = 1.4px, то есть линия остаётся линией даже в 16x16.
STROKE_DETAILED = 0.088
STROKE_COMPACT = 0.130

# Во сколько раз рисуем крупнее перед уменьшением: сглаживает края, которых
# у PIL при прямом рисовании нет.
SUPERSAMPLE = 8

# Размеры внутри .ico. 16/20/24 — панель задач и мелкие списки, 32 — Проводник,
# 48 — рабочий стол, 64/128/256 — крупные плитки и высокий DPI.
ICO_SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]


def rounded_tile(size: int) -> Image.Image:
    """Плитка с диагональным градиентом и скруглёнными углами."""
    gradient = Image.new("RGB", (size, size))
    pixels = gradient.load()
    for y in range(size):
        for x in range(size):
            # Проекция на диагональ: 0 в левом верхнем углу, 1 в правом нижнем.
            t = (x + y) / (2 * (size - 1)) if size > 1 else 0.0
            pixels[x, y] = tuple(
                round(a + (b - a) * t) for a, b in zip(GRADIENT_FROM, GRADIENT_TO)
            )

    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        (0, 0, size - 1, size - 1), radius=round(size * CORNER_RADIUS), fill=255
    )

    tile = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    tile.paste(gradient, (0, 0), mask)
    return tile


def draw_detailed(draw: ImageDraw.ImageDraw, size: int) -> None:
    """
    Полная версия: две вертикальные линии — место разреза файла, и две стрелки,
    сходящиеся к ним с боков. Тот же смысл, что и у прежнего логотипа
    (разделить/собрать), но линий меньше и они толще.
    """
    s = size
    w = max(1, round(STROKE_DETAILED * s))
    white = (255, 255, 255, 255)

    # Место разреза.
    for x in (0.415, 0.585):
        draw.line([(x * s, 0.22 * s), (x * s, 0.78 * s)], fill=white, width=w)

    # Стрелки внутрь: слева направо и справа налево. Наконечник — сплошной
    # треугольник, а не два штриха: штрихи такой же толщины, как древко,
    # сливались с ним в бесформенный утолщённый конец.
    for direction in (1, -1):
        base = 0.10 if direction == 1 else 0.90
        shaft_end = base + direction * 0.16
        tip = base + direction * 0.225
        draw.line([(base * s, 0.5 * s), (shaft_end * s, 0.5 * s)], fill=white, width=w)
        draw.polygon(
            [
                (shaft_end * s, (0.5 - 0.115) * s),
                (shaft_end * s, (0.5 + 0.115) * s),
                (tip * s, 0.5 * s),
            ],
            fill=white,
        )


def draw_compact(draw: ImageDraw.ImageDraw, size: int) -> None:
    """
    Версия для мелких размеров: линии толще и разнесены шире, а уголки стрелок
    заменены сплошными треугольниками — тонкие штрихи на 16px сливаются
    в пятно, а заливка нет.
    """
    s = size
    w = max(1, round(STROKE_COMPACT * s))
    white = (255, 255, 255, 255)

    for x in (0.395, 0.605):
        draw.line([(x * s, 0.24 * s), (x * s, 0.76 * s)], fill=white, width=w)

    for direction in (1, -1):
        base = 0.09 if direction == 1 else 0.91
        tip = base + direction * 0.17
        draw.polygon(
            [(base * s, 0.29 * s), (base * s, 0.71 * s), (tip * s, 0.5 * s)], fill=white
        )


def render(size: int) -> Image.Image:
    """Иконка нужного размера: рисуем крупнее и уменьшаем — ради сглаживания."""
    big = size * SUPERSAMPLE
    image = rounded_tile(big)
    draw = ImageDraw.Draw(image)

    if size <= COMPACT_MAX_SIZE:
        draw_compact(draw, big)
    else:
        draw_detailed(draw, big)

    return image.resize((size, size), Image.LANCZOS)


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
