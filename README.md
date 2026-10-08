# FrameTrace.Editor
视频生成素材记录库

## 配置文件

- `config.json`：环境相关配置，每台电脑或每个发布环境分别维护，包含素材库、网页目录和 FFmpeg/FFprobe 路径。
- `settings.json`：相对稳定的功能参数，包含项目名称、封面参数、分页参数和宽高比范围。

程序会优先读取这两份配置。为兼容旧部署，如果缺少 `settings.json`，会继续尝试把旧版单文件 `config.json` 作为完整配置读取。
