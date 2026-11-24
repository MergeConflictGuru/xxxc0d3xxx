# Nightcall artwork

The final ImageGen masters are in `Source/day.png` and `Source/night.png`. These are the selected large brick phones with thick aerials continuing out of frame. The daytime version uses ivory, turquoise and sunset pink. The night version uses charcoal, neon pink and cyan, no moon, and three simplified skyline shapes.

Generated with the built-in ImageGen tool. Exact final edit prompts are in `prompts.txt`. Earlier concept sheets were previews; the app uses only this final pair.

The Windows tray has a separate code-drawn brick-phone silhouette: `tray-day.png` and `tray-night.png`, plus native-size ICO frames from 16 to 64 pixels. It uses solid day/night colors, a bold aerial, speaker and keypad, with no palms or skyline. The detailed artwork remains in the launcher and app UI.

`python tools/brand_assets.py` derives the Windows ICOs, 512-pixel runtime artwork, Android launcher sizes and Android artwork from these masters. It only converts formats and sizes; it does not alter the design. WPF uses embedded resources and Android uses packaged resources. No generated-image cache path is a runtime dependency.
