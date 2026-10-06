## UI Toolkit: leading-icon text
Never put an emoji/symbol inline at the start of a `Button.text` (or a lone `Label`) — on Windows the fallback glyph draws wider than it measures and the following text overlaps the icon. Use `LevelPlayUIStyle.ApplyIconText(button, text)` (and `CreateIconLabel` for title labels) so the icon gets its own reserved box.
