![Rou](docs/logo-96.png)

# Rou 日程

常驻 Windows 11 桌面的轻量日程浮窗：**一句话添加、完成即归档、可被 Agent 直接写入**。

原生 WPF（.NET 10）实现，无第三方依赖，单文件 exe，双击即用。

![预览](docs/preview.png)

[下载 v1.0.0](https://github.com/LeastBit/Rou/releases/tag/v1.0.0) · [Agent Skill](skills/rou-schedule/SKILL.md)

---

## 特性

- **一句话录入**：`明天下午3点 产品评审 #工作 !!`，纯本地正则解析，单条 < 1 ms，不联网、不调用模型。
- **完成即归档**：点圆圈归档并写入历史，2.8 秒内可撤销；重复任务自动生成下一次。
- **贴边收纳**：拖到屏幕左/右/上边缘自动吸附成 `Qiu` 药丸，点击恢复原位。
- **托盘菜单**：左键显隐，右键「展开 / 新建任务 / 归档记录 / 设置 / 退出」。
- **全局热键**：默认 `Ctrl+Alt+P`，被占用时自动回退到 `Ctrl+Shift+R` 等，实际绑定显示在底栏与设置面板。
- **轻量**：轻量版 453 KB，私有内存 ≈ 67 MB，空闲工作集可回落到 6 MB 级。
- **浅绿主题**：与图标同源的薄荷绿配色（主色 `#3F9E70`），可切换深色模式。

## 两个版本

| 版本 | 文件 | 体积 | 内存 | 说明 |
|---|---|---|---|---|
| 轻量版（推荐） | `dist/Rou.exe` | **453 KB** | 私有 ≈ 67 MB | 需要 .NET 10 桌面运行时 |
| 独立版 | `dist/standalone/Rou.exe` | 124.9 MB | 私有 ≈ 68 MB | 自带运行时，任何 Win11 x64 直接双击 |

> 均以单文件发布（不压缩）。若改用压缩单文件，体积可降到 59 MB，但私有内存会涨到约 120 MB，因此独立版选择不压缩。
> 数据为本机实测（150% 缩放、虚拟化环境、无独显）。

## 快速开始

1. 双击 `Rou.exe`，浮窗出现在屏幕右侧。
2. 在顶部输入框写一句话，回车即添加。
3. 点任务左侧圆圈完成并归档。
4. 首次运行有 3 条示例任务，删掉即可。

## 界面与操作

| 操作 | 效果 |
|---|---|
| 拖动日期区域 | 移动浮窗，位置自动记忆 |
| 拖到屏幕左/右/上边缘 | 吸附为 `Qiu` 标签，点击恢复 |
| 头部三个按钮 | 归档记录 / 设置 / 收起 |
| 双击任务标题 | 就地改名，回车保存，Esc 取消 |
| 悬停任务行 | 右侧时间变为删除按钮 |
| 点圆圈 | 完成并归档（可撤销） |
| `Enter` / `Esc` | 提交 / 关闭面板 |
| `Ctrl+N` | 聚焦输入框 |
| 全局热键 | 默认 `Ctrl+Alt+P`，被占用时自动回退 |
| 托盘图标 | 左键显隐，右键菜单 |

设置项：深色主题、窗口置顶、桌面层模式（失焦沉底）、开机自启、打开投递目录、退出。
窗口高度随任务数量自适应，最多 540，也可一键收起为条状。

## 一句话语法

| 类型 | 写法 |
|---|---|
| 日期 | `今天` `明天` `后天` `下周一` `周五` `3天后` `10月25日` `2026-11-03` `月底` |
| 时刻 | `9:30` `15点` `下午3点` `晚8点半` `中午12点` `明晚8点` |
| 区间 | `9:00-10:30 评审`（取开始时间） |
| 优先级 | `!` 低 `!!` 中 `!!!` 高，或 `重要` `紧急` |
| 标签 | `#工作` `#财务` |
| 重复 | `每天` `工作日` `每周三` `每月1日` |
| 全天 | `明天 全天 搬家` |
| 多条 | 用 `；` 或 `然后` 分隔 |

```
明天下午3点 产品评审 #工作 !!
周五 10:00 复盘会
下周一 09:30 部门周会 #工作
每月1日 交房租 #生活
```

两个默认值：只写小时且 ≤6 按**下午**理解（"3点" = 15:00）；只说时刻且**已过点**顺延到**明天**。

## Agent / 脚本集成

配套 Skill：`skills/rou-schedule/SKILL.md`（本地副本在 `~/.workbuddy/skills/rou-schedule/`）。

### 命令行（返回单行 JSON）

```bash
Rou.exe --add "明天下午3点 产品评审 #工作 !!"
# {"ok":true,"added":[{"id":"bc8f645c9a4c","title":"产品评审","due":"2026-10-23 15:00",...}]}

Rou.exe --list
Rou.exe --done <id|序号>
Rou.exe --archive 20
Rou.exe --add --agent "周五 10:00 复盘"
```

### 收件箱投递

把 `.json` 放进 `%APPDATA%\Rou\inbox\`，1 秒内入库，文件随即移到 `inbox\_done\`：

```json
{ "tasks": [
  { "title": "提交报销单", "due": "2026-10-24 11:00", "priority": 3, "tags": ["财务"] },
  { "raw": "下周一 09:30 部门周会 #工作" }
] }
```

也支持 `["明天9点 交报告"]`、`{"raw":[...]}`、`{"text":"..."}`。

## 数据与隐私

全部本地存储，无网络请求：

```
%APPDATA%\Rou\
├─ tasks.json     未完成任务
├─ archive.json   归档记录（含完成时间）
├─ settings.json  偏好、窗口位置与吸附状态
├─ tray.ico       托盘图标（启动时从内嵌资源释放）
├─ inbox\         Agent 投递目录（处理后移入 _done）
└─ rou.log        异常与 CLI 调用记录
```

- 写入采用「临时文件 + 原子替换」，并用命名互斥量保护跨进程并发。
- 读档失败（文件被占用）时保留内存中的数据并**拒绝回写**，避免用空数据覆盖。
- 首次以 Rou 启动会自动接管旧版 Pebble 的数据。
- 卸载：退出后删除 `%APPDATA%\Rou`；若开过自启，还需删除 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\RouSchedule`。

## 性能

| 指标 | 实测 |
|---|---|
| 私有内存 | ≈ 67 MB |
| 工作集 | 空闲 ≈ 6 MB，交互时 ≈ 70 MB |
| 冷启动 | ≈ 0.3–0.5 s |
| 空闲 CPU | < 0.1 % |
| 输入解析 | < 1 ms / 条 |

关键取舍：

1. **软件渲染**（`RenderOptions.ProcessRenderMode = SoftwareOnly`）：小浮窗用不到 GPU，跳过 D3D/WARP 设备后私有内存从约 200 MB 降到 60 MB 级。
2. **空闲回收**：失焦时定时 `GC.Collect` + `SetProcessWorkingSetSize`，把闲置页还给系统（工作集因此能降到个位数 MB）。
3. **工作站非并发 GC**、关闭 TieredPGO，减少常驻代码与后台线程。
4. **零第三方依赖**：托盘、热键、窗口层级全部走 Win32 P/Invoke，未引入 WinForms / WinUI。
5. **阴影独立成层**：只对纯色圆角矩形做模糊，避免整棵视觉树参与离屏渲染。

## 已知限制

- 仅 Windows x64。
- 解析为规则引擎，复杂句式（"除周三以外的每个工作日"）不支持。
- 任务按「分组 → 时间 → 优先级」自动排序，无手动拖拽排序。
- 时间为本地时间，不做时区换算。
- 归档上限 3000 条，超出丢弃最旧记录。
- 托盘图标默认在 Win11 的"隐藏的图标"弹层里，需要常驻可手动拖出。

## 构建

```
src/Rou/
├─ Models/        TaskItem、TaskRow、AppSettings
├─ Services/      Store · Nlp · Cli · AgentInbox · Recurrence · AutoStart
├─ Interop/       Native.cs（Win32 P/Invoke）
├─ Themes/        Light.xaml（浅绿） · Dark.xaml · Controls.xaml
├─ assets/        rou.ico（程序图标） · rou-tray.ico（托盘图标）
├─ MainWindow.xaml(.cs)
└─ App.xaml(.cs)
skills/rou-schedule/  Agent 技能说明
tools/
├─ shot/          截图 / 点击 / 拖拽 / 组合键校验（开发用）
├─ icondump/      导出 ico 或 exe 内嵌图标
├─ make-icon.ps1     由源图生成多尺寸程序图标
├─ make-tray.ps1     生成高对比度托盘图标
├─ make-logo-small.ps1 生成 README 用的 96px logo
├─ release.py     发布 Release 并上传资产（无需 gh CLI）
└─ push_api.py    在 git 443 被拦截时用 Git Data API 同步仓库
```

```bash
# 轻量版
dotnet publish -c Release -r win-x64 --self-contained false \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=false -o dist

# 独立版
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=false -o dist/standalone
```
