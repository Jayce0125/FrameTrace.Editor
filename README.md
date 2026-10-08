# FrameTrace.Editor
视频生成素材记录库

## 配置文件

- `config.json`：环境相关配置，每台电脑或每个发布环境分别维护，包含素材库、网页目录和 FFmpeg/FFprobe 路径。
- `settings.json`：相对稳定的功能参数，包含项目名称、封面参数、分页参数、清晰度档位和宽高比范围。

清晰度档位由 `quality_options` 配置。程序先将视频宽高归一化，使用短边作为有效分辨率，并要求实际宽高比不超过档位的 `max_aspect_ratio`。档位按 `min_short_edge` 从高到低匹配，适用于横屏、竖屏和超宽视频。

程序会优先读取这两份配置。为兼容旧部署，如果缺少 `settings.json`，会继续尝试把旧版单文件 `config.json` 作为完整配置读取。
