<p align="center">
  <img src="docs/logo.png" width="96" alt="Rou" />
</p>

<h1 align="center">Rou 日程</h1>

<p align="center">常驻 Windows 11 桌面的轻量日程浮窗：一句话添加、完成即归档、可被 Agent 直接写入。</p>

<p align="center">
  <img src="docs/preview.png" width="380" alt="Rou 预览" />
</p>

---

## 特性

- **一句话录入**：`明天下午3点 产品评审 #工作 !!`，纯本地正则解析，单条 < 1 ms。
- **完成即归档**：点击圆圈归档，可撤销；重复任务自动生成下一次。
- **贴边收纳**：拖到屏幕边缘自动吸附成 `Qiu` 小标签，点击即恢复。
- **Agent 友好**：命令行 + 收件箱 JSON 双通道，配套 Skill 供 Agent 调用。
- **轻量**：单文件 448 KB（框架依赖版），空闲工作集约 74 MB，软件渲染、零第三方依赖。
- **浅绿主题**：与图标同源的薄荷绿配色，支持深色模式切换。

## 两个版本

| 版本 | 路径 | 体积 | 工作集 | 说明 |
|---|---|---|---|---|
| 轻量版（推荐） | `dist/Rou.exe` | **448 KB** | ≈ 74 MB | 需本机已安装 .NET 10 桌面运行时 |
| 独立版 | `dist/standalone/Rou.exe` | 125 MB | ≈ 129 MB | 自带运行时，任何 Win11 x64 直接运行 |

> 内存数据为本机实测（150% 缩放、虚拟化环境）。同一个 exe 若开启单文件压缩，体积可降到 59 MB，但私有内存会涨到约 120 MB，因此独立版选择不压缩。

## 快速开始

1. 双击 `Rou.exe`，浮窗出现在屏幕右侧。
2. 在顶部输入框写一句话，回车即添加。
3. 点任务左侧圆圈完成并归档，底部提示条可撤销。
4. 首次运行有 3 条示例任务，删掉即可。

## 界面与操作

| 操作 | 效果 |
|---|---|
| 拖动日期区域 | 移动浮窗，位置自动记忆 |
| 拖到屏幕左/右/上边缘 | 吸附为 `Qiu` 标签，点击恢复 |
| 头部按钮 | 归档记录 / 设置 / 收起 |
| 双击任务标题 | 就地改名，回车保存，Esc 取消 |
| 悬停任务行 | 右侧时间变为删除按钮 |
| 点圆圈 | 完成并归档（可撤销） |
| `Enter` / `Esc` | 提交 / 关闭面板 |
| `Ctrl+N` | 聚焦输入框 |
| 全局热键 | 默认 `Ctrl+Alt+P`；被占用时自动改用 `Ctrl+Shift+R` 等，实际绑定显示在设置面板 |
| 托盘图标 | 左键显示/隐藏；右键弹出菜单（新建、归档、设置、退出） |

设置项：深色主题、窗口置顶、桌面层模式（失焦沉底）、开机自启、打开投递目录、退出。

## 一句话语法

| 类型 | 写法 |
|---|---|
| 日期 | `今天` `明天` `后天` `下周一` `周五` `3天后` `10月25日` `2026-11-03` `月底` |
| 时刻 | `9:30` `15点` `下午3点` `晚8点半` `中午12点` `明晚8点` |
| 区间 | `9:00-10:30 评审`（取开始时间） |
| 优先级 | `!` 低 `!!` 中 `!!!` 高，或 `重要` `紧急` |
| 标签 | `#工作` `#财务` |
| 重复 | `每天` `工作日` `每周三` `每月1日`（完成后生成下一次） |
| 全天 | `明天 全天 搬家` |
| 多条 | 用 `；` 或 `然后` 分隔 |

```
明天下午3点 产品评审 #工作 !!
周五 10:00 复盘会
下周一 09:30 部门周会 #工作
每月1日 交房租 #生活
```

解析的两个默认值：只写小时且 ≤6 按**下午**理解；只说时刻且**已过点**顺延到**明天**。

## Agent / 脚本集成

配套 Skill：`~/.workbuddy/skills/rou-schedule/SKILL.md`，包含完整命令、JSON 格式与对话范式。

### 命令行（返回单行 JSON）

```bash
Rou.exe --add "明天下午3点 产品评审 #工作 !!"
# {"ok":true,"added":[{"id":"bc8f645c9a4c","title":"产品评审","due":"2026-10-23 15:00",...}]}

Rou.exe --list
Rou.exe --done <id|序号>
Rou.exe --archive 20
Rou.exe --add --agent "周五 10:00 复盘"   # 来源标记为 agent
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
├─ settings.json  偏好与窗口位置
├─ inbox\         Agent 投递目录（处理后移入 _done）
└─ rou.log        异常与 CLI 调用记录
```

- 写入采用「临时文件 + 原子替换」，并用命名互斥量保护跨进程并发。
- 读档失败（文件被占用）时**拒绝回写**，避免用空数据覆盖已有任务。
- 首次以 Rou 启动会自动接管旧版 Pebble 的数据。
- 卸载：退出后删除 `%APPDATA%\Rou`；若开过自启，还需删除 `HKCU\...\Run\RouSchedule`。

## 性能

| 指标 | 实测 |
|---|---|
| 冷启动 | ≈ 0.35 s |
| 工作集 / 私有内存 | ≈ 74 MB / 66 MB |
| 空闲 CPU | < 0.3 % |
| 输入解析 | < 1 ms / 条 |

关键取舍：

1. **软件渲染**（`RenderOptions.ProcessRenderMode = SoftwareOnly`）：小浮窗用不到 GPU，跳过 D3D/WARP 设备后内存从约 200 MB 降到 60 MB 级。
2. **空闲回收**：失焦时定期 `GC.Collect` + `SetProcessWorkingSetSize`，把闲置页还给系统。
3. **工作站非并发 GC**、关闭 TieredPGO，减少常驻代码与后台线程。
4. **零第三方依赖**：托盘、热键、窗口层级全部 Win32 P/Invoke。
5. **阴影独立成层**：只对纯色圆角矩形做模糊，避免整棵视觉树参与离屏渲染。

## 已知限制

- 仅 Windows x64。
- 解析为规则引擎，复杂句式（"除周三以外的每个工作日"）不支持。
- 任务按「分组 → 时间 → 优先级」自动排序，无手动拖拽排序。
- 时间为本地时间，不做时区换算。
- 归档上限 3000 条。

## 构建

```
src/Rou/
├─ Models/        TaskItem、TaskRow、AppSettings
├─ Services/      Store · Nlp · Cli · AgentInbox · Recurrence · AutoStart
├─ Interop/       Native.cs（Win32 P/Invoke）
├─ Themes/        Light.xaml · Dark.xaml · Controls.xaml
├─ assets/        rou.ico
├─ MainWindow.xaml(.cs)
└─ App.xaml(.cs)
tools/
├─ shot/          截图 / 点击 / 拖拽 / 组合键校验（开发用）
├─ icondump/      导出 exe 内嵌图标
└─ make-icon.ps1  由源图生成多尺寸压缩 ICO
```

```bash
# 轻量版
dotnet publish -c Release -r win-x64 --self-contained false \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=false -o dist

# 独立版
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=false -o dist/standalone
```
