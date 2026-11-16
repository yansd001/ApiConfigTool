# CC Switch 裁剪来源

- 上游：<https://github.com/farion1231/cc-switch>
- 固定参考提交：`a6f0101b9b25600651ebd95008594107603f712a`
- 上游版本：`4.0.32`
- 许可：MIT，Copyright (c) 2025 Jason Young，全文随源码与发布包保留于 `licenses/CC-Switch-MIT.txt`。

这是从 CC Switch 抽取组件和直接配置写入规则构建的精简版本，不包含完整上游功能，也不读取或修改 CC Switch 自身的数据库。

| 上游文件 | 本项目用途 |
| --- | --- |
| `src/components/ui/button.tsx` | 复用按钮组件，删除 MCP、代理和聚合模式样式分支 |
| `src/components/ui/input.tsx` | 复用输入框及焦点交互 |
| `src/components/ui/dialog.tsx` | 复用 Radix 弹窗，保留焦点管理、遮罩和布局 |
| `src/lib/utils.ts` | 复用 class 合并规则 |
| `src/components/shell/{Sidebar,AppPageHeader,AppGlyph}.tsx` | 按上游外壳结构裁剪，仅保留三个应用和配置操作，使用原生窗口标题栏 |
| `src/index.css`、`tailwind.config.cjs` | 提取浅 / 深色配色、字体角色和控件尺寸 |
| `src/icons/extracted/{openai,claude}.svg` | 应用识别图标 |
| `src-tauri/src/live/project/codex.rs` | 按上游规则写入 `model_providers.custom`、bearer 密钥、保留官方令牌，迁移保留 provider ID |
| `src-tauri/src/claude_desktop_config.rs` | 提取直连 gateway 字段、Claude 模型校验、平台路径、3P 部署模式及 configLibrary 元数据写法 |
| `src-tauri/src/live/floor.rs`、`live/project/claude.rs` | 官方账号方式下清理 Claude Code 的连接、鉴权、模型和协议选择字段；保留独立账号凭据 |
| `src-tauri/src/usage_script.rs`、`docs/user-manual/zh/2-providers/2.5-usage-query.md` | 参考 request / extractor 脚本格式、占位符与提取结果约定，使用受限 QuickJS 执行自定义余额查询；重新实现安全变量插入、缓存与 New API 自动查询 |

Rust 配置库与事务层为了去掉上游数据库、路由和管理模块的耦合重新收敛；不是复制完整上游后仅隐藏菜单。前端依赖也不包含上游的 Skills / MCP / 会话 / 图表 / 代理模块。

Claude Desktop 使用独立 profile ID `50f0a33c-e463-4c45-9eb0-120a99381f21`，名称 `API Config Tool`，不会覆盖 CC Switch 的 profile。保留 Desktop 现有共享配置和其他条目。

账号模式由应用自身完成登录：Codex 恢复 ChatGPT 登录配置，Claude Code 使用 Claude 账号登录，Claude Desktop 按上游 `restore_official_at_paths` 的方式恢复 `1p`。不接入上游多账号管理或 OAuth 托管模块。页面区分“已选择账号登录”与实际账号登录状态。

