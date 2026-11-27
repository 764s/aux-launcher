# 辅助启动器

Windows 本地 WPF 应用。选择 `.exe` 后，通过现有 V2Ray、Clash 等软件的本地 SOCKS5 入口启动受管进程树。使用 WinDivert 2.2.2-A 已签名驱动；启动、归属、TCP/UDP 转发、记忆和状态逻辑均在本工程中实现。

## 运行

使用 `release/win-x64/AuxLauncher.exe`，或解压 `release/AuxLauncher-win-x64.zip` 后运行。请保留同目录的全部组件。

- Windows 10/11 x64；本次构建为依赖框架的发布包，需要 **.NET 8 Windows Desktop Runtime x64**，不是仅安装普通 .NET Runtime。开发机已具备此运行时。
- 主程序以普通权限打开，网络接管组件单独请求 UAC；目标程序不继承管理员权限。
- 现有代理软件已连接，TUN 关闭，本地 SOCKS5 支持 TCP 与 UDP。
- 尚未配置入口时辅助探测常见本机端口；仅唯一明确时采用，否则显示确认卡片。地址、端口和可选认证保存后复用。
- 运行中关闭窗口会隐藏到托盘；目标及受管子进程全部结束后才释放本次会话。

## 工程结构

```text
src/
  AuxLauncher.App/       WPF 界面、普通用户运行核心、启动事务和恢复
  AuxLauncher.Core/      记忆、DPAPI、进程/Job、IPC、报文与 SOCKS5 协议
  AuxLauncher.Guard/     UAC 接管组件、WinDivert、进程归属、TCP 映射和 UDP 分发
  AuxLauncher.Relay/     普通用户独立转发组件、SOCKS5 TCP/UDP 中继
tests/
  AuxLauncher.Tests/     核心、运行核心及真实驱动集成测试
  AuxLauncher.Fixture/   受控目标和父子进程测试程序
vendor/WinDivert/        官方 DLL、已签名 SYS、原始许可和来源校验
docs/                   四份定稿文档及验证记录
test-results/           本次交付的机器可读验证结果
release/                可运行目录和 ZIP
build.ps1               构建及发布
test.ps1                自动化验证入口
```

## 构建与测试

在本目录打开普通权限 PowerShell，安装 .NET 8 SDK 后执行：

```powershell
.\build.ps1 -Publish
.\test.ps1 -NoBuild
.\test.ps1 -NoBuild -Driver
.\test.ps1 -NoBuild -Driver -IPv6
```

不依赖额外代理引擎或 NuGet 第三方运行库。驱动集成测试会通过 UAC 启动测试接管组件。测试自身必须保持普通权限；IPv4、IPv6 测试依次执行。

发布构建不包含 PDB，并对编译器输出路径做统一映射。打包使用全新暂存目录；发现个人运行配置、本机用户路径或凭据模式时会停止发布，避免旧产物混入运行包。

驱动测试创建专用目标、SOCKS5 服务和回显服务，不使用真实账户。它在显式自测模式下仅对测试目标启用回环拦截，以排除本机其他外网代理组件的影响；正式应用始终放行正常本机回环通信。测试要求代理侧计数增加且返回内容正确，直接联网成功不算代理通过。

结果见[验证记录](docs/verification/验证记录.md)和 [IPv4 报告](test-results/ipv4.json)、[IPv6 报告](test-results/ipv6.json)。临时运行结果写入 `test-results/runs/`，不提交到 Git。

## 真实软件实测

1. 在现有代理软件中关闭 TUN，保持连接并确认本地 **SOCKS5** 端口及 UDP 支持。应用不会修改系统全局代理和 DNS。若系统已经配置全局代理，需要自行考虑它对对照结果的影响。
2. 普通方式打开启动器，选择目标 `.exe`。尽量先退出该软件既有实例，避免单实例程序把请求交给旧进程。
3. 点击“经代理启动”，按需确认本地入口，完成 UAC。执行目标软件的实际关键业务，同时查看代理软件连接记录或日志，核对请求确实经过该入口。
4. 另行直接打开同名程序做对照，确认该实例未被本会话纳入。父进程退出而子进程存活时，启动器应继续锁定并接管。
5. 临时停用本地代理入口：目标应保留，显示代理中断；恢复入口后，新请求应自动恢复。已断开的 TCP 连接不会自动续传。
6. 自行退出所有受管进程，确认回收结束、目标选择解锁；再次打开启动器应恢复上次选择和已保存入口。

## 行为与边界

- 9 态：IDLE、READY、MISSING、PREPARING、RUNNING、PROXY_LOST、PROTECTION_INTERRUPTED、RECOVERING、FAILED。忙碌期间禁止更换目标和修改历史。
- 历史最多 4 项，移除当前项清空选择，不自动换选；文件失效保留记录并提示重新定位。
- 已确认受管流量不主动回退直连。归属超时仍未知的原始包放行并记录 `unknown-bypass`，不能将这些流量计作代理覆盖。
- 代理中断与保护中断区别处理。保护中断不强杀目标，不声称已阻止直连；恢复仅接管新连接，警告保留至会话结束。
- 自主 DNS 走对应 TCP/UDP 转发链。Windows 共享 DNS Client 服务查询仍使用系统原路径；它造成真实业务阻塞时需继续专项处理，不能宣称已代理所有 DNS。
- 已有实例、服务、broker 或其他系统网络组件代发的连接不因名称相同而纳入接管。需管理员权限的目标、脱离 Job 的工作进程、共享端点歧义、分片、广播/多播和公网入站业务不属于已验证范围。
- 本机受控 IPv4/IPv6 驱动转发已通过。真实外网测试观察到既有共享网络组件代发连接，未完成真实目标软件跨代理产品的业务验收，详见验证记录。

设置及日志位于 `%LOCALAPPDATA%\AuxLauncher`。密码与会话密钥使用当前 Windows 用户 DPAPI 加密，设置原子保存，日志分组件轮转。仅清理本会话资源，不停止现有代理软件，不全局卸载共享驱动。

## 定稿文档

- [核心需求](docs/requirements/辅助启动器核心需求.md)
- [痛点文档](docs/requirements/辅助启动器痛点文档.md)
- [逻辑实现方案](docs/design/辅助启动器逻辑实现方案.md)
- [UI 实现文档](docs/design/辅助启动器UI实现文档.html)
- [WinDivert 来源与许可](vendor/WinDivert/NOTICE.md)

定稿文档的产品与架构语义保持不变，示例路径使用通用目录。上传 GitHub 时提交源码、脚本、文档、测试和 WinDivert 许可；可运行 ZIP 适合放到 Releases。不要上传个人设置、会话数据、运行日志和本机调试符号。
